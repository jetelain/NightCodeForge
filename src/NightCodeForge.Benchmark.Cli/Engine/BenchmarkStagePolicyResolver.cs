using System.Text.Json;
using NightCodeForge.Benchmark.Cli.Models;
using NightCodeForge.Benchmark.Cli.Verification;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Benchmark.Cli.Engine;

/// <summary>
/// Resolved stage identity, tool permissions, skills configuration, and directive for a benchmark category.
/// </summary>
public sealed record BenchmarkStagePolicy(
    string StageId,
    AgentMode RequiredMode,
    IReadOnlyList<string> PermittedTools,
    string StageDirective);

/// <summary>
/// Resolves category-accurate NightCodeForge tool permissions, skill injection settings, and stage directives
/// aligned with <see cref="StandardWorkflowDefinitions"/>.
/// </summary>
public static class BenchmarkStagePolicyResolver
{
    public static BenchmarkStagePolicy Resolve(BenchmarkProblemCategory category, bool enableStackSkills)
    {
        var workflow = StandardWorkflowDefinitions.CreateStandard3AgentWorkflow();

        string stageId;
        IReadOnlyList<string> baseTools;
        switch (category)
        {
            case BenchmarkProblemCategory.Implementation:
                stageId = StandardStageIds.Implementation;
                baseTools = BenchmarkDefaults.ImplementationPermittedTools;
                break;
            case BenchmarkProblemCategory.CodeReview:
                stageId = StandardStageIds.CodeReviewAndFix;
                baseTools = BenchmarkDefaults.CodeReviewPermittedTools;
                break;
            case BenchmarkProblemCategory.TaskReview:
                stageId = StandardStageIds.TaskReview;
                baseTools = BenchmarkDefaults.TaskReviewPermittedTools;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(category), category, "Unsupported benchmark problem category.");
        }

        var permittedTools = new List<string>(baseTools);
        if (enableStackSkills)
        {
            permittedTools.Add(CanonicalToolNames.LoadSkill);
        }

        var rawDirective = ExtractStageDirective(workflow, stageId);
        var directive = AdaptDirectiveForBenchmark(rawDirective, category);
        return new BenchmarkStagePolicy(stageId, AgentMode.Full, permittedTools, directive);
    }

    public static WorkspaceProjectConfig BuildProjectConfig(
        BenchmarkInstance instance,
        string workspacePath,
        ExecutionMode executionMode)
    {
        ArgumentNullException.ThrowIfNull(instance);

        var normalizedLanguage = BenchmarkLanguages.Normalize(instance.Language);
        var stack = CodeStack.NormalizeStack(normalizedLanguage);
        var preset = CodeStackPresets.Resolve(stack);

        var buildCommand = ResolveBuildCommand(stack, preset, workspacePath);
        string testCommand;
        try
        {
            testCommand = BenchmarkTestCommandResolver.ResolveAgentTestCommand(instance, workspacePath, executionMode);
        }
        catch (BenchmarkVerificationException)
        {
            testCommand = preset.TestCommand;
        }

        return new WorkspaceProjectConfig
        {
            CodeStack = stack,
            BuildCommand = buildCommand,
            TestCommand = testCommand,
            FormatCommand = preset.FormatCommand,
            CoverageCommand = preset.CoverageCommand,
            BuildTimeoutSeconds = WorkflowDefaults.DefaultBuildTimeoutSeconds,
            TestTimeoutSeconds = WorkflowDefaults.DefaultTestTimeoutSeconds
        };
    }

    public static void ConfigureWorkspaceSkills(Workspace workspace, bool enableStackSkills, string? language)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        if (enableStackSkills)
        {
            workspace.GlobalSkillsInjectionMode = GlobalSkillsInjectionMode.ForceEnabled;
            var disabledSkills = new List<string> { BenchmarkSkillNames.MdFetch };
            if (!BenchmarkLanguages.IsCSharp(language))
            {
                disabledSkills.Add(BenchmarkSkillNames.CSharpNav);
                disabledSkills.Add(BenchmarkSkillNames.CodeCondenser);
            }

            workspace.DisabledGlobalSkillsJson = JsonSerializer.Serialize(disabledSkills);
        }
        else
        {
            workspace.GlobalSkillsInjectionMode = GlobalSkillsInjectionMode.ForceDisabled;
            workspace.DisabledGlobalSkillsJson = null;
        }
    }

    public static string? GetSandboxEnvironmentGuidance(string? language)
    {
        var normalized = BenchmarkLanguages.Normalize(language);
        if (normalized is BenchmarkLanguages.TypeScript or BenchmarkLanguages.JavaScript)
        {
            return $"[Sandbox Node Setup Guidance]: If `yarn install` / `pnpm install` / `npm install` fails on blocked native postinstall downloads (such as `sharp` from GitHub), retry directly with `--ignore-scripts` (prefer `--frozen-lockfile --ignore-scripts` first). If `yarn` or `pnpm` is missing, install into a user-writable prefix without sudo (`npm install -g --prefix {BenchmarkTestCommandResolver.NpmGlobalPrefix} --cache {BenchmarkTestCommandResolver.NpmCacheDir} --ignore-scripts yarn` and prepend `{BenchmarkTestCommandResolver.NpmGlobalPrefix}/bin` to `PATH`).";
        }

        return null;
    }

    private static string ResolveBuildCommand(string stack, CodeStackPreset preset, string workspacePath)
    {
        if (!string.Equals(stack, CodeStack.DotNet, StringComparison.OrdinalIgnoreCase))
        {
            return preset.BuildCommand;
        }

        var targets = BenchmarkTestCommandResolver.DiscoverDotNetTargets(workspacePath);
        return targets.Count == 0
            ? CodeStackPresets.DotNet.BuildCommand
            : string.Join(" && ", targets.Select(t => $"dotnet build {BenchmarkTestCommandResolver.QuotePathArgument(t)}"));
    }

    private static string AdaptDirectiveForBenchmark(string directive, BenchmarkProblemCategory category)
    {
        return category switch
        {
            BenchmarkProblemCategory.Implementation => AdaptImplementationDirective(directive),
            BenchmarkProblemCategory.CodeReview => ReplaceRequiredSegment(
                directive,
                "Compare against the specification in docs/tasks/ and the implementation claims.",
                "Compare against the task specification in <task> and the implementation claims.",
                category),
            BenchmarkProblemCategory.TaskReview => directive,
            _ => directive
        };
    }

    private static string AdaptImplementationDirective(string directive)
    {
        var adapted = ReplaceRequiredSegment(
            directive,
            "adhering to the task specification document in docs/tasks/, the approved plan, human answers, and prior reviewer feedback. If editing docs/tasks/, preserve the YAML frontmatter block intact without modifying the id or status fields.",
            "adhering to the task specification in <task>.",
            BenchmarkProblemCategory.Implementation);

        adapted = ReplaceRequiredSegment(
            adapted,
            "When source files are edited, always run 'dotnet build' before running 'dotnet test --no-build' to compile your changes.",
            "Use the active Build Command and Test Command specified in <execution_guidelines> when compiling and testing.",
            BenchmarkProblemCategory.Implementation);

        adapted = ReplaceRequiredSegment(
            adapted,
            " If unexpected breaking blockers arise, invoke 'ask'.",
            string.Empty,
            BenchmarkProblemCategory.Implementation);

        return adapted;
    }

    private static string ReplaceRequiredSegment(
        string directive,
        string expectedSegment,
        string replacement,
        BenchmarkProblemCategory category)
    {
        if (!directive.Contains(expectedSegment, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"StandardWorkflowDefinitions stage directive for '{category}' no longer contains expected segment '{expectedSegment}'.");
        }

        return directive.Replace(expectedSegment, replacement, StringComparison.Ordinal);
    }

    private static string ExtractStageDirective(WorkflowDefinition workflow, string stageId)
    {
        var stage = workflow.Stages.FirstOrDefault(s => string.Equals(s.StageId, stageId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Standard workflow definition does not contain stage '{stageId}'.");

        using var doc = JsonDocument.Parse(stage.ConfigurationJson);
        if (!doc.RootElement.TryGetProperty("StageDirective", out var directiveProp) ||
            directiveProp.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(directiveProp.GetString()))
        {
            throw new InvalidOperationException($"Stage '{stageId}' in StandardWorkflowDefinitions is missing a StageDirective.");
        }

        return directiveProp.GetString()!;
    }
}
