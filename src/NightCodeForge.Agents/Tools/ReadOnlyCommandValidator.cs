namespace NightCodeForge.Agents.Tools;

using System.Text.RegularExpressions;
using NightCodeForge.Core.Interfaces;
using Pmad.AI.Guardrails;

/// <summary>
/// Hardened security validator for shell commands executed by agents in ReadOnly mode
/// (e.g., Ideation, Untrusted Triage, and Task Review). Backed by <see cref="Pmad.AI.Guardrails.ReadOnlyCommandValidator"/>.
/// </summary>
public static class ReadOnlyCommandValidator
{
    public static readonly string[] AdditionalAllowedBinaries =
    [
        "fd", "locate", "which", "where", "whereis", "tree", "dir", "pwd",
        "ripgrep", "egrep", "fgrep", "sed", "awk", "file", "stat", "nl",
        "sort", "uniq", "cut", "tr", "diff", "cmp", "less", "more",
        "echo", "printf", "true", "false", "test", "csnav"
    ];

    public static readonly string[] CsNavFlagsWithArgs = ["-s", "--solution", "-f", "--framework", "--format"];

    public static readonly string[] CsNavAllowedSubcommands =
    [
        "references", "refs", "find-refs",
        "definition", "definitions", "def", "find-def", "goto-def",
        "implementations", "implementation", "impl", "find-impl", "goto-impl",
        "search", "api", "find", "api-search", "apis",
        "inspect", "explore", "type",
        "decompile",
        "sync",
        "server"
    ];

    public static readonly string[] CsNavServerAllowedSubcommands = ["status", "start", "reload"];

    /// <summary>
    /// Flags that load awk programs, include/load extensions or execute external files. Programs passed inline are
    /// still checked by <see cref="AwkSafeArgumentsPattern"/>.
    /// </summary>
    public static readonly string[] AwkBlockedFlags = ["-f", "--file", "-i", "--include", "-E", "--exec", "-l", "--load"];

    /// <summary>Flags that load sed scripts from files or modify files in place (script contents cannot be inspected).</summary>
    public static readonly string[] SedBlockedFlags = ["-i", "--in-place", "-f", "--file"];

    /// <summary>Flags that instruct <c>sort</c> to write output directly to a file.</summary>
    public static readonly string[] SortBlockedFlags = ["-o", "--output"];

    private static readonly TimeSpan PatternTimeout = TimeSpan.FromSeconds(1);

    // awk: reject output redirection of print/printf (>, >>), piping into a command, system(), `cmd | getline` and gawk @load/@include.
    // Comparisons such as `$3 > 100` outside print statements stay allowed.
    private const string AwkUnsafeProgramPattern =
        @"(?:\bprint(?:f)?\b(?:\s*/(?:\\.|[^/\\\r\n])*/)?(?>(?:""(?:\\.|[^""\\])*""|[(\[,!~?:=&|+\-*%^<]\s*/(?:\\.|[^/\\\r\n])*/|\\(?:\r\n|[\r\n])|>=|[^;}\r\n""/\\>]|\\(?![\r\n])|/)*)(?<![=!<>])>(?!=)" +
        @"|(?<!\|)\|(?!\|)" +
        @"|\bsystem\s*\(" +
        @"|\bgetline\b[^;}\n]*\|" +
        @"|@\s*(?:load|include)\b)";

    // sed: reject write (w/W, s///w/W) and execute (e, s///e) commands in all positional, flag-attached (-ew, --expression=w) and delimited forms.
    private const string SedUnsafeScriptPattern =
        @"(?:^(?:-[a-zA-Z]+\s+|--[a-z-]+(?:=\S+)?\s+)*(?:(?:[0-9]+|\$|/[^/\s]*/)(?:,(?:[0-9]+|\$|\+[0-9]+|/[^/\s]*/))?!?\s*)?[wWe]" +
        @"|(?:^|\s)(?:-[a-zA-Z]*e|--expression=)\s*(?:(?:[0-9]+|\$|/[^/\s]*/)(?:,(?:[0-9]+|\$|\+[0-9]+|/[^/\s]*/))?!?\s*)?[wWe]" +
        @"|(?:^|\s)[wWe](?=/|\s+\S|\s*$|\s*[;}])" +
        @"|(?:[;{}!$]|(?:^|[\s;{},])(?:[0-9]+(?:,(?:[0-9]+|\$|\+[0-9]+))?(?:~[0-9]+)?|!))\s*[wWe]" +
        @"|/[^/\s]*/!?\s*[wWe](?=/|\s+\S|\s*$|\s*[;}])" +
        @"|(?:^|[\s;{},!$0-9/])s(?<d>[^\s\da-zA-Z\\])(?>\\.|(?!\k<d>).)*\k<d>(?>\\.|(?!\k<d>).)*\k<d>[gpiImM0-9]*[wWe])";

    /// <summary>Arguments of <c>awk</c> must not contain file redirection or shell execution.</summary>
    public static readonly Regex AwkSafeArgumentsPattern = new(
        $"^(?!.*{AwkUnsafeProgramPattern}).*$",
        RegexOptions.Singleline | RegexOptions.CultureInvariant,
        PatternTimeout);

    /// <summary>Arguments of <c>sed</c> must not contain the write or execute commands.</summary>
    public static readonly Regex SedSafeArgumentsPattern = new(
        $"^(?!.*{SedUnsafeScriptPattern}).*$",
        RegexOptions.Singleline | RegexOptions.CultureInvariant,
        PatternTimeout);

    private static readonly Pmad.AI.Guardrails.ReadOnlyCommandValidator DefaultInstance = CreateDefault();

    /// <summary>
    /// Gets the shared default <see cref="Pmad.AI.Guardrails.ReadOnlyCommandValidator"/> instance configured for NightCodeForge.
    /// </summary>
    public static Pmad.AI.Guardrails.ReadOnlyCommandValidator Instance => DefaultInstance;

    /// <summary>
    /// Creates the declarative <see cref="CommandValidationRule"/> for <c>csnav</c>.
    /// </summary>
    public static CommandValidationRule CreateCsNavRule() => new()
    {
        FlagsWithArgs = [.. CsNavFlagsWithArgs],
        AllowedSubcommands = [.. CsNavAllowedSubcommands],
        NestedSubcommands =
        {
            ["server"] = new CommandValidationRule
            {
                AllowedSubcommands = [.. CsNavServerAllowedSubcommands]
            }
        }
    };

    /// <summary>
    /// Creates a new <see cref="ReadOnlyCommandValidatorOptions"/> pre-configured with NightCodeForge bash allowlists and rules.
    /// </summary>
    public static ReadOnlyCommandValidatorOptions CreateDefaultOptions()
    {
        var options = new ReadOnlyCommandValidatorOptions
        {
            Dialect = ShellDialect.Bash
        };

        foreach (var binary in AdditionalAllowedBinaries)
        {
            options.AllowedBinaries.Add(binary);
        }

        options.CommandRules["csnav"] = CreateCsNavRule();
        options.CommandRules["awk"] = new CommandValidationRule
        {
            BlockedFlags = [.. AwkBlockedFlags],
            ArgumentsRegex = AwkSafeArgumentsPattern
        };
        options.CommandRules["sed"] = new CommandValidationRule
        {
            BlockedFlags = [.. SedBlockedFlags],
            ArgumentsRegex = SedSafeArgumentsPattern
        };
        options.CommandRules["sort"] = new CommandValidationRule
        {
            BlockedFlags = [.. SortBlockedFlags]
        };

        return options;
    }

    /// <summary>
    /// Creates a new <see cref="Pmad.AI.Guardrails.ReadOnlyCommandValidator"/> using <see cref="CreateDefaultOptions"/>.
    /// </summary>
    public static Pmad.AI.Guardrails.ReadOnlyCommandValidator CreateDefault() =>
        new(CreateDefaultOptions());

    /// <summary>
    /// Validates a command line against the default NightCodeForge read-only policy.
    /// </summary>
    public static CommandValidationResult Validate(string? command) =>
        DefaultInstance.ValidateCommand(command ?? string.Empty);

    /// <summary>
    /// Validates a command line against the default NightCodeForge read-only policy, returning an (IsAllowed, ErrorMessage) tuple.
    /// </summary>
    public static (bool IsAllowed, string? ErrorMessage) ValidateCommand(string? command)
    {
        var result = Validate(command);
        return (result.IsAllowed, result.Reason);
    }

    /// <summary>
    /// Enforces mount-table visibility and read-only attachment protection for bash commands,
    /// throwing <see cref="UnauthorizedAccessException"/> if an unmounted or mutating <c>/workspace/attachments</c> access is attempted.
    /// </summary>
    public static void EnsureMountAccessAllowed(
        string? command,
        SandboxMountTable? mountTable,
        string? scopeCacheKey = null,
        string? workingDirectory = null,
        string? defaultWorkingDirectory = null)
    {
        SandboxPathResolver.ValidateBashAttachmentsAccess(
            command,
            mountTable,
            scopeCacheKey,
            workingDirectory,
            defaultWorkingDirectory);
    }
}
