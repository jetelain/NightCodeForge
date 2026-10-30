using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Core.Execution;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Infrastructure.Execution;
using Xunit;

namespace NightCodeForge.Infrastructure.Tests.Execution;

public class Step1121ContainerIcuAndUserIdentityTests
{
    [Fact]
    public void DefaultAgentBaseDockerfile_IncludesIcuLocalesAndNeutralCache()
    {
        var dockerfile = ContainerExecutorOptions.DefaultAgentBaseDockerfile;

        Assert.Contains("libicu-dev", dockerfile);
        Assert.Contains("libunwind8", dockerfile);
        Assert.Contains("locales", dockerfile);
        Assert.Contains("locale-gen en_US.UTF-8 fr_FR.UTF-8", dockerfile);
        Assert.Contains("ENV LANG=en_US.UTF-8", dockerfile);
        Assert.Contains("ENV LC_ALL=en_US.UTF-8", dockerfile);
        Assert.Contains("/cache/nuget", dockerfile);
        Assert.Contains("chmod -R 777 /cache", dockerfile);
        Assert.Contains("ENV NUGET_PACKAGES=/cache/nuget", dockerfile);
    }

    [Fact]
    public void BuildDockerRunArgs_WithUserUidGid_IncludesUserFlag()
    {
        var options = new ContainerExecutorOptions
        {
            ContainerNameOrId = "ncf-test",
            UserUidGid = "1002:1002",
            IsRestrictedReadOnly = false
        };

        var args = NightCodeForgeContainerExecutor.BuildDockerRunArgs(options);

        Assert.Contains("--user", args);
        var idx = args.IndexOf("--user");
        Assert.Equal("1002:1002", args[idx + 1]);
    }

    [Fact]
    public void BuildDockerRunArgs_RestrictedReadOnly_OverridesTo10001000Once()
    {
        var options = new ContainerExecutorOptions
        {
            ContainerNameOrId = "ncf-test",
            UserUidGid = "2000:2000",
            IsRestrictedReadOnly = true
        };

        var args = NightCodeForgeContainerExecutor.BuildDockerRunArgs(options);

        Assert.Contains("--user", args);
        var occurrences = args.Count(a => a == "--user");
        Assert.Equal(1, occurrences);
        var idx = args.IndexOf("--user");
        Assert.Equal("1000:1000", args[idx + 1]);
    }

    [Fact]
    public void BuildDockerRunArgs_IncludesGlobalizationAndCacheEnvVars()
    {
        var options = new ContainerExecutorOptions
        {
            ContainerNameOrId = "ncf-test",
            MountPackageCaches = true
        };

        var args = NightCodeForgeContainerExecutor.BuildDockerRunArgs(options);

        Assert.Contains("DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0", args);
        Assert.Contains("LANG=en_US.UTF-8", args);
        Assert.Contains("LC_ALL=en_US.UTF-8", args);
        Assert.Contains("NUGET_PACKAGES=/cache/nuget", args);
        Assert.Contains("npm_config_cache=/cache/npm", args);
        Assert.Contains("UV_CACHE_DIR=/cache/uv", args);
    }

    [Fact]
    public void BuildProcessStartSpec_WithUserUidGid_PassesUserFlagToDockerExec()
    {
        var options = new ContainerExecutorOptions
        {
            ContainerNameOrId = "ncf-test",
            UserUidGid = "1005:1005",
            IsRestrictedReadOnly = false
        };

        var spec = NightCodeForgeContainerExecutor.BuildProcessStartSpec(options);

        var argList = spec.ArgumentList!.ToList();
        Assert.Contains("--user", argList);
        var idx = argList.IndexOf("--user");
        Assert.Equal("1005:1005", argList[idx + 1]);
    }

    [Fact]
    public void BuildProcessStartInfo_WithUserUidGid_PassesUserFlagToDockerExec()
    {
        var options = new ContainerExecutorOptions
        {
            ContainerNameOrId = "ncf-test",
            UserUidGid = "1005:1005",
            IsRestrictedReadOnly = false
        };

        var psi = NightCodeForgeContainerExecutor.BuildProcessStartInfo(options);

        Assert.Contains("--user 1005:1005", psi.Arguments);
    }

    [Fact]
    public void HostUserIdentityResolver_ResolvesAndCachesUidGid()
    {
        int invocationCount = 0;
        var fakeRunner = new Mock<IProcessRunner>();
        fakeRunner
            .Setup(r => r.Run(It.IsAny<ProcessStartSpec>()))
            .Returns(() =>
            {
                invocationCount++;
                return new ProcessExecutionResult(0, "1001:1001\n", string.Empty);
            });

        var resolver = new HostUserIdentityResolver(
            fakeRunner.Object,
            NullLogger<HostUserIdentityResolver>.Instance);

        var options = new ContainerExecutorOptions
        {
            UseWsl = true,
            WslDistribution = "Ubuntu-24.04"
        };

        var id1 = resolver.ResolveUserIdentity(options);
        var id2 = resolver.ResolveUserIdentity(options);

        Assert.Equal("1001:1001", id1);
        Assert.Equal("1001:1001", id2);
        Assert.Equal("1001:1001", options.UserUidGid);
        Assert.Equal(1, invocationCount); // Cached!
    }

    [Fact]
    public void HostUserIdentityResolver_WhenProcessFails_UsesFallback10001000()
    {
        var fakeRunner = new Mock<IProcessRunner>();
        fakeRunner
            .Setup(r => r.Run(It.IsAny<ProcessStartSpec>()))
            .Returns(new ProcessExecutionResult(1, string.Empty, "Command failed"));

        var resolver = new HostUserIdentityResolver(
            fakeRunner.Object,
            NullLogger<HostUserIdentityResolver>.Instance);

        var options = new ContainerExecutorOptions
        {
            UseWsl = true,
            WslDistribution = "Ubuntu"
        };

        var identity = resolver.ResolveUserIdentity(options);

        Assert.Equal("1000:1000", identity);
        Assert.Equal("1000:1000", options.UserUidGid);
    }

    [Fact]
    public void HostUserIdentityResolver_FallbackIsNotCached_SoLaterCallRetries()
    {
        int invocationCount = 0;
        var fakeRunner = new Mock<IProcessRunner>();
        fakeRunner
            .Setup(r => r.Run(It.IsAny<ProcessStartSpec>()))
            .Returns(() => ++invocationCount == 1
                ? new ProcessExecutionResult(1, string.Empty, "distro starting")
                : new ProcessExecutionResult(0, "1234:1234\n", string.Empty));

        var resolver = new HostUserIdentityResolver(
            fakeRunner.Object,
            NullLogger<HostUserIdentityResolver>.Instance);

        var first = resolver.ResolveUserIdentity(true, "Ubuntu", "wsl.exe");
        var second = resolver.ResolveUserIdentity(true, "Ubuntu", "wsl.exe");

        Assert.Equal(HostUserIdentityResolver.DefaultUserUidGid, first);
        Assert.Equal("1234:1234", second);
    }

    [Fact]
    public void BuildDockerRunArgs_WiresPnpmAndMavenCaches()
    {
        var options = new ContainerExecutorOptions
        {
            ContainerNameOrId = "ncf-test",
            MountPackageCaches = true
        };

        var args = NightCodeForgeContainerExecutor.BuildDockerRunArgs(options);

        Assert.Contains("npm_config_store_dir=/cache/pnpm", args);
        Assert.Contains("MAVEN_OPTS=-Dmaven.repo.local=/cache/m2", args);
    }
}
