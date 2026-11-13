using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Agents.Context;
using NightCodeForge.Agents.Optimizations;
using NightCodeForge.Agents.Skills;
using NightCodeForge.Agents.Tests.Fixtures;
using NightCodeForge.Agents.Tools;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Services;

namespace NightCodeForge.Agents.Tests;

public class AgentWorkspaceContextServiceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "ncf-ctx-" + Guid.NewGuid().ToString("N"));

    public AgentWorkspaceContextServiceTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private AgentWorkspaceContextService CreateContextService(
        ITaskMarkdownSyncService? sync = null,
        IWorkspaceSymbolService? symbols = null,
        ITranscriptAnalysisService? transcriptAnalysis = null)
    {
        var resolver = new Mock<IWorkspacePathResolver>();
        resolver.Setup(r => r.ToHostPath(It.IsAny<string>())).Returns<string>(p => p);
        resolver.Setup(r => r.GetAgentWorkingClonePath(It.IsAny<int>())).Returns<int>(id => $"/clone/{id}");
        return new AgentWorkspaceContextService(
            resolver.Object,
            new AgentsMdService(),
            symbols ?? new Mock<IWorkspaceSymbolService>().Object,
            sync ?? new Mock<ITaskMarkdownSyncService>().Object,
            TestBacklogContext.CreateDefault(),
            transcriptAnalysis ?? new TranscriptAnalysisService(),
            NullLogger<AgentWorkspaceContextService>.Instance);
    }

    [Fact]
    public void ContextService_ResolveLocations_BareHostUsesRepositoryPath()
    {
        var service = CreateContextService();
        var workspace = new Workspace { Id = 1, RepositoryPath = _tempDir, ExecutionMode = ExecutionMode.BareHost };

        var locations = service.ResolveLocations(workspace, taskId: null, workspacePathOverride: null);

        Assert.Equal(_tempDir, locations.RepoPath);
        Assert.Equal(_tempDir, locations.AgentWorkingDir);
    }

    [Fact]
    public void ContextService_ResolveLocations_OverrideWinsOverRepositoryPath()
    {
        var service = CreateContextService();
        var workspace = new Workspace { Id = 1, RepositoryPath = "/other", ExecutionMode = ExecutionMode.WslDirect };

        var locations = service.ResolveLocations(workspace, 5, _tempDir);

        Assert.Equal(_tempDir, locations.RepoPath);
        Assert.Equal(_tempDir, locations.AgentWorkingDir);
    }

    [Fact]
    public void ContextService_ResolveLocations_WslDirectWithoutOverrideUsesWorkingClone()
    {
        var service = CreateContextService();
        var workspace = new Workspace { Id = 4, RepositoryPath = _tempDir, ExecutionMode = ExecutionMode.WslDirect };

        var locations = service.ResolveLocations(workspace, null, null);

        Assert.Equal("/clone/4", locations.AgentWorkingDir);
    }

    [Fact]
    public void ContextService_ResolveLocations_ContainerSandboxUsesTaskDirectoryOrClone()
    {
        var service = CreateContextService();
        var workspace = new Workspace { Id = 4, RepositoryPath = _tempDir, ExecutionMode = ExecutionMode.ContainerSandbox };

        Assert.Equal("/workspace/task-9", service.ResolveLocations(workspace, 9, _tempDir).AgentWorkingDir);
        Assert.Equal(AgentWorkspaceContextService.ContainerCloneDir, service.ResolveLocations(workspace, null, _tempDir).AgentWorkingDir);
    }

    [Fact]
    public async Task ContextService_SyncBacklog_SyncsOnlyExistingRepository()
    {
        var sync = new Mock<ITaskMarkdownSyncService>();
        var service = CreateContextService(sync.Object);
        var workspace = new Workspace { Id = 1, RepositoryPath = _tempDir };

        await service.SyncBacklogToDiskAsync(workspace, new AgentWorkspaceLocations(Path.Combine(_tempDir, "missing"), null, "x"), CancellationToken.None);
        sync.Verify(s => s.SyncAllTasksToDiskAsync(It.IsAny<Workspace>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);

        await service.SyncBacklogToDiskAsync(workspace, new AgentWorkspaceLocations(_tempDir, "clone", _tempDir), CancellationToken.None);
        sync.Verify(s => s.SyncAllTasksToDiskAsync(workspace, _tempDir, _tempDir, "clone", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ContextService_BuildPromptContext_LoadsSkillsCatalogOnlyWhenLoadSkillPermitted()
    {
        var service = CreateContextService();
        var workspace = new Workspace { Id = 1, RepositoryPath = _tempDir };
        var skills = new Mock<IWorkspaceSkillsService>();
        skills.Setup(s => s.GetActiveSkillsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new SkillDescriptor("alpha", "Alpha skill", _tempDir, Path.Combine(_tempDir, "SKILL.md")) });
        skills.Setup(s => s.GetSkillsCatalogPromptAsync(It.IsAny<CancellationToken>())).ReturnsAsync("<skills>alpha</skills>");

        var without = await service.BuildPromptContextAsync(workspace, _tempDir, new[] { CanonicalToolNames.Read }, skills.Object, CancellationToken.None);
        var with = await service.BuildPromptContextAsync(workspace, _tempDir, new[] { CanonicalToolNames.LoadSkill }, skills.Object, CancellationToken.None);

        Assert.Equal(string.Empty, without.SkillsCatalog);
        Assert.Empty(without.ActiveSkills);
        Assert.Equal("<skills>alpha</skills>", with.SkillsCatalog);
        Assert.Equal(new[] { "alpha" }, with.ActiveSkills);
    }

    [Fact]
    public void ContextService_DescribeTopLevelEntries_SkipsBuildAndGitFolders()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, "src"));
        Directory.CreateDirectory(Path.Combine(_tempDir, "bin"));
        Directory.CreateDirectory(Path.Combine(_tempDir, ".git"));
        var service = CreateContextService();

        var summary = service.DescribeTopLevelEntries(new Workspace { Id = 0 }, _tempDir);

        Assert.Equal("src", summary);
    }

    [Fact]
    public async Task GetSymbolMapPrompt_DelegatesToSymbolService()
    {
        var symbols = new Mock<IWorkspaceSymbolService>();
        var flags = new LocalOptimizationFlags();
        symbols.Setup(s => s.GetMatchingSymbolsPromptAsync(_tempDir, "task text", flags, It.IsAny<CancellationToken>())).ReturnsAsync("<symbols/>");
        var service = CreateContextService(symbols: symbols.Object);

        Assert.Equal("<symbols/>", await service.GetSymbolMapPromptAsync(_tempDir, "task text", flags, CancellationToken.None));
    }

    [Fact]
    public async Task BuildBacklogDigest_DelegatesToBacklogContext()
    {
        var backlog = new Mock<IBacklogPlanningContext>();
        backlog.Setup(b => b.BuildBacklogDigestAsync(5, 9, It.IsAny<CancellationToken>())).ReturnsAsync("<digest/>");
        var service = new AgentWorkspaceContextService(
            new Mock<IWorkspacePathResolver>().Object, new AgentsMdService(), new Mock<IWorkspaceSymbolService>().Object,
            new Mock<ITaskMarkdownSyncService>().Object, backlog.Object, new TranscriptAnalysisService(), NullLogger<AgentWorkspaceContextService>.Instance);

        Assert.Equal("<digest/>", await service.BuildBacklogDigestAsync(5, 9, CancellationToken.None));
    }

    [Fact]
    public void DescribeTopLevelEntries_ReturnsNullForEmptyOrMissingDirectory()
    {
        var service = CreateContextService();

        Assert.Null(service.DescribeTopLevelEntries(new Workspace { Id = 0 }, _tempDir));
        Assert.Null(service.DescribeTopLevelEntries(new Workspace { Id = 0 }, Path.Combine(_tempDir, "missing")));
    }

    [Fact]
    public void ResolveLocations_UsesExistingExt4MirrorAsCloneDirectory()
    {
        var mirror = Path.Combine(_tempDir, "mirror");
        Directory.CreateDirectory(mirror);
        var service = CreateContextService();

        var locations = service.ResolveLocations(new Workspace { Id = 2, RepositoryPath = _tempDir, Ext4MirrorPath = mirror, ExecutionMode = ExecutionMode.WslDirect }, null, null);

        Assert.Equal(mirror, locations.CloneDir);
        Assert.Equal(mirror, locations.AgentWorkingDir);
    }

    [Fact]
    public void ResolveLocations_ThrowsForNullWorkspace()
    {
        Assert.Throws<ArgumentNullException>(() => CreateContextService().ResolveLocations(null!, null, null));
    }

    [Fact]
    public async Task SyncBacklog_SwallowsAndLogsSyncFailures()
    {
        var sync = new Mock<ITaskMarkdownSyncService>();
        sync.Setup(s => s.SyncAllTasksToDiskAsync(It.IsAny<Workspace>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("disk full"));
        var service = CreateContextService(sync.Object);

        await service.SyncBacklogToDiskAsync(new Workspace { Id = 1 }, new AgentWorkspaceLocations(_tempDir, null, _tempDir), CancellationToken.None);
    }

    [Fact]
    public void Constructor_RejectsNullDependencies()
    {
        Assert.Throws<ArgumentNullException>(() => new AgentWorkspaceContextService(
            null!, new AgentsMdService(), new Mock<IWorkspaceSymbolService>().Object,
            new Mock<ITaskMarkdownSyncService>().Object, TestBacklogContext.CreateDefault(), new TranscriptAnalysisService(), NullLogger<AgentWorkspaceContextService>.Instance));
    }

    [Fact]
    public async Task BuildFrictionHotspotsDigestAsync_ReturnsEmptyWhenCleanAndDigestWhenFrictionPresent()
    {
        var service = CreateContextService();
        Assert.Equal(string.Empty, await service.BuildFrictionHotspotsDigestAsync(string.Empty));
        Assert.Equal(string.Empty, await service.BuildFrictionHotspotsDigestAsync(_tempDir));

        var transcriptDir = Path.Combine(_tempDir, "docs", "transcripts");
        Directory.CreateDirectory(transcriptDir);
        await File.WriteAllTextAsync(Path.Combine(transcriptDir, "2026-11-01-task-1-impl.md"), @"---
task_id: 1
stage_id: impl
status: Failed
---
### [ 00:00 ] 🤖 ASSISTANT
**Tool Invocation:** `edit`
```json
{ ""path"": ""src/Heavy.cs"" }
```
### [ 00:01 ] 🤖 ASSISTANT
**Tool Invocation:** `edit`
```json
{ ""path"": ""src/Heavy.cs"" }
```
### [ 00:02 ] 🤖 ASSISTANT
**Tool Invocation:** `edit`
```json
{ ""path"": ""src/Heavy.cs"" }
```
### [ 00:03 ] 🔧 TOOL
**Tool Invocation:** `bash`
**Tool Output:**
Exit Code: 1
src/Heavy.cs(5,1): error CS0246: Missing type
");

        var digest = await service.BuildFrictionHotspotsDigestAsync(_tempDir);
        Assert.Contains("<workspace_friction_hotspots", digest);
        Assert.Contains("src/Heavy.cs", digest);
        Assert.Contains("CS0246", digest);
    }
}
