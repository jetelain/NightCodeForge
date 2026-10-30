using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Web.Services;

namespace NightCodeForge.Web.Tests;

public class WorkspaceSkillsUiHelperTests
{
    [Fact]
    public void ParseDisabledSkills_HandlesValidJson_Null_AndCorruptedJson()
    {
        var valid = WorkspaceSkillsUiHelper.ParseDisabledSkills("[\"csharp-nav\", \"mdfetch\"]");
        Assert.Equal(2, valid.Count);
        Assert.Contains("csharp-nav", valid);

        var nullResult = WorkspaceSkillsUiHelper.ParseDisabledSkills(null);
        Assert.Empty(nullResult);

        var malformedResult = WorkspaceSkillsUiHelper.ParseDisabledSkills("{not-array}");
        Assert.Empty(malformedResult);
    }

    [Fact]
    public void ToggleSkillExclusion_AddsAndRemovesExclusions()
    {
        var initial = "[\"csharp-nav\"]";

        // Exclude mdfetch
        var added = WorkspaceSkillsUiHelper.ToggleSkillExclusion(initial, "mdfetch", isExcluded: true);
        var parsed = WorkspaceSkillsUiHelper.ParseDisabledSkills(added);
        Assert.Equal(2, parsed.Count);
        Assert.Contains("csharp-nav", parsed);
        Assert.Contains("mdfetch", parsed);

        // Include csharp-nav (remove exclusion)
        var removed = WorkspaceSkillsUiHelper.ToggleSkillExclusion(added, "csharp-nav", isExcluded: false);
        var parsedRemoved = WorkspaceSkillsUiHelper.ParseDisabledSkills(removed);
        Assert.Single(parsedRemoved);
        Assert.Contains("mdfetch", parsedRemoved);
    }

    [Fact]
    public void GetStatus_ReturnsCorrectBadge_AndSkillActiveState()
    {
        var ws = new Workspace
        {
            Id = 1,
            Name = "TestRepo",
            GlobalSkillsInjectionMode = GlobalSkillsInjectionMode.Inherit,
            DisabledGlobalSkillsJson = "[\"codecondenser\"]"
        };

        var descriptors = new List<SkillDescriptor>
        {
            new("csharp-nav", "Navigation", "", ""),
            new("codecondenser", "Condenser", "", ""),
            new("mdfetch", "Docs fetcher", "", "")
        };

        var status = WorkspaceSkillsUiHelper.GetStatus(ws, null, descriptors);

        Assert.True(status.IsEnabled);
        Assert.Equal("badge-teal", status.BadgeClass);
        Assert.Contains("Active via System Default", status.BadgeText);

        var csharpNav = status.CandidateSkills.First(s => s.Name == "csharp-nav");
        Assert.True(csharpNav.IsActive);
        Assert.False(csharpNav.IsLocallyDisabled);

        var codecondenser = status.CandidateSkills.First(s => s.Name == "codecondenser");
        Assert.False(codecondenser.IsActive);
        Assert.True(codecondenser.IsLocallyDisabled);
    }

    [Fact]
    public void GetStatus_ForceDisabled_ReturnsDisabledBadge()
    {
        var ws = new Workspace
        {
            Id = 2,
            Name = "DisabledRepo",
            GlobalSkillsInjectionMode = GlobalSkillsInjectionMode.ForceDisabled
        };

        var descriptors = new List<SkillDescriptor>
        {
            new("csharp-nav", "Navigation", "", "")
        };

        var status = WorkspaceSkillsUiHelper.GetStatus(ws, null, descriptors);

        Assert.False(status.IsEnabled);
        Assert.Equal("badge-amber", status.BadgeClass);
        Assert.Contains("Disabled via Local Workspace Override", status.BadgeText);
    }
}

