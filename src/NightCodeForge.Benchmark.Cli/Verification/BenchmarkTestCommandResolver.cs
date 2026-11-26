using System.Text.RegularExpressions;
using NightCodeForge.Benchmark.Cli.Models;
using NightCodeForge.Core.Domain;

namespace NightCodeForge.Benchmark.Cli.Verification;

/// <summary>
/// Resolves verification test commands across supported language stacks, including subdirectory
/// <c>.sln</c>/<c>.csproj</c> discovery, Vitest unit-config detection, and containerized dependency setup hooks.
/// </summary>
internal static partial class BenchmarkTestCommandResolver
{
    private const string PythonVenvDir = "/tmp/ncf-bench-venv";
    internal const string PythonPipConstraintsPath = "/tmp/ncf-pip-constraints.txt";
    private const string PythonPipConstraintsMarker = "/tmp/ncf-bench-venv/.ncf-pip-constraints";
    private const string SystemCaBundlePath = "/etc/ssl/certs/ca-certificates.crt";
    private const string DefaultSetuptoolsScmPretendVersion = "9.9.9";
    internal const string NpmGlobalPrefix = "/tmp/ncf-npm-global";
    internal const string NpmCacheDir = "/tmp/ncf-npm-cache";
    internal const string PnpmStoreDir = "/tmp/ncf-pnpm-store";
    private const string NodeDepsReadyMarker = "node_modules/.ncf-bench-deps-ok";
    private const string JestJUnitPrefix = "/tmp/ncf-jest-junit";
    private const string MochaJUnitPrefix = "/tmp/ncf-mocha-junit";

    private static readonly char[] ShellSpecialChars =
    [
        '"', '\'', '$', '`', '\\', '(', ')', '&', ';', '|', '<', '>', '*', '?', '[', ']'
    ];

    private static readonly string[] VitestUnitConfigCandidates =
    [
        "vitest.unit.config.ts",
        "vitest.unit.config.mts",
        "vitest.unit.config.js",
        "vitest.unit.config.mjs"
    ];

    private static readonly HashSet<string> SkippedDiscoveryDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git",
        "bin",
        "obj",
        "node_modules",
        ".venv",
        "__pycache__",
        "TestResults",
        BenchmarkPaths.BenchmarkResultDirRelative
    };

    private static readonly string[] LowPrioritySubstringTokens =
    [
        "sample",
        "benchmark",
        "example"
    ];

    private static readonly HashSet<string> LowPriorityExactSegmentNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "docs",
        "doc",
        "resources",
        "scripts",
        "tools"
    };

    private static readonly string[] AuxiliarySolutionNameTokens =
    [
        ".analyzer",
        ".sandbox",
        ".benchmarks",
        ".samples"
    ];

    [GeneratedRegex(@"^\s*dotnet\s+test(?<rest>(?:\s+.*)?)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BareDotNetTestRegex();

    [GeneratedRegex(@"(?:^|\s)--filter(?:\s|=|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExplicitDotNetFilterFlagRegex();

    [GeneratedRegex(@"^\s*cargo\s+test(?<rest>(?:\s+.*)?)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CargoTestCommandRegex();

    [GeneratedRegex(@"^[A-Za-z0-9_:-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeRustTestTokenRegex();

    [GeneratedRegex(@"^\s*(?:[A-Za-z_][A-Za-z0-9_]*=(?:""[^""]*""|'[^']*'|\S+)\s+)*pytest(?<rest>(?:\s+.*)?)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PytestCommandRegex();

    [GeneratedRegex(@"(?:^|\s)-k(?:\s|=|$)", RegexOptions.CultureInvariant)]
    private static partial Regex ExplicitPytestKFlagRegex();

    [GeneratedRegex(@"^[A-Za-z0-9_]+$", RegexOptions.CultureInvariant)]
    private static partial Regex SafePythonIdentifierRegex();

    [GeneratedRegex(@"\.(?:sln|slnx|csproj|fsproj|vbproj)(?:\s|$|[""'])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DotNetProjectOrSolutionRefRegex();

    [GeneratedRegex(@"Project\([^)]+\)\s*=\s*""(?<name>[^""]+)""\s*,\s*""(?<path>[^""]+\.(?:csproj|fsproj|vbproj))""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SlnProjectEntryRegex();

    [GeneratedRegex(@"<Project\b[^>]*\bPath\s*=\s*(?<q>[""'])(?<path>[^""']+\.(?:csproj|fsproj|vbproj))\k<q>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SlnxProjectEntryRegex();

    [GeneratedRegex(@"(?<prefix>\bvitest\s+run)(?<rest>.*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VitestRunRegex();

    [GeneratedRegex(@"(?:^|\s)(?:-c|--config)(?:\s|=|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExplicitConfigFlagRegex();

    [GeneratedRegex(@"(?:^|\s)(?:-R|--reporter)(?:\s|=|$)", RegexOptions.CultureInvariant)]
    private static partial Regex ExplicitMochaReporterRegex();

    [GeneratedRegex(@"^\s*(?:[A-Za-z_][A-Za-z0-9_]*=(?:""[^""]*""|'[^']*'|\S+)\s+)*npm\s+run\s+test:mocha(?<rest>(?:\s+.*)?)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NpmRunTestMochaRegex();

    [GeneratedRegex(@"^\s*(?:[A-Za-z_][A-Za-z0-9_]*=(?:""[^""]*""|'[^']*'|\S+)\s+)*(?:npx\s+(?:--\s+)?)?mocha(?<rest>(?:\s+.*)?)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MochaRunRegex();

    [GeneratedRegex(@"(?:^|\s)--(?:\s|$)", RegexOptions.CultureInvariant)]
    private static partial Regex DoubleDashSeparatorRegex();

    [GeneratedRegex(@"(?:^|[\s;&|()])yarn(?:\s|$|[;&|)])", RegexOptions.CultureInvariant)]
    private static partial Regex YarnCommandTokenRegex();

    [GeneratedRegex(@"(?:^|[\s;&|()])pnpm(?:\s|$|[;&|)])", RegexOptions.CultureInvariant)]
    private static partial Regex PnpmCommandTokenRegex();

    internal static string ResolveTestCommand(
        BenchmarkInstance instance,
        string workspacePath,
        ExecutionMode executionMode = ExecutionMode.ContainerSandbox,
        bool failToPassOnly = false)
    {
        var normalizedLanguage = BenchmarkLanguages.Normalize(instance.Language);
        var resultsDir = executionMode == ExecutionMode.ContainerSandbox
            ? BenchmarkPaths.ContainerResultsDir
            : BenchmarkPaths.BenchmarkResultDirRelative;

        IReadOnlyList<string>? dotNetFilterTests = failToPassOnly && instance.FailToPass.Count > 0
            ? instance.FailToPass
            : null;

        if (!string.IsNullOrWhiteSpace(instance.CustomTestCommand))
        {
            var custom = instance.CustomTestCommand.Trim();
            if (string.Equals(normalizedLanguage, BenchmarkLanguages.CSharp, StringComparison.OrdinalIgnoreCase))
            {
                var csharpCmd = ApplyDiscoveredDotNetTargetsToCustomCommand(custom, workspacePath, executionMode, dotNetFilterTests);
                return failToPassOnly
                    ? ApplyFailToPassFilter(csharpCmd, instance, workspacePath, executionMode)
                    : csharpCmd;
            }

            if (normalizedLanguage is BenchmarkLanguages.TypeScript or BenchmarkLanguages.JavaScript)
            {
                var withVitest = ApplyVitestConfigToCustomCommand(custom, workspacePath);
                var nodeCmd = ApplyMochaJUnitReporterToCustomCommand(withVitest, instance, resultsDir);
                return failToPassOnly
                    ? ApplyFailToPassFilter(nodeCmd, instance, workspacePath, executionMode)
                    : nodeCmd;
            }

            return failToPassOnly
                ? ApplyFailToPassFilter(custom, instance, workspacePath, executionMode)
                : custom;
        }

        var resolved = normalizedLanguage switch
        {
            BenchmarkLanguages.CSharp => ResolveCSharpTestCommand(workspacePath, resultsDir, executionMode, dotNetFilterTests),
            BenchmarkLanguages.Python => $"pytest --junitxml={resultsDir}/{BenchmarkPaths.JUnitFileName}",
            BenchmarkLanguages.TypeScript or BenchmarkLanguages.JavaScript => ResolveNodeTestCommand(workspacePath, resultsDir),
            BenchmarkLanguages.Rust => "cargo test --no-fail-fast",
            _ => throw new BenchmarkVerificationException(
                $"No default test command configured for language '{instance.Language}'. Provide custom_test_command.",
                instance.InstanceId)
        };

        if (!failToPassOnly || string.Equals(normalizedLanguage, BenchmarkLanguages.CSharp, StringComparison.OrdinalIgnoreCase))
        {
            return resolved;
        }

        return ApplyFailToPassFilter(resolved, instance, workspacePath, executionMode);
    }

    internal static string ResolveAgentTestCommand(
        BenchmarkInstance instance,
        string workspacePath,
        ExecutionMode executionMode = ExecutionMode.ContainerSandbox)
    {
        var normalizedLanguage = BenchmarkLanguages.Normalize(instance.Language);

        if (!string.IsNullOrWhiteSpace(instance.CustomTestCommand))
        {
            var custom = StripVerifierReportingFlags(instance.CustomTestCommand.Trim(), normalizedLanguage);
            if (string.Equals(normalizedLanguage, BenchmarkLanguages.CSharp, StringComparison.OrdinalIgnoreCase))
            {
                return ApplyDiscoveredDotNetTargetsToCustomCommand(custom, workspacePath, executionMode);
            }

            if (normalizedLanguage is BenchmarkLanguages.TypeScript or BenchmarkLanguages.JavaScript)
            {
                return ApplyVitestConfigToCustomCommand(custom, workspacePath);
            }

            return custom;
        }

        return normalizedLanguage switch
        {
            BenchmarkLanguages.CSharp => ResolveAgentCSharpTestCommand(workspacePath),
            BenchmarkLanguages.Python => CodeStackPresets.Python.TestCommand,
            BenchmarkLanguages.TypeScript or BenchmarkLanguages.JavaScript => ResolveAgentNodeTestCommand(workspacePath),
            BenchmarkLanguages.Rust => "cargo test --no-fail-fast",
            _ => throw new BenchmarkVerificationException(
                $"No default test command configured for language '{instance.Language}'. Provide custom_test_command.",
                instance.InstanceId)
        };
    }

    internal static string WrapWithContainerDependencySetup(
        string testCommand,
        string language,
        ExecutionMode executionMode) =>
        WrapWithContainerDependencySetup(testCommand, language, executionMode, pipConstraints: null);

    internal static string WrapWithContainerDependencySetup(
        string testCommand,
        string language,
        ExecutionMode executionMode,
        IReadOnlyList<string>? pipConstraints)
    {
        if (executionMode != ExecutionMode.ContainerSandbox || string.IsNullOrWhiteSpace(testCommand))
        {
            return testCommand;
        }

        var normalizedLanguage = BenchmarkLanguages.Normalize(language);
        return normalizedLanguage switch
        {
            BenchmarkLanguages.Python => WrapPythonContainerCommand(testCommand, pipConstraints),
            BenchmarkLanguages.TypeScript or BenchmarkLanguages.JavaScript => WrapNodeContainerCommand(testCommand),
            _ => testCommand
        };
    }

    internal static IReadOnlyList<string> DiscoverDotNetTargets(string workspacePath)
    {
        if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath))
        {
            return Array.Empty<string>();
        }

        if (HasRootDotNetProjectOrSolution(workspacePath))
        {
            return Array.Empty<string>();
        }

        var solutions = new List<string>();
        var testProjects = new List<string>();

        foreach (var fullPath in EnumerateFilesSkippingIgnoredDirs(workspacePath))
        {
            var ext = Path.GetExtension(fullPath);
            if (!ext.Equals(".sln", StringComparison.OrdinalIgnoreCase) &&
                !ext.Equals(".slnx", StringComparison.OrdinalIgnoreCase) &&
                !ext.Equals(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var relPath = Path.GetRelativePath(workspacePath, fullPath).Replace('\\', '/');
            if (ext.Equals(".sln", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".slnx", StringComparison.OrdinalIgnoreCase))
            {
                solutions.Add(relPath);
            }
            else if (IsTestProjectPath(relPath))
            {
                testProjects.Add(relPath);
            }
        }

        if (solutions.Count > 0)
        {
            var bestSolution = solutions
                .Select(p =>
                {
                    var (testCount, totalCount) = InspectSolutionProjects(workspacePath, p);
                    return (
                        Path: p,
                        IsLowPriority: IsLowPriorityTarget(p),
                        HasTests: testCount > 0,
                        IsAuxiliary: IsAuxiliarySolution(p),
                        TestCount: testCount,
                        TotalCount: totalCount,
                        Depth: CountPathSegments(p));
                })
                .OrderBy(c => c.IsLowPriority)
                .ThenByDescending(c => c.HasTests)
                .ThenBy(c => c.IsAuxiliary)
                .ThenByDescending(c => c.TestCount)
                .ThenByDescending(c => c.TotalCount)
                .ThenBy(c => c.Depth)
                .ThenBy(c => c.Path, StringComparer.OrdinalIgnoreCase)
                .First()
                .Path;
            return [bestSolution];
        }

        if (testProjects.Count > 0)
        {
            var preferredTestProjects = testProjects.Where(p => !IsLowPriorityTarget(p)).ToList();
            var candidates = preferredTestProjects.Count > 0 ? preferredTestProjects : testProjects;
            return candidates
                .OrderBy(CountPathSegments)
                .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return Array.Empty<string>();
    }

    internal static string? ResolveVitestUnitConfig(string workspacePath)
    {
        if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath))
        {
            return null;
        }

        foreach (var candidate in VitestUnitConfigCandidates)
        {
            if (File.Exists(Path.Combine(workspacePath, candidate)))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string ResolveCSharpTestCommand(
        string workspacePath,
        string resultsDir,
        ExecutionMode executionMode,
        IReadOnlyList<string>? failToPassTests = null)
    {
        var filterArg = FormatDotNetFilterArgument(failToPassTests, executionMode);
        var filterSuffix = string.IsNullOrEmpty(filterArg) ? string.Empty : $" {filterArg}";
        var targets = DiscoverDotNetTargets(workspacePath);
        if (targets.Count == 0)
        {
            return $"dotnet build --nologo && dotnet test --no-build --logger trx --results-directory {resultsDir}{filterSuffix}";
        }

        var commands = targets.Select(target =>
        {
            var quotedTarget = QuotePathArgument(target);
            return $"dotnet build {quotedTarget} --nologo && dotnet test {quotedTarget} --no-build --logger trx --results-directory {resultsDir}{filterSuffix}";
        }).ToList();

        return JoinMultiTargetCommands(commands, executionMode);
    }

    private static string ApplyDiscoveredDotNetTargetsToCustomCommand(
        string customCommand,
        string workspacePath,
        ExecutionMode executionMode,
        IReadOnlyList<string>? failToPassTests = null)
    {
        var match = BareDotNetTestRegex().Match(customCommand);
        if (!match.Success || DotNetProjectOrSolutionRefRegex().IsMatch(customCommand))
        {
            return customCommand;
        }

        var rest = match.Groups["rest"].Value;
        if (HasLeadingPositionalArgument(rest) || ContainsUnquotedShellOperator(rest))
        {
            return customCommand;
        }

        var targets = DiscoverDotNetTargets(workspacePath);
        if (targets.Count == 0)
        {
            return customCommand;
        }

        var filterArg = !ExplicitDotNetFilterFlagRegex().IsMatch(rest) && !DoubleDashSeparatorRegex().IsMatch(rest)
            ? FormatDotNetFilterArgument(failToPassTests, executionMode)
            : string.Empty;
        var filterSuffix = string.IsNullOrEmpty(filterArg) ? string.Empty : $" {filterArg}";

        var commands = targets
            .Select(target => $"dotnet test {QuotePathArgument(target)}{rest}{filterSuffix}")
            .ToList();

        return JoinMultiTargetCommands(commands, executionMode);
    }

    internal static string ApplyFailToPassFilter(
        string command,
        BenchmarkInstance instance,
        string workspacePath,
        ExecutionMode executionMode)
    {
        ArgumentNullException.ThrowIfNull(instance);

        if (string.IsNullOrWhiteSpace(command) || instance.FailToPass.Count == 0)
        {
            return command;
        }

        var normalizedLanguage = BenchmarkLanguages.Normalize(instance.Language);
        return normalizedLanguage switch
        {
            BenchmarkLanguages.CSharp => ApplyDotNetFailToPassFilter(command, instance.FailToPass, executionMode),
            BenchmarkLanguages.Rust => ApplyRustFailToPassFilter(command, instance.FailToPass),
            BenchmarkLanguages.Python => ApplyPythonFailToPassFilter(command, instance.FailToPass, executionMode),
            BenchmarkLanguages.TypeScript or BenchmarkLanguages.JavaScript => ApplyVitestFailToPassFilter(command, instance.FailToPass, workspacePath),
            _ => command
        };
    }

    internal static string? BuildDotNetFailToPassFilterExpression(IReadOnlyList<string>? failToPass)
    {
        if (failToPass == null || failToPass.Count == 0)
        {
            return null;
        }

        var terms = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawTest in failToPass)
        {
            if (string.IsNullOrWhiteSpace(rawTest))
            {
                continue;
            }

            var trimmed = rawTest.Trim();
            var parenIndex = trimmed.IndexOf('(');
            if (parenIndex >= 0)
            {
                trimmed = trimmed[..parenIndex].TrimEnd();
            }

            if (string.IsNullOrWhiteSpace(trimmed))
            {
                continue;
            }

            var escaped = EscapeVsTestFilterValue(trimmed);
            if (seen.Add(escaped))
            {
                terms.Add($"FullyQualifiedName~{escaped}");
            }
        }

        return terms.Count > 0 ? string.Join("|", terms) : null;
    }

    private static string EscapeVsTestFilterValue(string value)
    {
        var sb = new System.Text.StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (ch is '\\' or '(' or ')' or '&' or '|' or '=' or '!' or '~')
            {
                sb.Append('\\');
            }
            sb.Append(ch);
        }
        return sb.ToString();
    }

    private static string FormatDotNetFilterArgument(IReadOnlyList<string>? failToPass, ExecutionMode executionMode)
    {
        var expr = BuildDotNetFailToPassFilterExpression(failToPass);
        if (string.IsNullOrWhiteSpace(expr))
        {
            return string.Empty;
        }

        return executionMode == ExecutionMode.BareHost
            ? $"--filter \"{expr.Replace("\"", "\\\"", StringComparison.Ordinal)}\""
            : $"--filter {QuoteSinglePosix(expr)}";
    }

    private static string ApplyDotNetFailToPassFilter(
        string command,
        IReadOnlyList<string> failToPass,
        ExecutionMode executionMode)
    {
        var match = BareDotNetTestRegex().Match(command);
        if (!match.Success)
        {
            return command;
        }

        var rest = match.Groups["rest"].Value;
        if (ContainsUnquotedShellOperator(rest) ||
            ExplicitDotNetFilterFlagRegex().IsMatch(rest) ||
            DoubleDashSeparatorRegex().IsMatch(rest))
        {
            return command;
        }

        var filterArg = FormatDotNetFilterArgument(failToPass, executionMode);
        return string.IsNullOrEmpty(filterArg) ? command : $"{command} {filterArg}";
    }

    private static string ApplyRustFailToPassFilter(string command, IReadOnlyList<string> failToPass)
    {
        var match = CargoTestCommandRegex().Match(command);
        if (!match.Success)
        {
            return command;
        }

        var rest = match.Groups["rest"].Value;
        if (ContainsUnquotedShellOperator(rest) || DoubleDashSeparatorRegex().IsMatch(rest))
        {
            return command;
        }

        var tokens = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rawTest in failToPass)
        {
            if (string.IsNullOrWhiteSpace(rawTest))
            {
                continue;
            }

            var token = rawTest.Trim();
            if (!SafeRustTestTokenRegex().IsMatch(token))
            {
                return command;
            }

            if (seen.Add(token))
            {
                tokens.Add(token);
            }
        }

        return tokens.Count > 0 ? $"{command} -- {string.Join(" ", tokens)}" : command;
    }

    private static string ApplyPythonFailToPassFilter(
        string command,
        IReadOnlyList<string> failToPass,
        ExecutionMode executionMode)
    {
        var match = PytestCommandRegex().Match(command);
        if (!match.Success)
        {
            return command;
        }

        var rest = match.Groups["rest"].Value;
        if (ContainsUnquotedShellOperator(rest) ||
            ExplicitPytestKFlagRegex().IsMatch(rest) ||
            DoubleDashSeparatorRegex().IsMatch(rest))
        {
            return command;
        }

        var identifiers = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rawTest in failToPass)
        {
            if (string.IsNullOrWhiteSpace(rawTest))
            {
                continue;
            }

            var candidate = rawTest.Trim();
            var doubleColonIndex = candidate.LastIndexOf("::", StringComparison.Ordinal);
            if (doubleColonIndex >= 0)
            {
                candidate = candidate[(doubleColonIndex + 2)..];
            }

            var bracketIndex = candidate.IndexOf('[');
            if (bracketIndex >= 0)
            {
                candidate = candidate[..bracketIndex];
            }

            candidate = candidate.Trim();
            if (!SafePythonIdentifierRegex().IsMatch(candidate))
            {
                return command;
            }

            if (seen.Add(candidate))
            {
                identifiers.Add(candidate);
            }
        }

        if (identifiers.Count == 0)
        {
            return command;
        }

        var expr = string.Join(" or ", identifiers);
        return executionMode == ExecutionMode.BareHost
            ? $"{command} -k \"{expr}\""
            : $"{command} -k {QuoteSinglePosix(expr)}";
    }

    private static string ApplyVitestFailToPassFilter(
        string command,
        IReadOnlyList<string> failToPass,
        string workspacePath)
    {
        if (string.IsNullOrWhiteSpace(workspacePath) ||
            !VitestRunRegex().IsMatch(command) ||
            ContainsUnquotedShellOperator(command) ||
            DoubleDashSeparatorRegex().IsMatch(command))
        {
            return command;
        }

        var testFiles = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawTest in failToPass)
        {
            if (string.IsNullOrWhiteSpace(rawTest))
            {
                continue;
            }

            var separatorIdx = rawTest.IndexOf(" > ", StringComparison.Ordinal);
            if (separatorIdx <= 0)
            {
                return command;
            }

            var relPath = rawTest[..separatorIdx].Trim().Replace('\\', '/').TrimStart('/');
            if (string.IsNullOrWhiteSpace(relPath) ||
                Path.IsPathRooted(relPath) ||
                relPath.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(seg => seg == ".."))
            {
                return command;
            }

            var fullPath = Path.Combine(workspacePath, relPath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(fullPath))
            {
                return command;
            }

            if (seen.Add(relPath))
            {
                testFiles.Add(relPath);
            }
        }

        if (testFiles.Count == 0)
        {
            return command;
        }

        var toAppend = testFiles
            .Where(f => !command.Contains(f, StringComparison.OrdinalIgnoreCase))
            .Select(QuotePathArgument)
            .ToList();

        return toAppend.Count > 0 ? $"{command} {string.Join(" ", toAppend)}" : command;
    }

    private static string JoinMultiTargetCommands(IReadOnlyList<string> commands, ExecutionMode executionMode)
    {
        if (commands.Count == 1)
        {
            return commands[0];
        }

        if (executionMode == ExecutionMode.BareHost)
        {
            return string.Join(" && ", commands);
        }

        return "( ncf_rc=0; " +
               string.Join("; ", commands.Select(cmd => $"{{ {cmd}; }} || ncf_rc=$?")) +
               "; exit $ncf_rc )";
    }

    private static string ResolveAgentCSharpTestCommand(string workspacePath)
    {
        var targets = DiscoverDotNetTargets(workspacePath);
        if (targets.Count == 0)
        {
            return CodeStackPresets.DotNet.TestCommand;
        }

        return string.Join(" && ", targets.Select(target => $"dotnet test {QuotePathArgument(target)} --no-build"));
    }

    private static string ResolveAgentNodeTestCommand(string workspacePath)
    {
        var pkgJsonPath = Path.Combine(workspacePath, "package.json");
        if (File.Exists(pkgJsonPath))
        {
            var content = File.ReadAllText(pkgJsonPath);
            if (content.Contains("vitest", StringComparison.OrdinalIgnoreCase))
            {
                var unitConfig = ResolveVitestUnitConfig(workspacePath);
                return unitConfig != null ? $"npx vitest run -c {unitConfig}" : "npx vitest run";
            }

            if (content.Contains("jest", StringComparison.OrdinalIgnoreCase))
            {
                return "npx jest --ci";
            }

            if (content.Contains("mocha", StringComparison.OrdinalIgnoreCase))
            {
                return content.Contains("\"test:mocha\"", StringComparison.OrdinalIgnoreCase)
                    ? "npm run test:mocha"
                    : "npx mocha";
            }
        }

        return CodeStackPresets.Node.TestCommand;
    }

    private static string ResolveNodeTestCommand(string workspacePath, string resultsDir)
    {
        var pkgJsonPath = Path.Combine(workspacePath, "package.json");
        if (File.Exists(pkgJsonPath))
        {
            var content = File.ReadAllText(pkgJsonPath);
            if (content.Contains("vitest", StringComparison.OrdinalIgnoreCase))
            {
                var unitConfig = ResolveVitestUnitConfig(workspacePath);
                var configFlag = unitConfig != null ? $"-c {unitConfig} " : string.Empty;
                return $"npx vitest run {configFlag}--reporter=junit --outputFile={resultsDir}/{BenchmarkPaths.JUnitFileName}";
            }

            if (content.Contains("jest", StringComparison.OrdinalIgnoreCase))
            {
                return $"JEST_JUNIT_OUTPUT_DIR={resultsDir} JEST_JUNIT_OUTPUT_NAME={BenchmarkPaths.JUnitFileName} npx jest --ci --reporters=default --reporters=jest-junit";
            }

            if (content.Contains("mocha", StringComparison.OrdinalIgnoreCase))
            {
                return content.Contains("\"test:mocha\"", StringComparison.OrdinalIgnoreCase)
                    ? $"MOCHA_FILE={resultsDir}/{BenchmarkPaths.JUnitFileName} npm run test:mocha -- --reporter mocha-junit-reporter"
                    : $"MOCHA_FILE={resultsDir}/{BenchmarkPaths.JUnitFileName} npx mocha --reporter mocha-junit-reporter";
            }
        }

        return "npm test";
    }

    private static string ApplyVitestConfigToCustomCommand(string customCommand, string workspacePath)
    {
        if (ExplicitConfigFlagRegex().IsMatch(customCommand))
        {
            return customCommand;
        }

        var match = VitestRunRegex().Match(customCommand);
        if (!match.Success)
        {
            return customCommand;
        }

        var unitConfig = ResolveVitestUnitConfig(workspacePath);
        if (unitConfig == null)
        {
            return customCommand;
        }

        var prefixIndex = match.Groups["prefix"].Index + match.Groups["prefix"].Length;
        return $"{customCommand[..prefixIndex]} -c {unitConfig}{customCommand[prefixIndex..]}";
    }

    private static string ApplyMochaJUnitReporterToCustomCommand(
        string customCommand,
        BenchmarkInstance instance,
        string resultsDir)
    {
        if (instance.ResolveEffectiveTestResultFormat() != TestResultFormat.JUnitXml ||
            customCommand.Contains("mocha-junit-reporter", StringComparison.OrdinalIgnoreCase) ||
            ExplicitMochaReporterRegex().IsMatch(customCommand))
        {
            return customCommand;
        }

        string? rewritten = null;
        var npmMatch = NpmRunTestMochaRegex().Match(customCommand);
        if (npmMatch.Success)
        {
            var rest = npmMatch.Groups["rest"].Value;
            if (!ContainsUnquotedShellOperator(rest))
            {
                rewritten = DoubleDashSeparatorRegex().IsMatch(rest)
                    ? $"{customCommand} --reporter mocha-junit-reporter"
                    : $"{customCommand} -- --reporter mocha-junit-reporter";
            }
        }
        else
        {
            var mochaMatch = MochaRunRegex().Match(customCommand);
            if (mochaMatch.Success && !ContainsUnquotedShellOperator(mochaMatch.Groups["rest"].Value))
            {
                rewritten = $"{customCommand} --reporter mocha-junit-reporter";
            }
        }

        if (rewritten == null)
        {
            return customCommand;
        }

        return customCommand.Contains("MOCHA_FILE=", StringComparison.Ordinal)
            ? rewritten
            : $"MOCHA_FILE={resultsDir}/{BenchmarkPaths.JUnitFileName} {rewritten}";
    }

    private static string WrapPythonContainerCommand(string testCommand, IReadOnlyList<string>? pipConstraints)
    {
        var caBundleExport =
            $"if [ -z \"${{REQUESTS_CA_BUNDLE:-}}\" ] && [ -f {SystemCaBundlePath} ]; then export REQUESTS_CA_BUNDLE={SystemCaBundlePath}; fi";
        var scmVersionGuard =
            $"if [ -z \"${{SETUPTOOLS_SCM_PRETEND_VERSION:-}}\" ] && grep -qE \"setuptools[-_]scm\" pyproject.toml setup.cfg setup.py 2>/dev/null && ! git describe --tags --abbrev=0 >/dev/null 2>&1; then export SETUPTOOLS_SCM_PRETEND_VERSION={DefaultSetuptoolsScmPretendVersion}; fi";

        var sanitizedConstraints = pipConstraints?
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c.Trim())
            .ToList() ?? [];

        string constraintSetup = string.Empty;
        string staleVenvGuard;
        string constraintArg = string.Empty;
        string constraintMarkerUpdate = string.Empty;

        if (sanitizedConstraints.Count > 0)
        {
            var quotedLines = string.Join(" ", sanitizedConstraints.Select(QuoteSinglePosix));
            constraintSetup =
                $"printf '%s\\n' {quotedLines} > {PythonPipConstraintsPath} && export PIP_CONSTRAINT={PythonPipConstraintsPath} && ";
            staleVenvGuard =
                $"if [ -d {PythonVenvDir} ] && ! cmp -s {PythonPipConstraintsPath} {PythonPipConstraintsMarker} 2>/dev/null; then rm -rf {PythonVenvDir}; fi && ";
            constraintArg = $"-c {PythonPipConstraintsPath} ";
            constraintMarkerUpdate = $"cp {PythonPipConstraintsPath} {PythonPipConstraintsMarker} && ";
        }
        else
        {
            staleVenvGuard =
                $"if [ -f {PythonPipConstraintsMarker} ]; then rm -rf {PythonVenvDir} {PythonPipConstraintsPath}; fi && ";
        }

        return $"{caBundleExport} && {scmVersionGuard} && {constraintSetup}" +
               "if ! command -v pytest >/dev/null 2>&1; then " +
               $"{staleVenvGuard}" +
               $"if [ ! -d {PythonVenvDir}/bin ]; then python3 -m venv {PythonVenvDir}; fi && " +
               "(for req in requirements/tests.txt requirements/test.txt requirements-test.txt test-requirements.txt requirements/dev.txt requirements-dev.txt requirements.txt; do " +
               $"[ -f \"$req\" ] && {PythonVenvDir}/bin/pip install -q {constraintArg}-r \"$req\" || true; done) && " +
               "(if [ -f pyproject.toml ] || [ -f setup.py ] || [ -f setup.cfg ]; then " +
               $"{PythonVenvDir}/bin/pip install -q {constraintArg}-e . || {PythonVenvDir}/bin/pip install -q {constraintArg}--no-build-isolation -e . || true; fi) && " +
               $"if [ ! -x {PythonVenvDir}/bin/pytest ]; then {PythonVenvDir}/bin/pip install -q {constraintArg}pytest; fi && " +
               $"{constraintMarkerUpdate}" +
               $"export PATH=\"{PythonVenvDir}/bin:$PATH\"; " +
               $"fi && {WrapPosixCompoundBody(testCommand)}";
    }

    private static string QuoteSinglePosix(string value) =>
        "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private static string BuildWritableNpmCacheGuard() =>
        $"if [ -z \"${{npm_config_cache:-}}\" ] || [ ! -w \"${{npm_config_cache}}\" ]; then mkdir -p {NpmCacheDir} && export npm_config_cache=\"{NpmCacheDir}\"; fi";

    private static string BuildWritablePnpmStoreGuard() =>
        $"if [ -z \"${{npm_config_store_dir:-}}\" ] || [ ! -w \"${{npm_config_store_dir}}\" ]; then mkdir -p {PnpmStoreDir} && export npm_config_store_dir=\"{PnpmStoreDir}\"; fi";

    private static string BuildEnsureNodePackageManagerGuard(string packageManager) =>
        $"if ! command -v {packageManager} >/dev/null 2>&1; then if [ ! -x {NpmGlobalPrefix}/bin/{packageManager} ]; then mkdir -p {NpmGlobalPrefix} && npm install -g --prefix {NpmGlobalPrefix} --ignore-scripts --no-audit --no-fund {packageManager}; fi && export PATH=\"{NpmGlobalPrefix}/bin:$PATH\"; fi";

    private static string WrapNodeContainerCommand(string testCommand)
    {
        var pathGuard =
            $"if [ -d \"{NpmGlobalPrefix}/bin\" ]; then case \":$PATH:\" in *\":{NpmGlobalPrefix}/bin:\"*) ;; *) export PATH=\"{NpmGlobalPrefix}/bin:$PATH\" ;; esac; fi";

        var depsReadyCheck =
            "ncf_node_deps_ready=0 && if [ -d node_modules ]; then " +
            $"if [ -f {NodeDepsReadyMarker} ] || {{ [ -d node_modules/.bin ] && {{ " +
            "{ [ -f pnpm-lock.yaml ] && [ -f node_modules/.modules.yaml ]; } || " +
            "{ [ ! -f pnpm-lock.yaml ] && [ -f yarn.lock ] && { [ -f node_modules/.yarn-integrity ] || [ -f node_modules/.yarn-state.yml ]; }; } || " +
            "{ [ ! -f pnpm-lock.yaml ] && [ ! -f yarn.lock ] && [ -f node_modules/.package-lock.json ]; }; }; }; then " +
            "ncf_node_deps_ready=1; fi; fi";

        var cacheSetup = $"mkdir -p {NpmGlobalPrefix}";

        var installStep =
            $"{BuildWritableNpmCacheGuard()} && {BuildWritablePnpmStoreGuard()} && " +
            $"{depsReadyCheck} && " +
            "if [ -f package.json ] && [ \"$ncf_node_deps_ready\" -eq 0 ]; then " +
            $"{cacheSetup} && " +
            "if [ -f pnpm-lock.yaml ]; then " +
            $"{BuildEnsureNodePackageManagerGuard("pnpm")} && " +
            "(pnpm install --frozen-lockfile || pnpm install --frozen-lockfile --ignore-scripts || pnpm install --ignore-scripts); " +
            "elif [ -f yarn.lock ]; then " +
            $"{BuildEnsureNodePackageManagerGuard("yarn")} && " +
            "(yarn install --frozen-lockfile || yarn install --frozen-lockfile --ignore-scripts || yarn install --ignore-scripts); " +
            "elif [ -f package-lock.json ]; then (npm ci || npm ci --ignore-scripts || npm install --ignore-scripts); " +
            "else (npm install || npm install --ignore-scripts); fi && " +
            $"if [ -d node_modules ]; then touch {NodeDepsReadyMarker}; fi; " +
            "fi";

        if (YarnCommandTokenRegex().IsMatch(testCommand))
        {
            installStep += $" && {BuildEnsureNodePackageManagerGuard("yarn")}";
        }

        if (PnpmCommandTokenRegex().IsMatch(testCommand))
        {
            installStep += $" && {BuildEnsureNodePackageManagerGuard("pnpm")}";
        }

        installStep = AppendIsolatedNodeReporterSetup(installStep, testCommand, "jest-junit", JestJUnitPrefix, "jest");
        installStep = AppendIsolatedNodeReporterSetup(installStep, testCommand, "mocha-junit-reporter", MochaJUnitPrefix, "mocha");

        return $"{pathGuard} && {installStep} && {WrapPosixCompoundBody(testCommand)}";
    }

    private static string AppendIsolatedNodeReporterSetup(
        string installStep,
        string testCommand,
        string packageName,
        string prefix,
        string peerRunnerPackage)
    {
        if (!testCommand.Contains(packageName, StringComparison.OrdinalIgnoreCase))
        {
            return installStep;
        }

        return installStep +
               $" && if [ -f package.json ] && [ ! -d node_modules/{packageName} ]; then " +
               $"if [ -d {prefix}/node_modules/{peerRunnerPackage} ]; then rm -rf {prefix}; fi; " +
               $"if [ ! -d {prefix}/node_modules/{packageName} ]; then mkdir -p {prefix} && {BuildWritableNpmCacheGuard()} && (npm install --prefix {prefix} --legacy-peer-deps --omit=peer --ignore-scripts --no-audit --no-fund --no-save {packageName} || true) && rm -rf {prefix}/node_modules/{peerRunnerPackage}; fi; " +
               $"if [ -d {prefix}/node_modules/{packageName} ]; then export NODE_PATH=\"$PWD/node_modules:{prefix}/node_modules${{NODE_PATH:+:$NODE_PATH}}\"; fi; " +
               "fi";
    }

    private static string WrapPosixCompoundBody(string testCommand)
    {
        var trimmed = testCommand.Trim().TrimEnd(';').TrimEnd();
        return $"{{ {trimmed}; }}";
    }

    private static bool HasRootDotNetProjectOrSolution(string workspacePath)
    {
        try
        {
            return Directory.EnumerateFiles(workspacePath, "*", SearchOption.TopDirectoryOnly)
                .Any(f =>
                {
                    var ext = Path.GetExtension(f);
                    return ext.Equals(".sln", StringComparison.OrdinalIgnoreCase) ||
                           ext.Equals(".slnx", StringComparison.OrdinalIgnoreCase) ||
                           ext.Equals(".csproj", StringComparison.OrdinalIgnoreCase) ||
                           ext.Equals(".fsproj", StringComparison.OrdinalIgnoreCase) ||
                           ext.Equals(".vbproj", StringComparison.OrdinalIgnoreCase);
                });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static IEnumerable<string> EnumerateFilesSkippingIgnoredDirs(string rootDir)
    {
        var pending = new Stack<string>();
        pending.Push(rootDir);

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(current, "*", SearchOption.TopDirectoryOnly);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                yield return file;
            }

            IEnumerable<string> subDirs;
            try
            {
                subDirs = Directory.EnumerateDirectories(current, "*", SearchOption.TopDirectoryOnly);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var subDir in subDirs)
            {
                var dirName = Path.GetFileName(subDir);
                if (SkippedDiscoveryDirectories.Contains(dirName))
                {
                    continue;
                }

                try
                {
                    if ((File.GetAttributes(subDir) & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                pending.Push(subDir);
            }
        }
    }

    private static bool HasLeadingPositionalArgument(string rest)
    {
        var trimmed = rest.TrimStart();
        return trimmed.Length > 0 && trimmed[0] != '-';
    }

    private static bool ContainsUnquotedShellOperator(string text)
    {
        bool inSingleQuote = false;
        bool inDoubleQuote = false;
        bool escaped = false;

        foreach (var ch in text)
        {
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (inSingleQuote)
            {
                if (ch == '\'')
                {
                    inSingleQuote = false;
                }
                continue;
            }

            if (ch == '\\')
            {
                escaped = true;
                continue;
            }

            if (inDoubleQuote)
            {
                if (ch == '"')
                {
                    inDoubleQuote = false;
                }
                continue;
            }

            if (ch == '\'')
            {
                inSingleQuote = true;
            }
            else if (ch == '"')
            {
                inDoubleQuote = true;
            }
            else if (ch is ';' or '|' or '&' or '\n' or '\r')
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsTestProjectPath(string relativePosixPath)
    {
        var segments = relativePosixPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(seg => seg.Equals("test", StringComparison.OrdinalIgnoreCase) ||
                                seg.Equals("tests", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var fileName = Path.GetFileNameWithoutExtension(relativePosixPath);
        return fileName.Contains("test", StringComparison.OrdinalIgnoreCase);
    }

    private static int CountPathSegments(string relativePosixPath) =>
        relativePosixPath.Count(ch => ch == '/');

    private static bool IsLowPriorityTarget(string relativePosixPath)
    {
        var segments = relativePosixPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(seg =>
                LowPriorityExactSegmentNames.Contains(seg) ||
                LowPrioritySubstringTokens.Any(tok => seg.Contains(tok, StringComparison.OrdinalIgnoreCase))))
        {
            return true;
        }

        var fileNameWithoutExt = Path.GetFileNameWithoutExtension(relativePosixPath);
        return LowPriorityExactSegmentNames.Contains(fileNameWithoutExt);
    }

    private static bool IsAuxiliarySolution(string relativePosixPath)
    {
        var fileNameWithoutExt = Path.GetFileNameWithoutExtension(relativePosixPath);
        return AuxiliarySolutionNameTokens.Any(tok => fileNameWithoutExt.Contains(tok, StringComparison.OrdinalIgnoreCase));
    }

    private static (int TestProjectCount, int TotalProjectCount) InspectSolutionProjects(string workspacePath, string relativeSolutionPath)
    {
        var fullPath = Path.Combine(workspacePath, relativeSolutionPath.Replace('/', Path.DirectorySeparatorChar));
        string content;
        try
        {
            content = File.ReadAllText(fullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (0, 0);
        }

        int testCount = 0;
        int totalCount = 0;

        if (relativeSolutionPath.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
        {
            foreach (Match match in SlnxProjectEntryRegex().Matches(content))
            {
                totalCount++;
                var projPath = match.Groups["path"].Value.Replace('\\', '/');
                if (IsTestProjectPath(projPath))
                {
                    testCount++;
                }
            }

            return (testCount, totalCount);
        }

        foreach (Match match in SlnProjectEntryRegex().Matches(content))
        {
            totalCount++;
            var projName = match.Groups["name"].Value;
            var projPath = match.Groups["path"].Value.Replace('\\', '/');
            if (IsTestProjectPath(projPath) || projName.Contains("test", StringComparison.OrdinalIgnoreCase))
            {
                testCount++;
            }
        }

        return (testCount, totalCount);
    }

    internal static string StripVerifierReportingFlags(string command, string normalizedLanguage)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return command;
        }

        var tokens = TokenizeShellCommand(command);
        if (tokens.Count == 0)
        {
            return command;
        }

        var kept = new List<string>(tokens.Count);
        bool strippedNodeFlag = false;
        bool isCSharp = string.Equals(normalizedLanguage, BenchmarkLanguages.CSharp, StringComparison.OrdinalIgnoreCase);
        bool isNode = normalizedLanguage is BenchmarkLanguages.TypeScript or BenchmarkLanguages.JavaScript;
        bool isPython = string.Equals(normalizedLanguage, BenchmarkLanguages.Python, StringComparison.OrdinalIgnoreCase);

        for (int i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (IsShellOperatorToken(token))
            {
                if (strippedNodeFlag)
                {
                    TrimTrailingDoubleDash(kept);
                    strippedNodeFlag = false;
                }
                kept.Add(token);
                continue;
            }

            if (isCSharp)
            {
                if (IsFlagWithValue(token, "--logger", out var hasInlineValue, out _) ||
                    IsFlagWithValue(token, "-l", out hasInlineValue, out _) ||
                    IsFlagWithValue(token, "--results-directory", out hasInlineValue, out _))
                {
                    if (!hasInlineValue && i + 1 < tokens.Count && !IsShellOperatorToken(tokens[i + 1]))
                    {
                        i++;
                    }
                    continue;
                }
            }
            else if (isNode)
            {
                if (IsEnvAssignmentToken(token, "JEST_JUNIT_OUTPUT_DIR") ||
                    IsEnvAssignmentToken(token, "JEST_JUNIT_OUTPUT_NAME") ||
                    IsEnvAssignmentToken(token, "MOCHA_FILE"))
                {
                    continue;
                }

                if (IsFlagWithValue(token, "--reporters", out var hasReportersInline, out var reportersInlineVal))
                {
                    if (hasReportersInline)
                    {
                        if (string.Equals(UnquoteShellToken(reportersInlineVal), "jest-junit", StringComparison.OrdinalIgnoreCase))
                        {
                            strippedNodeFlag = true;
                            continue;
                        }
                    }
                    else if (i + 1 < tokens.Count &&
                             string.Equals(UnquoteShellToken(tokens[i + 1]), "jest-junit", StringComparison.OrdinalIgnoreCase))
                    {
                        strippedNodeFlag = true;
                        i++;
                        continue;
                    }
                }

                if (IsFlagWithValue(token, "--reporter", out var hasReporterInline, out var reporterInlineVal) ||
                    IsFlagWithValue(token, "-R", out hasReporterInline, out reporterInlineVal))
                {
                    var isLongFlag = token.StartsWith("--reporter", StringComparison.Ordinal);
                    if (hasReporterInline)
                    {
                        var val = UnquoteShellToken(reporterInlineVal);
                        if (string.Equals(val, "mocha-junit-reporter", StringComparison.OrdinalIgnoreCase) ||
                            (isLongFlag && string.Equals(val, "junit", StringComparison.OrdinalIgnoreCase)))
                        {
                            strippedNodeFlag = true;
                            continue;
                        }
                    }
                    else if (i + 1 < tokens.Count && !IsShellOperatorToken(tokens[i + 1]))
                    {
                        var nextVal = UnquoteShellToken(tokens[i + 1]);
                        if (string.Equals(nextVal, "mocha-junit-reporter", StringComparison.OrdinalIgnoreCase) ||
                            (isLongFlag && string.Equals(nextVal, "junit", StringComparison.OrdinalIgnoreCase)))
                        {
                            strippedNodeFlag = true;
                            i++;
                            continue;
                        }
                    }
                }

                if (IsFlagWithValue(token, "--outputFile", out var hasOutputFileInline, out _, StringComparison.OrdinalIgnoreCase))
                {
                    if (!hasOutputFileInline && i + 1 < tokens.Count && !IsShellOperatorToken(tokens[i + 1]))
                    {
                        i++;
                    }
                    strippedNodeFlag = true;
                    continue;
                }
            }
            else if (isPython)
            {
                if (IsFlagWithValue(token, "--junitxml", out var hasJunitXmlInline, out _, StringComparison.OrdinalIgnoreCase) ||
                    IsFlagWithValue(token, "--junit-xml", out hasJunitXmlInline, out _, StringComparison.OrdinalIgnoreCase))
                {
                    if (!hasJunitXmlInline && i + 1 < tokens.Count && !IsShellOperatorToken(tokens[i + 1]))
                    {
                        i++;
                    }
                    continue;
                }
            }

            kept.Add(token);
        }

        if (strippedNodeFlag)
        {
            TrimTrailingDoubleDash(kept);
        }

        return kept.Count > 0 ? string.Join(" ", kept) : command;
    }

    private static void TrimTrailingDoubleDash(List<string> tokens)
    {
        if (tokens.Count > 0 && string.Equals(tokens[^1], "--", StringComparison.Ordinal))
        {
            tokens.RemoveAt(tokens.Count - 1);
        }
    }

    private static bool IsShellOperatorToken(string token) =>
        token is "&&" or "||" or ";" or "|";

    private static bool IsEnvAssignmentToken(string token, string variableName) =>
        token.StartsWith(variableName + "=", StringComparison.Ordinal);

    private static bool IsFlagWithValue(
        string token,
        string flag,
        out bool hasInlineValue,
        out string inlineValue,
        StringComparison comparison = StringComparison.Ordinal)
    {
        if (string.Equals(token, flag, comparison))
        {
            hasInlineValue = false;
            inlineValue = string.Empty;
            return true;
        }

        if (token.Length >= flag.Length + 1 &&
            token.StartsWith(flag, comparison) &&
            (token[flag.Length] == '=' || token[flag.Length] == ':'))
        {
            hasInlineValue = true;
            inlineValue = token[(flag.Length + 1)..];
            return true;
        }

        hasInlineValue = false;
        inlineValue = string.Empty;
        return false;
    }

    private static string UnquoteShellToken(string token)
    {
        var trimmed = token.Trim();
        if (trimmed.Length >= 2 &&
            ((trimmed[0] == '"' && trimmed[^1] == '"') ||
             (trimmed[0] == '\'' && trimmed[^1] == '\'')))
        {
            return trimmed[1..^1];
        }
        return trimmed;
    }

    private static List<string> TokenizeShellCommand(string command)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        bool inSingleQuote = false;
        bool inDoubleQuote = false;
        bool escaped = false;

        void FlushCurrent()
        {
            if (current.Length > 0)
            {
                tokens.Add(current.ToString());
                current.Clear();
            }
        }

        for (int i = 0; i < command.Length; i++)
        {
            char ch = command[i];

            if (escaped)
            {
                current.Append(ch);
                escaped = false;
                continue;
            }

            if (inSingleQuote)
            {
                current.Append(ch);
                if (ch == '\'')
                {
                    inSingleQuote = false;
                }
                continue;
            }

            if (ch == '\\')
            {
                current.Append(ch);
                escaped = true;
                continue;
            }

            if (inDoubleQuote)
            {
                current.Append(ch);
                if (ch == '"')
                {
                    inDoubleQuote = false;
                }
                continue;
            }

            if (ch == '\'')
            {
                inSingleQuote = true;
                current.Append(ch);
                continue;
            }

            if (ch == '"')
            {
                inDoubleQuote = true;
                current.Append(ch);
                continue;
            }

            if (char.IsWhiteSpace(ch))
            {
                FlushCurrent();
                continue;
            }

            if (ch == ';')
            {
                FlushCurrent();
                tokens.Add(";");
                continue;
            }

            if (ch is '&' or '|')
            {
                if (i + 1 < command.Length && command[i + 1] == ch)
                {
                    FlushCurrent();
                    tokens.Add(new string(ch, 2));
                    i++;
                    continue;
                }

                if (ch == '|')
                {
                    FlushCurrent();
                    tokens.Add("|");
                    continue;
                }
            }

            current.Append(ch);
        }

        FlushCurrent();
        return tokens;
    }

    internal static string QuotePathArgument(string value)
    {
        if (!value.Any(ch => char.IsWhiteSpace(ch) || Array.IndexOf(ShellSpecialChars, ch) >= 0))
        {
            return value;
        }

        var escaped = value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("$", "\\$", StringComparison.Ordinal)
            .Replace("`", "\\`", StringComparison.Ordinal);
        return $"\"{escaped}\"";
    }
}
