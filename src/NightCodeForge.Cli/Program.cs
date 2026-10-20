namespace NightCodeForge.Cli;

using System.Text;
using NightCodeForge.Cli.Commands;
using NightCodeForge.Cli.Common;
using NightCodeForge.Core.Execution;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        if (args.Length == 0)
        {
            PrintUsage();
            return 0;
        }

        var firstArg = args[0].Trim();

        if (firstArg is "--help" or "-h" or "help")
        {
            PrintUsage();
            return 0;
        }

        if (firstArg is "--version" or "-v" or "version")
        {
            Console.WriteLine($"ncf version {Constants.NcfVersion} (NightCodeForge CLI)");
            return 0;
        }

        var processRunner = new SystemProcessRunner();

        try
        {
            if (firstArg.Equals("init", StringComparison.OrdinalIgnoreCase))
            {
                var remainingArgs = args.Skip(1).ToArray();
                if (remainingArgs.Any(a => a is "--help" or "-h" or "help"))
                {
                    PrintUsage();
                    return 0;
                }

                var options = ParseInitOptions(remainingArgs, out var error);
                if (options == null)
                {
                    Console.Error.WriteLine($"[ncf] Error: {error}\n");
                    PrintUsage();
                    return 2;
                }

                var initCommand = new InitCommand(processRunner);
                return await initCommand.ExecuteAsync(options, Console.WriteLine, cts.Token);
            }

            if (firstArg.Equals("audit", StringComparison.OrdinalIgnoreCase))
            {
                var remainingArgs = args.Skip(1).ToArray();
                if (remainingArgs.Any(a => a is "--help" or "-h" or "help"))
                {
                    PrintUsage();
                    return 0;
                }

                var options = ParseAuditOptions(remainingArgs, out var error);
                if (options == null)
                {
                    Console.Error.WriteLine($"[ncf] Error: {error}\n");
                    PrintUsage();
                    return 2;
                }

                var auditCommand = new AuditCommand(processRunner);
                return await auditCommand.ExecuteAsync(options, Console.WriteLine, cts.Token);
            }

            Console.Error.WriteLine($"[ncf] Unknown command: '{firstArg}'\n");
            PrintUsage();
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ncf] Fatal error: {ex.Message}");
            Console.Error.WriteLine(ex.StackTrace);
            return 1;
        }
    }

    public static InitOptions? ParseInitOptions(string[] args, out string? error)
    {
        error = null;
        var options = new InitOptions();
        bool pathSet = false;

        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i].Trim();
            if (arg.Equals("--dry-run", StringComparison.OrdinalIgnoreCase))
            {
                options.DryRun = true;
            }
            else if (arg.Equals("--skip-format", StringComparison.OrdinalIgnoreCase))
            {
                options.SkipFormat = true;
            }
            else if (arg.Equals("--stack", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length)
                {
                    error = "Missing value for --stack option.";
                    return null;
                }
                options.Stack = args[++i].Trim();
            }
            else if (arg.StartsWith("--stack=", StringComparison.OrdinalIgnoreCase))
            {
                options.Stack = arg.Substring("--stack=".Length).Trim();
            }
            else if (arg.StartsWith("-"))
            {
                error = $"Unrecognized option '{arg}'.";
                return null;
            }
            else
            {
                // Positional path
                if (pathSet)
                {
                    error = $"Unexpected extra argument '{arg}'.";
                    return null;
                }
                options.TargetPath = arg;
                pathSet = true;
            }
        }

        return options;
    }

    public static AuditOptions? ParseAuditOptions(string[] args, out string? error)
    {
        error = null;
        var options = new AuditOptions();
        bool pathSet = false;

        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i].Trim();
            if (arg.StartsWith("-"))
            {
                error = $"Unrecognized option '{arg}'.";
                return null;
            }

            if (pathSet)
            {
                error = $"Unexpected extra argument '{arg}'.";
                return null;
            }

            options.TargetPath = arg;
            pathSet = true;
        }

        return options;
    }

    private static void PrintUsage()
    {
        Console.WriteLine(@"NightCodeForge CLI (ncf) — Deterministic repository preparation & readiness audit

USAGE:
  ncf init [path] [options]    Prepare repository: gitignore, editorconfig, stack detection, format, skills
  ncf audit [path]             Audit repository readiness across the 5 pillars (green/yellow/red scorecard)
  ncf --version                Display version information
  ncf --help                   Display this help message

INIT OPTIONS:
  [path]                       Path to the repository (default: current directory)
  --dry-run                    Preview changes without writing to disk or formatting
  --skip-format                Skip the initial format pass
  --stack <name>               Explicit technology stack override (dotnet, node, python, rust, go)

AUDIT EXIT CODES:
  0                            🟢 Healthy — all 5 pillars passed verification
  1                            🟡 Warnings — advisory warnings present (isolation heuristics, version drift)
  2                            🔴 Blockers — critical issues detected (build failure, failing tests, dirty git)
");
    }
}
