using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NightCodeForge.Agents.Context;
using NightCodeForge.Agents.Crucible;
using NightCodeForge.Agents.Ideation;
using NightCodeForge.Agents.Inference;
using NightCodeForge.Agents.Optimizations;
using NightCodeForge.Agents.Tests.Fixtures;
using NightCodeForge.Agents.Tools;
using NightCodeForge.Agents.Tools.EditorConfig;
using NightCodeForge.Agents.Transcripts;
using NightCodeForge.Agents.Triage;
using NightCodeForge.Core.Concurrency;
using NightCodeForge.Core.Dependencies;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Resilience;
using NightCodeForge.Core.Security;
using NightCodeForge.Core.Services;
using NightCodeForge.Core.Streaming;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Infrastructure.Execution;

namespace NightCodeForge.Agents.Tests.Attachments;

public class Step122BAttachmentToolsAndPromptDigestTests : IDisposable
{
    private readonly string _tempRoot;

    public Step122BAttachmentToolsAndPromptDigestTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "ncf-step122b-" + Guid.NewGuid().ToString("N"));
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
    public void CanonicalTools_And_DefaultAgentTools_RemainUnchanged_WithZeroDedicatedAttachmentTools()
    {
        Assert.DoesNotContain(CanonicalToolNames.All, t => t.Contains("attachment", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(DefaultAgentTools.Ideation, t => t.Contains("attachment", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(DefaultAgentTools.Full, t => t.Contains("attachment", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(DefaultAgentTools.ReadOnly, t => t.Contains("attachment", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(DefaultAgentTools.Review, t => t.Contains("attachment", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CanonicalRead_ReadsManifestAndAttachmentMarkdown_WithPaginationAndCap()
    {
        var (repoDir, attachmentsDir) = CreateWorkspaceDirectories();
        var manifestJson = JsonSerializer.Serialize(new AttachmentManifest
        {
            WorkspaceId = 1,
            Attachments =
            [
                new AttachmentManifestEntry
                {
                    Id = 1,
                    WorkspaceId = 1,
                    Title = "API Spec",
                    Slug = "api-spec",
                    RelativePath = "1-api-spec.md",
                    ContentKind = TaskAttachmentContentKind.PlainText,
                    MediaType = "text/markdown",
                    SourceKind = TaskAttachmentSourceKind.FileDrop,
                    SourceRef = "api-spec.md",
                    IsSymlink = false,
                    Sha256 = new string('a', 64),
                    SizeBytes = 100
                }
            ]
        });

        await File.WriteAllTextAsync(Path.Combine(attachmentsDir, AttachmentConstants.ManifestFileName), manifestJson);
        var lines = Enumerable.Range(1, 20).Select(i => $"Line {i}: Endpoint contract detail").ToArray();
        await File.WriteAllTextAsync(Path.Combine(attachmentsDir, "1-api-spec.md"), string.Join('\n', lines));

        var module = CreateFileModule(repoDir, attachmentsDir, workspaceId: 1);

        var readManifest = await module.ReadAsync("/workspace/attachments/manifest.json");
        Assert.Contains("\"title\":\"API Spec\"", readManifest);

        var page1 = await module.ReadAsync("/workspace/attachments/1-api-spec.md", offset: 3, limit: 4);
        Assert.Contains("3 | Line 3: Endpoint contract detail", page1);
        Assert.Contains("6 | Line 6: Endpoint contract detail", page1);
        Assert.DoesNotContain("2 | Line 2:", page1);
        Assert.DoesNotContain("7 | Line 7:", page1);

        var capped = ReadOutputFormatter.FormatCapped(lines, startLine: 1, totalFileLines: 20, maxBytes: 500);
        Assert.Contains("[Output truncated: reached 1 KB limit at line", capped);

        var tightCap = ReadOutputFormatter.FormatCapped(lines, startLine: 1, totalFileLines: 20, maxBytes: 120);
        Assert.Contains("[Truncated: 120B limit]", tightCap);
    }

    [Fact]
    public async Task ReadOnlyGuards_BlockWritesEditsTraversalAndMutatingBash_TargetingAttachments()
    {
        var (repoDir, attachmentsDir) = CreateWorkspaceDirectories();
        var targetFile = Path.Combine(attachmentsDir, "1-notes.txt");
        await File.WriteAllTextAsync(targetFile, "Original attachment line");

        var module = CreateFileModule(repoDir, attachmentsDir, workspaceId: 1, isReadOnly: false);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            module.WriteAsync("/workspace/attachments/new.txt", "tamper"));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            module.EditAsync("/workspace/attachments/1-notes.txt", "Original", "Modified"));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            module.WriteAsync("/workspace/attachments/../attachments/new.txt", "tamper"));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            module.WriteAsync("/workspace/clone/../attachments/new.txt", "tamper"));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            module.ReadAsync("/workspace/attachments/../../../outside-secret.txt"));

        Assert.Throws<UnauthorizedAccessException>(() =>
            module.SandboxPathResolver.ResolveHostPath("/workspace/attachments/../../../outside-secret.txt", forWrite: false));

        var mountTable = SandboxMountTable.ForWorkspace(
            repoDir,
            hostAttachmentsDirectory: attachmentsDir);
        var resolver = CreateSandboxResolver(repoDir, attachmentsDir, SandboxSecurityTier.Standard, mountTable);

        var envMock = new Mock<IExecutionEnvironment>();
        envMock.SetupGet(e => e.Tier).Returns(ExecutionTier.ContainerSandbox);
        envMock.Setup(e => e.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CommandResult(0, "ok", string.Empty, TimeSpan.FromMilliseconds(5)));

        var bashModule = new SandboxBashToolModule(
            new CanonicalToolScope(repoDir, WorkspaceId: 1, SandboxPathResolver: resolver),
            resolver,
            envMock.Object,
            NightCodeForge.Core.Workflow.Filtering.CliOutputFilterRegistry.Default,
            NullLogger.Instance);

        var allowed = await bashModule.BashAsync("cat /workspace/attachments/1-notes.txt");
        Assert.Contains("ok", allowed);

        var allowedFromAttachmentsWd = await bashModule.BashAsync("cat 1-notes.txt", workingDirectory: "/workspace/attachments");
        Assert.Contains("ok", allowedFromAttachmentsWd);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            bashModule.BashAsync("rm -f /workspace/attachments/1-notes.txt"));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            bashModule.BashAsync("echo hack >> /workspace/attachments/1-notes.txt"));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            bashModule.BashAsync("rm -f 1-notes.txt", workingDirectory: "/workspace/attachments"));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            bashModule.BashAsync("cd /workspace/attachments && rm -f 1-notes.txt"));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            bashModule.BashAsync("sed -Ei 's/Original/Hack/' /workspace/attachments/1-notes.txt"));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            bashModule.BashAsync("find /workspace/attachments -delete"));

        Assert.Throws<UnauthorizedAccessException>(() =>
            ReadOnlyCommandValidator.EnsureMountAccessAllowed("touch /workspace/attachments/pwn.txt", mountTable));

        var roBashModule = new SandboxBashToolModule(
            new CanonicalToolScope(repoDir, WorkspaceId: 1, IsReadOnly: true, SandboxPathResolver: resolver),
            resolver,
            envMock.Object,
            NightCodeForge.Core.Workflow.Filtering.CliOutputFilterRegistry.Default,
            NullLogger.Instance);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            roBashModule.BashAsync("rm -f /workspace/attachments/1-notes.txt"));
    }

    [Theory]
    [InlineData(ExecutionTier.ContainerSandbox)]
    [InlineData(ExecutionTier.WslDirect)]
    [InlineData(ExecutionTier.BareHost)]
    public void BashPathTranslation_And_WorkingDirectory_WorkAcrossAllExecutionTiers(ExecutionTier tier)
    {
        var (repoDir, attachmentsDir) = CreateWorkspaceDirectories();
        var wslRepo = "/home/tester/.nightcodeforge/wks/5/clone";
        var wslAttach = "/home/tester/.nightcodeforge/wks/5/attachments";

        var mountTable = SandboxMountTable.ForWorkspace(
            repoDir,
            wslRepo,
            isReadOnly: false,
            hostAttachmentsDirectory: attachmentsDir,
            wslAttachmentsDirectory: wslAttach);
        var scope = SandboxScopeDescriptor.ForWorkspace(
            CreateWorkspace(5, repoDir),
            SandboxSecurityTier.Standard,
            taskId: null,
            mountTable);
        var resolver = new SandboxPathResolver(scope);

        var translatedCmd = resolver.TranslateBashCommand("grep -n \"Spec\" /workspace/attachments/1-spec.md", tier);
        var translatedWd = resolver.ResolveBashWorkingDirectory("/workspace/attachments", tier);

        if (tier == ExecutionTier.ContainerSandbox)
        {
            Assert.Contains("/workspace/attachments/1-spec.md", translatedCmd);
            Assert.Equal("/workspace/attachments", translatedWd);
        }
        else if (tier == ExecutionTier.WslDirect)
        {
            Assert.Contains(wslAttach + "/1-spec.md", translatedCmd);
            Assert.Equal(wslAttach, translatedWd);
        }
        else
        {
            var expectedHostAttach = Path.GetFullPath(attachmentsDir);
            var expectedForwardHost = expectedHostAttach.Replace('\\', '/');
            Assert.True(
                translatedCmd.Contains(expectedHostAttach + Path.DirectorySeparatorChar + "1-spec.md", StringComparison.OrdinalIgnoreCase) ||
                translatedCmd.Contains(expectedForwardHost + "/1-spec.md", StringComparison.OrdinalIgnoreCase),
                $"Expected host attachment path in translated command: {translatedCmd}");
            Assert.Equal(expectedHostAttach, Path.GetFullPath(translatedWd));
        }
    }

    [Fact]
    public async Task WorkspacePathResolverSandboxAdapter_ResolvesAndTranslatesAttachmentsReadOnly()
    {
        var (repoDir, attachmentsDir) = CreateWorkspaceDirectories();
        await File.WriteAllTextAsync(Path.Combine(attachmentsDir, "manifest.json"), "{}");

        var wslMock = new Mock<IWslMetadataService>();
        wslMock.Setup(w => w.GetDefaultUserName()).Returns("tester");
        wslMock.Setup(w => w.GetDefaultDistributionName()).Returns("Ubuntu");
        var pathResolver = new WorkspacePathResolver(
            wslMock.Object,
            Options.Create(new ExecutionEnvironmentProviderOptions()),
            isWindowsOverride: false);

        var scope = new CanonicalToolScope(repoDir, WorkspaceId: 7, TaskId: null, IsReadOnly: false);
        var adapter = new WorkspacePathResolverSandboxAdapter(scope, pathResolver, NullLogger.Instance);

        Assert.True(adapter.MountTable.HasAttachmentsMount);
        var resolved = adapter.ResolveHostPath("/workspace/attachments/manifest.json", forWrite: false);
        Assert.EndsWith("manifest.json", resolved, StringComparison.OrdinalIgnoreCase);

        Assert.Throws<UnauthorizedAccessException>(() =>
            adapter.ResolveHostPath("/workspace/attachments/manifest.json", forWrite: true));

        var bareCmd = adapter.TranslateBashCommand("cat /workspace/attachments/manifest.json", ExecutionTier.BareHost);
        Assert.DoesNotContain("/workspace/attachments/manifest.json", bareCmd);
    }

    [Fact]
    public async Task BrokenSymlink_And_SourceRefMismatch_ThrowAttachmentTargetNotFoundException()
    {
        var (repoDir, attachmentsDir) = CreateWorkspaceDirectories();
        var missingHostFile = Path.Combine(_tempRoot, "deleted-host-doc.md");
        var wrongHostFile = Path.Combine(_tempRoot, "wrong-host-doc.md");
        await File.WriteAllTextAsync(wrongHostFile, "Wrong target");

        var manifestPath = Path.Combine(attachmentsDir, AttachmentConstants.ManifestFileName);
        var entryMissing = new AttachmentManifestEntry
        {
            Id = 1,
            WorkspaceId = 1,
            Title = "Missing Symlink Doc",
            Slug = "missing-symlink-doc",
            RelativePath = "1-missing.md",
            ContentKind = TaskAttachmentContentKind.PlainText,
            MediaType = "text/markdown",
            SourceKind = TaskAttachmentSourceKind.HostPath,
            SourceRef = missingHostFile,
            IsSymlink = true,
            Sha256 = new string('b', 64),
            SizeBytes = 10
        };

        var manifestJson = JsonSerializer.Serialize(new AttachmentManifest
        {
            WorkspaceId = 1,
            Attachments = [entryMissing]
        });
        await File.WriteAllTextAsync(manifestPath, manifestJson);

        var linkPath = Path.Combine(attachmentsDir, "1-missing.md");
        await File.WriteAllTextAsync(linkPath, "placeholder");

        var taskRepo = new InMemoryTaskRepository();
        var attachmentRepo = new InMemoryTaskAttachmentRepository(taskRepo);
        var services = new ServiceCollection();
        services.AddSingleton<ITaskAttachmentRepository>(attachmentRepo);
        var sp = services.BuildServiceProvider();
        var module = CreateFileModule(repoDir, attachmentsDir, workspaceId: 1, scopeFactory: sp.GetRequiredService<IServiceScopeFactory>());

        // Manifest reports broken symlink target -> AttachmentTargetNotFoundException
        await Assert.ThrowsAsync<AttachmentTargetNotFoundException>(() =>
            module.ReadAsync("/workspace/attachments/1-missing.md"));

        // DB entry alone (without manifest) with IsSymlink=true pointing to missing host target also fails fast
        File.Delete(manifestPath);
        await attachmentRepo.CreateAsync(new TaskAttachment
        {
            WorkspaceId = 1,
            Title = "DB Missing Symlink",
            Slug = "db-missing",
            RelativePath = "1-missing.md",
            ContentKind = TaskAttachmentContentKind.PlainText,
            MediaType = "text/markdown",
            SourceKind = TaskAttachmentSourceKind.HostPath,
            SourceRef = missingHostFile,
            IsSymlink = true,
            Sha256 = new string('c', 64),
            SizeBytes = 10
        });

        await Assert.ThrowsAsync<AttachmentTargetNotFoundException>(() =>
            module.ReadAsync("/workspace/attachments/1-missing.md"));

        // Attempt native OS symlink test (runs cleanly when OS/privileges permit creating symlinks)
        var nativeLinkPath = Path.Combine(attachmentsDir, "2-native-link.md");
        var nativeTarget = Path.Combine(_tempRoot, "real-host-doc.md");
        await File.WriteAllTextAsync(nativeTarget, "Original host target");

        var symlinkSupported = false;
        try
        {
            File.CreateSymbolicLink(nativeLinkPath, wrongHostFile);
            symlinkSupported = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
        }

        if (symlinkSupported)
        {
            var mismatchManifest = JsonSerializer.Serialize(new AttachmentManifest
            {
                WorkspaceId = 1,
                Attachments =
                [
                    new AttachmentManifestEntry
                    {
                        Id = 2,
                        WorkspaceId = 1,
                        Title = "Native Link",
                        Slug = "native-link",
                        RelativePath = "2-native-link.md",
                        ContentKind = TaskAttachmentContentKind.PlainText,
                        MediaType = "text/markdown",
                        SourceKind = TaskAttachmentSourceKind.HostPath,
                        SourceRef = nativeTarget, // does not match wrongHostFile symlink target
                        IsSymlink = true,
                        Sha256 = new string('d', 64),
                        SizeBytes = 10
                    }
                ]
            });
            await File.WriteAllTextAsync(manifestPath, mismatchManifest);

            await Assert.ThrowsAsync<AttachmentTargetNotFoundException>(() =>
                module.ReadAsync("/workspace/attachments/2-native-link.md"));

            File.Delete(nativeLinkPath);
            File.CreateSymbolicLink(nativeLinkPath, missingHostFile);
            File.Delete(manifestPath);

            // Dangling native OS symlink must throw AttachmentTargetNotFoundException rather than FileNotFoundException
            await Assert.ThrowsAsync<AttachmentTargetNotFoundException>(() =>
                module.ReadAsync("/workspace/attachments/2-native-link.md"));

            // Unregistered native OS symlink pointing to an existing host file must still fail fast
            var unregisteredLinkPath = Path.Combine(attachmentsDir, "3-unregistered-link.md");
            File.CreateSymbolicLink(unregisteredLinkPath, wrongHostFile);
            await Assert.ThrowsAsync<AttachmentTargetNotFoundException>(() =>
                module.ReadAsync("/workspace/attachments/3-unregistered-link.md"));
        }

        // Reading the .md companion of a symlinked RichDocument with a missing SourceRef target must also throw
        await File.WriteAllTextAsync(Path.Combine(attachmentsDir, "4-spec.md"), "# Spec Companion");
        var richDocManifest = JsonSerializer.Serialize(new AttachmentManifest
        {
            WorkspaceId = 1,
            Attachments =
            [
                new AttachmentManifestEntry
                {
                    Id = 4,
                    WorkspaceId = 1,
                    Title = "Rich Spec PDF",
                    Slug = "rich-spec",
                    RelativePath = "4-spec.pdf",
                    MarkdownPath = "4-spec.md",
                    ContentKind = TaskAttachmentContentKind.RichDocument,
                    MediaType = "application/pdf",
                    SourceKind = TaskAttachmentSourceKind.HostPath,
                    SourceRef = missingHostFile,
                    IsSymlink = true,
                    Sha256 = new string('e', 64),
                    SizeBytes = 100
                }
            ]
        });
        await File.WriteAllTextAsync(manifestPath, richDocManifest);

        await Assert.ThrowsAsync<AttachmentTargetNotFoundException>(() =>
            module.ReadAsync("/workspace/attachments/4-spec.md"));
    }

    [Fact]
    public async Task Stage0TriageIsolation_OmitsAttachmentsMount_AndBlocksReadBashAndContainerMount()
    {
        var (repoDir, attachmentsDir) = CreateWorkspaceDirectories();
        await File.WriteAllTextAsync(Path.Combine(attachmentsDir, "manifest.json"), "{\"attachments\":[]}");
        var workspace = CreateWorkspace(3, repoDir);

        AgentSessionContext? capturedContext = null;
        var harnessMock = new Mock<IAgentHarness>();
        harnessMock.SetupGet(h => h.HarnessId).Returns("crucible");
        harnessMock.Setup(h => h.RunStageAsync(It.IsAny<AgentSessionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AgentSessionContext ctx, CancellationToken _) =>
            {
                capturedContext = ctx;
                return new AgentRunResult(AgentRunStatus.Completed, new AgentRunMetrics(), Proposals: []);
            });

        var wsRepoMock = new Mock<IWorkspaceRepository>();
        wsRepoMock.Setup(r => r.GetEffectiveAgentProfileAsync(It.IsAny<int?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentProfile
            {
                Id = "triage",
                Name = "Triage",
                HarnessId = "crucible",
                ModelName = "test-model",
                PermittedTools = DefaultAgentTools.ReadOnly,
                Mode = AgentMode.ReadOnly,
                SecurityTier = SandboxSecurityTier.RestrictedReadOnly
            });

        var taskRepo = new InMemoryTaskRepository();
        var pathResolver = new WorkspacePathResolver(
            Mock.Of<IWslMetadataService>(),
            Options.Create(new ExecutionEnvironmentProviderOptions()),
            isWindowsOverride: false);
        var (backlogContext, sp) = BuildRealBacklogContext(taskRepo, new InMemoryTaskAttachmentRepository(taskRepo), new InMemoryWorkspaceRepository(), pathResolver, repoDir);
        var wsContext = new AgentWorkspaceContextService(
            pathResolver,
            new AgentsMdService(),
            Mock.Of<IWorkspaceSymbolService>(),
            sp.GetRequiredService<ITaskMarkdownSyncService>(),
            backlogContext,
            new TranscriptAnalysisService(),
            new NightCodeForge.Agents.Tests.Fixtures.NoopAgentMemoryTurnTracker(), NullLogger<AgentWorkspaceContextService>.Instance);

        var triageService = new TriageService(
            taskRepo,
            wsRepoMock.Object,
            new HarnessRegistry([harnessMock.Object]),
            wsContext,
            sp.GetRequiredService<ITaskMarkdownSyncService>(),
            Mock.Of<IGlobalOptimizationSettingsService>(),
            Mock.Of<IOperationsTelemetryService>(),
            pathResolver,
            NullLogger<TriageService>.Instance);

        await triageService.TriageExternalReportAsync(
            workspace,
            new TriageReportRequest("Untrusted bug report", "Reproduce bug in CLI"));

        Assert.NotNull(capturedContext?.SandboxScope);
        var triageScope = capturedContext!.SandboxScope!;

        Assert.False(triageScope.MountTable.HasAttachmentsMount);
        Assert.DoesNotContain(triageScope.MountTable.Entries, e =>
            e.VirtualMountPath.Contains(AttachmentConstants.VirtualAttachmentsMountPath, StringComparison.OrdinalIgnoreCase));

        var resolver = new SandboxPathResolver(triageScope);
        Assert.Throws<UnauthorizedAccessException>(() => resolver.ResolveHostPath("/workspace/attachments/manifest.json"));
        Assert.Throws<UnauthorizedAccessException>(() => resolver.TranslateBashCommand("cat /workspace/attachments/manifest.json", ExecutionTier.ContainerSandbox));
        Assert.Throws<UnauthorizedAccessException>(() => resolver.ResolveBashWorkingDirectory("/workspace/attachments", ExecutionTier.ContainerSandbox));

        var fileModule = new WorkspaceFileToolModule(
            new CanonicalToolScope(repoDir, WorkspaceId: 3, IsReadOnly: true, SandboxScope: triageScope, SandboxPathResolver: resolver),
            resolver,
            new EditorConfigService(),
            new EditorConfigFileApplier(),
            NullLogger.Instance);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            fileModule.ReadAsync("/workspace/attachments/manifest.json"));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            fileModule.ReadAsync("/workspace/clone/../attachments/manifest.json"));

        var envMock = new Mock<IExecutionEnvironment>();
        var bashModule = new SandboxBashToolModule(
            new CanonicalToolScope(repoDir, WorkspaceId: 3, IsReadOnly: true, SandboxScope: triageScope, SandboxPathResolver: resolver),
            resolver,
            envMock.Object,
            NightCodeForge.Core.Workflow.Filtering.CliOutputFilterRegistry.Default,
            NullLogger.Instance);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            bashModule.BashAsync("cat /workspace/attachments/manifest.json"));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            bashModule.BashAsync("cat /workspace/clone/../attachments/manifest.json"));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            bashModule.BashAsync("ls -la", workingDirectory: "/workspace/attachments"));

        // Verify Docker container mount builder also omits /workspace/attachments when triageScope is passed to ContainerEnvironmentFactory
        var capturedArgs = new List<string>();
        var processRunnerMock = new Mock<IProcessRunner>();
        processRunnerMock.Setup(p => p.RunAsync(
                It.IsAny<ProcessStartSpec>(),
                It.IsAny<CancellationToken>()))
            .Returns<ProcessStartSpec, CancellationToken>((spec, _) =>
            {
                var cmd = $"{spec.FileName} {string.Join(" ", spec.Arguments)}";
                capturedArgs.Add(cmd);
                if (cmd.Contains("inspect", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(new ProcessExecutionResult(1, string.Empty, "No such container"));
                }
                return Task.FromResult(new ProcessExecutionResult(0, string.Empty, string.Empty));
            });

        var hostSdkMock = new Mock<IHostSdkDiscoveryService>();
        hostSdkMock.Setup(s => s.DiscoverSdkMountPaths(It.IsAny<string?>())).Returns(Array.Empty<string>());
        hostSdkMock.Setup(s => s.DiscoverSdkDetailsAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HostSdkDiscoveryDetails([], new Dictionary<string, string>(), new Dictionary<string, string>()));
        var identityMock = new Mock<IHostUserIdentityResolver>();
        identityMock.Setup(i => i.ResolveUserIdentity(It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .Returns("1000:1000");

        var envFactory = new ContainerEnvironmentFactory(
            processRunnerMock.Object,
            Options.Create(new ExecutionEnvironmentProviderOptions()),
            Options.Create(new SecurityOptions()),
            pathResolver,
            hostSdkMock.Object,
            identityMock.Object,
            Mock.Of<IOperationsTelemetryService>(),
            Mock.Of<ICompanionToolInstaller>(),
            NullLogger<NightCodeForgeContainerExecutor>.Instance);

        var containerEnv = await envFactory.CreateContainerEnvironmentAsync(triageScope);
        var executor = Assert.IsType<NightCodeForgeContainerExecutor>(containerEnv);
        Assert.Null(executor.Options.AttachmentsPath);

        var volumeMounts = ContainerMountAndDriftBuilder.BuildVolumeMounts(executor.Options);
        Assert.DoesNotContain(volumeMounts, m => m.ContainerPath.Contains("attachments", StringComparison.OrdinalIgnoreCase));

        var dockerRunArgs = string.Join(" ", ContainerMountAndDriftBuilder.BuildDockerRunArgs(executor.Options));
        Assert.DoesNotContain("/workspace/attachments", dockerRunArgs);
    }

    [Fact]
    public async Task PromptDigests_IdeationInjectsWorkspaceAttachments_And_CrucibleKeepsLayers1And2InvariantAcrossTasks()
    {
        var (repoDir, attachmentsDir) = CreateWorkspaceDirectories();
        var taskRepo = new InMemoryTaskRepository();
        var workspaceRepo = new InMemoryWorkspaceRepository();
        var attachmentRepo = new InMemoryTaskAttachmentRepository(taskRepo);

        var workspace = await workspaceRepo.CreateAsync(CreateWorkspace(1, repoDir));
        var attA = await attachmentRepo.CreateAsync(CreateDomainAttachment(workspace.Id, "Auth Flow PDF", "1-auth.pdf", "1-auth.pdf.md", TaskAttachmentContentKind.RichDocument));
        var attB = await attachmentRepo.CreateAsync(CreateDomainAttachment(workspace.Id, "UI Mockup", "2-ui.png", null, TaskAttachmentContentKind.Image, width: 1024, height: 768));

        var pathResolver = new WorkspacePathResolver(
            Mock.Of<IWslMetadataService>(),
            Options.Create(new ExecutionEnvironmentProviderOptions()),
            isWindowsOverride: false);

        var (backlogContext, serviceProvider) = BuildRealBacklogContext(taskRepo, attachmentRepo, workspaceRepo, pathResolver, repoDir);
        var workspaceContext = new AgentWorkspaceContextService(
            pathResolver,
            new AgentsMdService(),
            Mock.Of<IWorkspaceSymbolService>(),
            new TaskMarkdownSyncService(serviceProvider.GetRequiredService<IServiceScopeFactory>(), pathResolver, NullLogger<TaskMarkdownSyncService>.Instance),
            backlogContext,
            new TranscriptAnalysisService(),
            new NightCodeForge.Agents.Tests.Fixtures.NoopAgentMemoryTurnTracker(), NullLogger<AgentWorkspaceContextService>.Instance);

        // 1. Verify IdeationConversationCoordinator injects <workspace_attachments> and citation rule
        var capturedIdeationRequests = new List<HarnessAgentRequest>();
        var ideationProfile = new AgentProfile
        {
            Id = "ideation",
            Name = "Ideation",
            ModelName = "test-model",
            PermittedTools = DefaultAgentTools.Ideation,
            Mode = AgentMode.ReadOnly
        };
        workspaceRepo.SeedGlobalProfiles([ideationProfile]);

        var ideationHarnessFactoryMock = CreateCapturingHarnessAgentFactory(req => capturedIdeationRequests.Add(req));
        using var ideationBuilder = new TestIdeationCoordinatorBuilder
        {
            PathResolver = pathResolver,
            WorkspaceRepository = workspaceRepo,
            TaskRepository = taskRepo,
            BacklogContext = backlogContext,
            WorkspaceContextService = workspaceContext,
            HarnessAgentFactory = ideationHarnessFactoryMock.Object,
            BaseChatClient = new StaticTextChatClient("Explored workspace attachments.")
        };
        var ideationCoordinator = ideationBuilder.Build();

        await ideationBuilder.ConverseAsync(ideationCoordinator, workspace, "Review attached specs.", agentProfile: ideationProfile);

        var ideationReq = Assert.Single(capturedIdeationRequests);
        Assert.Contains("<workspace_attachments>", ideationReq.Instructions);
        Assert.Contains("6. WORKSPACE ATTACHMENTS & CITATIONS", ideationReq.Instructions);
        Assert.Contains("/workspace/attachments/1-auth.pdf.md", ideationReq.Instructions);
        Assert.Contains("/workspace/attachments/2-ui.png", ideationReq.Instructions);
        Assert.Contains("1024x768", ideationReq.Instructions);

        // 2. Verify PromptPrefixStabilizer isolates <task_attachments> strictly in Layer 3
        var stabilizer = new PromptPrefixStabilizer();
        var digestA = AttachmentDigestFormatter.FormatTaskAttachmentsDigest([attA]);
        var digestB = AttachmentDigestFormatter.FormatTaskAttachmentsDigest([attB]);
        var layersA = stabilizer.BuildLayers(
            "Stage persona",
            "<task id=\"1\" />",
            projectGuidelines: "Use clean tests.",
            skillsCatalog: "<skills />",
            flags: new LocalOptimizationFlags(),
            workingDirectory: "/workspace",
            backlogDigest: "<existing_workspace_tasks />",
            taskAttachmentsDigest: digestA);
        var layersB = stabilizer.BuildLayers(
            "Stage persona",
            "<task id=\"2\" />",
            projectGuidelines: "Use clean tests.",
            skillsCatalog: "<skills />",
            flags: new LocalOptimizationFlags(),
            workingDirectory: "/workspace",
            backlogDigest: "<existing_workspace_tasks />",
            taskAttachmentsDigest: digestB);

        Assert.Equal(layersA.Layer1GlobalRules, layersB.Layer1GlobalRules);
        Assert.Equal(layersA.Layer2StagePersona, layersB.Layer2StagePersona);
        Assert.DoesNotContain("<task_attachments>", layersA.Layer1GlobalRules);
        Assert.DoesNotContain("<task_attachments>", layersA.Layer2StagePersona);
        Assert.Contains("<task_attachments>", layersA.Layer3TaskScope);
        Assert.Contains("/workspace/attachments/1-auth.pdf.md", layersA.Layer3TaskScope);
        Assert.Contains("/workspace/attachments/2-ui.png", layersB.Layer3TaskScope);

        // 3. Verify CrucibleAgentHarness injects <task_attachments> in Stage 1+, excludes in Stage 0 Triage
        var task1 = await taskRepo.CreateAsync(new ForgeTask
        {
            WorkspaceId = workspace.Id,
            Title = "Task One",
            Description = $"Implement @attachment:{attA.Id}"
        });
        await attachmentRepo.SetTaskAttachmentLinksAsync(task1.Id, [attA.Id]);

        var task2 = await taskRepo.CreateAsync(new ForgeTask
        {
            WorkspaceId = workspace.Id,
            Title = "Task Two",
            Description = $"Design @attachment:{attB.Id}"
        });
        await attachmentRepo.SetTaskAttachmentLinksAsync(task2.Id, [attB.Id]);

        var capturedHarnessRequests = new List<HarnessAgentRequest>();
        var stageFactoryMock = CreateCapturingHarnessAgentFactory(req => capturedHarnessRequests.Add(req));
        var harnessBuilder = new TestCrucibleHarnessBuilder
        {
            PathResolver = pathResolver,
            TaskRepository = taskRepo
        };
        var coordinator = new CrucibleConversationCoordinator(
            taskRepo,
            harnessBuilder.TranscriptService,
            Mock.Of<ILlmGatewayService>(),
            NullLogger<CrucibleConversationCoordinator>.Instance);

        var crucibleHarness = new CrucibleAgentHarness(
            new CanonicalToolsetFactory(
                pathResolver,
                harnessBuilder.EnvProvider,
                harnessBuilder.EditorConfigService,
                harnessBuilder.EditorConfigFileApplier,
                harnessBuilder.WebSearchService,
                harnessBuilder.SkillsServiceFactory,
                harnessBuilder.McpManager,
                backlogContext,
                NightCodeForge.Core.Workflow.Filtering.CliOutputFilterRegistry.Default),
            workspaceContext,
            stageFactoryMock.Object,
            coordinator,
            harnessBuilder.ActivityStreamer,
            harnessBuilder.CancellationManager,
            harnessBuilder.OperationsControlService,
            Options.Create(new LlmRetryPolicyOptions()),
            NullLogger<CrucibleAgentHarness>.Instance,
            TimeProvider.System);

        var implProfile = new AgentProfile
        {
            Id = "impl",
            Name = "Impl",
            ModelName = "test-model",
            PermittedTools = DefaultAgentTools.Full,
            Mode = AgentMode.Full,
            OptimizationFlags = new LocalOptimizationFlags()
        };

        await crucibleHarness.RunStageAsync(new AgentSessionContext
        {
            ForgeTask = task1,
            Workspace = workspace,
            Profile = implProfile,
            StageId = StandardStageIds.Implementation,
            StageDirective = "Implement carefully.",
            RequiredMode = AgentMode.Full,
            BootstrapPrompt = "Start",
            WorkspacePath = repoDir
        });

        await crucibleHarness.RunStageAsync(new AgentSessionContext
        {
            ForgeTask = task2,
            Workspace = workspace,
            Profile = implProfile,
            StageId = StandardStageIds.Implementation,
            StageDirective = "Implement carefully.",
            RequiredMode = AgentMode.Full,
            BootstrapPrompt = "Start",
            WorkspacePath = repoDir
        });

        var inst1 = capturedHarnessRequests[0].Instructions;
        var inst2 = capturedHarnessRequests[1].Instructions;

        var prefixBeforeTaskScope1 = inst1[..inst1.IndexOf("<task_scope>", StringComparison.Ordinal)];
        var prefixBeforeTaskScope2 = inst2[..inst2.IndexOf("<task_scope>", StringComparison.Ordinal)];
        Assert.Equal(prefixBeforeTaskScope1, prefixBeforeTaskScope2);

        var layer3Part1 = inst1[inst1.IndexOf("<task_scope>", StringComparison.Ordinal)..];
        var layer3Part2 = inst2[inst2.IndexOf("<task_scope>", StringComparison.Ordinal)..];
        Assert.Contains("<task_attachments>", layer3Part1);
        Assert.Contains("/workspace/attachments/1-auth.pdf.md", layer3Part1);
        Assert.DoesNotContain("2-ui.png", layer3Part1);
        Assert.Contains("<task_attachments>", layer3Part2);
        Assert.Contains("/workspace/attachments/2-ui.png", layer3Part2);
        Assert.DoesNotContain("1-auth.pdf.md", layer3Part2);

        // Stage 0 Triage never injects <task_attachments>
        await crucibleHarness.RunStageAsync(new AgentSessionContext
        {
            ForgeTask = task1,
            Workspace = workspace,
            Profile = implProfile,
            StageId = StandardStageIds.Triage,
            StageDirective = "Triage safely.",
            RequiredMode = AgentMode.ReadOnly,
            BootstrapPrompt = "Triage",
            WorkspacePath = repoDir
        });

        Assert.DoesNotContain("<task_attachments>", capturedHarnessRequests[2].Instructions);
    }

    [Fact]
    public async Task CitationLinking_PersistsAndValidatesAcrossTools_And_MarkdownSync_FailingFastOnMissingOrForeignIds()
    {
        var (repoDir, _) = CreateWorkspaceDirectories();
        var taskRepo = new InMemoryTaskRepository();
        var workspaceRepo = new InMemoryWorkspaceRepository();
        var attachmentRepo = new InMemoryTaskAttachmentRepository(taskRepo);

        var ws1 = await workspaceRepo.CreateAsync(CreateWorkspace(1, repoDir));
        var ws2 = await workspaceRepo.CreateAsync(CreateWorkspace(2, Path.Combine(_tempRoot, "other-ws")));

        var att1 = await attachmentRepo.CreateAsync(CreateDomainAttachment(ws1.Id, "Spec One", "1-spec.md", null, TaskAttachmentContentKind.PlainText));
        var att2 = await attachmentRepo.CreateAsync(CreateDomainAttachment(ws1.Id, "Spec Two", "2-spec.md", null, TaskAttachmentContentKind.PlainText));
        var foreignAtt = await attachmentRepo.CreateAsync(CreateDomainAttachment(ws2.Id, "Foreign Spec", "1-foreign.md", null, TaskAttachmentContentKind.PlainText));

        var pathResolver = new WorkspacePathResolver(
            Mock.Of<IWslMetadataService>(),
            Options.Create(new ExecutionEnvironmentProviderOptions()),
            isWindowsOverride: false);

        var (_, serviceProvider) = BuildRealBacklogContext(taskRepo, attachmentRepo, workspaceRepo, pathResolver, repoDir);
        var mutationService = serviceProvider.GetRequiredService<IBacklogMutationToolService>();
        var syncService = serviceProvider.GetRequiredService<ITaskMarkdownSyncService>();
        var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();

        var backlogModule = new BacklogManagementToolModule(
            new CanonicalToolScope(repoDir, WorkspaceId: ws1.Id, IsReadOnly: true),
            scopeFactory,
            attachBacklogMutationService: true);

        // 1. Single proposal combining explicit attachment_ids and @attachment citation in description
        var proposedJson = await backlogModule.ProposeTasksAsync(
            title: "Build Auth API",
            description: $"Implement per @attachment:{att1.Id} specification.",
            attachmentIds: [att2.Id]);

        using var doc = JsonDocument.Parse(proposedJson);
        var createdId = doc.RootElement.GetProperty("id").GetInt32();
        var linked = await attachmentRepo.GetByTaskIdAsync(createdId);
        Assert.Equal([att1.Id, att2.Id], linked.Select(a => a.Id).OrderBy(id => id).ToArray());

        // Markdown emitted to disk contains attachment_ids
        var taskEntity = await taskRepo.GetByIdAsync(createdId);
        Assert.NotNull(taskEntity);
        var emittedMd = syncService.GenerateMarkdown(taskEntity!);
        Assert.Contains("attachment_ids:", emittedMd);

        // 2. EditTask unlinks citations removed from description while keeping explicitly linked attachments, and updates when explicit attachmentIds provided
        await backlogModule.EditTaskAsync(
            taskId: createdId,
            description: "Description with citation removed");

        var afterCitationRemoval = await attachmentRepo.GetByTaskIdAsync(createdId);
        Assert.Equal([att2.Id], afterCitationRemoval.Select(a => a.Id).ToArray());

        await backlogModule.EditTaskAsync(
            taskId: createdId,
            description: $"Updated with @attachment:{att1.Id}",
            attachmentIds: Array.Empty<int>());

        var afterEdit = await attachmentRepo.GetByTaskIdAsync(createdId);
        Assert.Equal([att1.Id], afterEdit.Select(a => a.Id).ToArray());

        // SyncAllTasksToDiskAsync resolves persisted links from ITaskAttachmentRepository even if in-memory AttachmentLinks is empty
        (await taskRepo.GetByIdAsync(createdId))!.AttachmentLinks = new List<TaskAttachmentLink>();
        var detachedSyncDir = Path.Combine(_tempRoot, "sync-all-ws");
        Directory.CreateDirectory(detachedSyncDir);
        var syncAllResult = await syncService.SyncAllTasksToDiskAsync(ws1, detachedSyncDir);
        var syncedAllContent = await File.ReadAllTextAsync(syncAllResult.WrittenPaths[createdId]);
        Assert.Contains("attachment_ids:", syncedAllContent);
        Assert.Contains($"- {att1.Id}", syncedAllContent);

        await Assert.ThrowsAsync<AttachmentNotFoundException>(() =>
            backlogModule.ProposeTasksAsync(
                title: "Bad attachment cite",
                description: "Points to @attachment:99999"));

        await Assert.ThrowsAsync<AttachmentNotFoundException>(() =>
            backlogModule.ProposeTasksAsync(
                title: "Cross-workspace attachment",
                description: $"Foreign @attachment:{foreignAtt.Id}"));

        // Batch JSON array parsing with attachment_ids, top-level fallback, and invalid element rejection
        var batchJsonPayload = JsonSerializer.Serialize(new[]
        {
            new
            {
                title = "Batch Task With Attachment",
                description = "Follows spec.",
                attachment_ids = new[] { att2.Id }
            }
        });
        var batchResult = await backlogModule.ProposeTasksAsync(tasks: batchJsonPayload, attachmentIds: [att1.Id]);
        using var batchDoc = JsonDocument.Parse(batchResult);
        var batchCreatedId = batchDoc.RootElement.GetProperty("id").GetInt32();
        var batchLinks = await attachmentRepo.GetByTaskIdAsync(batchCreatedId);
        Assert.Equal([att1.Id, att2.Id], batchLinks.Select(a => a.Id).OrderBy(id => id).ToArray());

        var invalidBatchPayload = """[{"title":"Invalid Batch Att","description":"Bad array","attachment_ids":["not-an-id"]}]""";
        await Assert.ThrowsAsync<AttachmentNotFoundException>(() =>
            backlogModule.ProposeTasksAsync(tasks: invalidBatchPayload));

        // 3. Markdown sync disk -> DB parses attachment_ids YAML and @attachment citations, rejects missing/foreign, and unlinks removed citations
        var tasksDir = Path.Combine(repoDir, "docs", "tasks");
        Directory.CreateDirectory(tasksDir);
        var validDiskFile = Path.Combine(tasksDir, "disk-valid.md");
        await File.WriteAllTextAsync(validDiskFile, $"""
            ---
            title: Disk Synced Feature
            attachment_ids:
              - {att2.Id}
            ---
            # Disk Synced Feature

            ## 📋 Requirements & Description
            Read @attachment:{att1.Id} before modifying auth.
            """);

        await syncService.ProcessFileContentAsync(validDiskFile, await File.ReadAllTextAsync(validDiskFile), ws1.Id);
        var syncedTask = (await taskRepo.GetByWorkspaceIdAsync(ws1.Id)).Single(t => t.Title == "Disk Synced Feature");
        var syncedLinks = await attachmentRepo.GetByTaskIdAsync(syncedTask.Id);
        Assert.Equal([att1.Id, att2.Id], syncedLinks.Select(a => a.Id).OrderBy(id => id).ToArray());

        var editedDiskContent = $"""
            ---
            id: {syncedTask.Id}
            title: Disk Synced Feature
            ---
            # Disk Synced Feature

            ## 📋 Requirements & Description
            Citation removed; no attachment_ids present.
            """;
        await File.WriteAllTextAsync(validDiskFile, editedDiskContent);
        await syncService.ProcessFileContentAsync(validDiskFile, editedDiskContent, ws1.Id);
        var afterRemovingCitation = await attachmentRepo.GetByTaskIdAsync(syncedTask.Id);
        Assert.Equal([att2.Id], afterRemovingCitation.Select(a => a.Id).ToArray());

        var clearedAttachmentsDiskContent = $"""
            ---
            id: {syncedTask.Id}
            title: Disk Synced Feature
            attachment_ids: []
            ---
            # Disk Synced Feature

            ## 📋 Requirements & Description
            Explicit attachment_ids cleared.
            """;
        await File.WriteAllTextAsync(validDiskFile, clearedAttachmentsDiskContent);
        await syncService.ProcessFileContentAsync(validDiskFile, clearedAttachmentsDiskContent, ws1.Id);
        var unlinkedAfterClearingExplicit = await attachmentRepo.GetByTaskIdAsync(syncedTask.Id);
        Assert.Empty(unlinkedAfterClearingExplicit);

        var invalidDiskFile = Path.Combine(tasksDir, "disk-invalid.md");
        var badDiskContent = """
            ---
            title: Invalid Attachment Markdown
            ---
            # Invalid Attachment Markdown

            ## 📋 Requirements & Description
            Requires nonexistent @attachment:88888.
            """;
        await File.WriteAllTextAsync(invalidDiskFile, badDiskContent);

        await Assert.ThrowsAsync<AttachmentNotFoundException>(() =>
            syncService.ProcessFileContentAsync(invalidDiskFile, badDiskContent, ws1.Id));
    }

    [Fact]
    public async Task EndToEnd_AttachmentIntake_IdeationCitationProposal_And_Stage1Layer3AndCanonicalRead()
    {
        var wslRoot = Path.Combine(_tempRoot, "wsl-home");
        var repoDir = Path.Combine(wslRoot, ".nightcodeforge", "wks", "1", "clone");
        var attachDir = Path.Combine(wslRoot, ".nightcodeforge", "wks", "1", "attachments");
        Directory.CreateDirectory(repoDir);
        Directory.CreateDirectory(attachDir);

        var pathResolverMock = new Mock<IWorkspacePathResolver>();
        pathResolverMock.Setup(p => p.GetAttachmentsPath(1)).Returns(attachDir);
        pathResolverMock.Setup(p => p.ToHostPath(It.IsAny<string>())).Returns<string>(s => s);
        pathResolverMock.Setup(p => p.GetWslDistributionName()).Returns(string.Empty);
        pathResolverMock.Setup(p => p.GetWslUserName()).Returns("tester");
        pathResolverMock.Setup(p => p.ResolveCruciblePath(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int?>()))
            .Returns<int, string, int?>((_, rel, _) => ResolveTestCruciblePath(repoDir, attachDir, rel));
        pathResolverMock.Setup(p => p.ResolveCruciblePath(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int?>()))
            .Returns<string, string, int?>((_, rel, _) => ResolveTestCruciblePath(repoDir, attachDir, rel));

        var taskRepo = new InMemoryTaskRepository();
        var workspaceRepo = new InMemoryWorkspaceRepository();
        var attachmentRepo = new InMemoryTaskAttachmentRepository(taskRepo);
        var workspace = await workspaceRepo.CreateAsync(CreateWorkspace(1, repoDir));

        var intakeService = new AttachmentIntakeService(
            attachmentRepo,
            workspaceRepo,
            pathResolverMock.Object,
            Mock.Of<IProcessRunner>(),
            Mock.Of<Pmad.Markitdown.IMarkitdown>(),
            new HttpClient(),
            Options.Create(new SecurityOptions()),
            Options.Create(new AttachmentIntakeOptions()),
            NullLogger<AttachmentIntakeService>.Instance);

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("# Design Contract\nMust support token refresh."));
        var ingested = await intakeService.IngestFileDropAsync(workspace.Id, "design-contract.md", stream);

        var (backlogContext, serviceProvider) = BuildRealBacklogContext(taskRepo, attachmentRepo, workspaceRepo, pathResolverMock.Object, repoDir);
        var workspaceContext = new AgentWorkspaceContextService(
            pathResolverMock.Object,
            new AgentsMdService(),
            Mock.Of<IWorkspaceSymbolService>(),
            serviceProvider.GetRequiredService<ITaskMarkdownSyncService>(),
            backlogContext,
            new TranscriptAnalysisService(),
            new NightCodeForge.Agents.Tests.Fixtures.NoopAgentMemoryTurnTracker(), NullLogger<AgentWorkspaceContextService>.Instance);

        var capturedIdeationPrompts = new List<string>();
        var ideationProfile = new AgentProfile
        {
            Id = "ideation",
            Name = "Ideation",
            ModelName = "test-model",
            PermittedTools = DefaultAgentTools.Ideation,
            Mode = AgentMode.ReadOnly
        };
        var ideationFactory = CreateCapturingHarnessAgentFactory(req => capturedIdeationPrompts.Add(req.Instructions));

        using var ideationBuilder = new TestIdeationCoordinatorBuilder
        {
            PathResolver = pathResolverMock.Object,
            WorkspaceRepository = workspaceRepo,
            TaskRepository = taskRepo,
            BacklogContext = backlogContext,
            WorkspaceContextService = workspaceContext,
            HarnessAgentFactory = ideationFactory.Object,
            BaseChatClient = new StaticTextChatClient("Proposed task referencing attachment.")
        };
        var coordinator = ideationBuilder.Build();

        await ideationBuilder.ConverseAsync(coordinator, workspace, "Plan token refresh task", agentProfile: ideationProfile);
        Assert.Contains($"\"id\":{ingested.Id}", Assert.Single(capturedIdeationPrompts));

        // Agent proposes task citing @attachment:{id}
        var backlogModule = new BacklogManagementToolModule(
            new CanonicalToolScope(repoDir, WorkspaceId: workspace.Id, IsReadOnly: true),
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            attachBacklogMutationService: true);

        var proposalJson = await backlogModule.ProposeTasksAsync(
            title: "Implement token refresh",
            description: $"Follow @attachment:{ingested.Id} design contract.");
        using var proposedDoc = JsonDocument.Parse(proposalJson);
        var createdTaskId = proposedDoc.RootElement.GetProperty("id").GetInt32();
        var proposedTask = await taskRepo.GetByIdAsync(createdTaskId);
        Assert.NotNull(proposedTask);

        // Stage 1 CrucibleAgentHarness receives <task_attachments> in Layer 3 & agent can read the virtual path
        var capturedStageRequests = new List<HarnessAgentRequest>();
        var stageFactory = CreateCapturingHarnessAgentFactory(req => capturedStageRequests.Add(req));
        var harnessBuilder = new TestCrucibleHarnessBuilder
        {
            PathResolver = pathResolverMock.Object,
            TaskRepository = taskRepo
        };
        var crucibleHarness = new CrucibleAgentHarness(
            new CanonicalToolsetFactory(
                pathResolverMock.Object,
                harnessBuilder.EnvProvider,
                harnessBuilder.EditorConfigService,
                harnessBuilder.EditorConfigFileApplier,
                harnessBuilder.WebSearchService,
                harnessBuilder.SkillsServiceFactory,
                harnessBuilder.McpManager,
                backlogContext,
                NightCodeForge.Core.Workflow.Filtering.CliOutputFilterRegistry.Default),
            workspaceContext,
            stageFactory.Object,
            new CrucibleConversationCoordinator(
                taskRepo,
                harnessBuilder.TranscriptService,
                Mock.Of<ILlmGatewayService>(),
                NullLogger<CrucibleConversationCoordinator>.Instance),
            harnessBuilder.ActivityStreamer,
            harnessBuilder.CancellationManager,
            harnessBuilder.OperationsControlService,
            Options.Create(new LlmRetryPolicyOptions()),
            NullLogger<CrucibleAgentHarness>.Instance,
            TimeProvider.System);

        var implProfile = new AgentProfile
        {
            Id = "impl",
            Name = "Impl",
            ModelName = "test-model",
            PermittedTools = DefaultAgentTools.Full,
            Mode = AgentMode.Full,
            OptimizationFlags = new LocalOptimizationFlags()
        };

        await crucibleHarness.RunStageAsync(new AgentSessionContext
        {
            ForgeTask = proposedTask!,
            Workspace = workspace,
            Profile = implProfile,
            StageId = StandardStageIds.Implementation,
            StageDirective = "Implement carefully.",
            RequiredMode = AgentMode.Full,
            BootstrapPrompt = "Execute",
            WorkspacePath = repoDir
        });

        var stageRequest = Assert.Single(capturedStageRequests);
        var expectedVirtualPath = AttachmentDigestFormatter.ResolveVirtualAttachmentPath(ingested);
        Assert.Contains("<task_attachments>", stageRequest.Instructions);
        Assert.Contains(expectedVirtualPath, stageRequest.Instructions);

        // Agent passes virtual path from <task_attachments> straight to canonical read tool
        var readTool = CreateFileModule(repoDir, attachDir, workspaceId: workspace.Id);
        var readResult = await readTool.ReadAsync(expectedVirtualPath);
        Assert.Contains("Must support token refresh.", readResult);
    }

    private (string RepoDir, string AttachmentsDir) CreateWorkspaceDirectories()
    {
        var wks = Path.Combine(_tempRoot, "wks-" + Guid.NewGuid().ToString("N"));
        var repoDir = Path.Combine(wks, "clone");
        var attachDir = Path.Combine(wks, AttachmentConstants.AttachmentsDirectoryName);
        Directory.CreateDirectory(repoDir);
        Directory.CreateDirectory(attachDir);
        return (repoDir, attachDir);
    }

    private static Workspace CreateWorkspace(int id, string repoDir) => new()
    {
        Id = id,
        Name = $"ws-{id}",
        RepositoryPath = repoDir,
        DefaultBaseBranch = "main",
        ExecutionMode = ExecutionMode.ContainerSandbox
    };

    private static TaskAttachment CreateDomainAttachment(
        int workspaceId,
        string title,
        string relPath,
        string? mdPath,
        TaskAttachmentContentKind kind,
        int? width = null,
        int? height = null) => new()
    {
        WorkspaceId = workspaceId,
        Title = title,
        Slug = title.ToLowerInvariant().Replace(' ', '-'),
        RelativePath = relPath,
        MarkdownPath = mdPath,
        ContentKind = kind,
        MediaType = kind == TaskAttachmentContentKind.Image ? "image/png" : "text/markdown",
        WidthPx = width,
        HeightPx = height,
        SourceKind = TaskAttachmentSourceKind.FileDrop,
        SourceRef = relPath,
        IsSymlink = false,
        Sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(relPath))).ToLowerInvariant(),
        SizeBytes = 64,
        CreatedAt = DateTime.UtcNow
    };

    private static SandboxPathResolver CreateSandboxResolver(
        string repoDir,
        string attachmentsDir,
        SandboxSecurityTier tier = SandboxSecurityTier.Standard,
        SandboxMountTable? mountTable = null)
    {
        var table = mountTable ?? SandboxMountTable.ForWorkspace(
            repoDir,
            isReadOnly: tier == SandboxSecurityTier.RestrictedReadOnly,
            hostAttachmentsDirectory: attachmentsDir);
        var scope = SandboxScopeDescriptor.ForWorkspace(CreateWorkspace(1, repoDir), tier, taskId: null, table);
        return new SandboxPathResolver(scope);
    }

    private static WorkspaceFileToolModule CreateFileModule(
        string repoDir,
        string attachmentsDir,
        int workspaceId,
        bool isReadOnly = true,
        IServiceScopeFactory? scopeFactory = null)
    {
        var resolver = CreateSandboxResolver(
            repoDir,
            attachmentsDir,
            isReadOnly ? SandboxSecurityTier.RestrictedReadOnly : SandboxSecurityTier.Standard);
        var scope = new CanonicalToolScope(repoDir, WorkspaceId: workspaceId, IsReadOnly: isReadOnly, SandboxPathResolver: resolver);
        return scopeFactory != null
            ? new WorkspaceFileToolModule(scope, resolver, new EditorConfigService(), new EditorConfigFileApplier(), scopeFactory, NullLogger.Instance)
            : new WorkspaceFileToolModule(scope, resolver, new EditorConfigService(), new EditorConfigFileApplier(), NullLogger.Instance);
    }

    private static (IBacklogPlanningContext Context, ServiceProvider Provider) BuildRealBacklogContext(
        InMemoryTaskRepository taskRepo,
        InMemoryTaskAttachmentRepository attachmentRepo,
        InMemoryWorkspaceRepository workspaceRepo,
        IWorkspacePathResolver pathResolver,
        string repoDir)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITaskRepository>(taskRepo);
        services.AddSingleton<ITaskAttachmentRepository>(attachmentRepo);
        services.AddSingleton<IWorkspaceRepository>(workspaceRepo);
        services.AddSingleton(pathResolver);
        services.AddSingleton<ITaskDependencyStore, InMemoryTaskDependencyStore>();
        services.AddSingleton<IDependencyResolutionEngine>(sp =>
            new DependencyResolutionEngine(sp.GetRequiredService<ITaskDependencyStore>()));
        services.AddSingleton<IWorkspaceBacklogSummaryService>(sp =>
            new WorkspaceBacklogSummaryService(sp.GetRequiredService<ITaskRepository>(), new WorkspaceBacklogDigestOptions()));
        services.AddSingleton<ITaskMarkdownValidator>(sp =>
            new TaskMarkdownValidator(
                sp.GetRequiredService<ITaskRepository>(),
                sp.GetRequiredService<IDependencyResolutionEngine>(),
                NullLogger<TaskMarkdownValidator>.Instance));
        services.AddSingleton<ITaskMarkdownSyncService>(sp =>
            new TaskMarkdownSyncService(
                sp.GetRequiredService<IServiceScopeFactory>(),
                sp.GetRequiredService<IWorkspacePathResolver>(),
                NullLogger<TaskMarkdownSyncService>.Instance));
        services.AddSingleton<IWorkspaceLockProvider, WorkspaceLockProvider>();
        services.AddSingleton<IAgentActivityStreamer>(Mock.Of<IAgentActivityStreamer>());
        services.AddSingleton<IGitHubPullRequestDeliveryService>(Mock.Of<IGitHubPullRequestDeliveryService>());
        services.AddSingleton(Options.Create(new SecurityOptions()));
        services.AddSingleton<IBacklogMergeModeValidator>(sp =>
            new BacklogMergeModeValidator(
                sp.GetRequiredService<IWorkspaceRepository>(),
                sp.GetRequiredService<IGitHubPullRequestDeliveryService>(),
                sp.GetRequiredService<IOptions<SecurityOptions>>()));
        services.AddSingleton<ITaskLifecycleMutationCoordinator>(Mock.Of<ITaskLifecycleMutationCoordinator>());
        services.AddSingleton<IBacklogMutationToolService>(sp =>
            new BacklogMutationToolService(
                sp.GetRequiredService<ITaskRepository>(),
                sp.GetRequiredService<IDependencyResolutionEngine>(),
                sp.GetRequiredService<ITaskMarkdownSyncService>(),
                sp.GetRequiredService<IWorkspaceLockProvider>(),
                sp.GetRequiredService<IAgentActivityStreamer>(),
                sp.GetRequiredService<IBacklogMergeModeValidator>(),
                sp.GetRequiredService<ITaskAttachmentRepository>(),
                sp.GetRequiredService<ITaskLifecycleMutationCoordinator>(),
                NullLogger<BacklogMutationToolService>.Instance));

        var provider = services.BuildServiceProvider();
        var context = new BacklogPlanningContext(
            provider.GetRequiredService<IWorkspaceBacklogSummaryService>(),
            provider.GetRequiredService<ITaskMarkdownValidator>(),
            provider.GetRequiredService<IBacklogMutationToolService>(),
            NullLogger<CanonicalTools>.Instance,
            provider.GetRequiredService<IServiceScopeFactory>());

        return (context, provider);
    }

    private static Mock<IHarnessAgentFactory> CreateCapturingHarnessAgentFactory(Action<HarnessAgentRequest> onRequest)
    {
        var factoryMock = new Mock<IHarnessAgentFactory>();
        factoryMock.Setup(f => f.ResolveOptimizationFlags(It.IsAny<AgentProfile>(), It.IsAny<LocalOptimizationFlags?>()))
            .Returns((AgentProfile p, LocalOptimizationFlags? f) => f ?? p.OptimizationFlags ?? new LocalOptimizationFlags());
        factoryMock.Setup(f => f.CreateAsync(It.IsAny<HarnessAgentRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((HarnessAgentRequest req, CancellationToken _) =>
            {
                onRequest(req);
                var baseClient = new StaticTextChatClient("Completed step cleanly.");
                var tracingClient = req.CreateTracingClient(baseClient);
                var options = new HarnessAgentOptions
                {
                    ChatOptions = new ChatOptions { ModelId = req.Profile.ModelName, Tools = req.Tools },
                    HarnessInstructions = req.Instructions,
                    DisableCompaction = true,
                    DisableFileMemory = true,
                    DisableWebSearch = true,
                    DisableTodoProvider = true,
                    DisableAgentModeProvider = true,
                    MaximumIterationsPerRequest = req.MaxTurns
                };
                var agent = new HarnessAgent(tracingClient, options, NullLoggerFactory.Instance, services: null);
                return new HarnessAgentSetup(baseClient, tracingClient, tracingClient, options, agent, req.Profile.ModelName);
            });
        return factoryMock;
    }

    private static string ResolveTestCruciblePath(string repoDir, string attachDir, string relativeOrContainerPath)
    {
        var norm = (relativeOrContainerPath ?? string.Empty).Replace('\\', '/').Trim();
        if (norm.Equals(AttachmentConstants.VirtualAttachmentsMountPath, StringComparison.OrdinalIgnoreCase))
        {
            return attachDir;
        }
        if (norm.StartsWith(AttachmentConstants.VirtualAttachmentsMountPath + "/", StringComparison.OrdinalIgnoreCase))
        {
            var rel = norm[(AttachmentConstants.VirtualAttachmentsMountPath.Length + 1)..];
            return Path.Combine(attachDir, rel.Replace('/', Path.DirectorySeparatorChar));
        }
        if (norm.StartsWith("/workspace/", StringComparison.OrdinalIgnoreCase))
        {
            var rel = norm["/workspace/".Length..];
            return Path.Combine(repoDir, rel.Replace('/', Path.DirectorySeparatorChar));
        }
        if (norm.Equals("/workspace", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(norm))
        {
            return repoDir;
        }
        return Path.Combine(repoDir, norm.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
    }

    private sealed class StaticTextChatClient(string responseText) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, responseText)));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, responseText);
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
