namespace NightCodeForge.Agents.Tools;

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
}
