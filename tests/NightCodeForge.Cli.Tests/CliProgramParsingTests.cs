namespace NightCodeForge.Cli.Tests;

using NightCodeForge.Cli.Commands;
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
    [InlineData("skills", "--help")]
    [InlineData("skills", "--HELP")]
    [InlineData("skills", "-h")]
    [InlineData("skills", "help")]
    [InlineData("skill", "--help")]
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

    [Fact]
    public void ParseSkillsOptions_WithNoArgs_DefaultsToWorkspaceInstall()
    {
        var options = Program.ParseSkillsOptions(Array.Empty<string>(), out var error);

        Assert.NotNull(options);
        Assert.Null(error);
        Assert.Equal(SkillsAction.Install, options.Action);
        Assert.False(options.Global);
        Assert.False(options.DryRun);
        Assert.Null(options.CustomDirectory);
    }

    [Theory]
    [InlineData("--global")]
    [InlineData("-g")]
    [InlineData("install", "--global")]
    [InlineData("install", "-g", "--dry-run")]
    public void ParseSkillsOptions_WithGlobalFlag_SetsGlobal(params string[] args)
    {
        var options = Program.ParseSkillsOptions(args, out var error);

        Assert.NotNull(options);
        Assert.Null(error);
        Assert.Equal(SkillsAction.Install, options.Action);
        Assert.True(options.Global);
        Assert.Equal(args.Contains("--dry-run"), options.DryRun);
    }

    [Theory]
    [InlineData("install --dir custom/skills", "custom/skills")]
    [InlineData("install -d custom/skills", "custom/skills")]
    [InlineData("--dir=custom/skills", "custom/skills")]
    [InlineData("-d=custom/skills", "custom/skills")]
    public void ParseSkillsOptions_WithCustomDirectory_SetsCustomDirectory(string commandLine, string expectedDir)
    {
        var args = commandLine.Split(' ');
        var options = Program.ParseSkillsOptions(args, out var error);

        Assert.NotNull(options);
        Assert.Null(error);
        Assert.Equal(SkillsAction.Install, options.Action);
        Assert.Equal(expectedDir, options.CustomDirectory);
    }

    [Fact]
    public void ParseSkillsOptions_WithPositionalPath_SetsTargetPath()
    {
        var options = Program.ParseSkillsOptions(new[] { "install", "my/repo", "--dry-run" }, out var error);

        Assert.NotNull(options);
        Assert.Null(error);
        Assert.Equal(SkillsAction.Install, options.Action);
        Assert.Equal("my/repo", options.TargetPath);
        Assert.True(options.DryRun);
    }

    [Theory]
    [InlineData("list")]
    [InlineData("ls")]
    public void ParseSkillsOptions_ListSubcommand_SetsListAction(params string[] args)
    {
        var options = Program.ParseSkillsOptions(args, out var error);

        Assert.NotNull(options);
        Assert.Null(error);
        Assert.Equal(SkillsAction.List, options.Action);
    }

    [Theory]
    [InlineData("show", null)]
    [InlineData("show ncf-backlog", "ncf-backlog")]
    [InlineData("cat ncf-project-readiness", "ncf-project-readiness")]
    [InlineData("view ncf-backlog-migrator", "ncf-backlog-migrator")]
    public void ParseSkillsOptions_ShowSubcommand_SetsShowActionAndOptionalSkillName(string commandLine, string? expectedName)
    {
        var args = commandLine.Split(' ');
        var options = Program.ParseSkillsOptions(args, out var error);

        Assert.NotNull(options);
        Assert.Null(error);
        Assert.Equal(SkillsAction.Show, options.Action);
        Assert.Equal(expectedName, options.SkillName);
    }

    [Theory]
    [InlineData("install", "--global", "--dir", "some/dir")]
    [InlineData("install", "some/repo", "--global")]
    [InlineData("install", "some/repo", "--dir", "some/dir")]
    [InlineData("install", "--dir")]
    [InlineData("install", "--dir=")]
    [InlineData("install", "--dir", "--dry-run")]
    [InlineData("install", "-d", "--global")]
    [InlineData("install", "--dir", "dir1", "--dir", "dir2")]
    [InlineData("install", "--dir=dir1", "-d=dir2")]
    [InlineData("install", "--unknown")]
    [InlineData("install", "")]
    [InlineData("install", "repo1", "repo2")]
    [InlineData("list", "extra")]
    [InlineData("show", "")]
    [InlineData("show", "   ")]
    [InlineData("show", "skill1", "skill2")]
    [InlineData("show", "--unknown")]
    public void ParseSkillsOptions_InvalidArguments_ReturnsError(params string[] args)
    {
        var options = Program.ParseSkillsOptions(args, out var error);

        Assert.Null(options);
        Assert.NotNull(error);
    }

    [Fact]
    public void ParseSkillsOptions_WithCustomDirectoryNamedHelp_SetsCustomDirectory()
    {
        var options = Program.ParseSkillsOptions(new[] { "install", "--dir", "help" }, out var error);

        Assert.NotNull(options);
        Assert.Null(error);
        Assert.Equal(SkillsAction.Install, options.Action);
        Assert.Equal("help", options.CustomDirectory);
    }
}
