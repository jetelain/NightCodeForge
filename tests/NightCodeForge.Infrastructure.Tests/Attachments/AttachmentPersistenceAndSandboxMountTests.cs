using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Security;
using NightCodeForge.Infrastructure;
using NightCodeForge.Infrastructure.Data;
using NightCodeForge.Infrastructure.Data.Repositories;
using NightCodeForge.Infrastructure.Execution;
using Pmad.Markitdown;
using Xunit;

namespace NightCodeForge.Infrastructure.Tests.Attachments;

public class AttachmentPersistenceAndSandboxMountTests : IDisposable
{
    private readonly string _tempRoot;

    public AttachmentPersistenceAndSandboxMountTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"ncf-infra-attach-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    [Fact]
    public void ServiceCollection_RegistersAttachmentsServicesAndRepositories_UnderStrictScopeValidation()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNightCodeForgeCoreAndInfrastructure();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IMarkitdown>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ITaskAttachmentRepository>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IAttachmentIntakeService>());
    }

    [Fact]
    public async Task EfCoreTaskAttachmentRepository_PersistsDeduplicatesLinksAndCascadesDeletes()
    {
        await using var conn = new SqliteConnection("DataSource=:memory:");
        await conn.OpenAsync();

        var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(conn)
            .Options;

        await using (var initCtx = new AppDbContext(dbOptions))
        {
            await initCtx.EnsureDatabaseCreatedAndWalAsync();
        }

        var factory = new TestDbContextFactory(dbOptions);
        var wsRepo = new EfCoreWorkspaceRepository(factory);
        var taskRepo = new EfCoreTaskRepository(factory);
        var attachRepo = new EfCoreTaskAttachmentRepository(factory);

        var ws1 = await wsRepo.CreateAsync(new Workspace { Name = "WS1", RepositoryPath = "/repo/1" });
        var ws2 = await wsRepo.CreateAsync(new Workspace { Name = "WS2", RepositoryPath = "/repo/2" });

        var task1 = await taskRepo.CreateAsync(new ForgeTask { WorkspaceId = ws1.Id, Title = "Task 1", BaseBranch = "main" });
        var task2 = await taskRepo.CreateAsync(new ForgeTask { WorkspaceId = ws1.Id, Title = "Task 2", BaseBranch = "main" });

        var attachment = await attachRepo.CreateAsync(new TaskAttachment
        {
            WorkspaceId = ws1.Id,
            Title = "spec.pdf",
            Slug = "spec",
            RelativePath = "001-spec.pdf",
            MarkdownPath = "001-spec.md",
            ContentKind = TaskAttachmentContentKind.RichDocument,
            MediaType = "application/pdf",
            SourceKind = TaskAttachmentSourceKind.FileDrop,
            SourceRef = "spec.pdf",
            Sha256 = "abc123",
            SizeBytes = 1024
        });

        var foreignAttachment = await attachRepo.CreateAsync(new TaskAttachment
        {
            WorkspaceId = ws2.Id,
            Title = "foreign.txt",
            Slug = "foreign",
            RelativePath = "002-foreign.txt",
            MarkdownPath = "002-foreign.txt",
            ContentKind = TaskAttachmentContentKind.PlainText,
            MediaType = "text/plain",
            SourceKind = TaskAttachmentSourceKind.FileDrop,
            SourceRef = "foreign.txt",
            Sha256 = "abc123",
            SizeBytes = 12
        });

        // Unique constraint on (WorkspaceId, Sha256)
        await Assert.ThrowsAsync<DbUpdateException>(() => attachRepo.CreateAsync(new TaskAttachment
        {
            WorkspaceId = ws1.Id,
            Title = "dup.pdf",
            Slug = "dup",
            RelativePath = "003-dup.pdf",
            ContentKind = TaskAttachmentContentKind.RichDocument,
            MediaType = "application/pdf",
            SourceKind = TaskAttachmentSourceKind.FileDrop,
            SourceRef = "dup.pdf",
            Sha256 = "abc123",
            SizeBytes = 1024
        }));

        var foundByHash = await attachRepo.GetByWorkspaceAndSha256Async(ws1.Id, "ABC123");
        Assert.NotNull(foundByHash);
        Assert.Equal(attachment.Id, foundByHash!.Id);

        // Link attachment to tasks and verify cross-workspace link rejection
        await attachRepo.SetTaskAttachmentLinksAsync(task1.Id, [attachment.Id]);
        await attachRepo.AddTaskAttachmentLinksAsync(task2.Id, [attachment.Id]);

        await Assert.ThrowsAsync<AttachmentNotFoundException>(() =>
            attachRepo.SetTaskAttachmentLinksAsync(task1.Id, [foreignAttachment.Id]));

        await Assert.ThrowsAsync<AttachmentNotFoundException>(() =>
            attachRepo.SetTaskAttachmentLinksAsync(task1.Id, [999999]));

        var byTask = await attachRepo.GetByTaskIdAsync(task1.Id);
        Assert.Single(byTask);
        Assert.Equal(attachment.Id, byTask[0].Id);

        var linkedTaskIds = await attachRepo.GetLinkedTaskIdsAsync(attachment.Id);
        Assert.Equal([task1.Id, task2.Id], linkedTaskIds);

        var loadedTask = await taskRepo.GetByIdAsync(task1.Id);
        Assert.NotNull(loadedTask);
        Assert.Single(loadedTask!.AttachmentLinks);
        Assert.Equal(attachment.Id, loadedTask.AttachmentLinks.First().TaskAttachment?.Id);

        // Deleting task1 removes its link while preserving attachment and task2 link
        await taskRepo.DeleteAsync(task1.Id);
        Assert.Equal([task2.Id], await attachRepo.GetLinkedTaskIdsAsync(attachment.Id));

        // Deleting workspace cascades to attachments and remaining links
        await wsRepo.DeleteAsync(ws1.Id);
        Assert.Null(await attachRepo.GetByIdAsync(attachment.Id));
        Assert.Empty(await attachRepo.GetLinkedTaskIdsAsync(attachment.Id));
    }

    [Fact]
    public async Task AppDbContext_BaselineExistingPreMigrationDatabase_RecordsAttachmentsMigrationWhenTableExists()
    {
        await using var conn = new SqliteConnection("DataSource=:memory:");
        await conn.OpenAsync();

        var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(conn)
            .Options;

        await using (var ensureCreatedCtx = new AppDbContext(dbOptions))
        {
            await ensureCreatedCtx.Database.EnsureCreatedAsync();
        }

        await using (var migratedCtx = new AppDbContext(dbOptions))
        {
            await migratedCtx.EnsureDatabaseCreatedAndWalAsync();
            var applied = (await migratedCtx.Database.GetAppliedMigrationsAsync()).ToList();
            Assert.Contains(AppDbContext.AddTaskAttachmentsAndLinksMigrationId, applied);
            Assert.Empty(await migratedCtx.Database.GetPendingMigrationsAsync());
        }
    }

    [Fact]
    public void WorkspacePathResolver_GetAttachmentsPathAndResolveCruciblePath_ResolveSiblingAttachmentsDirectory()
    {
        var wslMeta = new FakeWslMetadataService();
        var opts = Options.Create(new ExecutionEnvironmentProviderOptions());

        var winResolver = new WorkspacePathResolver(wslMeta, opts, isWindowsOverride: true, distributionOverride: "Ubuntu", userOverride: "devuser");
        Assert.Equal("/home/devuser/.nightcodeforge/wks/10/attachments", winResolver.GetAttachmentsPath(10));

        var winAttachFile = winResolver.ResolveCruciblePath(10, "/workspace/attachments/001-spec.md", taskId: 42);
        Assert.Equal(@"\\wsl.localhost\Ubuntu\home\devuser\.nightcodeforge\wks\10\attachments\001-spec.md", winAttachFile);

        var winEmbeddedWksAttach = winResolver.ResolveCruciblePath(10, "/home/devuser/.nightcodeforge/wks/10/attachments/002-img.png", taskId: 42);
        Assert.Equal(@"\\wsl.localhost\Ubuntu\home\devuser\.nightcodeforge\wks\10\attachments\002-img.png", winEmbeddedWksAttach);

        var unixResolver = new WorkspacePathResolver(wslMeta, opts, isWindowsOverride: false, distributionOverride: "Ubuntu", userOverride: "devuser");
        Assert.EndsWith(".nightcodeforge/wks/10/attachments", unixResolver.GetAttachmentsPath(10));

        var resolvedUnixFromTaskRoot = unixResolver.ResolveCruciblePath("/home/devuser/.nightcodeforge/wks/10/task-42", "/workspace/attachments/001-spec.md", taskId: 42);
        Assert.Equal("/home/devuser/.nightcodeforge/wks/10/attachments/001-spec.md", resolvedUnixFromTaskRoot);

        var nativeResolver = new WorkspacePathResolver(wslMeta, opts);
        var nativeTaskRoot = Path.Combine(_tempRoot, "wks-10", "task-42");
        Directory.CreateDirectory(nativeTaskRoot);
        var resolvedNativeFromTaskRoot = nativeResolver.ResolveCruciblePath(nativeTaskRoot, "/workspace/attachments/001-spec.md", taskId: 42);
        Assert.Equal(Path.GetFullPath(Path.Combine(_tempRoot, "wks-10", "attachments", "001-spec.md")), resolvedNativeFromTaskRoot);
    }

    [Fact]
    public async Task ContainerEnvironmentFactory_And_SandboxPathResolver_PreCreateAttachmentsAndEnforceReadOnlyMount()
    {
        var wksDir = Path.Combine(_tempRoot, "wks-55");
        var cloneDir = Path.Combine(wksDir, "clone");
        var taskDir = Path.Combine(wksDir, "task-9");
        Directory.CreateDirectory(cloneDir);
        Directory.CreateDirectory(taskDir);

        var pathResolverMock = new Mock<IWorkspacePathResolver>();
        pathResolverMock.Setup(p => p.GetAttachmentsPath(55)).Returns(Path.Combine(wksDir, "attachments"));
        pathResolverMock.Setup(m => m.GetWorkspaceMemoryPath(It.IsAny<int>())).Returns<int>(id => $"~/.nightcodeforge/wks/{id}/memory");
        pathResolverMock.Setup(p => p.ToHostPath(It.IsAny<string>())).Returns<string>(p => p);
        pathResolverMock.Setup(p => p.GetWslUserName()).Returns("devuser");

        var sdkMock = new Mock<IHostSdkDiscoveryService>();
        sdkMock.Setup(s => s.DiscoverSdkMountPaths(It.IsAny<string?>())).Returns(["/usr/share/dotnet"]);
        sdkMock.Setup(s => s.DiscoverSdkDetails(It.IsAny<string?>()))
            .Returns(new HostSdkDiscoveryDetails(["/usr/share/dotnet"], new Dictionary<string, string>(), new Dictionary<string, string>()));
        var identityMock = new Mock<IHostUserIdentityResolver>();
        identityMock.Setup(i => i.ResolveUserIdentity(It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<string>())).Returns("1000:1000");

        var factory = new ContainerEnvironmentFactory(
            new Mock<IProcessRunner>().Object,
            Options.Create(new ExecutionEnvironmentProviderOptions()),
            Options.Create(new SecurityOptions()),
            pathResolverMock.Object,
            sdkMock.Object,
            identityMock.Object,
            new Mock<IOperationsTelemetryService>().Object,
            new Mock<ICompanionToolInstaller>().Object,
            NullLogger<NightCodeForgeContainerExecutor>.Instance);

        var workspace = new Workspace
        {
            Id = 55,
            Name = "Attachments Scope Test",
            RepositoryPath = cloneDir,
            ExecutionMode = ExecutionMode.ContainerSandbox
        };

        var scope = factory.BuildWorkspaceScope(workspace, SandboxSecurityTier.Standard, effectiveTaskId: 9);

        // Pre-creation created attachments/ directory
        var expectedHostAttachmentsDir = Path.Combine(wksDir, "attachments");
        Assert.True(Directory.Exists(expectedHostAttachmentsDir));

        var resolver = new SandboxPathResolver(scope);
        var resolvedRead = resolver.ResolveHostPath("/workspace/attachments/001-spec.md", forWrite: false);
        Assert.Equal(Path.GetFullPath(Path.Combine(expectedHostAttachmentsDir, "001-spec.md")), resolvedRead);

        // Writes targeting /workspace/attachments are strictly denied
        Assert.Throws<UnauthorizedAccessException>(() =>
            resolver.ResolveHostPath("/workspace/attachments/001-spec.md", forWrite: true));

        // Path traversal escaping /workspace or canonicalizing into read-only /workspace/attachments for write is denied
        Assert.Throws<UnauthorizedAccessException>(() =>
            resolver.ResolveHostPath("/workspace/attachments/../../secret.txt", forWrite: false));
        Assert.Throws<UnauthorizedAccessException>(() =>
            resolver.ResolveHostPath("/workspace/task-9/../attachments/001-spec.md", forWrite: true));

        // Bash command path rewriting across tiers
        const string cmd = "cat /workspace/attachments/001-spec.md";
        Assert.Equal(cmd, resolver.TranslateBashCommand(cmd, ExecutionTier.ContainerSandbox));
        var wslCmd = resolver.TranslateBashCommand(cmd, ExecutionTier.WslDirect);
        Assert.Contains("/attachments/001-spec.md", wslCmd);
        Assert.DoesNotContain("/workspace/attachments", wslCmd);

        var bareCmd = resolver.TranslateBashCommand(cmd, ExecutionTier.BareHost);
        Assert.Equal($"cat {expectedHostAttachmentsDir.Replace('\\', '/')}/001-spec.md", bareCmd);

        // Docker executor options & volume mounts
        var env = Assert.IsType<NightCodeForgeContainerExecutor>(await factory.CreateContainerEnvironmentAsync(scope));
        Assert.False(string.IsNullOrWhiteSpace(env.Options.AttachmentsPath));

        var mounts = ContainerMountAndDriftBuilder.BuildVolumeMounts(env.Options);
        var attachMount = Assert.Single(mounts, m => m.ContainerPath == "/workspace/attachments");
        Assert.True(attachMount.IsReadOnly);
    }

    [Fact]
    public async Task ContainerMountAndDriftBuilder_MountsAttachmentsReadOnly_AndDetectsMissingWritableOrSourceDrift()
    {
        var options = new ContainerExecutorOptions
        {
            ContainerNameOrId = "ncf-ws-7-task-3",
            ContainerImage = "alpine",
            HostWorkspacePath = "/wks/7",
            CanonicalClonePath = "/wks/7/clone",
            AttachmentsPath = "/wks/7/attachments",
            ContainerWorkspacePath = "/workspace",
            DefaultWorkingDirectory = "/workspace/task-3",
            MountPackageCaches = false,
            MountHostSdks = false,
            ProtectCloneWorkingTree = false,
            IsRestrictedReadOnly = true
        };

        var mounts = ContainerMountAndDriftBuilder.BuildVolumeMounts(options);
        var attachMount = Assert.Single(mounts, m => m.ContainerPath == "/workspace/attachments");
        Assert.Equal("/wks/7/attachments", attachMount.HostPath);
        Assert.True(attachMount.IsReadOnly);

        var runArgs = ContainerMountAndDriftBuilder.BuildDockerRunArgs(options);
        Assert.Contains("/wks/7/attachments:/workspace/attachments:ro", runArgs);

        // 1. Missing /workspace/attachments triggers drift
        var missingDrift = await DetectDriftAsync(options, "/workspace=/wks/7=false;");
        Assert.Contains("/workspace/attachments (missing)", missingDrift);

        // 2. Writable (rw) /workspace/attachments triggers drift
        var writableDrift = await DetectDriftAsync(options, "/workspace=/wks/7=false;/workspace/attachments=/wks/7/attachments=true;");
        Assert.Contains("/workspace/attachments (expected ro)", writableDrift);

        // 3. Mismatched source triggers drift
        var sourceDrift = await DetectDriftAsync(options, "/workspace=/wks/7=false;/workspace/attachments=/wks/999/attachments=false;");
        Assert.Contains("/workspace/attachments (source '/wks/999/attachments' vs '/wks/7/attachments')", sourceDrift);

        // 4. Matching read-only mount produces zero drift
        var noDrift = await DetectDriftAsync(options, "/workspace=/wks/7=false;/workspace/attachments=/wks/7/attachments=false;");
        Assert.Null(noDrift);
    }

    [Fact]
    public async Task InMemoryTaskAttachmentRepository_MaintainsParityWithEfCore_HashNormalizationTaskLinksAndCascadePruning()
    {
        var taskRepo = new InMemoryTaskRepository();
        var attachRepo = new InMemoryTaskAttachmentRepository(taskRepo);

        var task1 = await taskRepo.CreateAsync(new ForgeTask { WorkspaceId = 1, Title = "T1", BaseBranch = "main" });
        var task2 = await taskRepo.CreateAsync(new ForgeTask { WorkspaceId = 1, Title = "T2", BaseBranch = "main" });

        var created = await attachRepo.CreateAsync(new TaskAttachment
        {
            WorkspaceId = 1,
            Title = "Spec",
            Slug = "spec",
            RelativePath = "001-spec.md",
            MarkdownPath = "001-spec.md",
            ContentKind = TaskAttachmentContentKind.PlainText,
            MediaType = "text/markdown",
            SourceKind = TaskAttachmentSourceKind.FileDrop,
            SourceRef = "spec.md",
            Sha256 = "AABBCCDD",
            SizeBytes = 10
        });

        Assert.Equal("aabbccdd", created.Sha256);
        Assert.NotNull(await attachRepo.GetByWorkspaceAndSha256Async(1, "AABBCCDD"));
        Assert.NotNull(await attachRepo.GetByWorkspaceAndSha256Async(1, "aabbccdd"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => attachRepo.CreateAsync(new TaskAttachment
        {
            WorkspaceId = 1,
            Title = "Dup",
            Slug = "dup",
            RelativePath = "002-dup.md",
            Sha256 = "aabbccdd",
            SizeBytes = 10
        }));

        await attachRepo.SetTaskAttachmentLinksAsync(task1.Id, [created.Id]);
        await attachRepo.AddTaskAttachmentLinksAsync(task2.Id, [created.Id]);

        var loadedTask1 = await taskRepo.GetByIdAsync(task1.Id);
        Assert.NotNull(loadedTask1);
        var navLink = Assert.Single(loadedTask1!.AttachmentLinks);
        Assert.Equal(created.Id, navLink.TaskAttachmentId);
        Assert.Equal("Spec", navLink.TaskAttachment?.Title);

        // Updating attachment refreshes task navigation link snapshot
        created.Title = "Updated Spec";
        await attachRepo.UpdateAsync(created);
        Assert.Equal("Updated Spec", loadedTask1.AttachmentLinks.Single().TaskAttachment?.Title);

        // Deleting task1 through InMemoryTaskRepository prunes stale links in attachment repo
        await taskRepo.DeleteAsync(task1.Id);
        Assert.Equal([task2.Id], await attachRepo.GetLinkedTaskIdsAsync(created.Id));
        var linkedTasks = await attachRepo.GetLinkedTasksAsync(created.Id);
        Assert.Single(linkedTasks);
        Assert.Equal(task2.Id, linkedTasks[0].Id);
        Assert.Empty(await attachRepo.GetByTaskIdAsync(task1.Id));

        // Deleting attachment clears remaining task navigation link
        await attachRepo.DeleteAsync(created.Id);
        var loadedTask2 = await taskRepo.GetByIdAsync(task2.Id);
        Assert.Empty(loadedTask2!.AttachmentLinks);
    }

    [Fact]
    public async Task EfCoreRepositories_NormalizeSha256OnWrite_AndIncludeAttachmentLinksInGetByStatusAsync()
    {
        await using var conn = new SqliteConnection("DataSource=:memory:");
        await conn.OpenAsync();
        var dbOptions = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(conn).Options;
        await using (var initCtx = new AppDbContext(dbOptions))
        {
            await initCtx.EnsureDatabaseCreatedAndWalAsync();
        }

        var factory = new TestDbContextFactory(dbOptions);
        var wsRepo = new EfCoreWorkspaceRepository(factory);
        var taskRepo = new EfCoreTaskRepository(factory);
        var attachRepo = new EfCoreTaskAttachmentRepository(factory);

        var ws = await wsRepo.CreateAsync(new Workspace { Name = "WS", RepositoryPath = "/repo" });
        var task = await taskRepo.CreateAsync(new ForgeTask
        {
            WorkspaceId = ws.Id,
            Title = "Open Task",
            Status = TaskOverallStatus.Open,
            BaseBranch = "main"
        });

        var attachment = await attachRepo.CreateAsync(new TaskAttachment
        {
            WorkspaceId = ws.Id,
            Title = "Spec",
            Slug = "spec",
            RelativePath = "001-spec.md",
            MarkdownPath = "001-spec.md",
            ContentKind = TaskAttachmentContentKind.PlainText,
            MediaType = "text/markdown",
            SourceKind = TaskAttachmentSourceKind.FileDrop,
            SourceRef = "spec.md",
            Sha256 = "UPPERCASEHASH",
            SizeBytes = 20
        });

        Assert.Equal("uppercasehash", attachment.Sha256);
        await Assert.ThrowsAsync<DbUpdateException>(() => attachRepo.CreateAsync(new TaskAttachment
        {
            WorkspaceId = ws.Id,
            Title = "Dup",
            Slug = "dup",
            RelativePath = "002-dup.md",
            ContentKind = TaskAttachmentContentKind.PlainText,
            MediaType = "text/plain",
            SourceKind = TaskAttachmentSourceKind.FileDrop,
            SourceRef = "dup.md",
            Sha256 = "uppercasehash",
            SizeBytes = 20
        }));

        await attachRepo.SetTaskAttachmentLinksAsync(task.Id, [attachment.Id]);

        var openTasks = await taskRepo.GetByStatusAsync(TaskOverallStatus.Open);
        var loaded = Assert.Single(openTasks, t => t.Id == task.Id);
        var link = Assert.Single(loaded.AttachmentLinks);
        Assert.Equal(attachment.Id, link.TaskAttachment?.Id);
    }

    [Fact]
    public void SandboxMountTable_And_SandboxPathResolver_EnforceReadOnlyOnWorkspaceAttachmentsAndWksMarkerPaths()
    {
        var mountTable = new SandboxMountTable(
            SandboxMountTable.WorkspaceVirtualRoot,
            [
                new VirtualMountEntry(
                    "attachments",
                    AttachmentConstants.VirtualAttachmentsMountPath,
                    "/wks/12/attachments",
                    "/wks/12/attachments",
                    isReadOnly: false),
                new VirtualMountEntry(
                    "task-5",
                    "/workspace/task-5",
                    "/wks/12/task-5",
                    "/wks/12/task-5",
                    isReadOnly: false),
                new VirtualMountEntry(
                    string.Empty,
                    SandboxMountTable.WorkspaceVirtualRoot,
                    "/wks/12/task-5",
                    "/wks/12/task-5",
                    isReadOnly: false)
            ]);

        var attachEntry = Assert.Single(mountTable.Entries, e => e.VirtualMountPath == AttachmentConstants.VirtualAttachmentsMountPath);
        Assert.True(attachEntry.IsReadOnly);

        var scope = SandboxScopeDescriptor.ForWorkspace(
            new Workspace { Id = 12, Name = "WS12", RepositoryPath = "/wks/12/clone" },
            SandboxSecurityTier.Standard,
            taskId: 5,
            mountTable: mountTable,
            defaultWorkingDirectory: "/workspace/task-5");

        var resolver = new SandboxPathResolver(scope);
        Assert.Equal("/wks/12/attachments/001-spec.md", resolver.ResolveHostPath("~/.nightcodeforge/wks/12/attachments/001-spec.md", forWrite: false));
        Assert.Equal("/wks/12/attachments/001-spec.md", resolver.ResolveHostPath("/home/someone/.nightcodeforge/wks/12/attachments/001-spec.md", forWrite: false));

        Assert.Throws<UnauthorizedAccessException>(() =>
            resolver.ResolveHostPath("~/.nightcodeforge/wks/12/attachments/001-spec.md", forWrite: true));
        Assert.Throws<UnauthorizedAccessException>(() =>
            resolver.ResolveHostPath("~/.nightcodeforge/wks/12/attachments/../../secret.txt", forWrite: false));
    }

    [Fact]
    public async Task ContainerMountAndDriftBuilder_AttachmentsPathTakesPrecedenceOverMountTable_AndMapsWorkingDir()
    {
        var mountTable = SandboxMountTable.ForWorkspace(
            hostWorkingDirectory: "/wks/7/task-3",
            taskId: 3,
            hostCloneDirectory: "/wks/7/clone",
            hostAttachmentsDirectory: "/wks/7/old-attachments");

        var options = new ContainerExecutorOptions
        {
            ContainerNameOrId = "ncf-ws-7-task-3",
            ContainerImage = "alpine",
            HostWorkspacePath = "/wks/7",
            CanonicalClonePath = "/wks/7/clone",
            AttachmentsPath = "/wks/7/attachments",
            MountTable = mountTable,
            ContainerWorkspacePath = "/workspace",
            DefaultWorkingDirectory = "/workspace/task-3",
            MountPackageCaches = false,
            MountHostSdks = false,
            ProtectCloneWorkingTree = false,
            IsRestrictedReadOnly = false
        };

        var mounts = ContainerMountAndDriftBuilder.BuildVolumeMounts(options);
        var attachMount = Assert.Single(mounts, m => m.ContainerPath == "/workspace/attachments");
        Assert.Equal("/wks/7/attachments", attachMount.HostPath);
        Assert.True(attachMount.IsReadOnly);

        Assert.Null(await DetectDriftAsync(options, "/workspace=/wks/7=true;/workspace/attachments=/wks/7/attachments=false;"));
        Assert.NotNull(await DetectDriftAsync(options, "/workspace=/wks/7=true;/workspace/attachments=/wks/7/old-attachments=false;"));

        Assert.Equal("/workspace/attachments", ContainerMountAndDriftBuilder.ResolveContainerWorkingDirectory("/wks/7/attachments", options));
        Assert.Equal("/workspace/attachments/sub", ContainerMountAndDriftBuilder.ResolveContainerWorkingDirectory("/wks/7/attachments/sub", options));
    }

    private static Task<string?> DetectDriftAsync(ContainerExecutorOptions options, string mountInspectOutput)
    {
        var runnerMock = new Mock<IProcessRunner>();
        runnerMock.Setup(r => r.RunAsync(It.Is<ProcessStartSpec>(s => s.ArgumentList != null && s.ArgumentList.Contains("{{range .Mounts}}{{.Destination}}={{.Source}}={{.RW}};{{end}}")), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult(0, mountInspectOutput, string.Empty));
        runnerMock.Setup(r => r.RunAsync(It.Is<ProcessStartSpec>(s => s.ArgumentList != null && s.ArgumentList.Contains("{{.Config.Image}}")), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult(0, "alpine\n", string.Empty));
        runnerMock.Setup(r => r.RunAsync(It.Is<ProcessStartSpec>(s => s.ArgumentList != null && s.ArgumentList.Contains("{{.Config.User}}")), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult(0, ContainerExecutorOptions.RestrictedReadOnlyUserUidGid + "\n", string.Empty));
        runnerMock.Setup(r => r.RunAsync(It.Is<ProcessStartSpec>(s => s.ArgumentList != null && s.ArgumentList.Contains("{{.HostConfig.NetworkMode}}")), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult(0, "none\n", string.Empty));
        runnerMock.Setup(r => r.RunAsync(It.Is<ProcessStartSpec>(s => s.ArgumentList != null && s.ArgumentList.Contains("{{range .Config.Env}}{{.}}\n{{end}}")), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult(0, "npm_config_offline=true\nNPM_CONFIG_OFFLINE=true\n", string.Empty));

        return ContainerMountAndDriftBuilder.DetectContainerDriftAsync(runnerMock.Object, options);
    }

    private sealed class TestDbContextFactory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }

    private sealed class FakeWslMetadataService : IWslMetadataService
    {
        public string GetDefaultDistributionName() => "Ubuntu";
        public string GetDefaultUserName() => "devuser";
        public void ClearCache() { }
    }
}
