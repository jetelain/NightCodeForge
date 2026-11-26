using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace NightCodeForge.Benchmark.Cli.Verification;

/// <summary>
/// Parsed test outcome from `.trx`, JUnit XML, or `cargo test` output.
/// </summary>
public sealed record ParsedTestOutcome(string Name, bool Passed, bool Skipped = false);

/// <summary>
/// Evaluates `FailToPass` and `PassToPass` test assertions against structured test execution outputs.
/// </summary>
public sealed record TestAssertionEvaluation(
    int FailToPassPassed,
    int FailToPassTotal,
    int PassToPassPassed,
    int PassToPassTotal,
    IReadOnlyList<string> FailedTests)
{
    public bool AllPassed => FailToPassPassed == FailToPassTotal && PassToPassPassed == PassToPassTotal;
}

public static class TrxTestResultParser
{
    public static IReadOnlyList<ParsedTestOutcome> ParseFile(string trxFilePath)
    {
        if (string.IsNullOrWhiteSpace(trxFilePath) || !File.Exists(trxFilePath))
        {
            return Array.Empty<ParsedTestOutcome>();
        }

        return Parse(File.ReadAllText(trxFilePath));
    }

    public static IReadOnlyList<ParsedTestOutcome> Parse(string trxXml)
    {
        if (string.IsNullOrWhiteSpace(trxXml))
        {
            return Array.Empty<ParsedTestOutcome>();
        }

        var doc = XDocument.Parse(trxXml);
        var root = doc.Root;
        if (root == null)
        {
            return Array.Empty<ParsedTestOutcome>();
        }

        var ns = root.Name.Namespace;
        var methodByTestId = new Dictionary<string, (string ClassName, string MethodName)>(StringComparer.OrdinalIgnoreCase);

        foreach (var unitTest in root.Descendants(ns + "UnitTest"))
        {
            var id = unitTest.Attribute("id")?.Value;
            var testMethod = unitTest.Element(ns + "TestMethod");
            if (!string.IsNullOrWhiteSpace(id) && testMethod != null)
            {
                var className = testMethod.Attribute("className")?.Value;
                var methodName = testMethod.Attribute("name")?.Value;
                if (!string.IsNullOrWhiteSpace(className) && !string.IsNullOrWhiteSpace(methodName))
                {
                    var commaIdx = className.IndexOf(',');
                    var cleanClassName = (commaIdx >= 0 ? className[..commaIdx] : className).Trim();
                    methodByTestId[id] = (cleanClassName, methodName.Trim());
                }
            }
        }

        var outcomes = new List<ParsedTestOutcome>();
        foreach (var result in root.Descendants(ns + "UnitTestResult"))
        {
            var testId = result.Attribute("testId")?.Value;
            var testName = result.Attribute("testName")?.Value?.Trim() ?? string.Empty;
            var outcomeStr = result.Attribute("outcome")?.Value ?? string.Empty;

            string name;
            if (!string.IsNullOrWhiteSpace(testId) && methodByTestId.TryGetValue(testId, out var info))
            {
                if (testName.StartsWith(info.ClassName + ".", StringComparison.OrdinalIgnoreCase))
                {
                    name = testName;
                }
                else if (!string.IsNullOrWhiteSpace(testName) && testName.StartsWith(info.MethodName, StringComparison.OrdinalIgnoreCase))
                {
                    name = $"{info.ClassName}.{testName}";
                }
                else
                {
                    name = $"{info.ClassName}.{info.MethodName}";
                }
            }
            else
            {
                name = testName;
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var passed = string.Equals(outcomeStr, "Passed", StringComparison.OrdinalIgnoreCase);
            var skipped = string.Equals(outcomeStr, "NotExecuted", StringComparison.OrdinalIgnoreCase)
                || string.Equals(outcomeStr, "Skipped", StringComparison.OrdinalIgnoreCase);
            outcomes.Add(new ParsedTestOutcome(name, passed, skipped));
        }

        return outcomes;
    }
}

public static class JUnitXmlTestResultParser
{
    public static IReadOnlyList<ParsedTestOutcome> ParseFile(string junitFilePath)
    {
        if (string.IsNullOrWhiteSpace(junitFilePath) || !File.Exists(junitFilePath))
        {
            return Array.Empty<ParsedTestOutcome>();
        }

        return Parse(File.ReadAllText(junitFilePath));
    }

    public static IReadOnlyList<ParsedTestOutcome> Parse(string xmlContent)
    {
        if (string.IsNullOrWhiteSpace(xmlContent))
        {
            return Array.Empty<ParsedTestOutcome>();
        }

        var doc = XDocument.Parse(xmlContent);
        var root = doc.Root;
        if (root == null)
        {
            return Array.Empty<ParsedTestOutcome>();
        }

        var outcomes = new List<ParsedTestOutcome>();
        foreach (var testCase in root.Descendants().Where(e => string.Equals(e.Name.LocalName, "testcase", StringComparison.OrdinalIgnoreCase)))
        {
            var className = testCase.Attribute("classname")?.Value?.Trim();
            var name = testCase.Attribute("name")?.Value?.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            bool nameAlreadyContainsClass = !string.IsNullOrWhiteSpace(className) &&
                (name.StartsWith(className, StringComparison.OrdinalIgnoreCase) ||
                 name.EndsWith(" " + className, StringComparison.OrdinalIgnoreCase) ||
                 name.EndsWith("." + className, StringComparison.OrdinalIgnoreCase) ||
                 name.EndsWith(" > " + className, StringComparison.OrdinalIgnoreCase) ||
                 name.EndsWith("::" + className, StringComparison.OrdinalIgnoreCase));

            var fullName = !string.IsNullOrWhiteSpace(className) && !nameAlreadyContainsClass
                ? $"{className}.{name}"
                : name;

            bool hasFailure = testCase.Elements().Any(e =>
                string.Equals(e.Name.LocalName, "failure", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(e.Name.LocalName, "error", StringComparison.OrdinalIgnoreCase));
            bool isSkipped = !hasFailure && testCase.Elements().Any(e =>
                string.Equals(e.Name.LocalName, "skipped", StringComparison.OrdinalIgnoreCase));

            outcomes.Add(new ParsedTestOutcome(fullName, Passed: !hasFailure && !isSkipped, Skipped: isSkipped));
        }

        return outcomes;
    }
}

public static partial class CargoTestResultParser
{
    [GeneratedRegex(@"^test\s+(?<name>\S+)(?:\s+-\s+should panic)?\s+\.\.\.\s+(?<status>ok|FAILED|ignored)\b.*$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex CargoLineRegex();

    public static IReadOnlyList<ParsedTestOutcome> Parse(string stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
        {
            return Array.Empty<ParsedTestOutcome>();
        }

        var outcomes = new List<ParsedTestOutcome>();
        foreach (Match match in CargoLineRegex().Matches(stdout))
        {
            var name = match.Groups["name"].Value.Trim();
            var status = match.Groups["status"].Value.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var passed = string.Equals(status, "ok", StringComparison.Ordinal);
            var skipped = string.Equals(status, "ignored", StringComparison.Ordinal);
            outcomes.Add(new ParsedTestOutcome(name, passed, skipped));
        }

        return outcomes;
    }
}

public static partial class TestAssertionEvaluator
{
    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRunRegex();

    public static TestAssertionEvaluation Evaluate(
        IReadOnlyList<string> failToPass,
        IReadOnlyList<string> passToPass,
        IReadOnlyList<ParsedTestOutcome> parsedOutcomes)
    {
        var f2p = failToPass ?? Array.Empty<string>();
        var p2p = passToPass ?? Array.Empty<string>();
        var outcomes = parsedOutcomes ?? Array.Empty<ParsedTestOutcome>();

        var failedTests = new List<string>();
        int f2pPassed = 0;
        foreach (var expected in f2p)
        {
            if (IsExpectedTestPassed(expected, outcomes))
            {
                f2pPassed++;
            }
            else
            {
                failedTests.Add(expected);
            }
        }

        int p2pPassed = 0;
        foreach (var expected in p2p)
        {
            if (IsExpectedTestPassed(expected, outcomes))
            {
                p2pPassed++;
            }
            else
            {
                failedTests.Add(expected);
            }
        }

        return new TestAssertionEvaluation(f2pPassed, f2p.Count, p2pPassed, p2p.Count, failedTests);
    }

    private static bool IsExpectedTestPassed(string expectedName, IReadOnlyList<ParsedTestOutcome> outcomes)
    {
        if (string.IsNullOrWhiteSpace(expectedName) || outcomes.Count == 0)
        {
            return false;
        }

        var trimmed = expectedName.Trim();
        var rawResult = TryEvaluateExpectedIdentifier(trimmed, outcomes, static o => o.Name);
        if (rawResult.HasValue)
        {
            return rawResult.Value;
        }

        var normalizedExpected = NormalizeTestIdentifier(trimmed);
        if (!string.Equals(normalizedExpected, trimmed, StringComparison.Ordinal) ||
            outcomes.Any(o => o.Name.Contains("::", StringComparison.Ordinal) ||
                              o.Name.Contains('/', StringComparison.Ordinal) ||
                              o.Name.Contains('\\', StringComparison.Ordinal) ||
                              o.Name.Contains(" > ", StringComparison.Ordinal)))
        {
            var normalizedResult = TryEvaluateExpectedIdentifier(normalizedExpected, outcomes, static o => NormalizeTestIdentifier(o.Name));
            if (normalizedResult.HasValue)
            {
                return normalizedResult.Value;
            }
        }

        return MatchesSpaceNormalizedMochaIdentifier(trimmed, outcomes);
    }

    private static bool MatchesSpaceNormalizedMochaIdentifier(string expected, IReadOnlyList<ParsedTestOutcome> outcomes)
    {
        var normalizedExpected = NormalizeMochaSpaceIdentifier(expected);
        if (string.IsNullOrWhiteSpace(normalizedExpected))
        {
            return false;
        }

        var exact = outcomes
            .Where(o => string.Equals(NormalizeMochaSpaceIdentifier(o.Name), normalizedExpected, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (exact.Count > 0)
        {
            return exact.All(o => o.Passed);
        }

        var spaceSuffix = " " + normalizedExpected;
        var suffixMatches = outcomes
            .Where(o => NormalizeMochaSpaceIdentifier(o.Name).EndsWith(spaceSuffix, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return suffixMatches.Count > 0 && suffixMatches.All(o => o.Passed);
    }

    private static string NormalizeMochaSpaceIdentifier(string name) =>
        WhitespaceRunRegex().Replace(name.Trim().Replace(" > ", " ", StringComparison.Ordinal), " ");

    private static bool? TryEvaluateExpectedIdentifier(
        string target,
        IReadOnlyList<ParsedTestOutcome> outcomes,
        Func<ParsedTestOutcome, string> nameSelector)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return null;
        }

        var exact = outcomes.Where(o => string.Equals(nameSelector(o), target, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count > 0)
        {
            return exact.All(o => o.Passed);
        }

        var suffixDot = "." + target;
        var suffixColon = "::" + target;
        var suffixSlash = "/" + target;
        var suffixMatches = outcomes.Where(o =>
        {
            var candidate = nameSelector(o);
            return candidate.EndsWith(suffixDot, StringComparison.OrdinalIgnoreCase) ||
                   candidate.EndsWith(suffixColon, StringComparison.OrdinalIgnoreCase) ||
                   candidate.EndsWith(suffixSlash, StringComparison.OrdinalIgnoreCase) ||
                   target.EndsWith("." + candidate, StringComparison.OrdinalIgnoreCase);
        }).ToList();

        if (suffixMatches.Count > 0)
        {
            return suffixMatches.All(o => o.Passed);
        }

        // Handle parameterized test names (e.g. xUnit/NUnit Method(args) or pytest test_name[args])
        var parenPrefix = target + "(";
        var bracketPrefix = target + "[";
        var paramMatches = outcomes.Where(o =>
        {
            var candidate = nameSelector(o);
            return candidate.StartsWith(parenPrefix, StringComparison.OrdinalIgnoreCase) ||
                   candidate.Contains("." + parenPrefix, StringComparison.OrdinalIgnoreCase) ||
                   candidate.StartsWith(bracketPrefix, StringComparison.OrdinalIgnoreCase) ||
                   candidate.Contains("." + bracketPrefix, StringComparison.OrdinalIgnoreCase);
        }).ToList();

        if (paramMatches.Count > 0)
        {
            return paramMatches.All(o => o.Passed);
        }

        // Upstream SWE-bench log parsers sometimes split parameterized pytest node IDs at spaces
        // or commas, leaving an unbalanced '[' prefix (e.g. "test_foo[hello" for "test_foo[hello world]").
        if (HasUnbalancedOpenBracket(target))
        {
            var truncatedMatches = outcomes
                .Where(o => MatchesTruncatedBracketCandidate(nameSelector(o), target))
                .ToList();

            if (truncatedMatches.Count > 0)
            {
                return truncatedMatches.All(o => o.Passed);
            }
        }

        return null;
    }

    private static bool MatchesTruncatedBracketCandidate(string candidate, string target)
    {
        if (!candidate.EndsWith(']'))
        {
            return false;
        }

        int nextIndex = -1;
        if (candidate.StartsWith(target, StringComparison.OrdinalIgnoreCase))
        {
            nextIndex = target.Length;
        }
        else
        {
            var dottedTarget = "." + target;
            int matchIndex = candidate.IndexOf(dottedTarget, StringComparison.OrdinalIgnoreCase);
            if (matchIndex >= 0)
            {
                nextIndex = matchIndex + dottedTarget.Length;
            }
        }

        if (nextIndex < 0 || nextIndex >= candidate.Length)
        {
            return false;
        }

        // Upstream SWE-bench splits occur at delimiters (spaces/commas), so either the truncated
        // prefix already ends on a non-alphanumeric delimiter (e.g. "[foo," or "[(((") or the next
        // character in the full candidate ID is a delimiter rather than continuing an identifier/number.
        return !char.IsLetterOrDigit(target[^1]) || !char.IsLetterOrDigit(candidate[nextIndex]);
    }

    private static bool HasUnbalancedOpenBracket(string value)
    {
        int depth = 0;
        foreach (var ch in value)
        {
            if (ch == '[')
            {
                depth++;
            }
            else if (ch == ']' && depth > 0)
            {
                depth--;
            }
        }

        return depth > 0;
    }

    internal static string NormalizeTestIdentifier(string name)
    {
        var normalized = name.Trim().Replace('\\', '/');
        normalized = normalized.Replace(".py::", ".", StringComparison.OrdinalIgnoreCase);
        normalized = normalized.Replace("::", ".", StringComparison.Ordinal);
        normalized = normalized.Replace(" > ", ".", StringComparison.Ordinal);
        normalized = normalized.Replace('/', '.');
        return normalized.TrimStart('.');
    }
}
