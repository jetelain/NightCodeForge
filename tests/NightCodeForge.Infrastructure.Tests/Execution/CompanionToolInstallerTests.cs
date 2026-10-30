using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Infrastructure.Execution;

namespace NightCodeForge.Infrastructure.Tests.Execution;

public class CompanionToolInstallerTests
{
    private readonly Mock<IProcessRunner> _processRunnerMock = new();

    [Fact]
    public async Task IsToolInstalledAsync_HostMode_ReturnsTrue_WhenOutputContainsTool()
    {
        _processRunnerMock
            .Setup(p => p.RunAsync(It.Is<ProcessStartSpec>(s => s.FileName == "dotnet" && s.Arguments == "tool list -g"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult(0, "Package Id      Version      Commands\npmad.csharpnav  1.2.0        csnav\n", ""));

        var installer = new CompanionToolInstaller(_processRunnerMock.Object, NullLogger<CompanionToolInstaller>.Instance);
        var installed = await installer.IsToolInstalledAsync("csnav", useWsl: false);

        Assert.True(installed);
    }

    [Fact]
    public async Task IsToolInstalledAsync_WslMode_RunsWslCommandWithDistro()
    {
        _processRunnerMock
            .Setup(p => p.RunAsync(It.Is<ProcessStartSpec>(s =>
                s.FileName == "wsl.exe" &&
                s.Arguments == "-d Ubuntu-22.04 dotnet tool list -g"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult(0, "Package Id      Version      Commands\nmarkdownfetch   0.9.0        mdfetch\n", ""));

        var installer = new CompanionToolInstaller(_processRunnerMock.Object, NullLogger<CompanionToolInstaller>.Instance);
        var installed = await installer.IsToolInstalledAsync("mdfetch", useWsl: true, wslDistribution: "Ubuntu-22.04");

        Assert.True(installed);
    }

    [Fact]
    public async Task EnsureToolsInstalledAsync_InstallsMissingTools_ForDotNetStack()
    {
        // First tool list -g says no tools installed
        _processRunnerMock
            .Setup(p => p.RunAsync(It.Is<ProcessStartSpec>(s => s.FileName == "dotnet" && s.Arguments == "tool list -g"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult(0, "Package Id      Version      Commands\n", ""));

        // Captures install commands
        var installCommands = new List<string>();
        _processRunnerMock
            .Setup(p => p.RunAsync(It.Is<ProcessStartSpec>(s => s.FileName == "dotnet" && s.Arguments!.StartsWith("tool install -g")), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProcessStartSpec spec, CancellationToken _) =>
            {
                installCommands.Add(spec.Arguments!);
                return new ProcessExecutionResult(0, "Installed successfully", "");
            });

        var installer = new CompanionToolInstaller(_processRunnerMock.Object, NullLogger<CompanionToolInstaller>.Instance);
        await installer.EnsureToolsInstalledAsync("dotnet", useWsl: false);

        Assert.Contains("tool install -g Pmad.CSharpNav", installCommands);
        Assert.Contains("tool install -g Pmad.MarkdownFetch", installCommands);
        Assert.Contains("tool install -g Pmad.CodeCondenser", installCommands);
        Assert.Equal(3, installCommands.Count);
    }

    [Fact]
    public async Task EnsureToolsInstalledAsync_WslMode_FormatsWslInstallCommands()
    {
        _processRunnerMock
            .Setup(p => p.RunAsync(It.Is<ProcessStartSpec>(s => s.FileName == "wsl.exe" && s.Arguments!.Contains("tool list -g")), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult(0, "Package Id      Version      Commands\n", ""));

        var installCommands = new List<string>();
        _processRunnerMock
            .Setup(p => p.RunAsync(It.Is<ProcessStartSpec>(s => s.FileName == "wsl.exe" && s.Arguments!.Contains("tool install -g")), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProcessStartSpec spec, CancellationToken _) =>
            {
                installCommands.Add(spec.Arguments!);
                return new ProcessExecutionResult(0, "Installed", "");
            });

        var installer = new CompanionToolInstaller(_processRunnerMock.Object, NullLogger<CompanionToolInstaller>.Instance);
        await installer.EnsureToolsInstalledAsync("node", useWsl: true, wslDistribution: "Ubuntu");

        // node stack only gets universal tools (Pmad.MarkdownFetch)
        Assert.Single(installCommands);
        Assert.Contains("-d Ubuntu dotnet tool install -g Pmad.MarkdownFetch", installCommands[0]);
    }

    [Fact]
    public async Task EnsureToolsInstalledAsync_CachesEnsuredEnvironments_UntilCacheCleared()
    {
        _processRunnerMock
            .Setup(p => p.RunAsync(It.Is<ProcessStartSpec>(s => s.FileName == "dotnet" && s.Arguments == "tool list -g"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult(0, "Package Id      Version      Commands\npmad.csharpnav  1.2.0        csnav\nmarkdownfetch   0.9.0        mdfetch\npmad.codecondenser 1.0.0 codecondenser\n", ""));

        var installer = new CompanionToolInstaller(_processRunnerMock.Object, NullLogger<CompanionToolInstaller>.Instance);

        // First call probes tools
        await installer.EnsureToolsInstalledAsync("dotnet", useWsl: false);

        // Second call should return immediately without executing any commands
        _processRunnerMock.Invocations.Clear();
        await installer.EnsureToolsInstalledAsync("dotnet", useWsl: false);
        _processRunnerMock.Verify(p => p.RunAsync(It.IsAny<ProcessStartSpec>(), It.IsAny<CancellationToken>()), Times.Never);

        // After ClearCache(), should execute again
        installer.ClearCache();
        await installer.EnsureToolsInstalledAsync("dotnet", useWsl: false);
        _processRunnerMock.Verify(p => p.RunAsync(It.IsAny<ProcessStartSpec>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }
}

