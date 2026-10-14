using NightCodeForge.ScenarioRunner.Common;
using NightCodeForge.ScenarioRunner.Environment;
using NightCodeForge.ScenarioRunner.Scenarios;

namespace NightCodeForge.ScenarioRunner;

public static class Program
{
    private static readonly List<IScenario> AvailableScenarios = new()
    {
        new InfraHealthScenario(),
        new AgentReasoningScenario(),
        new HumanInterruptionScenario(),
        new DependencyDagScenario(),
        new DeterministicQualityGatesScenario(),
        new EndToEndWorkflowScenario(),
        new StartupCrashRecoveryScenario(),
        new WorkspaceConcurrencyScenario(),
        new UntrustedTriageScenario(),
        new GitMirrorSyncScenario()
    };

    public static async Task<int> Main(string[] args)
    {
        var options = RunnerOptions.Parse(args);

        ConsoleFormatting.PrintBanner();
        PrintConfiguration(options);

        // Cancelation Handling
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Console.WriteLine($"\n{ConsoleFormatting.Yellow}Cancellation requested. Shutting down...{ConsoleFormatting.Reset}");
            cts.Cancel();
        };

        if (options.Interactive)
        {
            return await RunInteractiveLoopAsync(options, cts.Token);
        }

        return await RunConfiguredScenariosAsync(options, cts.Token);
    }

    private static void PrintConfiguration(RunnerOptions options)
    {
        Console.WriteLine($"  {ConsoleFormatting.Bold}Ollama Endpoint:{ConsoleFormatting.Reset} {ConsoleFormatting.Cyan}{options.Endpoint}{ConsoleFormatting.Reset}");
        Console.WriteLine($"  {ConsoleFormatting.Bold}Target Model:{ConsoleFormatting.Reset}    {ConsoleFormatting.Cyan}{options.ModelId}{ConsoleFormatting.Reset}");
        Console.WriteLine($"  {ConsoleFormatting.Bold}Inference Mode:{ConsoleFormatting.Reset}  {(options.MockLlm ? $"{ConsoleFormatting.Yellow}Mock LLM (Offline){ConsoleFormatting.Reset}" : $"{ConsoleFormatting.Green}Live Ollama Router{ConsoleFormatting.Reset}")}");
        Console.WriteLine($"  {ConsoleFormatting.Bold}Execution Mode:{ConsoleFormatting.Reset}  {(options.UseContainerMode ? $"{ConsoleFormatting.Magenta}Container Sandbox ({options.ContainerImage}){ConsoleFormatting.Reset}" : $"{ConsoleFormatting.Green}Bare Host{ConsoleFormatting.Reset}")}");
        Console.WriteLine($"  {ConsoleFormatting.Bold}Keep Artifacts:{ConsoleFormatting.Reset}  {(options.Keep ? $"{ConsoleFormatting.Yellow}YES (Inspectable){ConsoleFormatting.Reset}" : "NO (Ephemeral cleanup)")}");
        Console.WriteLine();
    }

    private static async Task<int> RunInteractiveLoopAsync(RunnerOptions options, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Console.WriteLine($"{ConsoleFormatting.Bold}Select an option:{ConsoleFormatting.Reset}");
            Console.WriteLine($"  {ConsoleFormatting.Cyan}1{ConsoleFormatting.Reset}. Run All Scenarios (1 - {AvailableScenarios.Count})");

            for (int i = 0; i < AvailableScenarios.Count; i++)
            {
                var s = AvailableScenarios[i];
                Console.WriteLine($"  {ConsoleFormatting.Cyan}{i + 2}{ConsoleFormatting.Reset}. Run Scenario #{s.Id}: {s.Name}");
            }

            Console.WriteLine($"  ─────────────────────────────────────────────────────────────────────────────");
            Console.WriteLine($"  {ConsoleFormatting.Yellow}C{ConsoleFormatting.Reset}. Toggle Execution Mode (Currently: {(options.UseContainerMode ? $"Container ({options.ContainerImage})" : "Bare Host")})");
            Console.WriteLine($"  {ConsoleFormatting.Yellow}K{ConsoleFormatting.Reset}. Toggle Keep Ephemeral Artifacts (Currently: {(options.Keep ? $"{ConsoleFormatting.Yellow}ON{ConsoleFormatting.Reset}" : "OFF")})");
            Console.WriteLine($"  {ConsoleFormatting.Yellow}M{ConsoleFormatting.Reset}. Toggle LLM Mode (Currently: {(options.MockLlm ? "Mock LLM" : $"Live Ollama {options.ModelId}")})");
            Console.WriteLine($"  {ConsoleFormatting.Red}Q{ConsoleFormatting.Reset}. Quit");
            Console.Write($"\n{ConsoleFormatting.Bold}Enter selection: {ConsoleFormatting.Reset}");

            var input = Console.ReadLine()?.Trim();
            if (string.IsNullOrWhiteSpace(input)) continue;

            if (input.Equals("Q", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            if (input.Equals("C", StringComparison.OrdinalIgnoreCase))
            {
                options.UseContainerMode = !options.UseContainerMode;
                Console.WriteLine($"\n{ConsoleFormatting.Yellow}Execution Mode toggled: {(options.UseContainerMode ? $"Container Mode ({options.ContainerImage})" : "Bare Host")}{ConsoleFormatting.Reset}\n");
                continue;
            }

            if (input.Equals("K", StringComparison.OrdinalIgnoreCase))
            {
                options.Keep = !options.Keep;
                Console.WriteLine($"\n{ConsoleFormatting.Yellow}Keep Ephemeral Artifacts toggled: {(options.Keep ? "ON (Results kept on disk)" : "OFF (Cleaned on exit)")}{ConsoleFormatting.Reset}\n");
                continue;
            }

            if (input.Equals("M", StringComparison.OrdinalIgnoreCase))
            {
                options.MockLlm = !options.MockLlm;
                Console.WriteLine($"\n{ConsoleFormatting.Yellow}LLM Inference Mode toggled: {(options.MockLlm ? "Mock LLM (Rapid offline)" : $"Live Ollama ({options.ModelId})")}{ConsoleFormatting.Reset}\n");
                continue;
            }

            if (input == "1")
            {
                await ExecuteScenariosAsync(AvailableScenarios, options, cancellationToken);
                continue;
            }

            if (int.TryParse(input, out var choice) && choice >= 2 && choice <= AvailableScenarios.Count + 1)
            {
                var selected = AvailableScenarios[choice - 2];
                await ExecuteScenariosAsync(new[] { selected }, options, cancellationToken);
                continue;
            }

            Console.WriteLine($"{ConsoleFormatting.Red}Invalid selection. Please try again.{ConsoleFormatting.Reset}\n");
        }

        return 0;
    }

    private static async Task<int> RunConfiguredScenariosAsync(RunnerOptions options, CancellationToken cancellationToken)
    {
        List<IScenario> scenariosToRun;

        if (options.RunAll)
        {
            scenariosToRun = AvailableScenarios;
        }
        else if (!string.IsNullOrWhiteSpace(options.ScenarioFilter))
        {
            scenariosToRun = AvailableScenarios.Where(s =>
                s.Id.Equals(options.ScenarioFilter, StringComparison.OrdinalIgnoreCase) ||
                s.Name.Contains(options.ScenarioFilter, StringComparison.OrdinalIgnoreCase)).ToList();

            if (scenariosToRun.Count == 0)
            {
                ConsoleFormatting.PrintFailure($"No scenarios found matching filter '{options.ScenarioFilter}'.");
                return 1;
            }
        }
        else
        {
            scenariosToRun = AvailableScenarios;
        }

        var results = await ExecuteScenariosAsync(scenariosToRun, options, cancellationToken);
        return results.All(r => r.Success) ? 0 : 1;
    }

    private static async Task<List<(string Id, string Name, bool Success, TimeSpan Duration, string Summary)>> ExecuteScenariosAsync(
        IEnumerable<IScenario> scenarios,
        RunnerOptions options,
        CancellationToken cancellationToken)
    {
        var summaryResults = new List<(string Id, string Name, bool Success, TimeSpan Duration, string Summary)>();

        // Create dedicated synthetic environment for this execution pass
        await using var env = new SyntheticEnvironment(options);

        ConsoleFormatting.PrintSection("Bootstrapping Ephemeral Synthetic Environment");
        ConsoleFormatting.PrintInfo($"Temporary Directory: {env.RootDirectory}");
        ConsoleFormatting.PrintInfo($"Synthetic Repository: {env.RepositoryPath}");
        ConsoleFormatting.PrintInfo($"Synthetic SQLite DB: {env.DatabasePath}");

        var initSw = System.Diagnostics.Stopwatch.StartNew();
        await env.InitializeAsync(cancellationToken);
        initSw.Stop();
        ConsoleFormatting.PrintSuccess($"Synthetic environment initialized in {initSw.ElapsedMilliseconds}ms!");

        var context = new ScenarioContext(env, options, cancellationToken);

        foreach (var scenario in scenarios)
        {
            ConsoleFormatting.PrintSection($"Scenario #{scenario.Id}: {scenario.Name}");
            ConsoleFormatting.PrintInfo(scenario.Description);

            try
            {
                var result = await scenario.RunAsync(context);
                summaryResults.Add((scenario.Id, scenario.Name, result.Success, result.Duration, result.Summary));

                if (result.Success)
                {
                    ConsoleFormatting.PrintSuccess($"Scenario #{scenario.Id} PASSED in {result.Duration.TotalSeconds:F2}s - {result.Summary}");
                }
                else
                {
                    ConsoleFormatting.PrintFailure($"Scenario #{scenario.Id} FAILED in {result.Duration.TotalSeconds:F2}s - {result.Summary}");
                }
            }
            catch (Exception ex)
            {
                ConsoleFormatting.PrintFailure($"Scenario #{scenario.Id} encountered unhandled exception: {ex.Message}");
                summaryResults.Add((scenario.Id, scenario.Name, false, TimeSpan.Zero, $"Exception: {ex.Message}"));
            }
        }

        ConsoleFormatting.PrintSummaryTable(summaryResults, options.Keep);
        return summaryResults;
    }
}
