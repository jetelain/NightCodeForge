using NightCodeForge.Benchmark.Cli.Engine;
using NightCodeForge.Benchmark.Cli.Models;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Benchmark.Cli.Tests.Engine;

public sealed class BenchmarkStagePolicyResolverTests
{
    [Theory]
    [InlineData(BenchmarkProblemCategory.Implementation, StandardStageIds.Implementation, true, true)]
    [InlineData(BenchmarkProblemCategory.Implementation, StandardStageIds.Implementation, false, false)]
    [InlineData(BenchmarkProblemCategory.CodeReview, StandardStageIds.CodeReviewAndFix, true, true)]
    [InlineData(BenchmarkProblemCategory.CodeReview, StandardStageIds.CodeReviewAndFix, false, false)]
    [InlineData(BenchmarkProblemCategory.TaskReview, StandardStageIds.TaskReview, true, true)]
    [InlineData(BenchmarkProblemCategory.TaskReview, StandardStageIds.TaskReview, false, false)]
    public void Resolve_GatesToolsAndSkillsPerCategory(
        BenchmarkProblemCategory category,
        string expectedStageId,
        bool enableStackSkills,
        bool expectLoadSkill)
    {
        var policy = BenchmarkStagePolicyResolver.Resolve(category, enableStackSkills);

        Assert.Equal(expectedStageId, policy.StageId);
        Assert.Equal(AgentMode.Full, policy.RequiredMode);
        Assert.False(string.IsNullOrWhiteSpace(policy.StageDirective));

        Assert.Contains(CanonicalToolNames.Read, policy.PermittedTools);
        Assert.Contains(CanonicalToolNames.Edit, policy.PermittedTools);
        Assert.Contains(CanonicalToolNames.Write, policy.PermittedTools);
        Assert.Contains(CanonicalToolNames.Bash, policy.PermittedTools);
        Assert.DoesNotContain(CanonicalToolNames.ProposeTasks, policy.PermittedTools);
        Assert.DoesNotContain(CanonicalToolNames.WebSearch, policy.PermittedTools);

        if (expectLoadSkill)
        {
            Assert.Contains(CanonicalToolNames.LoadSkill, policy.PermittedTools);
        }
        else
        {
            Assert.DoesNotContain(CanonicalToolNames.LoadSkill, policy.PermittedTools);
        }

        if (category == BenchmarkProblemCategory.Implementation)
        {
            Assert.DoesNotContain(CanonicalToolNames.Ask, policy.PermittedTools);
            Assert.DoesNotContain(CanonicalToolNames.RequestChanges, policy.PermittedTools);
        }
        else if (category == BenchmarkProblemCategory.CodeReview)
        {
            Assert.Contains(CanonicalToolNames.RequestChanges, policy.PermittedTools);
            Assert.DoesNotContain(CanonicalToolNames.Ask, policy.PermittedTools);
        }
        else if (category == BenchmarkProblemCategory.TaskReview)
        {
            Assert.Contains(CanonicalToolNames.Ask, policy.PermittedTools);
            Assert.DoesNotContain(CanonicalToolNames.RequestChanges, policy.PermittedTools);
        }
    }

    [Fact]
    public void ConfigureWorkspaceSkills_ExcludesMdFetchForCSharpAndForcesDisabledWhenOff()
    {
        var enabledWorkspace = new Workspace();
        BenchmarkStagePolicyResolver.ConfigureWorkspaceSkills(enabledWorkspace, enableStackSkills: true, BenchmarkLanguages.CSharp);

        Assert.Equal(GlobalSkillsInjectionMode.ForceEnabled, enabledWorkspace.GlobalSkillsInjectionMode);
        Assert.Contains(BenchmarkSkillNames.MdFetch, enabledWorkspace.DisabledGlobalSkillsJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(BenchmarkSkillNames.CSharpNav, enabledWorkspace.DisabledGlobalSkillsJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(BenchmarkSkillNames.CodeCondenser, enabledWorkspace.DisabledGlobalSkillsJson, StringComparison.OrdinalIgnoreCase);

        var disabledWorkspace = new Workspace();
        BenchmarkStagePolicyResolver.ConfigureWorkspaceSkills(disabledWorkspace, enableStackSkills: false, BenchmarkLanguages.CSharp);

        Assert.Equal(GlobalSkillsInjectionMode.ForceDisabled, disabledWorkspace.GlobalSkillsInjectionMode);
        Assert.Null(disabledWorkspace.DisabledGlobalSkillsJson);
    }

    [Theory]
    [InlineData("csharp")]
    [InlineData("c#")]
    [InlineData("dotnet")]
    public void ConfigureWorkspaceSkills_CSharpLanguages_KeepCSharpStackSkillsActive(string language)
    {
        var workspace = new Workspace();
        BenchmarkStagePolicyResolver.ConfigureWorkspaceSkills(workspace, enableStackSkills: true, language);

        var resolved = GlobalSkillsResolver.Resolve(
            workspace,
            projectConfig: null,
            [BenchmarkSkillNames.CSharpNav, BenchmarkSkillNames.CodeCondenser, BenchmarkSkillNames.MdFetch, "general-debugging"]);

        Assert.True(resolved.IsEnabled);
        Assert.Contains(BenchmarkSkillNames.CSharpNav, resolved.ActiveSkills);
        Assert.Contains(BenchmarkSkillNames.CodeCondenser, resolved.ActiveSkills);
        Assert.Contains("general-debugging", resolved.ActiveSkills);
        Assert.DoesNotContain(BenchmarkSkillNames.MdFetch, resolved.ActiveSkills);
    }

    [Theory]
    [InlineData("python")]
    [InlineData("javascript")]
    [InlineData("typescript")]
    [InlineData("rust")]
    public void ConfigureWorkspaceSkills_NonCSharpLanguages_ExcludeCSharpStackSkillsWhilePreservingOtherSkills(string language)
    {
        var workspace = new Workspace();
        BenchmarkStagePolicyResolver.ConfigureWorkspaceSkills(workspace, enableStackSkills: true, language);

        var resolved = GlobalSkillsResolver.Resolve(
            workspace,
            projectConfig: null,
            [BenchmarkSkillNames.CSharpNav, BenchmarkSkillNames.CodeCondenser, BenchmarkSkillNames.MdFetch, "general-debugging"]);

        Assert.True(resolved.IsEnabled);
        Assert.DoesNotContain(BenchmarkSkillNames.CSharpNav, resolved.ActiveSkills);
        Assert.DoesNotContain(BenchmarkSkillNames.CodeCondenser, resolved.ActiveSkills);
        Assert.DoesNotContain(BenchmarkSkillNames.MdFetch, resolved.ActiveSkills);
        Assert.Contains("general-debugging", resolved.ActiveSkills);

        var policy = BenchmarkStagePolicyResolver.Resolve(BenchmarkProblemCategory.Implementation, enableStackSkills: true);
        Assert.Contains(CanonicalToolNames.LoadSkill, policy.PermittedTools);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ConfigureWorkspaceSkills_NullOrWhitespaceLanguage_DefaultsToCSharp(string? language)
    {
        var workspace = new Workspace();
        BenchmarkStagePolicyResolver.ConfigureWorkspaceSkills(workspace, enableStackSkills: true, language);

        var resolved = GlobalSkillsResolver.Resolve(
            workspace,
            projectConfig: null,
            [BenchmarkSkillNames.CSharpNav, BenchmarkSkillNames.CodeCondenser, BenchmarkSkillNames.MdFetch, "general-debugging"]);

        Assert.True(resolved.IsEnabled);
        Assert.Contains(BenchmarkSkillNames.CSharpNav, resolved.ActiveSkills);
        Assert.Contains(BenchmarkSkillNames.CodeCondenser, resolved.ActiveSkills);
        Assert.DoesNotContain(BenchmarkSkillNames.MdFetch, resolved.ActiveSkills);
    }

    [Fact]
    public void ConfigureWorkspaceSkills_UnknownLanguageAndDisabledNonCSharp_GateSkillsCorrectly()
    {
        var goWorkspace = new Workspace();
        BenchmarkStagePolicyResolver.ConfigureWorkspaceSkills(goWorkspace, enableStackSkills: true, "go");

        var goResolved = GlobalSkillsResolver.Resolve(
            goWorkspace,
            projectConfig: null,
            [BenchmarkSkillNames.CSharpNav, BenchmarkSkillNames.CodeCondenser, BenchmarkSkillNames.MdFetch, "general-debugging"]);

        Assert.True(goResolved.IsEnabled);
        Assert.DoesNotContain(BenchmarkSkillNames.CSharpNav, goResolved.ActiveSkills);
        Assert.DoesNotContain(BenchmarkSkillNames.CodeCondenser, goResolved.ActiveSkills);
        Assert.DoesNotContain(BenchmarkSkillNames.MdFetch, goResolved.ActiveSkills);
        Assert.Contains("general-debugging", goResolved.ActiveSkills);

        var disabledPyWorkspace = new Workspace();
        BenchmarkStagePolicyResolver.ConfigureWorkspaceSkills(disabledPyWorkspace, enableStackSkills: false, BenchmarkLanguages.Python);

        Assert.Equal(GlobalSkillsInjectionMode.ForceDisabled, disabledPyWorkspace.GlobalSkillsInjectionMode);
        Assert.Null(disabledPyWorkspace.DisabledGlobalSkillsJson);
    }
}
