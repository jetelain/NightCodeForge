using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Core.Tests;

public class GlobalSkillsResolverTests
{
    private readonly Workspace _defaultWorkspace = new()
    {
        Id = 1,
        Name = "TestRepo",
        GlobalSkillsInjectionMode = GlobalSkillsInjectionMode.Inherit
    };

    private readonly string[] _candidates = ["csharp-nav", "mdfetch", "codecondenser"];

    [Fact]
    public void Resolve_Inherit_NoRepoConfig_EnablesSystemDefaultWithAllCandidates()
    {
        var result = GlobalSkillsResolver.Resolve(_defaultWorkspace, null, _candidates);

        Assert.True(result.IsEnabled);
        Assert.Equal(SkillsResolutionSource.SystemDefault, result.Source);
        Assert.Equal(3, result.ActiveSkills.Count);
        Assert.Contains("csharp-nav", result.ActiveSkills);
        Assert.Contains("mdfetch", result.ActiveSkills);
        Assert.Contains("codecondenser", result.ActiveSkills);
        Assert.Empty(result.ExcludedSkills);
    }

    [Fact]
    public void Resolve_ForceDisabled_OverridesRepoConfigAndDisablesAllSkills()
    {
        var ws = new Workspace
        {
            Id = 2,
            GlobalSkillsInjectionMode = GlobalSkillsInjectionMode.ForceDisabled
        };

        var config = new WorkspaceProjectConfig
        {
            Skills = new WorkspaceSkillsConfig { Enabled = true }
        };

        var result = GlobalSkillsResolver.Resolve(ws, config, _candidates);

        Assert.False(result.IsEnabled);
        Assert.Equal(SkillsResolutionSource.LocalDatabaseOverride, result.Source);
        Assert.Empty(result.ActiveSkills);
    }

    [Fact]
    public void Resolve_ForceEnabled_OverridesRepoConfigDisabled_ActivatesSkills()
    {
        var ws = new Workspace
        {
            Id = 3,
            GlobalSkillsInjectionMode = GlobalSkillsInjectionMode.ForceEnabled
        };

        var config = new WorkspaceProjectConfig
        {
            Skills = new WorkspaceSkillsConfig { Enabled = false }
        };

        var result = GlobalSkillsResolver.Resolve(ws, config, _candidates);

        Assert.True(result.IsEnabled);
        Assert.Equal(SkillsResolutionSource.LocalDatabaseOverride, result.Source);
        Assert.Equal(3, result.ActiveSkills.Count);
    }

    [Fact]
    public void Resolve_Inherit_RepoConfigDisabled_DisablesSkills()
    {
        var ws = new Workspace
        {
            Id = 4,
            GlobalSkillsInjectionMode = GlobalSkillsInjectionMode.Inherit
        };

        var config = new WorkspaceProjectConfig
        {
            Skills = new WorkspaceSkillsConfig { Enabled = false }
        };

        var result = GlobalSkillsResolver.Resolve(ws, config, _candidates);

        Assert.False(result.IsEnabled);
        Assert.Equal(SkillsResolutionSource.RepositoryConfig, result.Source);
        Assert.Empty(result.ActiveSkills);
    }

    [Fact]
    public void Resolve_MergesExclusionsFromRepoAndWorkspaceDb()
    {
        var ws = new Workspace
        {
            Id = 5,
            GlobalSkillsInjectionMode = GlobalSkillsInjectionMode.Inherit,
            DisabledGlobalSkillsJson = "[\"csharp-nav\"]"
        };

        var config = new WorkspaceProjectConfig
        {
            Skills = new WorkspaceSkillsConfig
            {
                Enabled = true,
                DisabledSkills = ["codecondenser"]
            }
        };

        var result = GlobalSkillsResolver.Resolve(ws, config, _candidates);

        Assert.True(result.IsEnabled);
        Assert.Equal(SkillsResolutionSource.RepositoryConfig, result.Source);
        Assert.Single(result.ActiveSkills);
        Assert.Contains("mdfetch", result.ActiveSkills);
        Assert.DoesNotContain("csharp-nav", result.ActiveSkills);
        Assert.DoesNotContain("codecondenser", result.ActiveSkills);
        Assert.Equal(2, result.ExcludedSkills.Count);
    }

    [Fact]
    public void Resolve_GracefullyHandlesMalformedDbJson()
    {
        var ws = new Workspace
        {
            Id = 6,
            GlobalSkillsInjectionMode = GlobalSkillsInjectionMode.Inherit,
            DisabledGlobalSkillsJson = "{invalid-json}"
        };

        var result = GlobalSkillsResolver.Resolve(ws, null, _candidates);

        Assert.True(result.IsEnabled);
        Assert.Equal(3, result.ActiveSkills.Count);
    }
}

