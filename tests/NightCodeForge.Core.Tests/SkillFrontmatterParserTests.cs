using NightCodeForge.Core.Skills;
using Xunit;

namespace NightCodeForge.Core.Tests;

public class SkillFrontmatterParserTests
{
    [Fact]
    public void Parse_ValidFrontmatter_ExtractsAllFieldsAndInstructions()
    {
        var markdown = @"---
name: csharp-nav
description: High-performance C# navigation
stack: dotnet
---

# Instructions
Use csnav search.";

        var result = SkillFrontmatterParser.Parse(markdown);

        Assert.Equal("csharp-nav", result.Name);
        Assert.Equal("High-performance C# navigation", result.Description);
        Assert.Equal("dotnet", result.Stack);
        Assert.Equal("# Instructions\nUse csnav search.", result.Instructions.Replace("\r\n", "\n"));
    }

    [Fact]
    public void Parse_QuotesAroundValues_CleansQuotes()
    {
        var markdown = @"---
name: ""mdfetch""
description: 'Web to markdown scraper'
stack: ""universal""
---
Fetch docs.";

        var result = SkillFrontmatterParser.Parse(markdown);

        Assert.Equal("mdfetch", result.Name);
        Assert.Equal("Web to markdown scraper", result.Description);
        Assert.Equal("universal", result.Stack);
        Assert.Equal("Fetch docs.", result.Instructions);
    }

    [Fact]
    public void Parse_BodyContainsNameAndDescription_DoesNotLeakIntoMetadata()
    {
        var markdown = @"---
name: actual-name
description: Actual description
stack: dotnet
---

# Code Snippet
In YAML, write:
name: fake-name
description: fake-description
stack: fake-stack";

        var result = SkillFrontmatterParser.Parse(markdown);

        Assert.Equal("actual-name", result.Name);
        Assert.Equal("Actual description", result.Description);
        Assert.Equal("dotnet", result.Stack);
        Assert.Contains("name: fake-name", result.Instructions);
    }

    [Fact]
    public void Parse_MissingStack_DefaultsToUniversal()
    {
        var markdown = @"---
name: generic-tool
description: A tool without explicit stack
---
Run tool.";

        var result = SkillFrontmatterParser.Parse(markdown);

        Assert.Equal("generic-tool", result.Name);
        Assert.Equal("universal", result.Stack);
    }

    [Theory]
    [InlineData("csharp", "dotnet")]
    [InlineData("cs", "dotnet")]
    [InlineData(".net", "dotnet")]
    [InlineData("DOTNET", "dotnet")]
    [InlineData("typescript", "node")]
    [InlineData("ts", "node")]
    [InlineData("javascript", "node")]
    [InlineData("js", "node")]
    [InlineData("npm", "node")]
    [InlineData("py", "python")]
    [InlineData("python", "python")]
    [InlineData("rs", "rust")]
    [InlineData("cargo", "rust")]
    [InlineData("rust", "rust")]
    [InlineData("golang", "go")]
    [InlineData("go", "go")]
    [InlineData("custom-stack", "custom-stack")]
    public void NormalizeStack_MapsKnownAliases(string inputStack, string expectedNormalized)
    {
        var normalized = SkillFrontmatterParser.NormalizeStack(inputStack);
        Assert.Equal(expectedNormalized, normalized);
    }

    [Fact]
    public void Parse_MissingName_UsesFallbackName()
    {
        var markdown = @"---
description: Tool with omitted name
stack: dotnet
---
Instructions.";

        var result = SkillFrontmatterParser.Parse(markdown, fallbackName: "directory-derived-name");

        Assert.Equal("directory-derived-name", result.Name);
    }

    [Fact]
    public void Parse_NoFrontmatterFences_ReturnsFallbackNameAndFullBody()
    {
        var rawMarkdown = "# Plain Markdown\nJust instructions without frontmatter.";

        var result = SkillFrontmatterParser.Parse(rawMarkdown, fallbackName: "plain-tool");

        Assert.Equal("plain-tool", result.Name);
        Assert.Equal(string.Empty, result.Description);
        Assert.Equal("universal", result.Stack);
        Assert.Equal(rawMarkdown, result.Instructions);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \r\n  ")]
    public void Parse_NullOrWhitespace_ReturnsSafeEmptyDefaults(string? input)
    {
        var result = SkillFrontmatterParser.Parse(input, fallbackName: "fallback");

        Assert.Equal("fallback", result.Name);
        Assert.Empty(result.Description);
        Assert.Equal("universal", result.Stack);
        Assert.Empty(result.Instructions);
    }

    [Fact]
    public void BuildMarkdown_FormatsFrontmatterAndBody_AndNormalizesStack()
    {
        var markdown = SkillFrontmatterParser.BuildMarkdown(
            name: "test-tool",
            description: "Test description",
            stack: "csharp",
            instructions: "# Instructions\nDo something.");

        Assert.StartsWith("---\nname: test-tool\ndescription: Test description\nstack: dotnet\n---\n", markdown);
        Assert.Contains("# Instructions\nDo something.", markdown);

        // Verify roundtrip
        var parsed = SkillFrontmatterParser.Parse(markdown);
        Assert.Equal("test-tool", parsed.Name);
        Assert.Equal("Test description", parsed.Description);
        Assert.Equal("dotnet", parsed.Stack);
        Assert.Equal("# Instructions\nDo something.", parsed.Instructions.Replace("\r\n", "\n"));
    }

    [Fact]
    public void Parse_HorizontalRuleInBodyWithoutFrontmatter_DoesNotTreatAsFrontmatter()
    {
        var markdown = "# Title\n\nSome text.\n\n---\nname: not-a-skill\n---\n\nTrailing text.";
        var result = SkillFrontmatterParser.Parse(markdown, fallbackName: "my-tool");

        Assert.Equal("my-tool", result.Name);
        Assert.Empty(result.Description);
        Assert.Equal("universal", result.Stack);
        Assert.Contains("name: not-a-skill", result.Instructions);
    }

    [Fact]
    public void Parse_WithUtf8Bom_ParsesFrontmatterProperly()
    {
        var markdown = "\uFEFF---\nname: bom-tool\ndescription: Has BOM\nstack: dotnet\n---\n# Instructions\nRun with BOM.";
        var result = SkillFrontmatterParser.Parse(markdown);

        Assert.Equal("bom-tool", result.Name);
        Assert.Equal("Has BOM", result.Description);
        Assert.Equal("dotnet", result.Stack);
        Assert.Equal("# Instructions\nRun with BOM.", result.Instructions);
    }

    [Fact]
    public void Parse_SingleCharacterOrUnbalancedQuotes_HandledSafely()
    {
        var markdown = "---\nname: \"\ndescription: '\nstack: universal\n---\nBody";
        var result = SkillFrontmatterParser.Parse(markdown, fallbackName: "fallback-name");

        Assert.Equal("\"", result.Name);
        Assert.Equal("'", result.Description);
        Assert.Equal("universal", result.Stack);
    }
}

