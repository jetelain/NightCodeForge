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
    private const string NpmGlobalPrefix = "/tmp/ncf-npm-global";
    private const string JestJUnitPrefix = "/tmp/ncf-jest-junit";

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

    private static readonly string[] LowPriorityDirectoryOrNameTokens =
    [
        "sample",
        "benchmark",
        "example"
    ];

    [GeneratedRegex(@"^\s*dotnet\s+test(?<rest>(?:\s+.*)?)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BareDotNetTestRegex();

    [GeneratedRegex(@"\.(?:sln|slnx|csproj|fsproj|vbproj)(?:\s|$|[""'])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DotNetProjectOrSolutionRefRegex();

    [GeneratedRegex(@"(?<prefix>\bvitest\s+run)(?<rest>.*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VitestRunRegex();

    [GeneratedRegex(@"(?:^|\s)(?:-c|--config)(?:\s|=|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExplicitConfigFlagRegex();

    internal static string ResolveTestCommand(
        BenchmarkInstance instance,
        string workspacePath,
        ExecutionMode executionMode = ExecutionMode.ContainerSandbox)
    {
        var normalizedLanguage = BenchmarkLanguages.Normalize(instance.Language);

        if (!string.IsNullOrWhiteSpace(instance.CustomTestCommand))
        {
            var custom = instance.CustomTestCommand.Trim();
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

        var resultsDir = executionMode == ExecutionMode.ContainerSandbox
            ? BenchmarkPaths.ContainerResultsDir
            : BenchmarkPaths.BenchmarkResultDirRelative;

        return normalizedLanguage switch
        {
            BenchmarkLanguages.CSharp => ResolveCSharpTestCommand(workspacePath, resultsDir, executionMode),
            BenchmarkLanguages.Python => $"pytest --junitxml={resultsDir}/{BenchmarkPaths.JUnitFileName}",
            BenchmarkLanguages.TypeScript or BenchmarkLanguages.JavaScript => ResolveNodeTestCommand(workspacePath, resultsDir),
            BenchmarkLanguages.Rust => "cargo test --no-fail-fast",
            _ => throw new BenchmarkVerificationException(
                $"No default test command configured for language '{instance.Language}'. Provide custom_test_command.",
                instance.InstanceId)
        };
    }

    internal static string WrapWithContainerDependencySetup(
        string testCommand,
        string language,
        ExecutionMode executionMode)
    {
        if (executionMode != ExecutionMode.ContainerSandbox || string.IsNullOrWhiteSpace(testCommand))
        {
            return testCommand;
        }

        var normalizedLanguage = BenchmarkLanguages.Normalize(language);
        return normalizedLanguage switch
        {
            BenchmarkLanguages.Python => WrapPythonContainerCommand(testCommand),
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
                .OrderBy(IsLowPriorityTarget)
                .ThenBy(CountPathSegments)
                .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
                .First();
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

    private static string ResolveCSharpTestCommand(string workspacePath, string resultsDir, ExecutionMode executionMode)
    {
        var targets = DiscoverDotNetTargets(workspacePath);
        if (targets.Count == 0)
        {
            return $"dotnet build --nologo && dotnet test --no-build --logger trx --results-directory {resultsDir}";
        }

        var commands = targets.Select(target =>
        {
            var quotedTarget = QuotePathArgument(target);
            return $"dotnet build {quotedTarget} --nologo && dotnet test {quotedTarget} --no-build --logger trx --results-directory {resultsDir}";
        }).ToList();

        return JoinMultiTargetCommands(commands, executionMode);
    }

    private static string ApplyDiscoveredDotNetTargetsToCustomCommand(
        string customCommand,
        string workspacePath,
        ExecutionMode executionMode)
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

        var commands = targets
            .Select(target => $"dotnet test {QuotePathArgument(target)}{rest}")
            .ToList();

        return JoinMultiTargetCommands(commands, executionMode);
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

    private static string WrapPythonContainerCommand(string testCommand) =>
        "if ! command -v pytest >/dev/null 2>&1; then " +
        $"if [ ! -x {PythonVenvDir}/bin/pytest ]; then " +
        $"python3 -m venv {PythonVenvDir} && " +
        $"{PythonVenvDir}/bin/pip install -q pytest && " +
        "(for req in requirements-dev.txt requirements/dev.txt requirements/tests.txt requirements/test.txt test-requirements.txt requirements.txt; do " +
        $"[ -f \"$req\" ] && {PythonVenvDir}/bin/pip install -q -r \"$req\" || true; done) && " +
        "(if [ -f pyproject.toml ] || [ -f setup.py ] || [ -f setup.cfg ]; then " +
        $"{PythonVenvDir}/bin/pip install -q -e . || {PythonVenvDir}/bin/pip install -q --no-build-isolation -e . || true; fi); " +
        $"fi && export PATH=\"{PythonVenvDir}/bin:$PATH\"; " +
        $"fi && {WrapPosixCompoundBody(testCommand)}";

    private static string WrapNodeContainerCommand(string testCommand)
    {
        var pathGuard =
            $"if [ -d \"{NpmGlobalPrefix}/bin\" ]; then case \":$PATH:\" in *\":{NpmGlobalPrefix}/bin:\"*) ;; *) export PATH=\"{NpmGlobalPrefix}/bin:$PATH\" ;; esac; fi";

        var installStep =
            "if [ -f package.json ] && [ ! -d node_modules ]; then " +
            "if [ -f pnpm-lock.yaml ]; then " +
            $"if ! command -v pnpm >/dev/null 2>&1; then if [ ! -x {NpmGlobalPrefix}/bin/pnpm ]; then npm install -g --prefix {NpmGlobalPrefix} pnpm; fi && export PATH=\"{NpmGlobalPrefix}/bin:$PATH\"; fi && " +
            "(pnpm install --frozen-lockfile || pnpm install); " +
            "elif [ -f yarn.lock ]; then " +
            $"if ! command -v yarn >/dev/null 2>&1; then if [ ! -x {NpmGlobalPrefix}/bin/yarn ]; then npm install -g --prefix {NpmGlobalPrefix} yarn; fi && export PATH=\"{NpmGlobalPrefix}/bin:$PATH\"; fi && " +
            "(yarn install --frozen-lockfile || yarn install); " +
            "elif [ -f package-lock.json ]; then (npm ci || npm install); " +
            "else npm install; fi; " +
            "fi";

        if (testCommand.Contains("jest-junit", StringComparison.OrdinalIgnoreCase))
        {
            installStep +=
                " && if [ -f package.json ] && [ ! -d node_modules/jest-junit ]; then " +
                $"if [ ! -d {JestJUnitPrefix}/node_modules/jest-junit ]; then mkdir -p {JestJUnitPrefix} && (npm install --prefix {JestJUnitPrefix} --no-save jest-junit || true); fi; " +
                $"if [ -d {JestJUnitPrefix}/node_modules/jest-junit ]; then export NODE_PATH=\"{JestJUnitPrefix}/node_modules${{NODE_PATH:+:$NODE_PATH}}\"; fi; " +
                "fi";
        }

        return $"{pathGuard} && {installStep} && {WrapPosixCompoundBody(testCommand)}";
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
        return segments.Any(seg => LowPriorityDirectoryOrNameTokens.Any(tok =>
            seg.Contains(tok, StringComparison.OrdinalIgnoreCase)));
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
