using NightCodeForge.Agents.Skills;
using Xunit;

namespace NightCodeForge.Agents.Tests;

public class BundledSkillsProviderTests
{
    private readonly BundledSkillsProvider _provider = new();

    [Fact]
    public void GetBundledSkills_ContainsAllExpectedCompanionTools()
    {
        var skills = _provider.GetBundledSkills();

        Assert.NotEmpty(skills);
        Assert.Equal(3, skills.Count);

        var csNav = skills.FirstOrDefault(s => s.Name == "csharp-nav");
        Assert.NotNull(csNav);
        Assert.Equal("dotnet", csNav.Stack);
        Assert.True(csNav.IsBuiltIn);
        Assert.False(string.IsNullOrWhiteSpace(csNav.Description));

        var mdFetch = skills.FirstOrDefault(s => s.Name == "mdfetch");
        Assert.NotNull(mdFetch);
        Assert.Equal("universal", mdFetch.Stack);
        Assert.True(mdFetch.IsBuiltIn);
        Assert.False(string.IsNullOrWhiteSpace(mdFetch.Description));

        var codeCondenser = skills.FirstOrDefault(s => s.Name == "codecondenser");
        Assert.NotNull(codeCondenser);
        Assert.Equal("dotnet", codeCondenser.Stack);
        Assert.True(codeCondenser.IsBuiltIn);
        Assert.False(string.IsNullOrWhiteSpace(codeCondenser.Description));
    }

    [Fact]
    public void GetBundledEntries_ReturnsEntriesWithRelativePathsAndContent()
    {
        var entries = _provider.GetBundledEntries();

        Assert.NotEmpty(entries);
        Assert.Equal(3, entries.Count);

        foreach (var entry in entries)
        {
            Assert.NotNull(entry.Descriptor);
            Assert.EndsWith("SKILL.md", entry.RelativePath);
            Assert.False(string.IsNullOrWhiteSpace(entry.Content));
        }
    }

    [Theory]
    [InlineData("csharp-nav", "csnav")]
    [InlineData("mdfetch", "mdfetch")]
    [InlineData("codecondenser", "codecondenser")]
    public void GetSkillContent_KnownSkill_ReturnsInstructionMarkdown(string skillName, string expectedKeyword)
    {
        var content = _provider.GetSkillContent(skillName);

        Assert.NotNull(content);
        Assert.Contains(expectedKeyword, content);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("non-existent-skill")]
    [InlineData("unknown_tool")]
    public void GetSkillContent_UnknownOrEmpty_ReturnsNull(string? skillName)
    {
        var content = _provider.GetSkillContent(skillName!);
        Assert.Null(content);
    }

    [Fact]
    public void TryGetSkillContent_CaseInsensitiveMatch_ReturnsContent()
    {
        var success = _provider.TryGetSkillContent("CSHARP-NAV", out var content);

        Assert.True(success);
        Assert.NotNull(content);
        Assert.Contains("csnav", content);
    }
}

