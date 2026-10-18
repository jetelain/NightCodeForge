using NightCodeForge.Core.Execution;

namespace NightCodeForge.Core.Tests;

public class ShellQuotingTests
{
    [Fact]
    public void QuotePosix_HandlesNullAndEmpty()
    {
        Assert.Equal("''", ShellQuoting.QuotePosix(null));
        Assert.Equal("''", ShellQuoting.QuotePosix(string.Empty));
    }

    [Theory]
    [InlineData("hello", "'hello'")]
    [InlineData("hello world", "'hello world'")]
    [InlineData("; rm -rf /; ", "'; rm -rf /; '")]
    [InlineData("$(cat /etc/passwd)", "'$(cat /etc/passwd)'")]
    [InlineData("`rm -rf /`", "'`rm -rf /`'")]
    [InlineData("abc\"def", "'abc\"def'")]
    [InlineData(@"a\b\c", @"'a\b\c'")]
    [InlineData("it's a test", @"'it'\''s a test'")]
    [InlineData("line1\nline2", "'line1\nline2'")]
    public void QuotePosix_ProperlyEscapesPosixStrings(string input, string expected)
    {
        var actual = ShellQuoting.QuotePosix(input);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("VALID_NAME", true)]
    [InlineData("_PREFIXED", true)]
    [InlineData("var123", true)]
    [InlineData("A", true)]
    [InlineData("_", true)]
    [InlineData("123INVALID", false)]
    [InlineData("INVALID-DASH", false)]
    [InlineData("INVALID.DOT", false)]
    [InlineData("INVALID SPACE", false)]
    [InlineData("INVALID$VAR", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    public void IsValidVariableName_ValidatesPosixVariableNames(string? name, bool expected)
    {
        Assert.Equal(expected, ShellQuoting.IsValidVariableName(name));
    }
}
