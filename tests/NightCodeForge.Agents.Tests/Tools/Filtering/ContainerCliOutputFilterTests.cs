using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Agents.Tools;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Execution;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Core.Workflow.Filtering;
using NightCodeForge.Infrastructure.Execution;
using Xunit;

namespace NightCodeForge.Agents.Tests;

[Trait("Category", "Acceptance")]
public sealed class ContainerCliOutputFilterTests : IAsyncLifetime
{
    private readonly string _rootTempDir;
    private readonly string _workspaceHostDir;
    private readonly string _tmpHostDir;
    private readonly string _containerName;
    private readonly bool _useWsl;
    private readonly SystemProcessRunner _processRunner = new(NullLogger<SystemProcessRunner>.Instance);

    private NightCodeForgeContainerExecutor? _executor;
    private SandboxBashToolModule? _bashModule;
    private SandboxPathResolver? _resolver;

    public ContainerCliOutputFilterTests()
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        _rootTempDir = Path.Combine(Path.GetTempPath(), $"ncf-cli-filter-acc-{suffix}");
        _workspaceHostDir = Path.Combine(_rootTempDir, "workspace");
        _tmpHostDir = Path.Combine(_rootTempDir, "tmp");
        _containerName = $"ncf-cli-filter-test-{suffix}";
        _useWsl = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        Directory.CreateDirectory(_workspaceHostDir);
        Directory.CreateDirectory(_tmpHostDir);

        File.WriteAllText(Path.Combine(_workspaceHostDir, "NuGet.Config"), """
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="local-cache" value="/cache/nuget" />
              </packageSources>
            </configuration>
            """);
    }

    public async Task InitializeAsync()
    {
        var identityResolver = new HostUserIdentityResolver(_processRunner, NullLogger<HostUserIdentityResolver>.Instance);
        var uidGid = identityResolver.ResolveUserIdentity(_useWsl, wslDistribution: null, wslExecutable: "wsl.exe");

        var workspace = new Workspace
        {
            Id = 9001,
            Name = "CliFilterAcceptance",
            RepositoryPath = _workspaceHostDir,
            ExecutionMode = ExecutionMode.ContainerSandbox,
            ContainerImage = ContainerExecutorOptions.DefaultAgentBaseImage
        };

        var mountTable = SandboxMountTable.ForWorkspace(
            hostWorkingDirectory: _workspaceHostDir,
            isReadOnly: false,
            hostTmpDirectory: _tmpHostDir);

        var scopeDescriptor = SandboxScopeDescriptor.ForWorkspace(
            workspace,
            SandboxSecurityTier.Standard,
            taskId: null,
            mountTable: mountTable,
            defaultWorkingDirectory: "/workspace",
            hostWorkspacePath: _workspaceHostDir);

        _resolver = new SandboxPathResolver(scopeDescriptor);

        var execOptions = new ContainerExecutorOptions
        {
            ContainerNameOrId = _containerName,
            ContainerImage = ContainerExecutorOptions.DefaultAgentBaseImage,
            DefaultWorkingDirectory = "/workspace",
            DefaultTimeoutSeconds = 180,
            UseWsl = _useWsl,
            AutoStartContainer = true,
            ProtectCloneWorkingTree = false,
            MountHostSdks = true,
            SdkMountPaths = ["/usr/lib/dotnet", "/usr/share/dotnet"],
            ExtraPathDirectories = ["/usr/lib/dotnet"],
            MountPackageCaches = true,
            ExpandTildeInMounts = true,
            HostWorkspacePath = _workspaceHostDir,
            Scope = scopeDescriptor,
            MountTable = mountTable,
            UserUidGid = uidGid,
            AdditionalVolumeMounts = [new ContainerVolumeMount(_tmpHostDir, "/tmp", isReadOnly: false)]
        };

        _executor = new NightCodeForgeContainerExecutor(
            execOptions,
            _processRunner,
            NullLogger<NightCodeForgeContainerExecutor>.Instance,
            identityResolver,
            Mock.Of<IOperationsTelemetryService>());

        var toolScope = new CanonicalToolScope(
            _workspaceHostDir,
            WorkspaceId: workspace.Id,
            IsReadOnly: false,
            SandboxScope: scopeDescriptor,
            SandboxPathResolver: _resolver,
            OptimizationFlags: new LocalOptimizationFlags { SmartCliOutputFiltering = true });

        _bashModule = new SandboxBashToolModule(
            toolScope,
            _resolver,
            _executor,
            CliOutputFilterRegistry.Default,
            NullLogger.Instance);

        // Warm container start & verify dotnet SDK availability inside the container
        var warm = await _executor.ExecuteAsync("dotnet --version", "/workspace", timeoutSeconds: 60);
        Assert.True(warm.ExitCode == 0, $"Expected dotnet SDK inside container, got exit {warm.ExitCode}: {warm.StandardOutput} {warm.StandardError}");
    }

    public async Task DisposeAsync()
    {
        _executor?.Dispose();

        try
        {
            var spec = ContainerMountAndDriftBuilder.BuildDockerProcessSpec(
                new ContainerExecutorOptions
                {
                    ContainerNameOrId = _containerName,
                    UseWsl = _useWsl
                },
                ["rm", "-f", _containerName],
                timeoutSeconds: 15);
            await _processRunner.RunAsync(spec, CancellationToken.None);
        }
        catch (Exception)
        {
            // Justification: Best-effort container cleanup in acceptance fixture teardown.
        }

        if (Directory.Exists(_rootTempDir))
        {
            try
            {
                Directory.Delete(_rootTempDir, recursive: true);
            }
            catch (IOException)
            {
                // Justification: Best-effort temp cleanup after WSL bind mount teardown.
            }
        }
    }

    [Fact]
    public async Task Case1_CleanMultiProjectBuild_CompressesOver90Percent_AndOffloadsFullLogToTmp()
    {
        var caseDir = Path.Combine(_workspaceHostDir, "case1");
        Directory.CreateDirectory(caseDir);

        var slnBuilder = new StringBuilder();
        var projectDirs = new List<string>();
        for (int i = 1; i <= 2; i++)
        {
            var projName = $"SampleLib{i:D2}";
            var projDir = Path.Combine(caseDir, projName);
            Directory.CreateDirectory(projDir);
            projectDirs.Add($"{projName}/{projName}.csproj");

            File.WriteAllText(Path.Combine(projDir, $"{projName}.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                </Project>
                """);

            File.WriteAllText(Path.Combine(projDir, "Class1.cs"), $$"""
                namespace {{projName}};
                public sealed class Widget{{i}} { public int Value => {{i}}; }
                """);
        }

        // Master project referencing both libraries; detailed MSBuild output exceeds thousands of chars cleanly offline
        var appDir = Path.Combine(caseDir, "AppRoot");
        Directory.CreateDirectory(appDir);
        var refs = string.Join("\n", Enumerable.Range(1, 2).Select(i => $"    <ProjectReference Include=\"../SampleLib{i:D2}/SampleLib{i:D2}.csproj\" />"));
        File.WriteAllText(Path.Combine(appDir, "AppRoot.csproj"), $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
              <ItemGroup>
            {refs}
              </ItemGroup>
            </Project>
            """);

        // Pre-restore once offline using mounted /cache/nuget so the timed/measured build tests offline build noise
        var restore = await _executor!.ExecuteAsync(
            "dotnet restore --source /cache/nuget --disable-parallel -m:1 /nr:false < /dev/null",
            "/workspace/case1/AppRoot",
            timeoutSeconds: 60);
        Assert.True(restore.ExitCode == 0, $"Offline restore failed: {restore.StandardOutput}\n{restore.StandardError}");

        var output = await _bashModule!.BashAsync("dotnet build --no-restore -m:1 /nr:false -v:detailed", workingDirectory: "/workspace/case1/AppRoot");

        Assert.True(_bashModule.TryTakeAuditEntry(output, out var audit) && audit != null);
        Assert.Contains("Exit Code: 0", output);
        Assert.Contains(CliOutputFilterConstants.SimplifiedOutputHeader, output);
        Assert.Contains(CliOutputFilterConstants.BuildSucceededSummary, output);
        Assert.NotNull(audit.VirtualTmpPath);
        Assert.Contains($"[Full output : {audit.VirtualTmpPath} ({audit.RawChars} chars)", output);

        double ratio = 1.0 - (double)output.Length / audit.RawChars;
        Assert.True(ratio > 0.90, $"Expected >90% reduction vs raw verbose build, got {ratio:P1} ({audit.RawChars} -> {output.Length}).");

        // Verify the raw log is actually accessible from inside the container at /tmp/bash-*.txt
        var catRaw = await _executor.ExecuteAsync($"cat {audit.VirtualTmpPath}", "/workspace", timeoutSeconds: 15);
        Assert.Equal(0, catRaw.ExitCode);
        Assert.Equal(audit.RawOutput.TrimEnd(), catRaw.StandardOutput.TrimEnd());
    }

    [Fact]
    public async Task Case2_FatalErrorsWithWarnings_PreservesRelativizedErrorsAndSuppressesWarnings()
    {
        var caseDir = Path.Combine(_workspaceHostDir, "case2");
        Directory.CreateDirectory(caseDir);

        File.WriteAllText(Path.Combine(caseDir, "BrokenProj.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);

        // Generate 8 warnings (CS0168) + 2 semantic fatal compile errors (CS0103 + CS0029) so Roslyn runs full semantic analysis and emits warnings too
        var warningMethods = string.Join("\n", Enumerable.Range(1, 8).Select(i => $"    public void Warn{i}() {{ int unused{i}; }}"));
        File.WriteAllText(Path.Combine(caseDir, "Program.cs"), $$"""
            public class BrokenProgram
            {
            {{warningMethods}}
                public void FailHere()
                {
                    var x = UndefinedIdentifier + 1;
                    int typeMismatch = "not_an_int";
                }
            }
            """);

        var restore = await _executor!.ExecuteAsync("dotnet restore --source /cache/nuget", "/workspace/case2", timeoutSeconds: 120);
        Assert.True(restore.ExitCode == 0, $"Offline restore failed: {restore.StandardOutput}\n{restore.StandardError}");

        var output = await _bashModule!.BashAsync("dotnet build --no-restore", workingDirectory: "/workspace/case2");

        Assert.True(_bashModule.TryTakeAuditEntry(output, out var audit) && audit != null);
        Assert.Contains("Exit Code: 1", output);
        Assert.Contains(CliOutputFilterConstants.SimplifiedOutputHeader, output);
        Assert.Contains("error CS0103", output);
        Assert.Contains("error CS0029", output);
        Assert.Contains("case2/Program.cs(", output);
        Assert.DoesNotContain("/workspace/", output);
        Assert.Contains("compiler warnings to focus on", output);
        Assert.DoesNotContain("warning CS0168", output);
        Assert.Contains(CliOutputFilterConstants.BuildFailedSummary, output);

        // Full log offloaded to /tmp MUST still have the suppressed warnings for developer/grep inspection
        Assert.Contains("warning CS0168", audit.RawOutput);
    }

    [Fact]
    public async Task Case3_WarningsOnlyExceedingCap_ShowsTop5AndOmissionNotice()
    {
        var caseDir = Path.Combine(_workspaceHostDir, "case3");
        Directory.CreateDirectory(caseDir);

        File.WriteAllText(Path.Combine(caseDir, "WarnOnlyProj.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);

        var warningMethods = string.Join("\n", Enumerable.Range(1, 12).Select(i => $"    public void W{i}() {{ int unused_{i:D2}; }}"));
        File.WriteAllText(Path.Combine(caseDir, "Warnings.cs"), $$"""
            public class WarnSample
            {
            {{warningMethods}}
            }
            """);

        var restore = await _executor!.ExecuteAsync("dotnet restore --source /cache/nuget", "/workspace/case3", timeoutSeconds: 120);
        Assert.True(restore.ExitCode == 0, $"Offline restore failed: {restore.StandardOutput}\n{restore.StandardError}");

        var output = await _bashModule!.BashAsync("dotnet build --no-restore", workingDirectory: "/workspace/case3");

        Assert.True(_bashModule.TryTakeAuditEntry(output, out var audit) && audit != null);
        Assert.Contains("Exit Code: 0", output);
        Assert.Contains(CliOutputFilterConstants.SimplifiedOutputHeader, output);
        Assert.Contains("warning CS0168", output);
        Assert.Contains("[... and 7 additional warnings omitted. Run with disable_filter=true for complete list.]", output);
        Assert.Contains(CliOutputFilterConstants.BuildSucceededSummary, output);

        // First 5 warnings kept, 12th omitted from inline response but present in /tmp file
        Assert.Contains("unused_01", output);
        Assert.Contains("unused_05", output);
        Assert.DoesNotContain("unused_12", output);
        Assert.Contains("unused_12", File.ReadAllText(_resolver!.ResolveHostPath(audit.VirtualTmpPath!, forWrite: false)));
    }

    [Fact]
    public async Task Case4_FailedTestSuite_StripsPassingTestsAndPreservesFailureStackAndAssertion()
    {
        var caseDir = Path.Combine(_workspaceHostDir, "case4");
        Directory.CreateDirectory(caseDir);

        File.WriteAllText(Path.Combine(caseDir, "ContainerTestSuite.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
                <IsPackable>false</IsPackable>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.0.1" />
                <PackageReference Include="xunit" Version="2.9.3" />
                <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
              </ItemGroup>
            </Project>
            """);

        var passingFacts = string.Join("\n", Enumerable.Range(1, 30).Select(i => $"    [Fact] public void FastPass_{i:D2}() => Assert.Equal({i}, {i});"));
        File.WriteAllText(Path.Combine(caseDir, "SuiteTests.cs"), $$"""
            using Xunit;
            namespace ContainerTestSuite;
            public class SuiteTests
            {
            {{passingFacts}}
                [Fact]
                public void BrokenCalculation_ShouldFail() => Assert.Equal(42, 40 + 1);
            }
            """);

        var restore = await _executor!.ExecuteAsync("dotnet restore --source /cache/nuget", "/workspace/case4", timeoutSeconds: 120);
        Assert.True(restore.ExitCode == 0, $"Offline test restore failed: {restore.StandardOutput}\n{restore.StandardError}");

        var output = await _bashModule!.BashAsync(
            "dotnet test --no-restore --verbosity normal",
            workingDirectory: "/workspace/case4");

        Assert.True(_bashModule.TryTakeAuditEntry(output, out var audit) && audit != null);
        Assert.Contains("Exit Code: 1", output);
        Assert.Contains(CliOutputFilterConstants.SimplifiedOutputHeader, output);
        Assert.Contains("BrokenCalculation_ShouldFail", output);
        Assert.Contains("Assert.Equal() Failure", output);
        Assert.Contains("case4/SuiteTests.cs:line", output);
        Assert.DoesNotContain("/workspace/", output);
        Assert.DoesNotContain("FastPass_01", output);
        Assert.DoesNotContain("FastPass_30", output);
        Assert.Contains("Test Run Failed", output);
        Assert.Contains("FastPass_01", audit.RawOutput);
    }

    [Fact]
    public async Task Case5_DisableFilterBypassAndGeneric50KbHeadTruncationWorkInContainer()
    {
        var caseDir = Path.Combine(_workspaceHostDir, "case5");
        Directory.CreateDirectory(caseDir);

        File.WriteAllText(Path.Combine(caseDir, "BypassProj.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);

        var warningMethods = string.Join("\n", Enumerable.Range(1, 8).Select(i => $"    public void WarnM{i}() {{ int bypassVar_{i:D2}; }}"));
        File.WriteAllText(Path.Combine(caseDir, "WarnFile.cs"), $$"""
            public class BypassSample
            {
            {{warningMethods}}
            }
            """);

        var restore = await _executor!.ExecuteAsync("dotnet restore --source /cache/nuget", "/workspace/case5", timeoutSeconds: 120);
        Assert.True(restore.ExitCode == 0, $"Offline restore failed: {restore.StandardOutput}\n{restore.StandardError}");

        // 5A: disable_filter: true via AIFunction tool returns complete raw warnings
        var bashFn = Assert.Single(_bashModule!.CreateFunctions());
        var bypassOutput = (await bashFn.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["command"] = "dotnet build --no-restore",
            ["working_directory"] = "/workspace/case5",
            ["disable_filter"] = true
        })))?.ToString() ?? string.Empty;

        Assert.Contains("Exit Code: 0", bypassOutput);
        Assert.DoesNotContain(CliOutputFilterConstants.SimplifiedOutputHeader, bypassOutput);
        Assert.Contains("bypassVar_08", bypassOutput);

        // 5B: Generic huge output (> 50 KB) head-truncates cleanly with ... additional X characters & /tmp pointer
        var genericOutput = await _bashModule.BashAsync(
            "bash -c 'echo HEADER_START_LINE; head -c 65000 /dev/zero | tr \"\\0\" \"Z\"; echo \"\"; echo TAIL_END_LINE'");

        Assert.True(_bashModule.TryTakeAuditEntry(genericOutput, out var genericAudit) && genericAudit != null);
        Assert.True(genericOutput.Length <= WorkflowDefaults.MaxToolInlineChars);
        Assert.Contains("Exit Code: 0", genericOutput);
        Assert.Contains("HEADER_START_LINE", genericOutput);
        Assert.DoesNotContain("TAIL_END_LINE", genericOutput);
        Assert.Contains("... additional ", genericOutput);
        Assert.Contains($"[Full output : {genericAudit.VirtualTmpPath}", genericOutput);

        var rawSaved = File.ReadAllText(_resolver!.ResolveHostPath(genericAudit.VirtualTmpPath!, forWrite: false));
        Assert.Contains("TAIL_END_LINE", rawSaved);
        Assert.True(rawSaved.Length > 65000);
    }
}
