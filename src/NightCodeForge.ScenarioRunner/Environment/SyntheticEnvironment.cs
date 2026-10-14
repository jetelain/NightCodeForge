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
    public InMemoryIssueJobQueue JobQueue { get; private set; } = null!;
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
        PipelineProvider = new DynamicPipelineProvider(issue => Default3AgentPipeline.Create());
        serviceCollection.AddSingleton(PipelineProvider);
        serviceCollection.AddSingleton<IWorkflowPipelineProvider>(PipelineProvider);

        // NightCodeForge Core & Infrastructure
        var connStr = $"Data Source={DatabasePath};";
        serviceCollection.AddNightCodeForgeCoreAndInfrastructure(connStr);

        // In-Memory Job Queue for inspection
        JobQueue = new InMemoryIssueJobQueue();
        serviceCollection.AddSingleton(JobQueue);
        serviceCollection.AddSingleton<IIssueJobQueue>(JobQueue);

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

        // 3. Ensure Database Created with WAL Mode
        using (var scope = Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.EnsureDatabaseCreatedAndWalAsync(cancellationToken);

            // 4. Seed Workspace
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

            // 5. Seed Agent Profiles
            var defaultProfiles = new List<AgentProfile>
            {
                new()
                {
                    Id = "default",
                    WorkspaceId = Workspace.Id,
                    Name = "Default Full Agent",
                    HarnessId = "crucible",
                    ModelName = Options.ModelId,
                    Mode = AgentMode.Full,
                    OptimizationFlags = new LocalOptimizationFlags()
                },
                new()
                {
                    Id = "issue-review",
                    WorkspaceId = Workspace.Id,
                    Name = "Issue Review Agent",
                    HarnessId = "crucible",
                    ModelName = Options.ModelId,
                    Mode = AgentMode.Full,
                    OptimizationFlags = new LocalOptimizationFlags()
                },
                new()
                {
                    Id = "implementation",
                    WorkspaceId = Workspace.Id,
                    Name = "Implementation Agent",
                    HarnessId = "crucible",
                    ModelName = Options.ModelId,
                    Mode = AgentMode.Full,
                    OptimizationFlags = new LocalOptimizationFlags()
                },
                new()
                {
                    Id = "code-review-fix",
                    WorkspaceId = Workspace.Id,
                    Name = "Code Review Agent",
                    HarnessId = "crucible",
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
        }

        if (Options.UseContainerMode && Workspace != null)
        {
            try
            {
                var containerName = $"ncf-sandbox-{Workspace.Id}";
                var (rmExe, rmArgs) = Infrastructure.Execution.NightCodeForgeContainerExecutor.BuildDockerCommand(
                    $"rm -f {containerName}",
                    useWsl: true,
                    wslDistribution: null,
                    shellExecutable: "docker",
                    wslExecutable: "wsl.exe");
                var runner = new Infrastructure.Execution.SystemProcessRunner();
                runner.Run(rmExe, rmArgs, timeoutSeconds: 10);
            }
            catch { }
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
                catch { }
            }

            Directory.Delete(path, true);
        }
        catch { }
    }

    private class SingleMockChatClientFactory : IOllamaChatClientFactory
    {
        private readonly IChatClient _client;
        public SingleMockChatClientFactory(IChatClient client) => _client = client;
        public IChatClient CreateClient(string? modelId = null) => _client;
        public IChatClient CreateClient(AgentProfile? profile) => _client;
    }

    public class DynamicPipelineProvider : IWorkflowPipelineProvider
    {
        private readonly Func<Issue, IWorkflowPipeline> _defaultPipelineFactory;
        public IWorkflowPipeline? CustomPipeline { get; set; }

        public DynamicPipelineProvider(Func<Issue, IWorkflowPipeline> defaultPipelineFactory)
        {
            _defaultPipelineFactory = defaultPipelineFactory ?? throw new ArgumentNullException(nameof(defaultPipelineFactory));
        }

        public IWorkflowPipeline GetPipeline(Issue issue) => CustomPipeline ?? _defaultPipelineFactory(issue);
    }
}
