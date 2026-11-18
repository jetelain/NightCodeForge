using System.Text.RegularExpressions;

namespace NightCodeForge.Core.Workflow.Filtering;

/// <summary>
/// Semantic CLI output filter for TypeScript (<c>tsc</c>) and Node/npm/pnpm/yarn commands.
/// </summary>
public sealed partial class NodeTscCliFilter : ICliOutputFilter
{
    private static readonly HashSet<string> PackageManagers = new(StringComparer.OrdinalIgnoreCase)
    {
        "npm", "pnpm", "yarn"
    };

    private static readonly HashSet<string> SupportedPackageScripts = new(StringComparer.OrdinalIgnoreCase)
    {
        "build", "test", "typecheck", "check", "lint"
    };

    [GeneratedRegex(@"(?:^|\s|\))(?::|\s*-)?\s*error\s+(?<code>TS\d+)\s*:", RegexOptions.IgnoreCase)]
    private static partial Regex TypeScriptErrorRegex();

    [GeneratedRegex(@"(?:^|\s)warning\s+(?<code>TS\d+)\s*:", RegexOptions.IgnoreCase)]
    private static partial Regex TypeScriptWarningRegex();

    [GeneratedRegex(@"^(?:SyntaxError|ReferenceError|TypeError|Error|Module not found):")]
    private static partial Regex NodeRuntimeErrorRegex();

    [GeneratedRegex(@"^\s*(?:npm\s+(?:WARN|notice|ERR!)|>\s+[^\r\n]+|added\s+\d+\s+packages|up to date|found\s+\d+\s+errors?)", RegexOptions.IgnoreCase)]
    private static partial Regex NodeBoilerplateRegex();

    public bool CanHandle(string command)
    {
        foreach (var (exe, sub, third) in CliOutputFilterHelpers.EnumerateCommands(command))
        {
            if (exe.Equals("tsc", StringComparison.OrdinalIgnoreCase) ||
                exe.Equals("tsc.cmd", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (exe.Equals("npx", StringComparison.OrdinalIgnoreCase) &&
                sub != null &&
                (sub.Equals("tsc", StringComparison.OrdinalIgnoreCase) ||
                 sub.Equals("vite", StringComparison.OrdinalIgnoreCase) ||
                 sub.Equals("vitest", StringComparison.OrdinalIgnoreCase) ||
                 sub.Equals("jest", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            if (PackageManagers.Contains(exe) && sub != null)
            {
                if (SupportedPackageScripts.Contains(sub))
                {
                    return true;
                }

                if (sub.Equals("run", StringComparison.OrdinalIgnoreCase) &&
                    third != null &&
                    SupportedPackageScripts.Contains(third))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public string FilterOutput(string rawOutput, string workspaceRoot, CommandFilterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(rawOutput))
        {
            return $"{CliOutputFilterConstants.SimplifiedOutputHeader}\n{CliOutputFilterConstants.BuildSucceededSummary}";
        }

        var lines = CliOutputFilterHelpers.SplitLines(rawOutput);
        var errors = new List<string>();
        var seenErrors = new HashSet<string>(StringComparer.Ordinal);
        var warnings = new List<string>();
        var seenWarnings = new HashSet<string>(StringComparer.Ordinal);
        var fallbackErrors = new List<string>();
        var seenFallback = new HashSet<string>(StringComparer.Ordinal);
        bool inStderr = false;
        bool inFailureContext = false;
        bool sawRecognizedSignal = false;

        foreach (var rawLine in lines)
        {
            var trimmed = rawLine.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                continue;
            }

            if (trimmed.Equals("[STDERR]", StringComparison.Ordinal))
            {
                inStderr = true;
                continue;
            }

            if (IsBuildOrTestErrorLine(trimmed))
            {
                sawRecognizedSignal = true;
                inFailureContext = true;
                var rel = RelativizeIfEnabled(trimmed, workspaceRoot, options);
                CliOutputFilterHelpers.AddUnique(errors, seenErrors, rel);
                continue;
            }

            if (inFailureContext && IsFailureDetailLine(trimmed))
            {
                sawRecognizedSignal = true;
                var rel = RelativizeIfEnabled(trimmed, workspaceRoot, options);
                CliOutputFilterHelpers.AddUnique(errors, seenErrors, rel);
                continue;
            }

            if (TypeScriptWarningRegex().IsMatch(trimmed))
            {
                sawRecognizedSignal = true;
                inFailureContext = false;
                var rel = RelativizeIfEnabled(trimmed, workspaceRoot, options);
                CliOutputFilterHelpers.AddUnique(warnings, seenWarnings, rel);
                continue;
            }

            if (trimmed.Contains("ELIFECYCLE", StringComparison.OrdinalIgnoreCase))
            {
                sawRecognizedSignal = true;
                inFailureContext = false;
                var rel = RelativizeIfEnabled(trimmed, workspaceRoot, options);
                CliOutputFilterHelpers.AddUnique(fallbackErrors, seenFallback, rel);
                continue;
            }

            if (NodeBoilerplateRegex().IsMatch(trimmed) ||
                trimmed.StartsWith("PASS ", StringComparison.Ordinal))
            {
                sawRecognizedSignal = true;
                inFailureContext = false;
                continue;
            }

            if (inStderr)
            {
                sawRecognizedSignal = true;
                var rel = RelativizeIfEnabled(trimmed, workspaceRoot, options);
                CliOutputFilterHelpers.AddUnique(fallbackErrors, seenFallback, rel);
            }
        }

        if (!sawRecognizedSignal)
        {
            return rawOutput.TrimEnd();
        }

        return CliOutputFilterHelpers.FormatCompilerDiagnosticsOutput(
            errors,
            warnings,
            fallbackErrors,
            options,
            explicitOutcomeSummary: null,
            assumedSuccess: errors.Count == 0 && fallbackErrors.Count == 0);
    }

    private static bool IsBuildOrTestErrorLine(string trimmed) =>
        TypeScriptErrorRegex().IsMatch(trimmed) ||
        trimmed.StartsWith("ERROR ", StringComparison.Ordinal) ||
        trimmed.StartsWith("FAIL ", StringComparison.Ordinal) ||
        trimmed.Contains("✘ [ERROR]", StringComparison.Ordinal) ||
        trimmed.StartsWith("error during build:", StringComparison.OrdinalIgnoreCase) ||
        NodeRuntimeErrorRegex().IsMatch(trimmed);

    private static bool IsFailureDetailLine(string trimmed) =>
        trimmed.StartsWith("● ", StringComparison.Ordinal) ||
        trimmed.StartsWith("✕ ", StringComparison.Ordinal) ||
        trimmed.StartsWith("× ", StringComparison.Ordinal) ||
        trimmed.StartsWith("AssertionError", StringComparison.Ordinal) ||
        trimmed.StartsWith("expect(", StringComparison.Ordinal) ||
        trimmed.StartsWith("Expected:", StringComparison.Ordinal) ||
        trimmed.StartsWith("Received:", StringComparison.Ordinal) ||
        trimmed.StartsWith("at ", StringComparison.Ordinal);

    private static string RelativizeIfEnabled(string line, string workspaceRoot, CommandFilterOptions options) =>
        options.RelativizePaths ? CliOutputPathRelativizer.RelativizeLinePaths(line, workspaceRoot) : line;
}
