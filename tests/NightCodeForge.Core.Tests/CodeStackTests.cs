using NightCodeForge.Core.Domain;
using Xunit;

namespace NightCodeForge.Core.Tests;

public class CodeStackTests
{
    [Fact]
    public void Constants_HaveExpectedValues()
    {
        Assert.Equal("dotnet", CodeStack.DotNet);
        Assert.Equal("node", CodeStack.Node);
        Assert.Equal("python", CodeStack.Python);
        Assert.Equal("rust", CodeStack.Rust);
        Assert.Equal("go", CodeStack.Go);
        Assert.Equal("universal", CodeStack.Universal);
    }

    [Fact]
    public void All_ContainsAllStandardStacks()
    {
        Assert.Contains(CodeStack.DotNet, CodeStack.All);
        Assert.Contains(CodeStack.Node, CodeStack.All);
        Assert.Contains(CodeStack.Python, CodeStack.All);
        Assert.Contains(CodeStack.Rust, CodeStack.All);
        Assert.Contains(CodeStack.Go, CodeStack.All);
        Assert.Contains(CodeStack.Universal, CodeStack.All);
        Assert.Equal(6, CodeStack.All.Count);
    }

    [Theory]
    [InlineData(null, CodeStack.Universal)]
    [InlineData("", CodeStack.Universal)]
    [InlineData("   ", CodeStack.Universal)]
    [InlineData("dotnet", CodeStack.DotNet)]
    [InlineData("DOTNET", CodeStack.DotNet)]
    [InlineData("csharp", CodeStack.DotNet)]
    [InlineData("cs", CodeStack.DotNet)]
    [InlineData("fsharp", CodeStack.DotNet)]
    [InlineData("fs", CodeStack.DotNet)]
    [InlineData(".net", CodeStack.DotNet)]
    [InlineData("node", CodeStack.Node)]
    [InlineData("npm", CodeStack.Node)]
    [InlineData("typescript", CodeStack.Node)]
    [InlineData("javascript", CodeStack.Node)]
    [InlineData("ts", CodeStack.Node)]
    [InlineData("js", CodeStack.Node)]
    [InlineData("python", CodeStack.Python)]
    [InlineData("py", CodeStack.Python)]
    [InlineData("rust", CodeStack.Rust)]
    [InlineData("cargo", CodeStack.Rust)]
    [InlineData("rs", CodeStack.Rust)]
    [InlineData("go", CodeStack.Go)]
    [InlineData("golang", CodeStack.Go)]
    [InlineData("universal", CodeStack.Universal)]
    [InlineData("UNIVERSAL", CodeStack.Universal)]
    [InlineData("ruby", "ruby")]
    [InlineData("  JAVA  ", "java")]
    public void NormalizeStack_ReturnsExpectedNormalizedValue(string? input, string expected)
    {
        var result = CodeStack.NormalizeStack(input);
        Assert.Equal(expected, result);
    }
}

