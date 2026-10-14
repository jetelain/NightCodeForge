namespace NightCodeForge.ScenarioRunner.Common;

/// <summary>
/// Command-line configuration options for the NightCodeForge scenario runner.
/// </summary>
public class RunnerOptions
{
    /// <summary>
    /// If true, executes all scenarios sequentially.
    /// </summary>
    public bool RunAll { get; set; }

    /// <summary>
    /// Specific scenario ID (e.g., "1", "2") or name filter to execute.
    /// </summary>
    public string? ScenarioFilter { get; set; }

    /// <summary>
    /// If true, keeps synthetic Git repositories and SQLite databases on disk for developer inspection.
    /// </summary>
    public bool Keep { get; set; }

    /// <summary>
    /// If true, uses a synthetic in-memory Mock LLM instead of Ollama (for fast offline validation).
    /// </summary>
    public bool MockLlm { get; set; }

    /// <summary>
    /// Base URL endpoint for Ollama or OllamaRouter (defaults to http://localhost:11434).
    /// </summary>
    public string Endpoint { get; set; } = "http://localhost:11434";

    /// <summary>
    /// LLM Model identifier (defaults to Qwen3.8-27B:latest).
    /// </summary>
    public string ModelId { get; set; } = "Qwen3.8-27B:latest";

    /// <summary>
    /// Timeout in minutes for LLM inference calls.
    /// </summary>
    public double TimeoutMinutes { get; set; } = 15.0;

    /// <summary>
    /// If true, executes scenarios in Container Mode (Docker sandbox) instead of BareHost.
    /// </summary>
    public bool UseContainerMode { get; set; }

    /// <summary>
    /// Container image to use for Container Mode (defaults to ncf-agent-base:latest).
    /// </summary>
    public string ContainerImage { get; set; } = Infrastructure.Execution.ContainerExecutorOptions.DefaultAgentBaseImage;

    /// <summary>
    /// If true, launches the interactive scenario selection menu.
    /// </summary>
    public bool Interactive { get; set; } = true;

    /// <summary>
    /// Parses command-line arguments into a RunnerOptions instance.
    /// </summary>
    public static RunnerOptions Parse(string[] args)
    {
        var options = new RunnerOptions();

        if (args.Length == 0)
        {
            options.Interactive = true;
            return options;
        }

        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i].Trim();

            if (arg.Equals("--all", StringComparison.OrdinalIgnoreCase) || arg.Equals("-a", StringComparison.OrdinalIgnoreCase))
            {
                options.RunAll = true;
                options.Interactive = false;
            }
            else if (arg.Equals("--keep", StringComparison.OrdinalIgnoreCase) || arg.Equals("-k", StringComparison.OrdinalIgnoreCase))
            {
                options.Keep = true;
            }
            else if (arg.Equals("--mock-llm", StringComparison.OrdinalIgnoreCase) ||
                     arg.Equals("--offline", StringComparison.OrdinalIgnoreCase) ||
                     arg.Equals("--no-llm", StringComparison.OrdinalIgnoreCase))
            {
                options.MockLlm = true;
            }
            else if (arg.Equals("--scenario", StringComparison.OrdinalIgnoreCase) || arg.Equals("-s", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length)
                {
                    options.ScenarioFilter = args[++i];
                    options.Interactive = false;
                }
            }
            else if (arg.Equals("--endpoint", StringComparison.OrdinalIgnoreCase) || arg.Equals("-e", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length)
                {
                    options.Endpoint = args[++i];
                }
            }
            else if (arg.Equals("--model", StringComparison.OrdinalIgnoreCase) || arg.Equals("-m", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length)
                {
                    options.ModelId = args[++i];
                }
            }
            else if (arg.Equals("--timeout", StringComparison.OrdinalIgnoreCase) || arg.Equals("-t", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length && double.TryParse(args[++i], out var tVal))
                {
                    options.TimeoutMinutes = tVal;
                }
            }
            else if (arg.Equals("--container", StringComparison.OrdinalIgnoreCase) || arg.Equals("-c", StringComparison.OrdinalIgnoreCase))
            {
                options.UseContainerMode = true;
            }
            else if (arg.Equals("--image", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length)
                {
                    options.ContainerImage = args[++i];
                }
            }
            else if (arg.Equals("--interactive", StringComparison.OrdinalIgnoreCase) || arg.Equals("-i", StringComparison.OrdinalIgnoreCase))
            {
                options.Interactive = true;
            }
            else if (arg.Equals("--help", StringComparison.OrdinalIgnoreCase) || arg.Equals("-h", StringComparison.OrdinalIgnoreCase))
            {
                PrintHelp();
                System.Environment.Exit(0);
            }
        }

        return options;
    }

    public static void PrintHelp()
    {
        Console.WriteLine(@"
NightCodeForge Scenario & Workflow Runner
=========================================
Tests workflows, backend services, and local LLM integration on synthetic ephemeral scenarios.

Usage:
  dotnet run --project src/NightCodeForge.ScenarioRunner [options]

Options:
  -a, --all               Run all test scenarios sequentially
  -s, --scenario <id>     Run specific scenario by number (1..8) or name
  -k, --keep              Keep synthetic Git repos and SQLite databases on disk (default: ephemeral cleanup)
  -c, --container         Run scenarios in Container mode (Docker sandbox) instead of BareHost
  --image <name>          Docker image for container mode (default: ubuntu:24.04)
  --mock-llm, --offline   Use synthetic Mock LLM instead of Ollama (rapid offline test)
  -e, --endpoint <url>    Ollama Router endpoint (default: http://localhost:11434)
  -m, --model <id>        LLM model identifier (default: Qwen3.8-27B:latest)
  -t, --timeout <min>     LLM request timeout in minutes (default: 15)
  -i, --interactive       Launch interactive menu (default when no flags passed)
  -h, --help              Show this help message
");
    }
}
