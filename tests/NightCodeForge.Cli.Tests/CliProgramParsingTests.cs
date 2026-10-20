namespace NightCodeForge.Cli.Tests;

using NightCodeForge.Cli.Common;

public class CliProgramParsingTests
{
    [Fact]
    public void ParseInitOptions_WithDefaults_SetsCurrentDirectory()
    {
        var options = Program.ParseInitOptions(Array.Empty<string>(), out var error);
        Assert.NotNull(options);
        Assert.Null(error);
        Assert.False(options.DryRun);
        Assert.False(options.SkipFormat);
        Assert.Null(options.Stack);
    }

    [Fact]
    public void ParseInitOptions_WithFlagsAndPath_SetsAllProperties()
    {
        var args = new[] { "my/repo/path", "--dry-run", "--skip-format", "--stack", "rust" };
        var options = Program.ParseInitOptions(args, out var error);

        Assert.NotNull(options);
        Assert.Null(error);
        Assert.Equal("my/repo/path", options.TargetPath);
        Assert.True(options.DryRun);
        Assert.True(options.SkipFormat);
        Assert.Equal("rust", options.Stack);
    }

    [Fact]
    public void ParseInitOptions_WithStackEqualSyntax_SetsStack()
    {
        var args = new[] { "--stack=python" };
        var options = Program.ParseInitOptions(args, out var error);

        Assert.NotNull(options);
        Assert.Null(error);
        Assert.Equal("python", options.Stack);
    }

    [Fact]
    public void ParseInitOptions_WithUnrecognizedOption_ReturnsError()
    {
        var args = new[] { "--invalid-flag" };
        var options = Program.ParseInitOptions(args, out var error);

        Assert.Null(options);
        Assert.NotNull(error);
        Assert.Contains("Unrecognized option", error);
    }

    [Fact]
    public void ParseInitOptions_WithMissingStackValue_ReturnsError()
    {
        var args = new[] { "--stack" };
        var options = Program.ParseInitOptions(args, out var error);

        Assert.Null(options);
        Assert.NotNull(error);
        Assert.Contains("Missing value", error);
    }

    [Fact]
    public void ParseAuditOptions_WithPositionalPath_SetsTargetPath()
    {
        var args = new[] { "some/project" };
        var options = Program.ParseAuditOptions(args, out var error);

        Assert.NotNull(options);
        Assert.Null(error);
        Assert.Equal("some/project", options.TargetPath);
    }

    [Fact]
    public void ParseAuditOptions_WithUnexpectedOption_ReturnsError()
    {
        var args = new[] { "--unknown" };
        var options = Program.ParseAuditOptions(args, out var error);

        Assert.Null(options);
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("init", "--help")]
    [InlineData("init", "-h")]
    [InlineData("audit", "--help")]
    [InlineData("audit", "-h")]
    public async Task Program_Subcommands_ReturnZeroForHelp(string cmd, string helpFlag)
    {
        var exitCode = await Program.Main(new[] { cmd, helpFlag });
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void SplitCommandLine_HandlesQuotedExecutablePath()
    {
        var commandLine = "\"C:\\Program Files\\dotnet\\dotnet.exe\" build -c Release";
        var (fileName, arguments) = CliProcessHelper.SplitCommandLine(commandLine);

        Assert.Equal("C:\\Program Files\\dotnet\\dotnet.exe", fileName);
        Assert.Equal("build -c Release", arguments);
    }

    [Fact]
    public void SplitCommandLine_HandlesUnquotedExecutable()
    {
        var commandLine = "dotnet test --no-build";
        var (fileName, arguments) = CliProcessHelper.SplitCommandLine(commandLine);

        Assert.Equal("dotnet", fileName);
        Assert.Equal("test --no-build", arguments);
    }

    [Fact]
    public void SplitCommandLine_HandlesSingleWordCommand()
    {
        var commandLine = "pytest";
        var (fileName, arguments) = CliProcessHelper.SplitCommandLine(commandLine);

        Assert.Equal("pytest", fileName);
        Assert.Equal(string.Empty, arguments);
    }

    [Fact]
    public void SplitCommandLine_HandlesEmptyString()
    {
        var (fileName, arguments) = CliProcessHelper.SplitCommandLine("   ");

        Assert.Equal(string.Empty, fileName);
        Assert.Equal(string.Empty, arguments);
    }

    [Fact]
    public void SplitCommandLine_HandlesSingleQuotedExecutablePath()
    {
        var commandLine = "\'C:\\Program Files\\tool.exe\' --flag value";
        var (fileName, arguments) = CliProcessHelper.SplitCommandLine(commandLine);

        Assert.Equal("C:\\Program Files\\tool.exe", fileName);
        Assert.Equal("--flag value", arguments);
    }

    [Fact]
    public void ParseInitOptions_WithMultiplePositionalArguments_ReturnsError()
    {
        var args = new[] { "path1", "path2" };
        var options = Program.ParseInitOptions(args, out var error);

        Assert.Null(options);
        Assert.NotNull(error);
        Assert.Contains("Unexpected extra argument 'path2'", error);
    }

    [Fact]
    public void ParseAuditOptions_WithMultiplePositionalArguments_ReturnsError()
    {
        var args = new[] { "path1", "path2" };
        var options = Program.ParseAuditOptions(args, out var error);

        Assert.Null(options);
        Assert.NotNull(error);
        Assert.Contains("Unexpected extra argument 'path2'", error);
    }
}
