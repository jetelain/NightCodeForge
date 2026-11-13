using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Agents.Tests.Fixtures;
using NightCodeForge.Agents.Tools;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Workflow;
using Xunit;

namespace NightCodeForge.Agents.Tests;

public sealed class SandboxBashToolModuleTests : IDisposable
{
    private readonly string _workspaceRoot;
    private readonly ISandboxPathResolver _pathResolver;

    public SandboxBashToolModuleTests()
    {
        _workspaceRoot = Path.Combine(Path.GetTempPath(), $"ncf-bashmodule-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workspaceRoot);
        var scope = new CanonicalToolScope(_workspaceRoot);
        _pathResolver = new WorkspacePathResolverSandboxAdapter(scope, TestPathResolver.Create(), NullLogger.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspaceRoot))
        {
            Directory.Delete(_workspaceRoot, recursive: true);
        }
    }

    [Fact]
    public void Constructor_WithNullArguments_ThrowsArgumentNullException()
    {
        var scope = new CanonicalToolScope(_workspaceRoot);
        var execEnv = new Mock<IExecutionEnvironment>().Object;

        Assert.Throws<ArgumentNullException>(() => new SandboxBashToolModule(null!, _pathResolver, execEnv, NullLogger.Instance));
        Assert.Throws<ArgumentNullException>(() => new SandboxBashToolModule(scope, null!, execEnv, NullLogger.Instance));
        Assert.Throws<ArgumentNullException>(() => new SandboxBashToolModule(scope, _pathResolver, null!, NullLogger.Instance));
        Assert.Throws<ArgumentNullException>(() => new SandboxBashToolModule(scope, _pathResolver, execEnv, null!));
    }

    [Fact]
    public async Task CreateFunctions_WhenSchemaOnly_ThrowsMissingModuleInstanceMessage()
    {
        var functions = SandboxBashToolModule.CreateFunctions(null);
        var bashFn = Assert.Single(functions);
        Assert.Equal(CanonicalToolNames.Bash, bashFn.Name);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            bashFn.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
            {
                ["command"] = "echo hi"
            })).AsTask());

        Assert.Equal(SandboxBashToolModule.MissingModuleInstanceMessage, ex.Message);
    }

    [Fact]
    public async Task CreateFunctions_BoundToModule_InvokesBashAsync()
    {
        var execEnv = new Mock<IExecutionEnvironment>(MockBehavior.Strict);
        execEnv.SetupGet(e => e.Tier).Returns(ExecutionTier.BareHost);
        execEnv
            .Setup(e => e.ExecuteAsync("echo ok", _workspaceRoot, It.IsAny<int?>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CommandResult(0, "ok\n", string.Empty, TimeSpan.FromMilliseconds(5)));

        var module = new SandboxBashToolModule(new CanonicalToolScope(_workspaceRoot), _pathResolver, execEnv.Object, NullLogger.Instance);
        var bashFn = Assert.Single(module.CreateFunctions());

        var result = await bashFn.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["command"] = "echo ok"
        }));

        Assert.Equal("Exit Code: 0\nok", result?.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task BashAsync_WithEmptyCommand_ReturnsEmptyBashCommandMarker(string command)
    {
        var execEnv = new Mock<IExecutionEnvironment>(MockBehavior.Strict);
        var module = new SandboxBashToolModule(new CanonicalToolScope(_workspaceRoot), _pathResolver, execEnv.Object, NullLogger.Instance);

        var result = await module.BashAsync(command);

        Assert.Equal("[Empty bash command]", result);
    }

    [Fact]
    public async Task BashAsync_WhenReadOnlyOnScopeOrSandbox_BlocksDestructiveCommands()
    {
        var execEnv = new Mock<IExecutionEnvironment>(MockBehavior.Strict);

        var scopeReadOnlyModule = new SandboxBashToolModule(
            new CanonicalToolScope(_workspaceRoot, IsReadOnly: true),
            _pathResolver,
            execEnv.Object,
            NullLogger.Instance);

        var violation1 = await scopeReadOnlyModule.BashAsync("git commit -m 'bad'");
        Assert.StartsWith("[READ-ONLY VIOLATION]:", violation1);

        var roResolver = new WorkspacePathResolverSandboxAdapter(
            new CanonicalToolScope(_workspaceRoot, IsReadOnly: true),
            TestPathResolver.Create(),
            NullLogger.Instance);
        var sandboxReadOnlyModule = new SandboxBashToolModule(
            new CanonicalToolScope(_workspaceRoot, IsReadOnly: false),
            roResolver,
            execEnv.Object,
            NullLogger.Instance);

        var violation2 = await sandboxReadOnlyModule.BashAsync("rm -rf src");
        Assert.StartsWith("[READ-ONLY VIOLATION]:", violation2);
    }

    [Fact]
    public async Task BashAsync_WhenWorkingDirectoryEscapesSandbox_ReturnsError()
    {
        var execEnv = new Mock<IExecutionEnvironment>(MockBehavior.Strict);
        execEnv.SetupGet(e => e.Tier).Returns(ExecutionTier.BareHost);

        var module = new SandboxBashToolModule(new CanonicalToolScope(_workspaceRoot), _pathResolver, execEnv.Object, NullLogger.Instance);

        var result = await module.BashAsync("ls", workingDirectory: "../../outside");

        Assert.StartsWith("Error:", result);
    }

    [Fact]
    public async Task BashAsync_CalculatesCombinedSuiteTimeout_AndRespectsLargerExplicitTimeout()
    {
        var capturedTimeouts = new List<int?>();
        var execEnv = new Mock<IExecutionEnvironment>(MockBehavior.Strict);
        execEnv.SetupGet(e => e.Tier).Returns(ExecutionTier.BareHost);
        execEnv
            .Setup(e => e.ExecuteAsync(It.IsAny<string>(), _workspaceRoot, It.IsAny<int?>(), null, It.IsAny<CancellationToken>()))
            .Callback<string, string?, int?, IReadOnlyDictionary<string, string>?, CancellationToken>((_, _, t, _, _) => capturedTimeouts.Add(t))
            .ReturnsAsync(new CommandResult(0, "done", string.Empty, TimeSpan.FromMilliseconds(5)));

        var scope = new CanonicalToolScope(_workspaceRoot, BuildTimeoutSeconds: 400, TestTimeoutSeconds: 500);
        var module = new SandboxBashToolModule(scope, _pathResolver, execEnv.Object, NullLogger.Instance);

        await module.BashAsync("echo 1", timeout: 60);
        await module.BashAsync("echo 2", timeout: 1200);

        Assert.Equal([900, 1200], capturedTimeouts);
    }

    [Fact]
    public async Task BashAsync_HandlesTimeoutException_TimeoutExitCode_AndTruncatedTimeoutBuffer()
    {
        var execEnv = new Mock<IExecutionEnvironment>(MockBehavior.Strict);
        execEnv.SetupGet(e => e.Tier).Returns(ExecutionTier.BareHost);

        execEnv
            .Setup(e => e.ExecuteAsync("cmd-throw", _workspaceRoot, It.IsAny<int?>(), null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("Timed out"));

        execEnv
            .Setup(e => e.ExecuteAsync("cmd-exitcode", _workspaceRoot, It.IsAny<int?>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CommandResult(WorkflowDefaults.ProcessTimeoutExitCode, string.Empty, "process killed", TimeSpan.FromSeconds(1)));

        execEnv
            .Setup(e => e.ExecuteAsync("cmd-buffer", _workspaceRoot, It.IsAny<int?>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CommandResult(1, "Operation timed out while waiting", string.Empty, TimeSpan.FromSeconds(1), Truncated: true));

        var module = new SandboxBashToolModule(new CanonicalToolScope(_workspaceRoot), _pathResolver, execEnv.Object, NullLogger.Instance);

        var res1 = await module.BashAsync("cmd-throw");
        var res2 = await module.BashAsync("cmd-exitcode");
        var res3 = await module.BashAsync("cmd-buffer");

        Assert.Contains($"Exit Code: {WorkflowDefaults.ProcessTimeoutExitCode}\n[TIMEOUT]:", res1);
        Assert.Contains($"Exit Code: {WorkflowDefaults.ProcessTimeoutExitCode}\n[TIMEOUT]:", res2);
        Assert.Contains($"Exit Code: {WorkflowDefaults.ProcessTimeoutExitCode}\n[TIMEOUT]:", res3);
    }

    [Fact]
    public async Task BashAsync_PropagatesCancellation_AndFormatsGeneralExecutionException()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var execEnv = new Mock<IExecutionEnvironment>(MockBehavior.Strict);
        execEnv.SetupGet(e => e.Tier).Returns(ExecutionTier.BareHost);
        execEnv
            .Setup(e => e.ExecuteAsync("cancel-me", _workspaceRoot, It.IsAny<int?>(), null, cts.Token))
            .ThrowsAsync(new OperationCanceledException(cts.Token));
        execEnv
            .Setup(e => e.ExecuteAsync("boom", _workspaceRoot, It.IsAny<int?>(), null, CancellationToken.None))
            .ThrowsAsync(new InvalidOperationException("Failed to start process"));

        var module = new SandboxBashToolModule(new CanonicalToolScope(_workspaceRoot), _pathResolver, execEnv.Object, NullLogger.Instance);

        await Assert.ThrowsAsync<OperationCanceledException>(() => module.BashAsync("cancel-me", cancellationToken: cts.Token));

        var errResult = await module.BashAsync("boom");
        Assert.Equal("Error: Command execution failed: Failed to start process", errResult);
    }

    [Fact]
    public async Task BashAsync_CombinesStdoutAndStderr_ExtractsDiagnosticsOnBuildOrTest_AndTruncatesHugeOutput()
    {
        var compilerErrorOutput = "src/Foo.cs(12,5): error CS1002: ; expected [c:/repo/Demo.csproj]";
        var hugeOutput = new string('A', WorkflowDefaults.MaxStageOutputChars + 5000);

        var execEnv = new Mock<IExecutionEnvironment>(MockBehavior.Strict);
        execEnv.SetupGet(e => e.Tier).Returns(ExecutionTier.BareHost);
        execEnv
            .Setup(e => e.ExecuteAsync("dotnet build", _workspaceRoot, It.IsAny<int?>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CommandResult(1, compilerErrorOutput, "Build failed warning", TimeSpan.FromMilliseconds(10)));
        execEnv
            .Setup(e => e.ExecuteAsync("echo huge", _workspaceRoot, It.IsAny<int?>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CommandResult(0, hugeOutput, string.Empty, TimeSpan.FromMilliseconds(10)));

        var module = new SandboxBashToolModule(new CanonicalToolScope(_workspaceRoot), _pathResolver, execEnv.Object, NullLogger.Instance);

        var buildResult = await module.BashAsync("dotnet build");
        Assert.Contains("Exit Code: 1", buildResult);
        Assert.Contains("[STDERR]\nBuild failed warning", buildResult);
        Assert.Contains("[Structured Diagnostics Extracted]:", buildResult);
        Assert.Contains("CS1002", buildResult);

        var hugeResult = await module.BashAsync("echo huge");
        Assert.True(hugeResult.Length <= WorkflowDefaults.MaxStageOutputChars + 200);
    }
}
