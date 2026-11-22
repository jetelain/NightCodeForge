using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using NightCodeForge.Agents.Tools;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Security;
using NightCodeForge.Core.Services;

namespace NightCodeForge.Benchmark.Cli.Hosting;

/// <summary>
/// Thread-safe in-memory <see cref="IWorkflowRepository"/> stub for DB-free benchmark execution.
/// </summary>
public sealed class InMemoryBenchmarkWorkflowRepository : IWorkflowRepository
{
    private readonly object _lock = new();
    private readonly Dictionary<int, WorkflowDefinition> _workflows = new();
    private int _nextId = 1;

    public Task<WorkflowDefinition?> GetByIdWithStagesAsync(int workflowId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            return Task.FromResult(_workflows.TryGetValue(workflowId, out var workflow) ? workflow : null);
        }
    }

    public Task<WorkflowDefinition?> GetDefaultWorkflowWithStagesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            var workflow = _workflows.Values.FirstOrDefault(w => w.IsDefault);
            return Task.FromResult(workflow);
        }
    }

    public Task<IReadOnlyList<WorkflowDefinition>> GetWorkflowsAsync(int? workspaceId = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            IReadOnlyList<WorkflowDefinition> list = _workflows.Values
                .Where(w => !workspaceId.HasValue || w.WorkspaceId == workspaceId.Value || w.WorkspaceId == null)
                .OrderBy(w => w.Id)
                .ToList();
            return Task.FromResult(list);
        }
    }

    public Task<WorkflowDefinition> CreateWorkflowAsync(WorkflowDefinition workflow, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(workflow);

        lock (_lock)
        {
            if (workflow.Id <= 0)
            {
                workflow.Id = _nextId++;
            }
            else if (workflow.Id >= _nextId)
            {
                _nextId = workflow.Id + 1;
            }

            _workflows[workflow.Id] = workflow;
            return Task.FromResult(workflow);
        }
    }

    public Task UpdateWorkflowAsync(WorkflowDefinition workflow, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(workflow);

        lock (_lock)
        {
            _workflows[workflow.Id] = workflow;
            return Task.CompletedTask;
        }
    }

    public Task DeleteWorkflowAsync(int workflowId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            _workflows.Remove(workflowId);
            return Task.CompletedTask;
        }
    }
}

/// <summary>
/// DB-free <see cref="IOperationsControlService"/> stub reporting active, non-paused operations state.
/// </summary>
public sealed class NoOpBenchmarkOperationsControlService : IOperationsControlService
{
    public bool IsPaused => false;
    public OperationsReclaimStatus ReclaimStatus => OperationsReclaimStatus.Idle;
    public string? LastReclaimError => null;

    public event Action<bool>? OnOperationsStateChanged
    {
        add { }
        remove { }
    }

    public Task<bool> PauseOperationsAsync(
        string reason = "User initiated pause (Gamer / Dedicated Workload Mode)",
        CancellationToken cancellationToken = default) => Task.FromResult(false);

    public Task<bool> ResumeOperationsAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);

    public Task<int> GetDeferredTaskCountAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
}

/// <summary>
/// DB-free in-memory <see cref="IGlobalOptimizationSettingsService"/> holding default local optimization flags.
/// </summary>
public sealed class InMemoryGlobalOptimizationSettingsService : IGlobalOptimizationSettingsService
{
    private readonly object _lock = new();
    private LocalOptimizationFlags _flags = new();

    public event Action<LocalOptimizationFlags>? OnFlagsChanged;

    public LocalOptimizationFlags GetFlags()
    {
        lock (_lock)
        {
            return _flags.Clone();
        }
    }

    public Task<LocalOptimizationFlags> LoadFlagsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(GetFlags());
    }

    public Task UpdateFlagsAsync(LocalOptimizationFlags flags, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(flags);

        LocalOptimizationFlags snapshot;
        lock (_lock)
        {
            _flags = flags.Clone();
            snapshot = _flags.Clone();
        }

        OnFlagsChanged?.Invoke(snapshot);
        return Task.CompletedTask;
    }
}

/// <summary>
/// DB-free in-memory <see cref="IWebSearchSettingsService"/> holding default web search settings.
/// </summary>
public sealed class InMemoryWebSearchSettingsService : IWebSearchSettingsService
{
    private readonly object _lock = new();
    private WebSearchSettings _settings = new();

    public event Action<WebSearchSettings>? OnSettingsChanged;

    public WebSearchSettings GetSettings()
    {
        lock (_lock)
        {
            return _settings.Clone();
        }
    }

    public Task<WebSearchSettings> LoadSettingsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(GetSettings());
    }

    public Task UpdateSettingsAsync(WebSearchSettings settings, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(settings);

        WebSearchSettings snapshot;
        lock (_lock)
        {
            _settings = settings.Clone();
            snapshot = _settings.Clone();
        }

        OnSettingsChanged?.Invoke(snapshot);
        return Task.CompletedTask;
    }
}

/// <summary>
/// No-op <see cref="ITaskMarkdownSyncService"/> stub avoiding DB-backed task markdown mirroring in standalone benchmarks.
/// </summary>
public sealed class NoOpTaskMarkdownSyncService : ITaskMarkdownSyncService
{
    public string GenerateMarkdown(ForgeTask task, IReadOnlyList<int>? dependencies = null, string? agentProfileId = null) =>
        throw new NotSupportedException("Database task markdown generation is not supported in standalone benchmark mode.");

    public (TaskFrontmatter Frontmatter, string Body) ParseMarkdown(string markdownContent) =>
        throw new NotSupportedException("Database task markdown parsing is not supported in standalone benchmark mode.");

    public string ComputeContentHash(string content) => string.Empty;

    public Task<string> SyncTaskToDiskAsync(
        ForgeTask task,
        string workspacePath,
        IReadOnlyList<int>? dependencies = null,
        string? agentProfileId = null,
        CancellationToken cancellationToken = default) => Task.FromResult(string.Empty);

    public Task<SyncAllToDiskResult> SyncAllTasksToDiskAsync(
        Workspace workspace,
        string targetDirectory,
        string? hostRepositoryPath = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new SyncAllToDiskResult(new Dictionary<int, string>(), new Dictionary<int, TaskBaselineEntry>()));

    public Task ProcessFileContentAsync(string filePath, string content, int workspaceId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task ProcessFileChangedAsync(string filePath, int workspaceId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task ArchiveOrCleanupTaskFileAsync(ForgeTask task, string workspacePath, bool archive = true, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public void StartWatching(int workspaceId, string workspacePath) { }

    public void StopWatching(int workspaceId) { }

    public void Dispose() { }
}

/// <summary>
/// Environment-variable-backed <see cref="IApiKeySecretStore"/> for headless standalone CLI execution
/// (reads <c>NCF_BENCHMARK_API_KEY</c> then <c>OPENAI_API_KEY</c> without accessing OS keyrings).
/// </summary>
public sealed class EnvironmentBenchmarkApiKeySecretStore : IApiKeySecretStore
{
    private readonly string? _explicitOpenAiApiKey;
    private readonly Func<string, string?> _getEnvironmentVariable;

    public EnvironmentBenchmarkApiKeySecretStore(string? explicitOpenAiApiKey)
        : this(explicitOpenAiApiKey, Environment.GetEnvironmentVariable)
    {
    }

    internal EnvironmentBenchmarkApiKeySecretStore(
        string? explicitOpenAiApiKey,
        Func<string, string?> getEnvironmentVariable)
    {
        _explicitOpenAiApiKey = string.IsNullOrWhiteSpace(explicitOpenAiApiKey) ? null : explicitOpenAiApiKey.Trim();
        _getEnvironmentVariable = getEnvironmentVariable ?? throw new ArgumentNullException(nameof(getEnvironmentVariable));
    }

    public event EventHandler<ApiKeySecretChangedEventArgs>? SecretChanged
    {
        add { }
        remove { }
    }

    public bool IsKeyringAvailable() => false;

    public string? GetOpenAiApiKey(string? endpoint) => null;

    public string? ResolveEffectiveOpenAiApiKey(string? endpoint)
    {
        if (!string.IsNullOrWhiteSpace(_explicitOpenAiApiKey))
        {
            return _explicitOpenAiApiKey;
        }

        var benchmarkKey = _getEnvironmentVariable(Cli.BenchmarkCliConstants.BenchmarkApiKeyEnvVar);
        if (!string.IsNullOrWhiteSpace(benchmarkKey))
        {
            return benchmarkKey.Trim();
        }

        var openAiKey = _getEnvironmentVariable(ApiKeySecretConstants.OpenAiApiKeyEnvironmentVariable);
        return string.IsNullOrWhiteSpace(openAiKey) ? null : openAiKey.Trim();
    }

    public bool HasOpenAiApiKey(string? endpoint) => !string.IsNullOrWhiteSpace(ResolveEffectiveOpenAiApiKey(endpoint));

    public void SetOpenAiApiKey(string endpoint, string apiKey) =>
        throw new NotSupportedException("OS keyring writes are not supported in standalone benchmark CLI mode.");

    public void DeleteOpenAiApiKey(string endpoint) =>
        throw new NotSupportedException("OS keyring writes are not supported in standalone benchmark CLI mode.");

    public string? GetOllamaWebSearchApiKey() => null;

    public string? ResolveEffectiveOllamaWebSearchApiKey()
    {
        var key = _getEnvironmentVariable(ApiKeySecretConstants.OllamaApiKeyEnvironmentVariable);
        return string.IsNullOrWhiteSpace(key) ? null : key.Trim();
    }

    public bool HasOllamaWebSearchApiKey() => !string.IsNullOrWhiteSpace(ResolveEffectiveOllamaWebSearchApiKey());

    public void SetOllamaWebSearchApiKey(string apiKey) =>
        throw new NotSupportedException("OS keyring writes are not supported in standalone benchmark CLI mode.");

    public void DeleteOllamaWebSearchApiKey() =>
        throw new NotSupportedException("OS keyring writes are not supported in standalone benchmark CLI mode.");
}

/// <summary>
/// Disabled <see cref="IBacklogMutationToolService"/> stub ensuring benchmark DI remains free of DB backlog engines.
/// </summary>
public sealed class DisabledBenchmarkBacklogMutationToolService : IBacklogMutationToolService
{
    public Task<string> GetTaskAsync(BacklogToolScope scope, int taskId, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Backlog tools are disabled in standalone benchmark CLI mode.");

    public Task<string> EditTaskAsync(BacklogToolScope scope, TaskEditRequest request, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Backlog tools are disabled in standalone benchmark CLI mode.");

    public Task<string> SetTaskPriorityAsync(BacklogToolScope scope, int taskId, int priority, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Backlog tools are disabled in standalone benchmark CLI mode.");

    public Task<string> ReworkPrioritiesAsync(BacklogToolScope scope, IReadOnlyList<TaskPriorityUpdate> updates, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Backlog tools are disabled in standalone benchmark CLI mode.");

    public Task<string> RemoveTaskAsync(BacklogToolScope scope, int taskId, string? reason, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Backlog tools are disabled in standalone benchmark CLI mode.");

    public Task<string> ProposeTasksAsync(BacklogToolScope scope, IReadOnlyList<TaskProposal> proposals, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Backlog tools are disabled in standalone benchmark CLI mode.");
}

/// <summary>
/// Disabled <see cref="IBacklogMergeModeValidator"/> stub avoiding GitHub PR delivery wiring in standalone benchmark mode.
/// </summary>
public sealed class DisabledBenchmarkBacklogMergeModeValidator : IBacklogMergeModeValidator
{
    public Task<MergeMode> ParseAndValidateAsync(
        int workspaceId,
        string rawMergeMode,
        string paramName,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Backlog merge mode validation is disabled in standalone benchmark CLI mode.");
}

/// <summary>
/// Lightweight <see cref="IHostEnvironment"/> stub for standalone CLI execution without ASP.NET Core host builder.
/// </summary>
public sealed class BenchmarkHostEnvironment : IHostEnvironment
{
    public string EnvironmentName { get; set; } = Environments.Production;
    public string ApplicationName { get; set; } = Cli.BenchmarkCliConstants.ToolName;
    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
