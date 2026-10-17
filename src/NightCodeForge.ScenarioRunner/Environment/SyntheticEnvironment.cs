using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightCodeForge.Agents;
using NightCodeForge.Agents.Inference;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Infrastructure;
using NightCodeForge.Infrastructure.Data;
using NightCodeForge.Infrastructure.Inference;
using NightCodeForge.ScenarioRunner.Common;

namespace NightCodeForge.ScenarioRunner.Environment;

/// <summary>
/// Fully isolated, synthetic test environment containing an ephemeral Git repository, SQLite database,
/// and configured dependency injection container. Supports the '--keep' option for post-run inspection.
/// </summary>
public sealed class SyntheticEnvironment : IAsyncDisposable
{
    public string RootDirectory { get; }
    public string RepositoryPath { get; }
    public string DatabasePath { get; }
    public bool KeepOnExit { get; set; }
    public RunnerOptions Options { get; }
    public SyntheticGitRepo GitRepo { get; private set; } = null!;
    public ServiceProvider Services { get; private set; } = null!;
    public Workspace Workspace { get; private set; } = null!;
    public InMemoryTaskJobQueue JobQueue { get; private set; } = null!;
    public DynamicPipelineProvider PipelineProvider { get; private set; } = null!;

    public void SetCustomPipeline(IWorkflowPipeline? pipeline)
    {
        PipelineProvider.CustomPipeline = pipeline;
    }

    public SyntheticEnvironment(RunnerOptions options)
    {
        Options = options ?? throw new ArgumentNullException(nameof(options));
        KeepOnExit = options.Keep;

        var runId = Guid.NewGuid().ToString("N")[..8];
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        RootDirectory = Path.Combine(Path.GetTempPath(), $"NCF_Scenario_{timestamp}_{runId}");
        RepositoryPath = Path.Combine(RootDirectory, "clone");
        DatabasePath = Path.Combine(RootDirectory, "nightcodeforge.db");
    }

    /// <summary>
    /// Bootstraps the synthetic Git repository, initializes SQLite DB with WAL mode, and seeds workspace.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(RootDirectory);

        // 1. Initialize Synthetic Git Repository
        GitRepo = new SyntheticGitRepo(RepositoryPath);
        await GitRepo.InitializeAsync("main", cancellationToken);

        // 2. Build Dependency Injection Container
        var serviceCollection = new ServiceCollection();

        serviceCollection.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Warning);
        });

        // Dynamic Workflow Pipeline Provider
        PipelineProvider = new DynamicPipelineProvider();
        serviceCollection.AddSingleton(PipelineProvider);
        serviceCollection.AddSingleton<IWorkflowPipelineProvider>(PipelineProvider);

        // NightCodeForge Core & Infrastructure
        var connStr = $"Data Source={DatabasePath};";
        serviceCollection.AddNightCodeForgeCoreAndInfrastructure(connStr);

        // In-Memory Job Queue for inspection
        JobQueue = new InMemoryTaskJobQueue();
        serviceCollection.AddSingleton(JobQueue);
        serviceCollection.AddSingleton<ITaskJobQueue>(JobQueue);

        // Configure LLM inference
        if (Options.MockLlm)
        {
            var mockClient = new SyntheticMockChatClient();
            serviceCollection.AddSingleton<IChatClient>(mockClient);
            serviceCollection.AddSingleton<IOllamaChatClientFactory>(new SingleMockChatClientFactory(mockClient));
        }
        else
        {
            serviceCollection.Configure<LlmProviderOptions>(opt =>
            {
                opt.Endpoint = Options.Endpoint;
                opt.ModelId = Options.ModelId;
                opt.RequestTimeoutMinutes = Options.TimeoutMinutes;
            });

            serviceCollection.Configure<OllamaRouterOptions>(opt =>
            {
                opt.BaseUrl = Options.Endpoint;
                opt.TimeoutSeconds = 30;
            });
        }

        // NightCodeForge Agents
        serviceCollection.AddNightCodeForgeAgents();

        Services = serviceCollection.BuildServiceProvider();
        GitRepo.WslMetadataService = Services.GetRequiredService<IWslMetadataService>();

        // 3. Ensure Database Created with WAL Mode
        using (var scope = Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.EnsureDatabaseCreatedAndWalAsync(cancellationToken);

            // 4. Seed sqlite_sequence so generated Workspace IDs start at 1,000,000
            // This prevents ID and path collisions with real NightCodeForge workspaces (~/.nightcodeforge/wks/{id})
            const int baseWorkspaceId = 1_000_000;
            await db.Database.ExecuteSqlRawAsync(
                $"INSERT OR REPLACE INTO sqlite_sequence (name, seq) VALUES ('Workspaces', {baseWorkspaceId});",
                cancellationToken);

            Workspace = new Workspace
            {
                Name = "Synthetic Workspace",
                RepositoryPath = RepositoryPath,
                DefaultBaseBranch = "main",
                ExecutionMode = Options.UseContainerMode ? ExecutionMode.ContainerSandbox : ExecutionMode.BareHost,
                ContainerImage = Options.UseContainerMode ? Options.ContainerImage : null
            };
            db.Workspaces.Add(Workspace);
            await db.SaveChangesAsync(cancellationToken);

            if (Workspace.Id <= baseWorkspaceId)
            {
                throw new InvalidOperationException($"Expected generated workspace ID to be > {baseWorkspaceId}, but got {Workspace.Id}.");
            }

            // In container mode, initialize the ext4 mirror immediately at bootstrap so all worktrees are provisioned in ext4
            if (Options.UseContainerMode)
            {
                var pathResolver = scope.ServiceProvider.GetRequiredService<IWorkspacePathResolver>();
                var mirrorSync = scope.ServiceProvider.GetRequiredService<IGitMirrorSyncService>();
                var distro = pathResolver.GetWslDistributionName();
                var user = pathResolver.GetWslUserName();

                // Clean up any stale mirror from previous scenario runs
                try
                {
                    using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "wsl.exe",
                        Arguments = $"-d {distro} -- rm -rf /home/{user}/.nightcodeforge/wks/{Workspace.Id}",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });
                    p?.WaitForExit(5000);
                }
                catch (Exception)
                {
                    // Best-effort WSL directory cleanup before initialization
                }

                var defaultMirrorDir = pathResolver.GetAgentWorkingClonePath(Workspace.Id);
                var initialized = await mirrorSync.InitializeMirrorAsync(Workspace, defaultMirrorDir, cancellationToken);
                if (!string.IsNullOrWhiteSpace(initialized))
                {
                    Workspace.Ext4MirrorPath = initialized;
                    await db.SaveChangesAsync(cancellationToken);
                }
            }

            // 5. Seed Agent Profiles
            var defaultProfiles = new List<AgentProfile>
            {
                new()
                {
                    Id = "default",
                    WorkspaceId = Workspace.Id,
                    Name = "Default Full Agent",
                    HarnessId = StandardHarnessIds.Crucible,
                    ModelName = Options.ModelId,
                    Mode = AgentMode.Full,
                    OptimizationFlags = new LocalOptimizationFlags()
                },
                new()
                {
                    Id = StandardStageIds.TaskReview,
                    WorkspaceId = Workspace.Id,
                    Name = "ForgeTask Review Agent",
                    HarnessId = StandardHarnessIds.Crucible,
                    ModelName = Options.ModelId,
                    Mode = AgentMode.Full,
                    OptimizationFlags = new LocalOptimizationFlags()
                },
                new()
                {
                    Id = StandardStageIds.Implementation,
                    WorkspaceId = Workspace.Id,
                    Name = "Implementation Agent",
                    HarnessId = StandardHarnessIds.Crucible,
                    ModelName = Options.ModelId,
                    Mode = AgentMode.Full,
                    OptimizationFlags = new LocalOptimizationFlags()
                },
                new()
                {
                    Id = StandardStageIds.CodeReviewAndFix,
                    WorkspaceId = Workspace.Id,
                    Name = "Code Review Agent",
                    HarnessId = StandardHarnessIds.Crucible,
                    ModelName = Options.ModelId,
                    Mode = AgentMode.Full,
                    OptimizationFlags = new LocalOptimizationFlags()
                }
            };

            db.AgentProfiles.AddRange(defaultProfiles);
            await db.SaveChangesAsync(cancellationToken);
            Workspace.AgentProfiles = defaultProfiles;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Services != null)
        {
            await Services.DisposeAsync();
        }

        // Clear all connection pools before deleting or inspecting
        SqliteConnection.ClearAllPools();

        if (KeepOnExit)
        {
            ConsoleFormatting.PrintInspectionBox(RootDirectory, RepositoryPath, DatabasePath);
        }
        else
        {
            DeleteDirectorySafe(RootDirectory);

            if (Options.UseContainerMode && Workspace != null)
            {
                try
                {
                    using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "wsl.exe",
                        Arguments = $"-- rm -rf ~/.nightcodeforge/wks/{Workspace.Id}",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });
                    p?.WaitForExit(5000);
                }
                catch (Exception)
                {
                    // Best-effort WSL directory cleanup during teardown
                }
            }
        }

        if (Options.UseContainerMode && Workspace != null)
        {
            try
            {
                var (rmExe, rmArgs) = Infrastructure.Execution.NightCodeForgeContainerExecutor.BuildDockerCommand(
                    $"rm -f ncf-sandbox-{Workspace.Id} $(docker ps -aq --filter name=ncf-sandbox-{Workspace.Id})",
                    useWsl: true,
                    wslDistribution: null,
                    shellExecutable: "docker",
                    wslExecutable: "wsl.exe");
                var runner = new NightCodeForge.Core.Execution.SystemProcessRunner();
                runner.Run(rmExe, rmArgs, timeoutSeconds: 10);
            }
            catch (Exception)
            {
                // Best-effort Docker container cleanup during teardown
            }
        }
    }

    private static void DeleteDirectorySafe(string path)
    {
        if (!Directory.Exists(path)) return;

        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
                catch (Exception)
                {
                    // Best-effort file attribute reset
                }
            }

            Directory.Delete(path, true);
        }
        catch (Exception)
        {
            // Best-effort test directory removal
        }
    }

    private class SingleMockChatClientFactory : IOllamaChatClientFactory
    {
        private readonly IChatClient _client;
        public SingleMockChatClientFactory(IChatClient client) => _client = client;
        public IChatClient CreateClient(string? modelId = null) => _client;
        public IChatClient CreateClient(string? modelId, OllamaRoutingTarget routingTarget) => _client;
        public IChatClient CreateClient(AgentProfile? profile) => _client;
    }

    public class DynamicPipelineProvider : IWorkflowPipelineProvider
    {
        private readonly Func<ForgeTask, IWorkflowPipeline>? _defaultPipelineFactory;
        public IWorkflowPipeline? CustomPipeline { get; set; }

        public DynamicPipelineProvider(Func<ForgeTask, IWorkflowPipeline>? defaultPipelineFactory = null)
        {
            _defaultPipelineFactory = defaultPipelineFactory;
        }

        public Task<IWorkflowPipeline> GetPipelineAsync(ForgeTask task, CancellationToken cancellationToken = default)
        {
            if (CustomPipeline != null) return Task.FromResult(CustomPipeline);
            if (_defaultPipelineFactory != null) return Task.FromResult(_defaultPipelineFactory(task));
            return Task.FromResult<IWorkflowPipeline>(new WorkflowPipeline(Array.Empty<IWorkflowStage>()));
        }
    }
}
