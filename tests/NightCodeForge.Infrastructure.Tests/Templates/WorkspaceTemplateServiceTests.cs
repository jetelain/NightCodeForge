using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Services;
using NightCodeForge.Infrastructure.Templates;

namespace NightCodeForge.Infrastructure.Tests.Templates;

public class WorkspaceTemplateServiceTests : IDisposable
{
    private readonly string _tempRoot;

    public WorkspaceTemplateServiceTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "NCF_TemplateSvcTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
            // Best-effort temp cleanup
        }
    }

    [Fact]
    public void GetAvailableTemplates_ReturnsAllFourBuiltInTemplates()
    {
        var service = CreateService();

        var templates = service.GetAvailableTemplates();

        Assert.Equal(4, templates.Count);
        Assert.NotNull(service.GetTemplate(WorkspaceTemplateConstants.DotNetCleanWebAppId));
        Assert.NotNull(service.GetTemplate(WorkspaceTemplateConstants.DotNetCleanWebApiId));
        Assert.NotNull(service.GetTemplate(WorkspaceTemplateConstants.DotNetCleanCliId));
        Assert.NotNull(service.GetTemplate(WorkspaceTemplateConstants.DotNetClassLibId));
    }

    [Theory]
    [InlineData(WorkspaceTemplateConstants.DotNetCleanWebAppId, 11, "Acme.Billing", "src/Acme.Billing.Web")]
    [InlineData(WorkspaceTemplateConstants.DotNetCleanWebApiId, 11, "OrdersApi", "src/OrdersApi.Api")]
    [InlineData(WorkspaceTemplateConstants.DotNetCleanCliId, 9, "forge-cli", "src/forge-cli.Cli")]
    [InlineData(WorkspaceTemplateConstants.DotNetClassLibId, 5, "Shared.Kernel", "src/Shared.Kernel")]
    public void RenderArguments_SubstitutesProjectNameAndUsesDeterministicSlnFormat(
        string templateId,
        int expectedCommandCount,
        string projectName,
        string expectedProject)
    {
        var service = CreateService();
        var template = service.GetTemplate(templateId);

        Assert.NotNull(template);
        Assert.Equal(expectedCommandCount, template.BootstrapCommands.Count);

        var firstArgs = WorkspaceTemplateCatalog.RenderArguments(template.BootstrapCommands[0], projectName);
        Assert.Equal(["new", "sln", "-n", projectName, "--format", "sln"], firstArgs);

        foreach (var cmd in template.BootstrapCommands)
        {
            var args = WorkspaceTemplateCatalog.RenderArguments(cmd, projectName);
            Assert.DoesNotContain(args, a => a.Contains(WorkspaceTemplateConstants.ProjectNamePlaceholder, StringComparison.Ordinal));
        }

        var resolvedProjects = WorkspaceTemplateCatalog.ResolveGeneratedProjects(template, projectName);
        Assert.Contains(expectedProject, resolvedProjects);
    }

    [Theory]
    [InlineData("AcmeBilling", true)]
    [InlineData("Acme.Billing", true)]
    [InlineData("my-new-app", true)]
    [InlineData("App_01", true)]
    [InlineData("", false)]
    [InlineData("123App", false)]
    [InlineData("my app", false)]
    [InlineData("foo/bar", false)]
    [InlineData("foo\\bar", false)]
    [InlineData("Acme..Billing", false)]
    [InlineData("Acme.", false)]
    [InlineData("CON", false)]
    [InlineData("nul.txt", false)]
    public void ProjectNameValidator_ValidatesExpectedPatterns(string candidate, bool expectedValid)
    {
        var result = WorkspaceTemplateProjectNameValidator.Validate(candidate);
        Assert.Equal(expectedValid, result.IsValid);
    }

    [Fact]
    public async Task ScaffoldWorkspaceAsync_UnknownTemplate_ThrowsWorkspaceTemplateExecutionException()
    {
        var service = CreateService();
        var targetDir = Path.Combine(_tempRoot, "unknown-template");

        await Assert.ThrowsAsync<WorkspaceTemplateExecutionException>(() =>
            service.ScaffoldWorkspaceAsync(new TemplateScaffoldingRequest(
                TemplateId: "does-not-exist",
                ProjectName: "MyApp",
                TargetDirectoryPath: targetDir,
                InitializeGit: true,
                PublishToRemoteForge: false,
                RemoteProviderType: null,
                RemoteIsPrivate: true,
                RemoteDescription: null)));
    }

    [Fact]
    public async Task ScaffoldWorkspaceAsync_NonEmptyTargetDirectory_FailsFastWithoutExecutingCli()
    {
        var processMock = new Mock<IProcessRunner>();
        var service = CreateService(processRunner: processMock);

        var nonEmptyDir = Path.Combine(_tempRoot, "existing-non-empty");
        Directory.CreateDirectory(nonEmptyDir);
        await File.WriteAllTextAsync(Path.Combine(nonEmptyDir, "keep.txt"), "occupied");

        await Assert.ThrowsAsync<WorkspaceTemplateExecutionException>(() =>
            service.ScaffoldWorkspaceAsync(new TemplateScaffoldingRequest(
                TemplateId: WorkspaceTemplateConstants.DotNetClassLibId,
                ProjectName: "MyLib",
                TargetDirectoryPath: nonEmptyDir,
                InitializeGit: true,
                PublishToRemoteForge: false,
                RemoteProviderType: null,
                RemoteIsPrivate: true,
                RemoteDescription: null)));

        processMock.Verify(
            p => p.RunAsync(It.IsAny<ProcessStartSpec>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ScaffoldWorkspaceAsync_CliFailure_ThrowsWorkspaceTemplateExecutionExceptionWithDiagnostics()
    {
        var processMock = new Mock<IProcessRunner>();
        processMock.Setup(p => p.RunAsync(It.IsAny<ProcessStartSpec>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult(ExitCode: 1, StandardOutput: string.Empty, StandardError: "SDK missing"));

        var service = CreateService(processRunner: processMock);
        var targetDir = Path.Combine(_tempRoot, "cli-fail");

        var ex = await Assert.ThrowsAsync<WorkspaceTemplateExecutionException>(() =>
            service.ScaffoldWorkspaceAsync(new TemplateScaffoldingRequest(
                TemplateId: WorkspaceTemplateConstants.DotNetClassLibId,
                ProjectName: "MyLib",
                TargetDirectoryPath: targetDir,
                InitializeGit: true,
                PublishToRemoteForge: false,
                RemoteProviderType: null,
                RemoteIsPrivate: true,
                RemoteDescription: null)));

        Assert.Equal(WorkspaceTemplateConstants.DotNetClassLibId, ex.TemplateId);
        Assert.Equal(1, ex.ExitCode);
        Assert.Contains("dotnet new sln", ex.FailedCommand);
        Assert.Contains("SDK missing", ex.Message);
    }

    [Fact]
    public async Task ScaffoldWorkspaceAsync_FullFlowWithRemotePublish_WritesHygieneCommitsAndPushesEphemerally()
    {
        var executedDotNetCommands = new List<string[]>();
        var processMock = new Mock<IProcessRunner>();
        processMock.Setup(p => p.RunAsync(It.IsAny<ProcessStartSpec>(), It.IsAny<CancellationToken>()))
            .Callback<ProcessStartSpec, CancellationToken>((spec, _) =>
            {
                executedDotNetCommands.Add(spec.ArgumentList?.ToArray() ?? []);
            })
            .ReturnsAsync(new ProcessExecutionResult(0, "ok", string.Empty));

        var executedGitCommands = new List<string[]>();
        var gitMock = new Mock<IGitCliRunner>();
        gitMock.Setup(g => g.ExecuteAsync(
                It.IsAny<string>(),
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<int?>(),
                It.IsAny<CancellationToken>()))
            .Returns<string, IEnumerable<string>, int?, CancellationToken>((_, args, _, _) =>
            {
                var arr = args.ToArray();
                executedGitCommands.Add(arr);
                if (arr.Length >= 3 && arr[0] == "config" && arr[1] == "--get")
                {
                    throw new GitCommandException(_tempRoot, arr, 1, string.Empty, string.Empty);
                }
                if (arr.Length >= 3 && arr[0] == "remote" && arr[1] == "get-url")
                {
                    throw new GitCommandException(_tempRoot, arr, 2, string.Empty, "No such remote 'origin'");
                }
                return Task.FromResult(new GitCommandResult(0, string.Empty, string.Empty, TimeSpan.FromMilliseconds(5)));
            });

        IReadOnlyDictionary<string, string>? pushEnv = null;
        string[]? pushArgs = null;
        gitMock.Setup(g => g.ExecuteAsync(
                It.IsAny<string>(),
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<int?>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, IEnumerable<string>, int?, IReadOnlyDictionary<string, string>?, CancellationToken>((_, args, _, env, _) =>
            {
                pushArgs = args.ToArray();
                pushEnv = env;
            })
            .ReturnsAsync(new GitCommandResult(0, string.Empty, string.Empty, TimeSpan.FromMilliseconds(5)));

        var providerMock = new Mock<IVcsHostingProvider>();
        providerMock.SetupGet(p => p.ProviderType).Returns(VcsHostingProviderType.GitHub);
        providerMock.SetupGet(p => p.DisplayName).Returns("GitHub");
        providerMock.Setup(p => p.IsAuthenticatedAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        providerMock.Setup(p => p.CreateRepositoryAsync("BillingApp", "Billing service", true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VcsRepositorySummary(
                Id: "42",
                Name: "BillingApp",
                FullName: "octocat/BillingApp",
                Owner: "octocat",
                Description: "Billing service",
                IsPrivate: true,
                DefaultBranch: "main",
                HttpsCloneUrl: "https://github.com/octocat/BillingApp.git",
                SshCloneUrl: "git@github.com:octocat/BillingApp.git",
                UpdatedAt: DateTimeOffset.UtcNow));
        providerMock.Setup(p => p.GetGitHttpAuthHeaderAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("Authorization: Basic Z2h1X3NlY3JldA==");

        var registryMock = new Mock<IVcsHostingProviderRegistry>();
        registryMock.Setup(r => r.GetProvider(VcsHostingProviderType.GitHub)).Returns(providerMock.Object);

        var discoveryMock = new Mock<ILocalRepositoryDiscoveryService>();
        discoveryMock.Setup(d => d.GetDiscoveredRepositoriesAsync(true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<DiscoveredRepository>());

        var service = CreateService(processMock, gitMock, registryMock, discoveryMock);
        var targetDir = Path.Combine(_tempRoot, "BillingApp");

        var reportedProgress = new List<TemplateScaffoldingProgress>();
        var progress = new SynchronousProgress<TemplateScaffoldingProgress>(p => reportedProgress.Add(p));

        var result = await service.ScaffoldWorkspaceAsync(
            new TemplateScaffoldingRequest(
                TemplateId: WorkspaceTemplateConstants.DotNetCleanWebApiId,
                ProjectName: "BillingApp",
                TargetDirectoryPath: targetDir,
                InitializeGit: true,
                PublishToRemoteForge: true,
                RemoteProviderType: VcsHostingProviderType.GitHub,
                RemoteIsPrivate: true,
                RemoteDescription: "Billing service",
                RemoteRepositoryName: "BillingApp"),
            progress);

        Assert.True(result.LocalScaffoldCompleted);
        Assert.True(result.GitInitialized);
        Assert.NotNull(result.PublishedRepository);
        Assert.Equal("octocat/BillingApp", result.PublishedRepository.FullName);
        Assert.Equal(11, executedDotNetCommands.Count);

        // Verify progress milestones and single discovery refresh
        Assert.NotEmpty(reportedProgress);
        Assert.Contains(reportedProgress, p => p.LocalScaffoldCompleted);
        var finalProgress = reportedProgress[^1];
        Assert.True(finalProgress.IsCompleted);
        Assert.Equal(100, finalProgress.Percentage);
        discoveryMock.Verify(d => d.GetDiscoveredRepositoriesAsync(true, It.IsAny<CancellationToken>()), Times.Once);

        // Verify hygiene files exist with LF line endings
        Assert.True(File.Exists(Path.Combine(targetDir, "docs", "ARCHITECTURE.md")));
        Assert.True(File.Exists(Path.Combine(targetDir, "docs", "tasks", ".gitkeep")));
        Assert.True(File.Exists(Path.Combine(targetDir, ".gitignore")));
        Assert.True(File.Exists(Path.Combine(targetDir, ".editorconfig")));
        Assert.True(File.Exists(Path.Combine(targetDir, ".gitattributes")));
        Assert.True(File.Exists(Path.Combine(targetDir, "README.md")));

        var gitignoreText = await File.ReadAllTextAsync(Path.Combine(targetDir, ".gitignore"));
        Assert.DoesNotContain("\r\n", gitignoreText);
        Assert.Contains("docs/tasks/", gitignoreText);

        var gitattributesText = await File.ReadAllTextAsync(Path.Combine(targetDir, ".gitattributes"));
        Assert.Equal("* text=auto eol=lf\n", gitattributesText);

        // Verify git init, autocrlf, identity, add, commit, remote add
        Assert.Contains(executedGitCommands, a => a.SequenceEqual(["init", "-b", "main"]));
        Assert.Contains(executedGitCommands, a => a.SequenceEqual(["config", "core.autocrlf", "false"]));
        Assert.Contains(executedGitCommands, a => a.SequenceEqual(["config", "user.name", "NightCodeForge"]));
        Assert.Contains(executedGitCommands, a => a.SequenceEqual(["config", "user.email", "forge@localhost"]));
        Assert.Contains(executedGitCommands, a => a.SequenceEqual(["add", "-A"]));
        Assert.Contains(executedGitCommands, a => a.Length == 3 && a[0] == "commit" && a[1] == "-m" && a[2].Contains("Web API"));
        Assert.Contains(executedGitCommands, a => a.SequenceEqual(["remote", "add", "origin", "https://github.com/octocat/BillingApp.git"]));

        // Verify ephemeral push header and GIT_TERMINAL_PROMPT=0
        Assert.NotNull(pushArgs);
        Assert.Equal(["-c", "http.extraHeader=Authorization: Basic Z2h1X3NlY3JldA==", "push", "-u", "origin", "main"], pushArgs);
        Assert.NotNull(pushEnv);
        Assert.Equal("0", pushEnv["GIT_TERMINAL_PROMPT"]);
    }

    [Fact]
    public async Task PublishToRemoteForgeAsync_UnauthenticatedProvider_ThrowsVcsHostingProviderException()
    {
        var targetDir = Path.Combine(_tempRoot, "unauth-repo");
        Directory.CreateDirectory(targetDir);

        var providerMock = new Mock<IVcsHostingProvider>();
        providerMock.SetupGet(p => p.ProviderType).Returns(VcsHostingProviderType.GitHub);
        providerMock.SetupGet(p => p.DisplayName).Returns("GitHub");
        providerMock.Setup(p => p.IsAuthenticatedAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var registryMock = new Mock<IVcsHostingProviderRegistry>();
        registryMock.Setup(r => r.GetProvider(VcsHostingProviderType.GitHub)).Returns(providerMock.Object);

        var service = CreateService(registry: registryMock);

        await Assert.ThrowsAsync<VcsHostingProviderException>(() =>
            service.PublishToRemoteForgeAsync(
                targetDir,
                VcsHostingProviderType.GitHub,
                "MyRepo",
                description: null,
                isPrivate: true));
    }

    [Fact]
    public async Task PublishToRemoteForgeAsync_WhenOriginAlreadyExists_UpdatesRemoteUrlAndRedactsPushError()
    {
        var targetDir = Path.Combine(_tempRoot, "retry-publish");
        Directory.CreateDirectory(targetDir);

        var executedGitCommands = new List<string[]>();
        var gitMock = new Mock<IGitCliRunner>();
        gitMock.Setup(g => g.ExecuteAsync(
                It.IsAny<string>(),
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<int?>(),
                It.IsAny<CancellationToken>()))
            .Returns<string, IEnumerable<string>, int?, CancellationToken>((_, args, _, _) =>
            {
                var arr = args.ToArray();
                executedGitCommands.Add(arr);
                return Task.FromResult(new GitCommandResult(0, "https://github.com/old/url.git", string.Empty, TimeSpan.Zero));
            });

        gitMock.Setup(g => g.ExecuteAsync(
                It.IsAny<string>(),
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<int?>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitCommandException(
                targetDir,
                ["-c", "http.extraHeader=Authorization: Basic ghu_1234567890", "push", "-u", "origin", "main"],
                128,
                string.Empty,
                "remote: Permission denied for ghu_1234567890"));

        var providerMock = new Mock<IVcsHostingProvider>();
        providerMock.SetupGet(p => p.ProviderType).Returns(VcsHostingProviderType.GitHub);
        providerMock.SetupGet(p => p.DisplayName).Returns("GitHub");
        providerMock.Setup(p => p.IsAuthenticatedAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        providerMock.Setup(p => p.CreateRepositoryAsync("MyRepo", null, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VcsRepositorySummary("9", "MyRepo", "octocat/MyRepo", "octocat", string.Empty, false, "main", "https://github.com/octocat/MyRepo.git", "git@github.com:octocat/MyRepo.git", DateTimeOffset.UtcNow));
        providerMock.Setup(p => p.GetGitHttpAuthHeaderAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("Authorization: Basic secret");

        var registryMock = new Mock<IVcsHostingProviderRegistry>();
        registryMock.Setup(r => r.GetProvider(VcsHostingProviderType.GitHub)).Returns(providerMock.Object);

        var service = CreateService(gitRunner: gitMock, registry: registryMock);

        var ex = await Assert.ThrowsAsync<VcsHostingProviderException>(() =>
            service.PublishToRemoteForgeAsync(
                targetDir,
                VcsHostingProviderType.GitHub,
                "MyRepo",
                description: null,
                isPrivate: false));

        Assert.Contains(executedGitCommands, a => a.SequenceEqual(["remote", "set-url", "origin", "https://github.com/octocat/MyRepo.git"]));
        Assert.DoesNotContain("ghu_1234567890", ex.Message);
    }

    [Fact]
    public async Task PublishToRemoteForgeAsync_WhenAuthHeaderUnavailable_ThrowsWithoutCreatingRepository()
    {
        var targetDir = Path.Combine(_tempRoot, "no-auth-header");
        Directory.CreateDirectory(targetDir);

        var providerMock = new Mock<IVcsHostingProvider>();
        providerMock.SetupGet(p => p.ProviderType).Returns(VcsHostingProviderType.GitHub);
        providerMock.SetupGet(p => p.DisplayName).Returns("GitHub");
        providerMock.Setup(p => p.IsAuthenticatedAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        providerMock.Setup(p => p.GetGitHttpAuthHeaderAsync(It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);

        var registryMock = new Mock<IVcsHostingProviderRegistry>();
        registryMock.Setup(r => r.GetProvider(VcsHostingProviderType.GitHub)).Returns(providerMock.Object);

        var service = CreateService(registry: registryMock);

        await Assert.ThrowsAsync<VcsHostingProviderException>(() =>
            service.PublishToRemoteForgeAsync(
                targetDir,
                VcsHostingProviderType.GitHub,
                "MyRepo",
                description: null,
                isPrivate: true));

        providerMock.Verify(
            p => p.CreateRepositoryAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Theory]
    [InlineData("ssh://github.com/octocat/MyRepo.git")]
    [InlineData("file:///tmp/repo")]
    [InlineData("not-a-valid-url")]
    public async Task PublishToRemoteForgeAsync_InvalidCloneUrlScheme_ThrowsVcsHostingProviderException(string invalidCloneUrl)
    {
        var targetDir = Path.Combine(_tempRoot, "invalid-clone-url");
        Directory.CreateDirectory(targetDir);

        var gitMock = new Mock<IGitCliRunner>();
        var providerMock = new Mock<IVcsHostingProvider>();
        providerMock.SetupGet(p => p.ProviderType).Returns(VcsHostingProviderType.GitHub);
        providerMock.SetupGet(p => p.DisplayName).Returns("GitHub");
        providerMock.Setup(p => p.IsAuthenticatedAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        providerMock.Setup(p => p.GetGitHttpAuthHeaderAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("Authorization: Basic secret");
        providerMock.Setup(p => p.CreateRepositoryAsync("MyRepo", null, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VcsRepositorySummary(
                "10",
                "MyRepo",
                "octocat/MyRepo",
                "octocat",
                string.Empty,
                true,
                "main",
                invalidCloneUrl,
                "git@github.com:octocat/MyRepo.git",
                DateTimeOffset.UtcNow));

        var registryMock = new Mock<IVcsHostingProviderRegistry>();
        registryMock.Setup(r => r.GetProvider(VcsHostingProviderType.GitHub)).Returns(providerMock.Object);

        var service = CreateService(gitRunner: gitMock, registry: registryMock);

        await Assert.ThrowsAsync<VcsHostingProviderException>(() =>
            service.PublishToRemoteForgeAsync(
                targetDir,
                VcsHostingProviderType.GitHub,
                "MyRepo",
                description: null,
                isPrivate: true));

        gitMock.Verify(
            g => g.ExecuteAsync(It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task PublishToRemoteForgeAsync_Success_RefreshesLocalDiscoveryCache()
    {
        var targetDir = Path.Combine(_tempRoot, "publish-refresh");
        Directory.CreateDirectory(targetDir);

        var gitMock = new Mock<IGitCliRunner>();
        gitMock.Setup(g => g.ExecuteAsync(
                It.IsAny<string>(),
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<int?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitCommandResult(0, string.Empty, string.Empty, TimeSpan.Zero));
        gitMock.Setup(g => g.ExecuteAsync(
                It.IsAny<string>(),
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<int?>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitCommandResult(0, string.Empty, string.Empty, TimeSpan.Zero));

        var providerMock = new Mock<IVcsHostingProvider>();
        providerMock.SetupGet(p => p.ProviderType).Returns(VcsHostingProviderType.GitHub);
        providerMock.SetupGet(p => p.DisplayName).Returns("GitHub");
        providerMock.Setup(p => p.IsAuthenticatedAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        providerMock.Setup(p => p.GetGitHttpAuthHeaderAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("Authorization: Basic secret");
        providerMock.Setup(p => p.CreateRepositoryAsync("MyRepo", null, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VcsRepositorySummary(
                "11",
                "MyRepo",
                "octocat/MyRepo",
                "octocat",
                string.Empty,
                true,
                "main",
                "https://github.com/octocat/MyRepo.git",
                "git@github.com:octocat/MyRepo.git",
                DateTimeOffset.UtcNow));

        var registryMock = new Mock<IVcsHostingProviderRegistry>();
        registryMock.Setup(r => r.GetProvider(VcsHostingProviderType.GitHub)).Returns(providerMock.Object);

        var discoveryMock = new Mock<ILocalRepositoryDiscoveryService>();
        discoveryMock.Setup(d => d.GetDiscoveredRepositoriesAsync(true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<DiscoveredRepository>());

        var service = CreateService(gitRunner: gitMock, registry: registryMock, discovery: discoveryMock);

        await service.PublishToRemoteForgeAsync(
            targetDir,
            VcsHostingProviderType.GitHub,
            "MyRepo",
            description: null,
            isPrivate: true);

        discoveryMock.Verify(d => d.GetDiscoveredRepositoriesAsync(true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ScaffoldWorkspaceAsync_WhenRemotePublishFailsAfterLocalScaffold_StillRefreshesLocalDiscoveryCache()
    {
        var processMock = new Mock<IProcessRunner>();
        processMock.Setup(p => p.RunAsync(It.IsAny<ProcessStartSpec>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult(0, "ok", string.Empty));

        var gitMock = new Mock<IGitCliRunner>();
        gitMock.Setup(g => g.ExecuteAsync(
                It.IsAny<string>(),
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<int?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitCommandResult(0, string.Empty, string.Empty, TimeSpan.Zero));

        var providerMock = new Mock<IVcsHostingProvider>();
        providerMock.SetupGet(p => p.ProviderType).Returns(VcsHostingProviderType.GitHub);
        providerMock.SetupGet(p => p.DisplayName).Returns("GitHub");
        providerMock.Setup(p => p.IsAuthenticatedAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        providerMock.Setup(p => p.GetGitHttpAuthHeaderAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("Authorization: Basic secret");
        providerMock.Setup(p => p.CreateRepositoryAsync("MyLib", null, true, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new VcsHostingProviderException(VcsHostingProviderType.GitHub, "Name already exists"));

        var registryMock = new Mock<IVcsHostingProviderRegistry>();
        registryMock.Setup(r => r.GetProvider(VcsHostingProviderType.GitHub)).Returns(providerMock.Object);

        var discoveryMock = new Mock<ILocalRepositoryDiscoveryService>();
        discoveryMock.Setup(d => d.GetDiscoveredRepositoriesAsync(true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<DiscoveredRepository>());

        var service = CreateService(processMock, gitMock, registryMock, discoveryMock);
        var targetDir = Path.Combine(_tempRoot, "partial-fail-refresh");

        await Assert.ThrowsAsync<VcsHostingProviderException>(() =>
            service.ScaffoldWorkspaceAsync(new TemplateScaffoldingRequest(
                TemplateId: WorkspaceTemplateConstants.DotNetClassLibId,
                ProjectName: "MyLib",
                TargetDirectoryPath: targetDir,
                InitializeGit: true,
                PublishToRemoteForge: true,
                RemoteProviderType: VcsHostingProviderType.GitHub,
                RemoteIsPrivate: true,
                RemoteDescription: null)));

        discoveryMock.Verify(d => d.GetDiscoveredRepositoriesAsync(true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("src/../../outside")]
    [InlineData("/etc")]
    public void ResolveCommandWorkingDirectory_TraversalOrRootedPath_ThrowsWorkspaceTemplateExecutionException(string relativeDir)
    {
        var root = Path.Combine(_tempRoot, "sandbox-root");

        Assert.Throws<WorkspaceTemplateExecutionException>(() =>
            WorkspaceTemplateService.ResolveCommandWorkingDirectory("test-template", root, relativeDir));
    }

    [Fact]
    public void ResolveCommandWorkingDirectory_ValidRelativeSubdirectory_ReturnsPathWithinRoot()
    {
        var root = Path.Combine(_tempRoot, "sandbox-root");

        var resolved = WorkspaceTemplateService.ResolveCommandWorkingDirectory("test-template", root, "src/App");

        Assert.Equal(Path.GetFullPath(Path.Combine(root, "src", "App")), resolved);
    }

    [Fact]
    public async Task ScaffoldWorkspaceAsync_TargetIsExistingFile_ThrowsWorkspaceTemplateExecutionException()
    {
        var filePath = Path.Combine(_tempRoot, "occupied-file.txt");
        await File.WriteAllTextAsync(filePath, "hello");
        var service = CreateService();

        await Assert.ThrowsAsync<WorkspaceTemplateExecutionException>(() =>
            service.ScaffoldWorkspaceAsync(new TemplateScaffoldingRequest(
                TemplateId: WorkspaceTemplateConstants.DotNetClassLibId,
                ProjectName: "MyLib",
                TargetDirectoryPath: filePath,
                InitializeGit: true,
                PublishToRemoteForge: false,
                RemoteProviderType: null,
                RemoteIsPrivate: true,
                RemoteDescription: null)));
    }

    private sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    private WorkspaceTemplateService CreateService(
        Mock<IProcessRunner>? processRunner = null,
        Mock<IGitCliRunner>? gitRunner = null,
        Mock<IVcsHostingProviderRegistry>? registry = null,
        Mock<ILocalRepositoryDiscoveryService>? discovery = null)
    {
        return new WorkspaceTemplateService(
            (processRunner ?? new Mock<IProcessRunner>()).Object,
            (gitRunner ?? new Mock<IGitCliRunner>()).Object,
            (registry ?? new Mock<IVcsHostingProviderRegistry>()).Object,
            (discovery ?? new Mock<ILocalRepositoryDiscoveryService>()).Object,
            NullLogger<WorkspaceTemplateService>.Instance);
    }
}
