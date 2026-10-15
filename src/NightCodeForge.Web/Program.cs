using Hangfire;
using NightCodeForge.Agents;
using NightCodeForge.Core.Services;
using NightCodeForge.Infrastructure;
using NightCodeForge.Infrastructure.BackgroundJobs;
using NightCodeForge.Web.Components;
using NightCodeForge.Web.Services;

var builder = WebApplication.CreateBuilder(args);

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
builder.Services.AddScoped<ITranscriptBrowserService, TranscriptBrowserService>();
builder.Services.AddScoped<IGitSyncService, GitSyncService>();
builder.Services.AddScoped<IDevQueueService, DevQueueService>();

// Register NightCodeForge domain and infrastructure services
var dbConnection = builder.Configuration.GetConnectionString("DefaultConnection") ?? "Data Source=nightcodeforge.db;";
var hangfireConnection = builder.Configuration.GetConnectionString("HangfireConnection") ?? "Data Source=nightcodeforge_hangfire.db;";

builder.Services.AddNightCodeForgeCoreAndInfrastructure(dbConnection);
builder.Services.Configure<NightCodeForge.Agents.Inference.LlmProviderOptions>(builder.Configuration.GetSection(NightCodeForge.Agents.Inference.LlmProviderOptions.SectionName));
builder.Services.AddNightCodeForgeAgents();
builder.Services.AddNightCodeForgeHangfire(hangfireConnection);
builder.Services.AddNightCodeForgeHangfireServers();

// Register SignalR Telemetry Hub & Streaming Infrastructure
builder.Services.AddSignalR();
builder.Services.AddHostedService<NightCodeForge.Infrastructure.Hubs.SignalRActivityBridgeService>();
builder.Services.AddScoped<OrchestratorHubClientConnection>();

var app = builder.Build();

// Run startup DB initialization, recovery, crash reconciliation, and start markdown watchers
using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<NightCodeForge.Infrastructure.Data.AppDbContext>();
    await dbContext.EnsureDatabaseCreatedAndWalAsync();

    var defaultModel = builder.Configuration["LlmProvider:ModelId"] ?? "Qwen3.8-27B:latest";
    await dbContext.EnsureDefaultGlobalAgentProfilesAsync(defaultModel);

    var globalOptSettings = scope.ServiceProvider.GetRequiredService<NightCodeForge.Core.Interfaces.IGlobalOptimizationSettingsService>();
    await globalOptSettings.LoadFlagsAsync();

    var recoveryService = scope.ServiceProvider.GetRequiredService<IStartupRecoveryService>();
    await recoveryService.ReconcileWorkspacesAsync();

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
