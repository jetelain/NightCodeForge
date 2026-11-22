using System.Text.Json;
using NightCodeForge.Core.Benchmarking;
using NightCodeForge.Core.Domain;

namespace NightCodeForge.Agents.Benchmarking;

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

        var directive = ExtractStageDirective(workflow, stageId);
        return new BenchmarkStagePolicy(stageId, AgentMode.Full, permittedTools, directive);
    }

    public static void ConfigureWorkspaceSkills(Workspace workspace, bool enableStackSkills)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        if (enableStackSkills)
        {
            workspace.GlobalSkillsInjectionMode = GlobalSkillsInjectionMode.ForceEnabled;
            workspace.DisabledGlobalSkillsJson = JsonSerializer.Serialize(new[] { BenchmarkSkillNames.MdFetch });
        }
        else
        {
            workspace.GlobalSkillsInjectionMode = GlobalSkillsInjectionMode.ForceDisabled;
            workspace.DisabledGlobalSkillsJson = null;
        }
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
