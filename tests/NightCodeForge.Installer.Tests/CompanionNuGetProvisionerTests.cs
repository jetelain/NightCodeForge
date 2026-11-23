namespace NightCodeForge.Installer.Tests;

using Moq;
using NightCodeForge.Installer.Core.Abstractions;
using NightCodeForge.Installer.Core.Constants;
using NightCodeForge.Installer.Core.Exceptions;
using NightCodeForge.Installer.Core.Models;
using NightCodeForge.Installer.Core.Services;

public sealed class CompanionNuGetProvisionerTests : IDisposable
{
    private readonly TempTestWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public void Catalog_ContainsAllFiveRequiredToolsAndMarksAssayHostOnly()
    {
        var platform = TestArchiveHelper.CreateMockPlatform(HostPlatformType.Windows, InstallerConstants.RuntimeWinX64, _workspace.RootPath);
        var runner = new Mock<IProcessRunner>();
        var sut = new CompanionNuGetProvisioner(platform.Object, runner.Object);

        Assert.Equal(5, sut.Catalog.Count);
        Assert.Contains(sut.Catalog, t => t.PackageId == "NightCodeForge.Cli" && t.ToolCommand == "ncf" && t.InstallOnHost && t.InstallInWsl);
        Assert.Contains(sut.Catalog, t => t.PackageId == "NightCodeForge.Assay" && t.ToolCommand == "nightcodeassay" && t.InstallOnHost && !t.InstallInWsl);
        Assert.Contains(sut.Catalog, t => t.PackageId == "Pmad.CSharpNav" && t.ToolCommand == "csnav" && t.InstallOnHost && t.InstallInWsl);
        Assert.Contains(sut.Catalog, t => t.PackageId == "Pmad.MarkdownFetch" && t.ToolCommand == "mdfetch" && t.InstallOnHost && t.InstallInWsl);
        Assert.Contains(sut.Catalog, t => t.PackageId == "Pmad.CodeCondenser" && t.ToolCommand == "codecondenser" && t.InstallOnHost && t.InstallInWsl);
    }

    [Fact]
    public void ParseToolListOutput_ExtractsPackageIdsAndVersions()
    {
        const string sampleOutput = """
        Package Id              Version      Commands
        --------------------------------------------------
        nightcodeforge.cli      1.0.0        ncf
        pmad.csharpnav          0.1.3        csnav
        """;

        var parsed = CompanionNuGetProvisioner.ParseToolListOutput(sampleOutput);

        Assert.Equal("1.0.0", parsed["NightCodeForge.Cli"]);
        Assert.Equal("0.1.3", parsed["Pmad.CSharpNav"]);
    }

    [Fact]
    public async Task ProvisionAllAsync_Online_InstallsMissingUpdatesExistingAndSkipsAssayInWsl()
    {
        var platform = TestArchiveHelper.CreateMockPlatform(HostPlatformType.Windows, InstallerConstants.RuntimeWinX64, _workspace.RootPath);
        var runner = new Mock<IProcessRunner>();
        var commands = new List<(string FileName, string Args)>();

        runner.Setup(r => r.RunAsync(It.IsAny<ProcessRunRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProcessRunRequest req, CancellationToken _) =>
            {
                var joined = string.Join(' ', req.Arguments);
                commands.Add((req.FileName, joined));

                if (req.FileName == "dotnet" && joined == "tool list -g")
                {
                    return new ProcessExecutionResult(0, "Package Id   Version   Commands\n---\nnightcodeforge.cli 0.9.0 ncf\n", string.Empty);
                }

                if (req.FileName == "wsl.exe" && joined == "-d Ubuntu -- dotnet tool list -g")
                {
                    return new ProcessExecutionResult(0, "Package Id   Version   Commands\n---\n", string.Empty);
                }

                return new ProcessExecutionResult(0, "ok", string.Empty);
            });

        var sut = new CompanionNuGetProvisioner(platform.Object, runner.Object);
        var outcomes = await sut.ProvisionAllAsync(new InstallSourceOptions(InstallSourceMode.Online, null), "Ubuntu");

        // 5 host tools + 4 WSL tools (Assay excluded from WSL) = 9 outcomes
        Assert.Equal(9, outcomes.Count);
        Assert.Contains(commands, c => c.FileName == "dotnet" && c.Args == "tool update -g NightCodeForge.Cli");
        Assert.Contains(commands, c => c.FileName == "dotnet" && c.Args == "tool install -g NightCodeForge.Assay");
        Assert.DoesNotContain(commands, c => c.FileName == "wsl.exe" && c.Args.Contains("NightCodeForge.Assay", StringComparison.Ordinal));
        Assert.Contains(commands, c => c.FileName == "wsl.exe" && c.Args == "-d Ubuntu -- dotnet tool install -g Pmad.CSharpNav");
        Assert.DoesNotContain(commands, c => c.Args.Contains("/etc/profile.d/dotnet.sh", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProvisionAllAsync_AirGapped_UsesIsolatedNuGetConfigSupportsPrereleaseAndTranslatesWslPath()
    {
        var platform = TestArchiveHelper.CreateMockPlatform(HostPlatformType.Windows, InstallerConstants.RuntimeWinX64, _workspace.RootPath);
        var bundleDir = _workspace.CreateSubdirectory("bundle");
        await File.WriteAllTextAsync(Path.Combine(bundleDir, "NightCodeForge.Cli.1.0.0-preview.1.nupkg"), "nupkg");
        var homeCacheDir = Path.Combine(platform.Object.UserHomeDirectory, ".nuget", "packages", "nightcodeforge.cli");
        Directory.CreateDirectory(homeCacheDir);
        File.WriteAllText(Path.Combine(homeCacheDir, "stale.txt"), "old");

        var runner = new Mock<IProcessRunner>();
        var commands = new List<(string FileName, string Args)>();

        runner.Setup(r => r.RunAsync(It.IsAny<ProcessRunRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProcessRunRequest req, CancellationToken _) =>
            {
                var joined = string.Join(' ', req.Arguments);
                commands.Add((req.FileName, joined));

                if (joined.Contains("tool list -g", StringComparison.Ordinal))
                {
                    return new ProcessExecutionResult(0, "Package Id   Version   Commands\n---\nnightcodeforge.cli 1.0.0 ncf\n", string.Empty);
                }

                if (joined.Contains("wslpath -u", StringComparison.Ordinal))
                {
                    return new ProcessExecutionResult(0, "/mnt/c/bundle\0\n", string.Empty);
                }

                // Simulate same-version update failing so fallback to uninstall + install executes.
                if (req.FileName == "dotnet" && joined.StartsWith("tool update -g NightCodeForge.Cli ", StringComparison.Ordinal))
                {
                    return new ProcessExecutionResult(1, string.Empty, "Tool 'nightcodeforge.cli' (version '1.0.0') is already installed.");
                }

                return new ProcessExecutionResult(0, "ok", string.Empty);
            });

        var sut = new CompanionNuGetProvisioner(platform.Object, runner.Object);
        var outcomes = await sut.ProvisionAllAsync(new InstallSourceOptions(InstallSourceMode.AirGappedBundle, bundleDir), "Ubuntu");

        Assert.Equal(9, outcomes.Count);
        Assert.False(Directory.Exists(homeCacheDir));
        Assert.Contains(commands, c => c.FileName == "dotnet" && c.Args == "tool uninstall -g NightCodeForge.Cli");
        Assert.Contains(commands, c =>
            c.FileName == "dotnet" &&
            c.Args.Contains("tool install -g NightCodeForge.Cli --configfile ", StringComparison.Ordinal) &&
            c.Args.Contains("--no-cache", StringComparison.Ordinal) &&
            c.Args.Contains("--prerelease", StringComparison.Ordinal));
        Assert.Contains(commands, c => c.FileName == "wsl.exe" && c.Args.Contains("rm -rf \"$HOME/.nuget/packages/nightcodeforge.cli\"", StringComparison.Ordinal));
        Assert.Contains(commands, c =>
            c.FileName == "wsl.exe" &&
            c.Args.Contains("-d Ubuntu -- dotnet tool update -g NightCodeForge.Cli --configfile ", StringComparison.Ordinal) &&
            c.Args.Contains("--no-cache --prerelease", StringComparison.Ordinal));

        var configXml = CompanionNuGetProvisioner.BuildIsolatedNuGetConfigXml(Path.GetFullPath(bundleDir));
        Assert.Contains("<clear />", configXml, StringComparison.Ordinal);
        Assert.Contains("<packageSourceMapping>", configXml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProvisionAllAsync_AirGappedWithMissingBundleDir_ThrowsBundleValidationException()
    {
        var platform = TestArchiveHelper.CreateMockPlatform(HostPlatformType.Linux, InstallerConstants.RuntimeLinuxX64, _workspace.RootPath);
        var runner = new Mock<IProcessRunner>();
        var sut = new CompanionNuGetProvisioner(platform.Object, runner.Object);

        await Assert.ThrowsAsync<BundleValidationException>(() =>
            sut.ProvisionAllAsync(new InstallSourceOptions(InstallSourceMode.AirGappedBundle, Path.Combine(_workspace.RootPath, "missing")), null));
    }

    [Fact]
    public async Task UninstallAllAsync_TreatsNotInstalledAsSkippedAndUninstallsExisting()
    {
        var platform = TestArchiveHelper.CreateMockPlatform(HostPlatformType.Linux, InstallerConstants.RuntimeLinuxX64, _workspace.RootPath);
        var runner = new Mock<IProcessRunner>();

        runner.Setup(r => r.RunAsync(It.IsAny<ProcessRunRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProcessRunRequest req, CancellationToken _) =>
            {
                var joined = string.Join(' ', req.Arguments);
                return joined.Contains("NightCodeForge.Assay", StringComparison.Ordinal)
                    ? new ProcessExecutionResult(1, string.Empty, "Tool 'nightcodeforge.assay' is not installed.")
                    : new ProcessExecutionResult(0, "uninstalled", string.Empty);
            });

        var sut = new CompanionNuGetProvisioner(platform.Object, runner.Object);
        var outcomes = await sut.UninstallAllAsync(null);

        Assert.Equal(5, outcomes.Count);
        Assert.Contains(outcomes, o => o.PackageId == "NightCodeForge.Assay" && o.Status == ProvisioningStatus.Skipped);
        Assert.Contains(outcomes, o => o.PackageId == "NightCodeForge.Cli" && o.Status == ProvisioningStatus.Completed);
    }

    [Fact]
    public void ParseToolListOutput_IgnoresLocalizedHeadersAndDotNetWelcomeBanner()
    {
        const string sampleOutput = """

            Welcome to .NET 10.0!
            ---------------------
            SDK Version: 10.0.100

            ID du package           Version      Commandes
            --------------------------------------------------
            nightcodeforge.cli      1.2.0        ncf
            pmad.csharpnav          0.1.3        csnav
            """;

        var parsed = CompanionNuGetProvisioner.ParseToolListOutput(sampleOutput);

        Assert.Equal(2, parsed.Count);
        Assert.Equal("1.2.0", parsed["NightCodeForge.Cli"]);
        Assert.Equal("0.1.3", parsed["Pmad.CSharpNav"]);
        Assert.False(parsed.ContainsKey("ID"));
        Assert.False(parsed.ContainsKey("Welcome"));
    }

    [Fact]
    public async Task ProvisionAllAsync_PassesEnglishUiLanguageAndWslUtf8EnvironmentVariables()
    {
        var platform = TestArchiveHelper.CreateMockPlatform(HostPlatformType.Windows, InstallerConstants.RuntimeWinX64, _workspace.RootPath);
        platform.Setup(p => p.GetEnvironmentVariable(InstallerConstants.WslEnvVar)).Returns("MY_VAR");

        var runner = new Mock<IProcessRunner>();
        var capturedRequests = new List<ProcessRunRequest>();

        runner.Setup(r => r.RunAsync(It.IsAny<ProcessRunRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProcessRunRequest req, CancellationToken _) =>
            {
                capturedRequests.Add(req);
                return new ProcessExecutionResult(0, "Package Id   Version   Commands\n---\n", string.Empty);
            });

        var sut = new CompanionNuGetProvisioner(platform.Object, runner.Object);
        await sut.ProvisionAllAsync(new InstallSourceOptions(InstallSourceMode.Online, null), "Ubuntu");

        var hostCalls = capturedRequests.Where(r => r.FileName == InstallerConstants.DotNetExecutable).ToList();
        Assert.NotEmpty(hostCalls);
        Assert.All(hostCalls, r =>
        {
            Assert.NotNull(r.EnvironmentVariables);
            Assert.Equal("en", r.EnvironmentVariables![InstallerConstants.DotNetCliUiLanguageEnvVar]);
        });

        var wslCalls = capturedRequests.Where(r => r.FileName == InstallerConstants.WslExecutable).ToList();
        Assert.NotEmpty(wslCalls);
        Assert.All(wslCalls, r =>
        {
            Assert.NotNull(r.EnvironmentVariables);
            Assert.Equal("1", r.EnvironmentVariables![InstallerConstants.WslUtf8EnvVar]);
            Assert.Equal("en", r.EnvironmentVariables[InstallerConstants.DotNetCliUiLanguageEnvVar]);
            Assert.Equal("MY_VAR:DOTNET_CLI_UI_LANGUAGE", r.EnvironmentVariables[InstallerConstants.WslEnvVar]);
        });
    }

    [Fact]
    public async Task UninstallAllAsync_WindowsMissingWslDistroOrDotNet_SkipsWslGracefullyWithoutThrowing()
    {
        var platform = TestArchiveHelper.CreateMockPlatform(HostPlatformType.Windows, InstallerConstants.RuntimeWinX64, _workspace.RootPath);
        var runner = new Mock<IProcessRunner>();

        runner.Setup(r => r.RunAsync(It.IsAny<ProcessRunRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProcessRunRequest req, CancellationToken _) =>
            {
                if (req.FileName == InstallerConstants.WslExecutable)
                {
                    return new ProcessExecutionResult(
                        1,
                        string.Empty,
                        "T\0h\0e\0r\0e\0 \0i\0s\0 \0n\0o\0 \0d\0i\0s\0t\0r\0i\0b\0u\0t\0i\0o\0n\0 \0w\0i\0t\0h\0 \0t\0h\0e\0 \0s\0u\0p\0p\0l\0i\0e\0d\0 \0n\0a\0m\0e\0.\0");
                }

                return new ProcessExecutionResult(0, "uninstalled", string.Empty);
            });

        var sut = new CompanionNuGetProvisioner(platform.Object, runner.Object);
        var outcomes = await sut.UninstallAllAsync("Ubuntu");

        Assert.Equal(9, outcomes.Count);
        var wslOutcomes = outcomes.Where(o => o.TargetEnvironment == "WSL2:Ubuntu").ToList();
        Assert.Equal(4, wslOutcomes.Count);
        Assert.All(wslOutcomes, o => Assert.Equal(ProvisioningStatus.Skipped, o.Status));
    }

    [Fact]
    public async Task ProbeToolsAsync_Windows_ReturnsHostAndWslStatuses()
    {
        var platform = TestArchiveHelper.CreateMockPlatform(HostPlatformType.Windows, InstallerConstants.RuntimeWinX64, _workspace.RootPath);
        var runner = new Mock<IProcessRunner>();

        runner.Setup(r => r.RunAsync(It.IsAny<ProcessRunRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProcessRunRequest req, CancellationToken _) =>
            {
                if (req.FileName == InstallerConstants.DotNetExecutable)
                {
                    return new ProcessExecutionResult(
                        0,
                        "Package Id   Version   Commands\n---\nnightcodeforge.cli 1.0.0 ncf\nnightcodeforge.assay 0.5.0 nightcodeassay\n",
                        string.Empty);
                }

                if (req.FileName == InstallerConstants.WslExecutable &&
                    string.Join(' ', req.Arguments) == "-d Ubuntu -- dotnet tool list -g")
                {
                    return new ProcessExecutionResult(
                        0,
                        "Package Id   Version   Commands\n---\nnightcodeforge.cli 1.0.0 ncf\n",
                        string.Empty);
                }

                return new ProcessExecutionResult(1, string.Empty, string.Empty);
            });

        var sut = new CompanionNuGetProvisioner(platform.Object, runner.Object);
        var statuses = await sut.ProbeToolsAsync("Ubuntu");

        Assert.Equal(5, statuses.Count);
        var ncf = Assert.Single(statuses, s => s.Spec.PackageId == CompanionToolCatalog.PackageNcf);
        Assert.True(ncf.InstalledOnHost);
        Assert.Equal("1.0.0", ncf.HostVersion);
        Assert.True(ncf.InstalledInWsl);
        Assert.Equal("1.0.0", ncf.WslVersion);

        var assay = Assert.Single(statuses, s => s.Spec.PackageId == CompanionToolCatalog.PackageNightCodeAssay);
        Assert.True(assay.InstalledOnHost);
        Assert.False(assay.InstalledInWsl);
    }

    [Fact]
    public async Task UninstallAllAsync_WhenHostDotNetMissing_SkipsHostToolsGracefullyWithoutThrowing()
    {
        var platform = TestArchiveHelper.CreateMockPlatform(HostPlatformType.Windows, InstallerConstants.RuntimeWinX64, _workspace.RootPath);
        var runner = new Mock<IProcessRunner>();
        runner.Setup(r => r.RunAsync(It.IsAny<ProcessRunRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult(1, string.Empty, "The system cannot find the file specified."));

        var sut = new CompanionNuGetProvisioner(platform.Object, runner.Object);
        var outcomes = await sut.UninstallAllAsync(wslDistroName: null);

        Assert.Equal(5, outcomes.Count);
        Assert.All(outcomes, o =>
        {
            Assert.Equal("Host", o.TargetEnvironment);
            Assert.Equal(ProvisioningStatus.Skipped, o.Status);
        });
    }

    [Fact]
    public async Task ProvisionAllAsync_EmitsPerToolChecklistProgressEventsAcrossHostAndWsl()
    {
        var platform = TestArchiveHelper.CreateMockPlatform(HostPlatformType.Windows, InstallerConstants.RuntimeWinX64, _workspace.RootPath);
        var runner = new Mock<IProcessRunner>();
        runner.Setup(r => r.RunAsync(It.IsAny<ProcessRunRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult(0, "Package Id   Version   Commands\n---\n", string.Empty));

        var itemEvents = new List<InstallerProgressEvent>();
        var sut = new CompanionNuGetProvisioner(platform.Object, runner.Object);
        await sut.ProvisionAllAsync(
            new InstallSourceOptions(InstallSourceMode.Online, null),
            "Ubuntu",
            new SynchronousProgress<InstallerProgressEvent>(ev =>
            {
                if (!string.IsNullOrWhiteSpace(ev.ItemKey) && ev.ItemStatus.HasValue)
                {
                    itemEvents.Add(ev);
                }
            }));

        foreach (var spec in CompanionToolCatalog.All)
        {
            var expectedKey = DeploymentComponentKeys.ForCompanionTool(spec.PackageId);
            Assert.Contains(itemEvents, e => e.ItemKey == expectedKey && e.ItemStatus == ProvisioningStatus.InProgress);
            Assert.Contains(itemEvents, e => e.ItemKey == expectedKey && e.ItemStatus == ProvisioningStatus.Completed);
        }
    }

    [Fact]
    public async Task ProvisionAllAsync_LinuxHostOnly_ReportsIncremental20To100PercentCompletionProgress()
    {
        var platform = TestArchiveHelper.CreateMockPlatform(HostPlatformType.Linux, InstallerConstants.RuntimeLinuxX64, _workspace.RootPath);
        var runner = new Mock<IProcessRunner>();
        runner.Setup(r => r.RunAsync(It.IsAny<ProcessRunRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult(0, "Package Id   Version   Commands\n---\n", string.Empty));

        var completionPercents = new List<double>();
        var sut = new CompanionNuGetProvisioner(platform.Object, runner.Object);
        await sut.ProvisionAllAsync(
            new InstallSourceOptions(InstallSourceMode.Online, null),
            wslDistroName: null,
            new SynchronousProgress<InstallerProgressEvent>(ev =>
            {
                if (!string.IsNullOrWhiteSpace(ev.ItemKey) &&
                    ev.ItemStatus == ProvisioningStatus.Completed &&
                    ev.ProgressPercent.HasValue)
                {
                    completionPercents.Add(ev.ProgressPercent.Value);
                }
            }));

        Assert.Equal([20.0, 40.0, 60.0, 80.0, 100.0], completionPercents);
    }

    private sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
