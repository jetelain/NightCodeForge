using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Execution;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Infrastructure.Git;
using NightCodeForge.Infrastructure.Templates;

namespace NightCodeForge.Infrastructure.Tests.Templates;

public class WorkspaceTemplateScaffoldingIntegrationTests : IDisposable
{
    private readonly string _tempRoot;

    public WorkspaceTemplateScaffoldingIntegrationTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "NCF_TemplateIntTests_" + Guid.NewGuid().ToString("N"));
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
    public async Task ScaffoldWorkspaceAsync_DotNetClassLib_CreatesCleanArchitectureGitRepoAndBuildsCleanly()
    {
        var processRunner = new SystemProcessRunner();
        var wslMetadataMock = new Mock<IWslMetadataService>();
        wslMetadataMock.Setup(w => w.GetDefaultDistributionName()).Returns("Ubuntu");
        var gitCliRunner = new GitCliRunner(
            processRunner,
            new Mock<IWorkspacePathResolver>().Object,
            wslMetadataMock.Object,
            NullLogger<GitCliRunner>.Instance);

        var discoveryMock = new Mock<ILocalRepositoryDiscoveryService>();
        discoveryMock.Setup(d => d.GetDiscoveredRepositoriesAsync(true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<DiscoveredRepository>());

        var service = new WorkspaceTemplateService(
            processRunner,
            gitCliRunner,
            new Mock<IVcsHostingProviderRegistry>().Object,
            discoveryMock.Object,
            NullLogger<WorkspaceTemplateService>.Instance);

        var projectName = "SampleKernel";
        var targetDir = Path.Combine(_tempRoot, projectName);

        var result = await service.ScaffoldWorkspaceAsync(new TemplateScaffoldingRequest(
            TemplateId: WorkspaceTemplateConstants.DotNetClassLibId,
            ProjectName: projectName,
            TargetDirectoryPath: targetDir,
            InitializeGit: true,
            PublishToRemoteForge: false,
            RemoteProviderType: null,
            RemoteIsPrivate: true,
            RemoteDescription: null));

        Assert.True(result.LocalScaffoldCompleted);
        Assert.True(result.GitInitialized);
        Assert.Null(result.PublishedRepository);

        Assert.True(File.Exists(Path.Combine(targetDir, $"{projectName}.sln")));
        Assert.True(File.Exists(Path.Combine(targetDir, "src", projectName, $"{projectName}.csproj")));
        Assert.True(File.Exists(Path.Combine(targetDir, "tests", $"{projectName}.Tests", $"{projectName}.Tests.csproj")));
        Assert.True(File.Exists(Path.Combine(targetDir, "docs", "ARCHITECTURE.md")));
        Assert.True(File.Exists(Path.Combine(targetDir, "docs", "tasks", ".gitkeep")));
        Assert.True(File.Exists(Path.Combine(targetDir, ".gitignore")));
        Assert.True(File.Exists(Path.Combine(targetDir, ".editorconfig")));
        Assert.True(File.Exists(Path.Combine(targetDir, ".gitattributes")));
        Assert.True(File.Exists(Path.Combine(targetDir, "README.md")));
        Assert.True(Directory.Exists(Path.Combine(targetDir, ".git")));

        var branchResult = await gitCliRunner.ExecuteAsync(targetDir, ["branch", "--show-current"]);
        Assert.Equal("main", branchResult.StandardOutput.Trim());

        var statusResult = await gitCliRunner.ExecuteAsync(targetDir, ["status", "--porcelain"]);
        Assert.Equal(string.Empty, statusResult.StandardOutput.Trim());

        var logResult = await gitCliRunner.ExecuteAsync(targetDir, ["log", "-1", "--pretty=%s"]);
        Assert.Contains("NightCodeForge Class Library template", logResult.StandardOutput);

        var buildResult = await processRunner.RunAsync(new ProcessStartSpec
        {
            FileName = "dotnet",
            ArgumentList = ["build", $"{projectName}.sln", "--disable-build-servers", "-v", "minimal"],
            WorkingDirectory = targetDir,
            TimeoutSeconds = 120,
            EnvironmentVariables = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["MSBUILDDISABLENODEREUSE"] = "1"
            }
        });
        Assert.True(
            buildResult.ExitCode == 0,
            $"dotnet build failed with exit code {buildResult.ExitCode}.\nSTDOUT:\n{buildResult.StandardOutput}\nSTDERR:\n{buildResult.StandardError}");
    }
}
