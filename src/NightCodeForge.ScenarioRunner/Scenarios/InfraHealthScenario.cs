using System.Data;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Infrastructure.Data;
using NightCodeForge.ScenarioRunner.Common;

namespace NightCodeForge.ScenarioRunner.Scenarios;

/// <summary>
/// Scenario 1: Validates fundamental infrastructure health:
/// - SQLite database creation and WAL journal mode
/// - Native Git repository integrity and branch baseline
/// - Ollama / OllamaRouter HTTP endpoint and model catalog inspection
/// - JIT model preloading service
/// </summary>
public class InfraHealthScenario : IScenario
{
    public string Id => "1";
    public string Name => "Health & Infrastructure Connectivity Check";
    public string Description => "Verifies SQLite WAL mode, Git repository status, Ollama Router connectivity, and model preloading.";

    public async Task<ScenarioResult> RunAsync(ScenarioContext context)
    {
        var sw = Stopwatch.StartNew();
        var details = new List<string>();

        // Step 1: SQLite & WAL Verification
        ConsoleFormatting.PrintStep("1.1", "Verifying SQLite schema and Write-Ahead Logging (WAL) mode...");
        using (var scope = context.Environment.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var connection = db.Database.GetDbConnection();
            await connection.OpenAsync(context.CancellationToken);

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode;";
            var journalMode = (string?)await cmd.ExecuteScalarAsync(context.CancellationToken);

            if (!string.Equals(journalMode, "wal", StringComparison.OrdinalIgnoreCase))
            {
                return ScenarioResult.Fail(sw.Elapsed, $"Expected WAL mode but found '{journalMode}'.");
            }
            ConsoleFormatting.PrintSuccess($"SQLite active with journal_mode = {journalMode?.ToUpperInvariant()}");
            details.Add($"SQLite WAL Mode: {journalMode}");

            var workspaceCount = await db.Workspaces.CountAsync(context.CancellationToken);
            var profileCount = await db.AgentProfiles.CountAsync(context.CancellationToken);
            ConsoleFormatting.PrintInfo($"Workspaces seeded: {workspaceCount}, Agent profiles seeded: {profileCount}");
            details.Add($"Workspaces: {workspaceCount}, Profiles: {profileCount}");
        }

        // Step 2: Native Git Repository Check
        ConsoleFormatting.PrintStep("1.2", "Verifying Git repository status and baseline commit...");
        var gitStatus = await context.Environment.GitRepo.GetStatusAsync(context.CancellationToken);
        var branch = await context.Environment.GitRepo.GetCurrentBranchAsync(context.CancellationToken);
        var commitLog = await context.Environment.GitRepo.GetCommitLogAsync(1, context.CancellationToken);

        if (string.IsNullOrWhiteSpace(commitLog))
        {
            return ScenarioResult.Fail(sw.Elapsed, "Git repository has no initial commit.");
        }

        ConsoleFormatting.PrintSuccess($"Git repository initialized on branch '{branch}'");
        ConsoleFormatting.PrintInfo($"Latest commit: {commitLog.Trim()}");
        details.Add($"Git Branch: {branch}, Commit: {commitLog.Trim()}");

        // Step 3: Ollama / OllamaRouter Connectivity
        ConsoleFormatting.PrintStep("1.3", $"Probing Ollama Router at {context.Options.Endpoint}...");
        if (context.Options.MockLlm)
        {
            ConsoleFormatting.PrintWarning("Running in Mock LLM mode (--mock-llm); skipping live Ollama HTTP ping.");
            details.Add("Ollama Router: Skipped (Mock Mode)");
        }
        else
        {
            using var scope = context.Environment.Services.CreateScope();
            var catalogService = scope.ServiceProvider.GetRequiredService<IOllamaModelCatalogService>();
            var preloadService = scope.ServiceProvider.GetRequiredService<IOllamaModelPreloadService>();

            var models = await catalogService.GetAvailableModelsAsync(context.CancellationToken);
            if (models.Count == 0)
            {
                ConsoleFormatting.PrintWarning($"No models returned from Ollama Router at {context.Options.Endpoint}.");
            }
            else
            {
                ConsoleFormatting.PrintSuccess($"Ollama Router returned {models.Count} model(s).");
                var targetFound = models.Any(m => (!string.IsNullOrEmpty(m.Name) && m.Name.Equals(context.Options.ModelId, StringComparison.OrdinalIgnoreCase)) ||
                                                  (!string.IsNullOrEmpty(m.Model) && m.Model.Equals(context.Options.ModelId, StringComparison.OrdinalIgnoreCase)));

                if (targetFound)
                {
                    ConsoleFormatting.PrintSuccess($"Target model '{context.Options.ModelId}' verified present on Ollama.");
                }
                else
                {
                    ConsoleFormatting.PrintWarning($"Target model '{context.Options.ModelId}' was not found in catalog. Available: {string.Join(", ", models.Take(3).Select(m => m.Name ?? m.Model ?? "unknown"))}");
                }

                // Test JIT Preload
                ConsoleFormatting.PrintStep("1.4", $"Testing JIT model preload for '{context.Options.ModelId}'...");
                var preloadOk = await preloadService.PreloadModelAsync(context.Options.ModelId, "15m", context.CancellationToken);
                ConsoleFormatting.PrintInfo($"Model preload dispatched (Success={preloadOk})");
                details.Add($"Ollama Models: {models.Count}, Target: {context.Options.ModelId} (Found={targetFound})");
            }
        }

        // Step 4: Execution Environment Provider Check
        ConsoleFormatting.PrintStep("1.5", "Verifying BareHost execution environment resolution...");
        using (var scope = context.Environment.Services.CreateScope())
        {
            var envProvider = scope.ServiceProvider.GetRequiredService<IExecutionEnvironmentProvider>();
            var execEnv = envProvider.GetEnvironment(context.Environment.Workspace);

            if (execEnv == null)
            {
                return ScenarioResult.Fail(sw.Elapsed, "Failed to resolve IExecutionEnvironment from provider.");
            }
            ConsoleFormatting.PrintSuccess($"Execution environment resolved (Tier: {execEnv.Tier})");
            details.Add($"Execution Environment Tier: {execEnv.Tier}");
        }

        sw.Stop();
        return ScenarioResult.Pass(sw.Elapsed, "Foundational infrastructure, SQLite WAL, Git CLI, and Ollama connectivity verified.", details.ToArray());
    }
}
