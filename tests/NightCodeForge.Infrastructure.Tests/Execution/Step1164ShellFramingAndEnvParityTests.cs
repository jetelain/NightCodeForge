using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Infrastructure.Execution;

namespace NightCodeForge.Infrastructure.Tests.Execution;

public sealed class Step1164ShellFramingAndEnvParityTests
{
    [Fact]
    public void BuildFramedCommand_WrapsCommandInSingleQuotedIsolatedSubshellWithDevNullStdin()
    {
        const string workDir = "/workspace/repo's dir";
        const string backtickCmd = "csnav decompile --type \"System.Collections.ObjectModel.ReadOnlyDictionary`2\" --format text 2>&1 | head -15";

        var framed = PersistentContainerShellSession.BuildFramedCommand(workDir, backtickCmd);

        Assert.StartsWith("( cd '/workspace/repo'\\''s dir' && bash --noprofile --norc -c -- '", framed, StringComparison.Ordinal);
        Assert.EndsWith("' ) < /dev/null 2>&1", framed, StringComparison.Ordinal);
        Assert.Contains("ReadOnlyDictionary`2", framed, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("echo 'unclosed single")]
    [InlineData("echo \"unclosed double")]
    [InlineData("(((")]
    [InlineData("cat <<EOF\nhello")]
    [InlineData("-v")]
    [InlineData("echo __NCF_SENTINEL_1234567890abcdef1234567890abcdef__ 0")]
    public void BuildFramedCommand_SafelyQuotesMalformedAndSpecialCommandsWithoutOuterInterpretation(string rawCommand)
    {
        var framed = PersistentContainerShellSession.BuildFramedCommand("/workspace", rawCommand);

        Assert.Contains("bash --noprofile --norc -c -- '", framed, StringComparison.Ordinal);
        Assert.EndsWith(") < /dev/null 2>&1", framed, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildFramedCommand_VeryLargeCommand_UsesUniqueSingleQuotedHeredocOnFd3()
    {
        var largeCommand = "echo " + new string('x', 70_000);

        var framed = PersistentContainerShellSession.BuildFramedCommand("/workspace", largeCommand);

        Assert.Contains("bash --noprofile --norc /dev/fd/3 ) < /dev/null 2>&1 3<<'__NCF_CMD_", framed, StringComparison.Ordinal);
        Assert.Contains(largeCommand, framed, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildFramedCommand_QuoteHeavyCommandUnderRawLimit_SwitchesToHeredocWhenQuotedExceedsLimit()
    {
        // 20,000 single quotes is 20 KB raw (< 65,536 bytes) but expands to ~80 KB when POSIX-quoted (> 65,536 bytes).
        var quoteHeavyCommand = "echo " + new string('\'', 20_000);

        var framed = PersistentContainerShellSession.BuildFramedCommand("/workspace", quoteHeavyCommand);

        Assert.Contains("bash --noprofile --norc /dev/fd/3 ) < /dev/null 2>&1 3<<'__NCF_CMD_", framed, StringComparison.Ordinal);
        Assert.DoesNotContain("bash --noprofile --norc -c --", framed, StringComparison.Ordinal);
        Assert.Contains(quoteHeavyCommand, framed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteEnvironmentPreambleAsync_ExportsDotNetRollForwardAndWritablePackageCacheFallbacks()
    {
        var options = new ContainerExecutorOptions
        {
            ContainerNameOrId = "ncf-step1164-preamble",
            MountPackageCaches = true,
            MountHostSdks = false
        };

        var defaultPreamble = await CapturePreambleAsync(options, null);
        Assert.Contains("export DOTNET_ROLL_FORWARD=LatestMajor", defaultPreamble, StringComparison.Ordinal);
        Assert.Contains(
            "if [ -d \"/cache/pnpm\" ] && [ -w \"/cache/pnpm\" ]; then export npm_config_store_dir=\"/cache/pnpm\"; else mkdir -p \"/tmp/ncf-pnpm-store\" 2>/dev/null || true; export npm_config_store_dir=\"/tmp/ncf-pnpm-store\"; fi",
            defaultPreamble,
            StringComparison.Ordinal);
        Assert.Contains(
            "if [ -d \"/cache/npm\" ] && [ -w \"/cache/npm\" ]; then export npm_config_cache=\"/cache/npm\"; else mkdir -p \"/tmp/ncf-npm-cache\" 2>/dev/null || true; export npm_config_cache=\"/tmp/ncf-npm-cache\"; fi",
            defaultPreamble,
            StringComparison.Ordinal);

        var overridePreamble = await CapturePreambleAsync(
            options,
            new Dictionary<string, string>
            {
                ["DOTNET_ROLL_FORWARD"] = "Major",
                ["npm_config_store_dir"] = "/custom/pnpm-store",
                ["npm_config_cache"] = "/custom/npm-cache"
            });

        Assert.DoesNotContain("export DOTNET_ROLL_FORWARD=LatestMajor", overridePreamble, StringComparison.Ordinal);
        Assert.Contains("export DOTNET_ROLL_FORWARD='Major'", overridePreamble, StringComparison.Ordinal);
        Assert.DoesNotContain("/tmp/ncf-pnpm-store", overridePreamble, StringComparison.Ordinal);
        Assert.DoesNotContain("/tmp/ncf-npm-cache", overridePreamble, StringComparison.Ordinal);
        Assert.Contains("export npm_config_store_dir='/custom/pnpm-store'", overridePreamble, StringComparison.Ordinal);
        Assert.Contains("export npm_config_cache='/custom/npm-cache'", overridePreamble, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task WriteEnvironmentPreambleAsync_ForcesFallbackNpmCacheAndOfflineVarsWhenOffline(
        bool disableNetwork,
        bool isRestrictedReadOnly)
    {
        var options = new ContainerExecutorOptions
        {
            ContainerNameOrId = "ncf-step1164-offline-preamble",
            MountPackageCaches = true,
            MountHostSdks = false,
            DisableNetwork = disableNetwork,
            IsRestrictedReadOnly = isRestrictedReadOnly
        };

        var defaultPreamble = await CapturePreambleAsync(options, null);
        Assert.DoesNotContain("if [ -d \"/cache/npm\" ]", defaultPreamble, StringComparison.Ordinal);
        Assert.Contains(
            $"mkdir -p \"{ContainerExecutorOptions.FallbackNpmCachePath}\" 2>/dev/null || true; export {ContainerExecutorOptions.NpmCacheEnvVarName}=\"{ContainerExecutorOptions.FallbackNpmCachePath}\"",
            defaultPreamble,
            StringComparison.Ordinal);
        Assert.Contains(
            $"export {ContainerExecutorOptions.NpmConfigOfflineEnvVarName}={ContainerExecutorOptions.OfflineTrueValue}",
            defaultPreamble,
            StringComparison.Ordinal);
        Assert.Contains(
            $"export {ContainerExecutorOptions.NpmConfigOfflineUpperEnvVarName}={ContainerExecutorOptions.OfflineTrueValue}",
            defaultPreamble,
            StringComparison.Ordinal);

        var overridePreamble = await CapturePreambleAsync(
            options,
            new Dictionary<string, string>
            {
                [ContainerExecutorOptions.NpmCacheEnvVarName] = "/custom/offline-npm",
                [ContainerExecutorOptions.NpmConfigOfflineEnvVarName] = "false",
                [ContainerExecutorOptions.NpmConfigOfflineUpperEnvVarName] = "false"
            });

        Assert.DoesNotContain(ContainerExecutorOptions.FallbackNpmCachePath, overridePreamble, StringComparison.Ordinal);
        Assert.DoesNotContain($"export {ContainerExecutorOptions.NpmConfigOfflineEnvVarName}={ContainerExecutorOptions.OfflineTrueValue}", overridePreamble, StringComparison.Ordinal);
        Assert.DoesNotContain($"export {ContainerExecutorOptions.NpmConfigOfflineUpperEnvVarName}={ContainerExecutorOptions.OfflineTrueValue}", overridePreamble, StringComparison.Ordinal);
        Assert.Contains($"export {ContainerExecutorOptions.NpmCacheEnvVarName}='/custom/offline-npm'", overridePreamble, StringComparison.Ordinal);
        Assert.Contains($"export {ContainerExecutorOptions.NpmConfigOfflineEnvVarName}='false'", overridePreamble, StringComparison.Ordinal);
        Assert.Contains($"export {ContainerExecutorOptions.NpmConfigOfflineUpperEnvVarName}='false'", overridePreamble, StringComparison.Ordinal);
    }

    private static async Task<string> CapturePreambleAsync(
        ContainerExecutorOptions options,
        IReadOnlyDictionary<string, string>? envVars)
    {
        using var stdinBuffer = new MemoryStream();
        using var stdinWriter = new StreamWriter(stdinBuffer, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var stdoutStream = new MemoryStream();
        using var stdoutReader = new StreamReader(stdoutStream);

        var streamSession = new Mock<IProcessStreamSession>();
        streamSession.SetupGet(s => s.StandardInput).Returns(stdinWriter);
        streamSession.SetupGet(s => s.StandardOutput).Returns(stdoutReader);
        streamSession.SetupGet(s => s.HasExited).Returns(false);

        var runnerMock = new Mock<IProcessRunner>();
        runnerMock.Setup(r => r.StartSession(It.IsAny<ProcessStartSpec>())).Returns(streamSession.Object);

        using var session = new PersistentContainerShellSession(
            options,
            runnerMock.Object,
            new Mock<IOperationsTelemetryService>().Object,
            NullLogger.Instance);
        session.Start();
        await session.WriteEnvironmentPreambleAsync(envVars);
        await stdinWriter.FlushAsync();

        stdinBuffer.Position = 0;
        using var reader = new StreamReader(stdinBuffer, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }
}
