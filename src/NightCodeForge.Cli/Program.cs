namespace NightCodeForge.Cli;

using System.Text;
using NightCodeForge.Cli.Commands;
using NightCodeForge.Cli.Common;
using NightCodeForge.Core.Execution;
using NightCodeForge.Core.Services;

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

            if (firstArg.Equals("transcript", StringComparison.OrdinalIgnoreCase) ||
                firstArg.Equals("analyze", StringComparison.OrdinalIgnoreCase) ||
                firstArg.Equals("analyze-transcript", StringComparison.OrdinalIgnoreCase))
            {
                var remainingArgs = args.Skip(1).ToArray();
                if (remainingArgs.Any(a => a is "--help" or "-h" or "help"))
                {
                    PrintUsage();
                    return 0;
                }

                var options = ParseTranscriptOptions(remainingArgs, out var error);
                if (options == null)
                {
                    Console.Error.WriteLine($"[ncf] Error: {error}\n");
                    PrintUsage();
                    return 2;
                }

                var transcriptCommand = new TranscriptCommand(new TranscriptAnalysisService());
                return await transcriptCommand.ExecuteAsync(options, Console.WriteLine, cts.Token);
            }

            if (firstArg.Equals("skills", StringComparison.OrdinalIgnoreCase) ||
                firstArg.Equals("skill", StringComparison.OrdinalIgnoreCase))
            {
                var remainingArgs = args.Skip(1).ToArray();
                if (remainingArgs.Any(a => a.Equals("--help", StringComparison.OrdinalIgnoreCase) ||
                                           a.Equals("-h", StringComparison.OrdinalIgnoreCase)) ||
                    (remainingArgs.Length == 1 && remainingArgs[0].Equals("help", StringComparison.OrdinalIgnoreCase)))
                {
                    PrintUsage();
                    return 0;
                }

                var options = ParseSkillsOptions(remainingArgs, out var error);
                if (options == null)
                {
                    Console.Error.WriteLine($"[ncf] Error: {error}\n");
                    PrintUsage();
                    return 2;
                }

                var skillsCommand = new SkillsCommand();
                return await skillsCommand.ExecuteAsync(options, Console.WriteLine, cts.Token);
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

    public static TranscriptOptions? ParseTranscriptOptions(string[] args, out string? error)
    {
        error = null;
        var options = new TranscriptOptions();
        bool pathSet = false;

        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i].Trim();
            if (arg.Equals("--verbose", StringComparison.OrdinalIgnoreCase) || arg.Equals("-v", StringComparison.OrdinalIgnoreCase))
            {
                options.Verbose = true;
            }
            else if (arg.Equals("--json", StringComparison.OrdinalIgnoreCase))
            {
                options.Json = true;
            }
            else if (arg.Equals("--limit", StringComparison.OrdinalIgnoreCase) ||
                     arg.StartsWith("--limit=", StringComparison.OrdinalIgnoreCase))
            {
                string rawLimit;
                if (arg.StartsWith("--limit=", StringComparison.OrdinalIgnoreCase))
                {
                    rawLimit = arg["--limit=".Length..];
                }
                else
                {
                    if (i + 1 >= args.Length)
                    {
                        error = "Missing value for '--limit'. Expected a non-negative integer (0 for all).";
                        return null;
                    }
                    rawLimit = args[++i];
                }

                if (!int.TryParse(rawLimit, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsedLimit) ||
                    parsedLimit < 0)
                {
                    error = $"Invalid value '{rawLimit}' for '--limit'. Expected a non-negative integer (0 for all).";
                    return null;
                }

                options.Limit = parsedLimit == 0 ? null : parsedLimit;
            }
            else if (arg.StartsWith("-"))
            {
                error = $"Unrecognized option '{arg}'.";
                return null;
            }
            else
            {
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

    public static SkillsOptions? ParseSkillsOptions(string[] args, out string? error)
    {
        error = null;
        var options = new SkillsOptions();

        int index = 0;
        if (args.Length > 0)
        {
            var subcommand = args[0].Trim();
            if (subcommand.Equals("install", StringComparison.OrdinalIgnoreCase))
            {
                options.Action = SkillsAction.Install;
                index = 1;
            }
            else if (subcommand.Equals("list", StringComparison.OrdinalIgnoreCase) ||
                     subcommand.Equals("ls", StringComparison.OrdinalIgnoreCase))
            {
                options.Action = SkillsAction.List;
                if (args.Length > 1)
                {
                    error = $"Unexpected argument '{args[1].Trim()}' for 'skills list'.";
                    return null;
                }
                return options;
            }
            else if (subcommand.Equals("show", StringComparison.OrdinalIgnoreCase) ||
                     subcommand.Equals("cat", StringComparison.OrdinalIgnoreCase) ||
                     subcommand.Equals("view", StringComparison.OrdinalIgnoreCase))
            {
                options.Action = SkillsAction.Show;
                for (int i = 1; i < args.Length; i++)
                {
                    var arg = args[i].Trim();
                    if (string.IsNullOrWhiteSpace(arg))
                    {
                        error = "Skill name cannot be empty.";
                        return null;
                    }
                    if (arg.StartsWith("-"))
                    {
                        error = $"Unrecognized option '{arg}'.";
                        return null;
                    }
                    if (!string.IsNullOrEmpty(options.SkillName))
                    {
                        error = $"Unexpected extra argument '{arg}'.";
                        return null;
                    }
                    options.SkillName = arg;
                }
                return options;
            }
        }

        bool pathSet = false;
        for (int i = index; i < args.Length; i++)
        {
            var arg = args[i].Trim();
            if (string.IsNullOrWhiteSpace(arg))
            {
                error = "Argument cannot be empty.";
                return null;
            }
            if (arg.Equals("--global", StringComparison.OrdinalIgnoreCase) || arg.Equals("-g", StringComparison.OrdinalIgnoreCase))
            {
                options.Global = true;
            }
            else if (arg.Equals("--dry-run", StringComparison.OrdinalIgnoreCase))
            {
                options.DryRun = true;
            }
            else if (arg.Equals("--dir", StringComparison.OrdinalIgnoreCase) || arg.Equals("-d", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(options.CustomDirectory))
                {
                    error = "Option '--dir' cannot be specified multiple times.";
                    return null;
                }
                if (i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1]) || args[i + 1].Trim().StartsWith('-'))
                {
                    error = $"Missing value for '{arg}' option.";
                    return null;
                }
                options.CustomDirectory = args[++i].Trim();
            }
            else if (arg.StartsWith("--dir=", StringComparison.OrdinalIgnoreCase) || arg.StartsWith("-d=", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(options.CustomDirectory))
                {
                    error = "Option '--dir' cannot be specified multiple times.";
                    return null;
                }
                var eqIndex = arg.IndexOf('=');
                var val = arg[(eqIndex + 1)..].Trim();
                if (string.IsNullOrWhiteSpace(val))
                {
                    error = $"Missing value for '{arg[..eqIndex]}' option.";
                    return null;
                }
                options.CustomDirectory = val;
            }
            else if (arg.StartsWith("-"))
            {
                error = $"Unrecognized option '{arg}'.";
                return null;
            }
            else
            {
                if (pathSet)
                {
                    error = $"Unexpected extra argument '{arg}'.";
                    return null;
                }
                options.TargetPath = arg;
                pathSet = true;
            }
        }

        if (options.Global && !string.IsNullOrWhiteSpace(options.CustomDirectory))
        {
            error = "Cannot combine '--global' with '--dir'.";
            return null;
        }

        if (pathSet && options.Global)
        {
            error = "Cannot combine a target path with '--global'.";
            return null;
        }

        if (pathSet && !string.IsNullOrWhiteSpace(options.CustomDirectory))
        {
            error = "Cannot combine a target path with '--dir'.";
            return null;
        }

        return options;
    }

    private static void PrintUsage()
    {
        Console.WriteLine(@"NightCodeForge CLI (ncf) — Deterministic repository preparation & readiness audit

USAGE:
  ncf init [path] [options]        Prepare repository: gitignore, editorconfig, stack detection, format, skills
  ncf audit [path]                 Audit repository readiness across the 5 pillars (green/yellow/red scorecard)
  ncf skills [install] [path] [options]  Install NightCodeForge agent skills (workspace or global scope)
  ncf skills list                  List embedded NightCodeForge agent skills
  ncf skills show [name]           Display embedded SKILL.md content to standard output
  ncf transcript [path] [options]  Standard analysis of execution transcript(s) for friction, metrics & recommendations
  ncf --version                    Display version information
  ncf --help                       Display this help message

INIT OPTIONS:
  [path]                           Path to the repository (default: current directory)
  --dry-run                        Preview changes without writing to disk or formatting
  --skip-format                    Skip the initial format pass
  --stack <name>                   Explicit technology stack override (dotnet, node, python, rust, go)

SKILLS OPTIONS:
  [path]                           Repository root for workspace install into .agents/skills/ (default: current directory)
  --global, -g                     Install globally into ~/.gemini/config/skills/ and ~/.agents/skills/
  --dir, -d <path>                 Install into a custom destination directory
  --dry-run                        Preview skill writes without modifying disk

TRANSCRIPT OPTIONS:
  [path]                           Path to transcript markdown file or directory (default: current directory)
  --verbose, -v                    Display detailed breakdowns including top read files and turn details
  --json                           Output analysis in machine-readable JSON format
  --limit <N>                      Analyze at most the N most recent transcripts (default: 50; 0 for all)

AUDIT EXIT CODES:
  0                                🟢 Healthy — all 5 pillars passed verification
  1                                🟡 Warnings — advisory warnings present (isolation heuristics, version drift)
  2                                🔴 Blockers — critical issues detected (build failure, failing tests, dirty git)
");
    }
}
