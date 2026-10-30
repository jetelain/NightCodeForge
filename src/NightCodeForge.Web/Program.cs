using Hangfire;
using NightCodeForge.Agents;
using NightCodeForge.Core.Security;
using NightCodeForge.Core.Services;
using NightCodeForge.Infrastructure;
using NightCodeForge.Infrastructure.BackgroundJobs;
using NightCodeForge.Infrastructure.Configuration;
using NightCodeForge.Web.Components;
using NightCodeForge.Web.Middleware;
using NightCodeForge.Web.Services;
using Serilog;
using Serilog.Events;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

Microsoft.Extensions.Logging.ILogger? appLogger = null;

// User-owned configuration layer (config/config.json next to the executable / content root), layered on top of
// the read-only factory appsettings.json. Must run before any configuration is consumed.
// Fail-open: an invalid user file is reported and ignored, never prevents startup.
var userConfigStatus = HostConfigurationSources.AddUserConfiguration(
    builder.Configuration,
    builder.Environment.ContentRootPath,
    builder.Environment.EnvironmentName,
    logger: null,
    loggerAccessor: () => appLogger);

// Configure Serilog file & console logging
var rawLogsDir = builder.Configuration["Logging:LogsDirectory"] ?? "logs";
var logsDirectory = Path.IsPathRooted(rawLogsDir)
    ? rawLogsDir
    : Path.Combine(AppContext.BaseDirectory, rawLogsDir);
Directory.CreateDirectory(logsDirectory);
var logFilePath = Path.Combine(logsDirectory, "nightcodeforge-.log");

builder.Logging.ClearProviders();

builder.Services.AddSerilog((services, lc) => lc
    .MinimumLevel.Warning()
    .MinimumLevel.Override("NightCodeForge", LogEventLevel.Information)
    .ReadFrom.Configuration(builder.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .WriteTo.Console(restrictedToMinimumLevel: LogEventLevel.Fatal)
    .WriteTo.File(
        path: logFilePath,
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 31,
        shared: true,
        outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}"));

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Register Web UI State Services
builder.Services.AddScoped<WorkspaceStateService>();
builder.Services.AddSingleton<WorkerStateService>();
builder.Services.AddScoped<ToastService>();
builder.Services.AddScoped<IdeationSessionState>();
builder.Services.AddScoped<IWorkspaceProvisioningService, WorkspaceProvisioningService>();
builder.Services.AddScoped<ReviewCommentStateService>();
builder.Services.AddScoped<ITaskReviewService, TaskReviewService>();
builder.Services.AddSingleton<ITranscriptAnalysisService, TranscriptAnalysisService>();
builder.Services.AddScoped<ITranscriptBrowserService, TranscriptBrowserService>();
builder.Services.AddScoped<IGitSyncService, GitSyncService>();
builder.Services.AddSingleton<IDevQueueSkippedJobRegistry, DevQueueSkippedJobRegistry>();
builder.Services.AddScoped<IDevQueueService, DevQueueService>();
builder.Services.AddSingleton<ITaskHistoryDiffService, TaskHistoryDiffService>();
builder.Services.AddScoped<ITaskMutationService, TaskMutationService>();
builder.Services.AddScoped<ITaskDiskSyncCoordinator, TaskDiskSyncCoordinator>();
builder.Services.AddSingleton<IDagLayoutEngine, DagLayoutEngine>();
builder.Services.AddScoped<OperationsTelemetryUiService>();
builder.Services.AddSingleton<ISyntaxHighlightingService, SimpleSyntaxHighlighter>();
builder.Services.AddScoped<ITaskDiffService, TaskDiffService>();

// Register NightCodeForge domain and infrastructure services
var defaultConnStr = builder.Configuration.GetConnectionString("DefaultConnection") ?? "Data Source=nightcodeforge.db;";
var hangfireConnStr = builder.Configuration.GetConnectionString("HangfireConnection") ?? "Data Source=nightcodeforge_hangfire.db;";

var baseDbDir = AppContext.BaseDirectory;
if (builder.Environment.IsDevelopment())
{
    var projectDir = FindWebProjectDirectory(AppContext.BaseDirectory);
    if (!string.IsNullOrWhiteSpace(projectDir))
    {
        baseDbDir = projectDir;
    }
}

var dbConnection = NormalizeSqliteConnectionString(defaultConnStr, baseDbDir);
var hangfireConnection = NormalizeSqliteConnectionString(hangfireConnStr, baseDbDir);

EnsureSqliteDirectoryExists(dbConnection);
EnsureSqliteDirectoryExists(hangfireConnection);

// The LLM provider is fixed at startup (changing it requires a restart): each provider has dedicated implementations.
var llmProviderSection = builder.Configuration.GetSection(NightCodeForge.Agents.Inference.LlmProviderOptions.SectionName);
var llmProviderType = (llmProviderSection.Get<NightCodeForge.Agents.Inference.LlmProviderOptions>()
    ?? new NightCodeForge.Agents.Inference.LlmProviderOptions()).ProviderType;
var securitySection = builder.Configuration.GetSection(SecurityOptions.SectionName);
builder.Services.Configure<SecurityOptions>(securitySection);
builder.Services.AddNightCodeForgeCoreAndInfrastructure(dbConnection);
builder.Services.Configure<NightCodeForge.Agents.Inference.LlmProviderOptions>(llmProviderSection);
builder.Services.Configure<NightCodeForge.Infrastructure.Inference.LlmEndpointOptions>(llmProviderSection);
builder.Services.AddNightCodeForgeInference(llmProviderType);
builder.Services.AddNightCodeForgeAgents(llmProviderType);
builder.Services.AddNightCodeForgeHangfire(hangfireConnection);
builder.Services.AddNightCodeForgeHangfireServers();

// Configure host shutdown timeout to give background services adequate time for graceful shutdown
builder.Services.Configure<HostOptions>(options =>
{
    options.ShutdownTimeout = HangfireConfigurationExtensions.DefaultHostShutdownTimeout;
});

// Register SignalR Telemetry Hub & Streaming Infrastructure
builder.Services.AddSignalR();
builder.Services.AddNightCodeForgeSignalRBridge();
builder.Services.AddScoped<OrchestratorHubClientConnection>();

var app = builder.Build();
appLogger = app.Logger;

app.Logger.LogInformation(
    "User configuration status: {Status} (Path: {Path})",
    userConfigStatus,
    HostConfigurationSources.GetFullPath(builder.Environment.ContentRootPath));

// Run startup DB initialization, recovery, crash reconciliation, and start markdown watchers
using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<NightCodeForge.Infrastructure.Data.AppDbContext>();
    await dbContext.EnsureDatabaseCreatedAndWalAsync();

    var configSeed = builder.Configuration[NightCodeForge.Infrastructure.Data.AppDbContext.WorkspaceIdSeedEnvironmentVariable]
        ?? builder.Configuration["WorkspaceIdSeed"];
    if (int.TryParse(configSeed, out var nextWsId) && nextWsId > 1)
    {
        await dbContext.SeedWorkspaceIdSequenceAsync(nextWsId);
    }

    var llmOptions = builder.Configuration.GetSection(NightCodeForge.Agents.Inference.LlmProviderOptions.SectionName)
        .Get<NightCodeForge.Agents.Inference.LlmProviderOptions>() ?? new NightCodeForge.Agents.Inference.LlmProviderOptions();

    if (string.IsNullOrWhiteSpace(llmOptions.DefaultModelId))
    {
        var catalogService = scope.ServiceProvider.GetService<NightCodeForge.Core.Interfaces.ILlmModelCatalogService>();
        if (catalogService != null)
        {
            try
            {
                var detectedModel = await catalogService.GetDefaultModelAsync();
                if (detectedModel != null && !string.IsNullOrWhiteSpace(detectedModel.ModelId))
                {
                    llmOptions.DefaultModelId = detectedModel.ModelId;
                }
            }
            catch (Exception ex)
            {
                var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
                logger.LogWarning(ex, "Failed to resolve default model from catalog during startup seeding; falling back to configured or baseline model.");
            }
        }

        if (string.IsNullOrWhiteSpace(llmOptions.DefaultModelId) &&
            llmOptions.ProviderType == NightCodeForge.Core.Domain.LlmProviderType.OpenAiCompatible &&
            llmOptions.OpenAiCompatible?.AllowedModels is { Count: > 0 } allowedModels)
        {
            var fallback = allowedModels.FirstOrDefault(m => !string.IsNullOrWhiteSpace(m?.ModelId));
            if (fallback != null)
            {
                llmOptions.DefaultModelId = fallback.ModelId.Trim();
            }
        }
    }

    await dbContext.EnsureDefaultGlobalAgentProfilesAsync(llmOptions);
    await dbContext.EnsureDefaultWorkflowsAsync();

    var globalOptSettings = scope.ServiceProvider.GetRequiredService<NightCodeForge.Core.Interfaces.IGlobalOptimizationSettingsService>();
    await globalOptSettings.LoadFlagsAsync();

    var webSearchSettings = scope.ServiceProvider.GetRequiredService<NightCodeForge.Core.Interfaces.IWebSearchSettingsService>();
    await webSearchSettings.LoadSettingsAsync();

    var gitPreflight = scope.ServiceProvider.GetRequiredService<NightCodeForge.Core.Interfaces.IGitWorkspaceManager>();
    await gitPreflight.ValidateGitVersionAsync();

    var recoveryService = scope.ServiceProvider.GetRequiredService<IStartupRecoveryService>();
    await recoveryService.ReconcileWorkspacesAsync();

    var devQueueService = scope.ServiceProvider.GetRequiredService<IDevQueueService>();
    try
    {
        await devQueueService.ReorderInferenceQueueByPriorityAsync();
    }
    catch (AggregateException ex)
    {
        Log.Error(ex, "Failed to reorder and migrate inference queues during startup: {Message}", ex.Message);
        throw;
    }

    var workspaceRepo = scope.ServiceProvider.GetRequiredService<NightCodeForge.Core.Repositories.IWorkspaceRepository>();
    var markdownSync = scope.ServiceProvider.GetRequiredService<ITaskMarkdownSyncService>();
    var workspaces = await workspaceRepo.GetAllAsync();
    foreach (var ws in workspaces)
    {
        if (!string.IsNullOrWhiteSpace(ws.RepositoryPath) && Directory.Exists(ws.RepositoryPath))
        {
            markdownSync.StartWatching(ws.Id, ws.RepositoryPath);
        }
    }
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseContentSecurityPolicy();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapHub<NightCodeForge.Infrastructure.Hubs.OrchestratorHub>("/hubs/orchestrator");

// Configure Hangfire Dashboard with local requests allowed
app.UseHangfireDashboard("/hangfire", new DashboardOptions
{
    DashboardTitle = "NightCodeForge Dev-Queue Monitor",
    DarkModeEnabled = true,
    Authorization = new[] { new HangfireLocalRequestsOnlyAuthorizationFilter() }
});

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

static void EnsureSqliteDirectoryExists(string? connectionString)
{
    if (string.IsNullOrWhiteSpace(connectionString)) return;
    var parts = connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries);
    foreach (var part in parts)
    {
        var trimmed = part.Trim();
        if (trimmed.StartsWith("Data Source=", StringComparison.OrdinalIgnoreCase))
        {
            var filePath = trimmed["Data Source=".Length..].Trim();
            if (!string.IsNullOrWhiteSpace(filePath) && filePath != ":memory:")
            {
                var dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
            }
            break;
        }
    }
}

static string NormalizeSqliteConnectionString(string? connectionString, string baseDirectory)
{
    if (string.IsNullOrWhiteSpace(connectionString)) return connectionString ?? string.Empty;
    var parts = connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries);
    var modified = false;
    for (int i = 0; i < parts.Length; i++)
    {
        var trimmed = parts[i].Trim();
        if (trimmed.StartsWith("Data Source=", StringComparison.OrdinalIgnoreCase))
        {
            var filePath = trimmed["Data Source=".Length..].Trim();
            if (!string.IsNullOrWhiteSpace(filePath) && filePath != ":memory:" && !Path.IsPathRooted(filePath))
            {
                var fullPath = Path.Combine(baseDirectory, filePath);
                parts[i] = $"Data Source={fullPath}";
                modified = true;
            }
            break;
        }
    }
    return modified ? string.Join(";", parts) + ";" : connectionString;
}

static string? FindWebProjectDirectory(string baseDirectory)
{
    var current = new DirectoryInfo(baseDirectory);
    while (current != null)
    {
        if (File.Exists(Path.Combine(current.FullName, "NightCodeForge.Web.csproj")))
        {
            return current.FullName;
        }
        current = current.Parent;
    }
    return null;
}


