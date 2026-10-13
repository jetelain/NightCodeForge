using NightCodeForge.Agents;
using NightCodeForge.Core.Services;
using NightCodeForge.Infrastructure;
using NightCodeForge.Infrastructure.BackgroundJobs;
using NightCodeForge.Web.Components;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Register NightCodeForge domain and infrastructure services
var dbConnection = builder.Configuration.GetConnectionString("DefaultConnection") ?? "Data Source=nightcodeforge.db;";
var hangfireConnection = builder.Configuration.GetConnectionString("HangfireConnection") ?? "Data Source=nightcodeforge_hangfire.db;";

builder.Services.AddNightCodeForgeCoreAndInfrastructure(dbConnection);
builder.Services.Configure<NightCodeForge.Agents.Inference.LlmProviderOptions>(builder.Configuration.GetSection(NightCodeForge.Agents.Inference.LlmProviderOptions.SectionName));
builder.Services.AddNightCodeForgeAgents();
builder.Services.AddNightCodeForgeHangfire(hangfireConnection);
builder.Services.AddNightCodeForgeHangfireServers();

var app = builder.Build();

// Run startup recovery and crash reconciliation before background processing starts
using (var scope = app.Services.CreateScope())
{
    var recoveryService = scope.ServiceProvider.GetRequiredService<IStartupRecoveryService>();
    await recoveryService.ReconcileWorkspacesAsync();
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
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
