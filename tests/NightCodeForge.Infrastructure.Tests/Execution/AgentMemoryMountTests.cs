using Microsoft.Extensions.Options;
using Moq;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Memory;
using NightCodeForge.Infrastructure.Execution;

namespace NightCodeForge.Infrastructure.Tests.Execution;

/// <summary>Step 12.39 §2: selective :rw /workspace/memory mount, Triage :ro isolation and path resolution.</summary>
public class AgentMemoryMountTests
{
    private const string HostWorkspace = "/wks/5";
    private const string HostMemory = "/wks/5/memory";

    private static ContainerExecutorOptions BuildOptions(
        SandboxSecurityTier tier,
        bool memoryReadOnly,
        bool memoryAllowWriteInReadOnlyScope,
        string containerName)
    {
        var isReadOnly = tier == SandboxSecurityTier.RestrictedReadOnly;
        var table = SandboxMountTable.ForWorkspace(
            hostWorkingDirectory: "/wks/5/clone",
            wslWorkingDirectory: "/wks/5/clone",
            isReadOnly: isReadOnly,
            hostCloneDirectory: "/wks/5/clone",
            wslCloneDirectory: "/wks/5/clone",
            additionalEntries:
            [
                new VirtualMountEntry(
                    virtualSubPath: AgentMemoryConstants.LocalMemoryDirectoryName,
                    virtualMountPath: AgentMemoryConstants.VirtualLocalMemoryPath,
                    hostDirectoryPath: HostMemory,
                    wslTargetDirectoryPath: HostMemory,
                    isReadOnly: memoryReadOnly,
                    allowWriteInReadOnlyScope: memoryAllowWriteInReadOnlyScope)
            ]);

        return new ContainerExecutorOptions
        {
            ContainerNameOrId = containerName,
            ContainerImage = "alpine",
            HostWorkspacePath = HostWorkspace,
            ContainerWorkspacePath = "/workspace",
            DefaultWorkingDirectory = "/workspace/clone",
            MountTable = table,
            IsRestrictedReadOnly = isReadOnly,
            MountPackageCaches = false,
            MountHostSdks = false,
            ProtectCloneWorkingTree = true
        };
    }

    [Fact]
    public void BuildVolumeMounts_IdeationReadOnlyContainer_MountsMemoryRwOverReadOnlyWorkspace()
    {
        var options = BuildOptions(SandboxSecurityTier.RestrictedReadOnly, memoryReadOnly: false, memoryAllowWriteInReadOnlyScope: true, "ncf-sandbox-5-ro");

        var mounts = ContainerMountAndDriftBuilder.BuildVolumeMounts(options);

        Assert.Single(mounts, m => m.ContainerPath == "/workspace" && m.IsReadOnly);
        Assert.Single(mounts, m => m.ContainerPath == "/workspace/memory" && m.HostPath == HostMemory && !m.IsReadOnly);
    }

    [Fact]
    public void BuildVolumeMounts_TriageReadOnlyContainer_MountsMemoryReadOnly()
    {
        var options = BuildOptions(SandboxSecurityTier.RestrictedReadOnly, memoryReadOnly: true, memoryAllowWriteInReadOnlyScope: false, "ncf-sandbox-5-triage-ro");

        var mounts = ContainerMountAndDriftBuilder.BuildVolumeMounts(options);

        Assert.Single(mounts, m => m.ContainerPath == "/workspace/memory" && m.HostPath == HostMemory && m.IsReadOnly);
    }

    [Fact]
    public void BuildVolumeMounts_StandardContainer_ExposesMemoryRwThroughWorkspaceMount()
    {
        var options = BuildOptions(SandboxSecurityTier.Standard, memoryReadOnly: false, memoryAllowWriteInReadOnlyScope: true, "ncf-sandbox-5");

        var mounts = ContainerMountAndDriftBuilder.BuildVolumeMounts(options);

        Assert.Single(mounts, m => m.ContainerPath == "/workspace" && !m.IsReadOnly);
        Assert.DoesNotContain(mounts, m => m.ContainerPath == "/workspace/memory" && m.IsReadOnly);
    }

    [Fact]
    public void ResolveEffectiveReadOnly_AppliesSelectiveWriteException()
    {
        var writable = new VirtualMountEntry("memory", "/workspace/memory", HostMemory, HostMemory, isReadOnly: false, allowWriteInReadOnlyScope: true);
        var plain = new VirtualMountEntry("memory", "/workspace/memory", HostMemory, HostMemory, isReadOnly: false, allowWriteInReadOnlyScope: false);
        var readOnly = new VirtualMountEntry("memory", "/workspace/memory", HostMemory, HostMemory, isReadOnly: true, allowWriteInReadOnlyScope: true);

        Assert.False(ContainerMountAndDriftBuilder.ResolveEffectiveReadOnly(writable, isRestrictedReadOnly: true));
        Assert.True(ContainerMountAndDriftBuilder.ResolveEffectiveReadOnly(plain, isRestrictedReadOnly: true));
        Assert.True(ContainerMountAndDriftBuilder.ResolveEffectiveReadOnly(readOnly, isRestrictedReadOnly: true));
        Assert.False(ContainerMountAndDriftBuilder.ResolveEffectiveReadOnly(plain, isRestrictedReadOnly: false));
    }

    private static Mock<IProcessRunner> BuildRunner(string mountInspectOutput)
    {
        var runner = new Mock<IProcessRunner>();
        runner.Setup(r => r.RunAsync(
                It.Is<ProcessStartSpec>(s => s.ArgumentList != null && s.ArgumentList.Contains("{{range .Mounts}}{{.Destination}}={{.Source}}={{.RW}};{{end}}")),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult(0, mountInspectOutput, string.Empty));
        runner.Setup(r => r.RunAsync(
                It.Is<ProcessStartSpec>(s => s.ArgumentList != null && s.ArgumentList.Contains("{{.Config.Image}}")),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult(0, "alpine\n", string.Empty));
        return runner;
    }

    private static string InspectOutput(IEnumerable<ContainerVolumeMount> mounts, Func<ContainerVolumeMount, bool> include, Func<ContainerVolumeMount, bool?> rwOverride)
    {
        return string.Concat(mounts.Where(include).Select(m =>
            $"{m.ContainerPath}={m.HostPath}={(rwOverride(m) ?? !m.IsReadOnly).ToString().ToLowerInvariant()};"));
    }

    [Fact]
    public async Task DetectContainerDrift_StaleContainerMissingMemoryMount_IsReported()
    {
        var options = BuildOptions(SandboxSecurityTier.RestrictedReadOnly, memoryReadOnly: false, memoryAllowWriteInReadOnlyScope: true, "ncf-sandbox-5-ro");
        var mounts = ContainerMountAndDriftBuilder.BuildVolumeMounts(options);
        var runner = BuildRunner(InspectOutput(mounts, m => m.ContainerPath != "/workspace/memory", _ => null));

        var drift = await ContainerMountAndDriftBuilder.DetectContainerDriftAsync(runner.Object, options);

        Assert.NotNull(drift);
        Assert.Contains("/workspace/memory (missing)", drift!);
    }

    [Fact]
    public async Task DetectContainerDrift_MemoryMountedReadOnlyInsteadOfRw_IsReported()
    {
        var options = BuildOptions(SandboxSecurityTier.RestrictedReadOnly, memoryReadOnly: false, memoryAllowWriteInReadOnlyScope: true, "ncf-sandbox-5-ro");
        var mounts = ContainerMountAndDriftBuilder.BuildVolumeMounts(options);
        var runner = BuildRunner(InspectOutput(mounts, _ => true, m => m.ContainerPath == "/workspace/memory" ? false : null));

        var drift = await ContainerMountAndDriftBuilder.DetectContainerDriftAsync(runner.Object, options);

        Assert.NotNull(drift);
        Assert.Contains("/workspace/memory (expected rw)", drift!);
    }

    [Fact]
    public async Task DetectContainerDrift_MemoryMountedRwForTriage_IsReported()
    {
        var options = BuildOptions(SandboxSecurityTier.RestrictedReadOnly, memoryReadOnly: true, memoryAllowWriteInReadOnlyScope: false, "ncf-sandbox-5-triage-ro");
        var mounts = ContainerMountAndDriftBuilder.BuildVolumeMounts(options);
        var runner = BuildRunner(InspectOutput(mounts, _ => true, m => m.ContainerPath == "/workspace/memory" ? true : null));

        var drift = await ContainerMountAndDriftBuilder.DetectContainerDriftAsync(runner.Object, options);

        Assert.NotNull(drift);
        Assert.Contains("/workspace/memory (expected ro)", drift!);
    }

    [Fact]
    public async Task DetectContainerDrift_MemoryMountedWithWrongSource_IsReported()
    {
        var options = BuildOptions(SandboxSecurityTier.RestrictedReadOnly, memoryReadOnly: false, memoryAllowWriteInReadOnlyScope: true, "ncf-sandbox-5-ro");
        var mounts = ContainerMountAndDriftBuilder.BuildVolumeMounts(options);
        var output = string.Concat(mounts.Select(m =>
            $"{m.ContainerPath}={(m.ContainerPath == "/workspace/memory" ? "/elsewhere/memory" : m.HostPath)}={(!m.IsReadOnly).ToString().ToLowerInvariant()};"));
        var runner = BuildRunner(output);

        var drift = await ContainerMountAndDriftBuilder.DetectContainerDriftAsync(runner.Object, options);

        Assert.NotNull(drift);
        Assert.Contains("/workspace/memory (source", drift!);
    }

    [Fact]
    public void TriageContainerName_IsDistinctFromIdeationReadOnlyContainer()
    {
        var workspace = new Workspace { Id = 5 };
        var ideationScope = SandboxScopeDescriptor.ForWorkspace(workspace, SandboxSecurityTier.RestrictedReadOnly) with { ContainerName = "ncf-sandbox-5" };
        var triageScope = SandboxScopeDescriptor.ForWorkspace(workspace, SandboxSecurityTier.RestrictedReadOnly) with
        {
            ContainerName = SandboxScopeDescriptor.BuildTriageContainerBaseName("ncf-sandbox-5")
        };

        Assert.Equal("ncf-sandbox-5-ro", ContainerMountAndDriftBuilder.ResolveContainerName(ideationScope));
        Assert.Equal("ncf-sandbox-5-triage-ro", ContainerMountAndDriftBuilder.ResolveContainerName(triageScope));
    }

    private static WorkspacePathResolver CreateResolver(bool isWindows) =>
        new(Mock.Of<IWslMetadataService>(), Options.Create(new ExecutionEnvironmentProviderOptions()), isWindows, "Ubuntu", "julien");

    [Fact]
    public void GetWorkspaceMemoryPath_IsSiblingOfClone()
    {
        var resolver = CreateResolver(isWindows: true);

        Assert.Equal("/home/julien/.nightcodeforge/wks/42/memory", resolver.GetWorkspaceMemoryPath(42));
    }

    [Theory]
    [InlineData("/workspace/memory/MEMORY.md", "/memory/MEMORY.md")]
    [InlineData("/workspace/memory", "/memory")]
    [InlineData("workspace/memory/topic.md", "/memory/topic.md")]
    [InlineData("/home/julien/.nightcodeforge/wks/5/memory/topic.md", "/memory/topic.md")]
    public void ResolveCruciblePath_MapsMemoryToSiblingDirectory_Unix(string requested, string expectedSuffix)
    {
        var resolver = CreateResolver(isWindows: false);

        foreach (var taskId in new int?[] { null, 12 })
        {
            var path = resolver.ResolveCruciblePath(5, requested, taskId);

            Assert.EndsWith(".nightcodeforge/wks/5" + expectedSuffix, path.Replace('\\', '/'));
            Assert.DoesNotContain("/clone/", path.Replace('\\', '/'));
            Assert.DoesNotContain("/task-12/", path.Replace('\\', '/'));
        }
    }

    [Fact]
    public void ResolveCruciblePath_MapsMemoryToWslUncSibling_Windows()
    {
        var resolver = CreateResolver(isWindows: true);

        var path = resolver.ResolveCruciblePath(5, "/workspace/memory/MEMORY.md", taskId: 3);

        Assert.Equal(@"\\wsl.localhost\Ubuntu\home\julien\.nightcodeforge\wks\5\memory\MEMORY.md", path);
    }

    [Fact]
    public void ResolveCruciblePath_RejectsMemoryTraversal()
    {
        var resolver = CreateResolver(isWindows: false);

        Assert.Throws<UnauthorizedAccessException>(() => resolver.ResolveCruciblePath(5, "/workspace/memory/../../etc/passwd"));
    }

    [Fact]
    public void ResolveCruciblePath_BareHostRepositoryRoot_NeverResolvesMemoryInsideRepository()
    {
        var resolver = CreateResolver(isWindows: false);

        // Workspace-id based resolution never uses the repository path.
        var byId = resolver.ResolveCruciblePath(5, "/workspace/memory/x.md");
        Assert.DoesNotContain("my-repo", byId);

        // Root-based resolution from a bare repository path cannot know the wks layout: fail fast instead of <repo>/memory.
        Assert.Throws<UnauthorizedAccessException>(() =>
            resolver.ResolveCruciblePath("/home/julien/src/my-repo", "/workspace/memory/x.md", null));
    }

    [Fact]
    public void ResolveCruciblePath_RootBasedResolutionFromClone_UsesSiblingMemory()
    {
        var resolver = CreateResolver(isWindows: false);

        var path = resolver.ResolveCruciblePath("/home/julien/.nightcodeforge/wks/5/clone", "/workspace/memory/x.md", null);

        Assert.Equal("/home/julien/.nightcodeforge/wks/5/memory/x.md", path.Replace('\\', '/'));
    }

    [Theory]
    [InlineData(".nightcodeforge/wks/5/memory/topic.md")]
    [InlineData("/home/julien/.nightcodeforge/wks/5/memory/topic.md")]
    [InlineData("/other/prefix/.nightcodeforge/wks/5/memory/topic.md")]
    public void ResolveCruciblePath_RootBasedResolutionWithEmbeddedWksMemory_UsesSiblingMemory(string requested)
    {
        var resolver = CreateResolver(isWindows: false);

        var path = resolver.ResolveCruciblePath("/home/julien/.nightcodeforge/wks/5/clone", requested, null);

        Assert.Equal("/home/julien/.nightcodeforge/wks/5/memory/topic.md", path.Replace('\\', '/'));
        Assert.DoesNotContain("/clone/", path.Replace('\\', '/'));
    }
}
