using NightCodeForge.Agents.Benchmarking;
using NightCodeForge.Core.Benchmarking;
using NightCodeForge.Core.Domain;

namespace NightCodeForge.Agents.Tests.Benchmarking;

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
    public void ConfigureWorkspaceSkills_ExcludesMdFetchWhenEnabledAndForcesDisabledWhenOff()
    {
        var enabledWorkspace = new Workspace();
        BenchmarkStagePolicyResolver.ConfigureWorkspaceSkills(enabledWorkspace, enableStackSkills: true);

        Assert.Equal(GlobalSkillsInjectionMode.ForceEnabled, enabledWorkspace.GlobalSkillsInjectionMode);
        Assert.Contains("mdfetch", enabledWorkspace.DisabledGlobalSkillsJson, StringComparison.OrdinalIgnoreCase);

        var disabledWorkspace = new Workspace();
        BenchmarkStagePolicyResolver.ConfigureWorkspaceSkills(disabledWorkspace, enableStackSkills: false);

        Assert.Equal(GlobalSkillsInjectionMode.ForceDisabled, disabledWorkspace.GlobalSkillsInjectionMode);
        Assert.Null(disabledWorkspace.DisabledGlobalSkillsJson);
    }
}
