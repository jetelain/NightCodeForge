namespace NightCodeForge.Core.Services;

using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

public partial class TranscriptAnalysisService
{
    private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    [GeneratedRegex(@"^---\r?\n(?<frontmatter>[\s\S]*?)\r?\n---\r?\n?", RegexOptions.Multiline)]
    private static partial Regex FrontmatterRegex();

    [GeneratedRegex(@"^###\s+\[\s*(?<offset>[0-9:]+)\s*\]\s+(?:(?<icon>[^\s\w]+)\s+)?(?<role>[A-Za-z0-9_]+)", RegexOptions.Multiline)]
    private static partial Regex TurnHeaderRegex();

    [GeneratedRegex(@"(?ms)\*\*Tool Invocation:\*\*\s*`(?<name>[^`]+)`\s*`{3,4}(?:json)?\s*(?<args>\{[\s\S]*?\})\s*`{3,4}")]
    private static partial Regex ToolInvocationWithArgsRegex();

    [GeneratedRegex(@"\*\*Tool Invocation:\*\*\s*`(?<name>[^`]+)`")]
    private static partial Regex ToolInvocationRegex();

    [GeneratedRegex(@"""path""\s*:\s*""(?<path>[^""]+)""")]
    private static partial Regex FilePathArgRegex();

    [GeneratedRegex(@"""command""\s*:\s*""(?<cmd>(?:\\.|[^""\\])*)""")]
    private static partial Regex BashCommandArgRegex();

    [GeneratedRegex(@"Exit Code:\s*(?<code>-?[1-9]\d*)")]
    private static partial Regex ExitCodeRegex();

    [GeneratedRegex(@"(?i)(?:could not locate target chunk|neither exact nor fuzzy indentation match succeeded|did not match target content|Error:\s*Edit\s*#\d+\s*failed)")]
    private static partial Regex EditFailureRegex();

    [GeneratedRegex(@"error\s+(?<code>CS\d+):")]
    private static partial Regex CompilerErrorRegex();

    [GeneratedRegex(@"(?:^|\r?\n)\s*Failed\s+(?<test>[A-Za-z0-9_]+(?:\.[A-Za-z0-9_<>]+)+(?:\([^\r\n]*\))?)")]
    private static partial Regex TestFailedLineRegex();

    [GeneratedRegex(@"Assert\.[A-Za-z]+\(\)\s+Failure")]
    private static partial Regex AssertFailureRegex();

    [GeneratedRegex(@"(?ms)\*\*Tool Invocation:\*\*\s*`(?<name>[^`]+)`(?!\s*\*\*Tool Output:\*\*)")]
    private static partial Regex AssistantToolInvocationRegex();

    [GeneratedRegex(@"\[CONTEXT COMPACTION CHECKPOINT\]")]
    private static partial Regex CheckpointRegex();

    [GeneratedRegex(@"✂️\s*\*\*COMPACTION\*\*")]
    private static partial Regex CompactionHeaderRegex();

    [GeneratedRegex(@"History rewritten since the previous call")]
    private static partial Regex HistoryRewriteRegex();

    public async Task<IReadOnlyList<TranscriptAnalysis>> AnalyzeDirectoryAsync(string directoryPath, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(directoryPath))
        {
            return Array.Empty<TranscriptAnalysis>();
        }

        var files = Directory.GetFiles(directoryPath, "*.md", SearchOption.TopDirectoryOnly);
        var results = new List<TranscriptAnalysis>();

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var analysis = await AnalyzeFileAsync(file, cancellationToken);
            results.Add(analysis);
        }

        return results.OrderByDescending(r => r.Frontmatter?.TaskId ?? 0)
                      .ThenBy(r => r.Frontmatter?.StageId ?? string.Empty)
                      .ToList();
    }

    public async Task<TranscriptAnalysis> AnalyzeFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var content = await File.ReadAllTextAsync(filePath, cancellationToken);
        var fileName = Path.GetFileName(filePath);
        return Analyze(content, fileName, filePath);
    }

    public TranscriptAnalysis Analyze(string content, string fileName, string filePath = "")
    {
        var frontmatter = ParseFrontmatter(content);

        // Turn counting & segmentation
        int totalTurns = 0;
        int userTurns = 0;
        int assistantTurns = 0;
        int toolTurns = 0;
        int thinkingTurns = 0;

        var turnMatches = TurnHeaderRegex().Matches(content);
        totalTurns = turnMatches.Count;

        foreach (Match m in turnMatches)
        {
            var role = m.Groups["role"].Value.ToUpperInvariant();
            if (role.Contains("USER")) userTurns++;
            else if (role.Contains("ASSISTANT")) assistantTurns++;
            else if (role.Contains("TOOL")) toolTurns++;
            else if (role.Contains("THINKING")) thinkingTurns++;
        }

        // Tool usage and error tracking
        var toolUsage = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var readFiles = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var editedFiles = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var frictionPoints = new List<string>();
        var recommendations = new List<string>();

        // Find tool invocations with arguments
        bool lastActionWasEditOrWrite = false;
        bool ranStaleTest = false;

        // Count tool invocations
        foreach (Match m in AssistantToolInvocationRegex().Matches(content))
        {
            var name = m.Groups["name"].Value.Trim();
            toolUsage[name] = toolUsage.GetValueOrDefault(name, 0) + 1;
        }

        if (toolUsage.Count == 0)
        {
            foreach (Match m in ToolInvocationWithArgsRegex().Matches(content))
            {
                var name = m.Groups["name"].Value.Trim();
                toolUsage[name] = toolUsage.GetValueOrDefault(name, 0) + 1;
            }
        }

        foreach (Match m in ToolInvocationWithArgsRegex().Matches(content))
        {
            var name = m.Groups["name"].Value.Trim();
            var args = m.Groups["args"].Value;
            if (name.Equals("read", StringComparison.OrdinalIgnoreCase))
            {
                var pathMatch = FilePathArgRegex().Match(args);
                if (pathMatch.Success)
                {
                    var p = pathMatch.Groups["path"].Value.Trim();
                    readFiles[p] = readFiles.GetValueOrDefault(p, 0) + 1;
                }
            }
            else if (name.Equals("edit", StringComparison.OrdinalIgnoreCase) ||
                     name.Equals("write", StringComparison.OrdinalIgnoreCase))
            {
                var pathMatch = FilePathArgRegex().Match(args);
                if (pathMatch.Success)
                {
                    var p = pathMatch.Groups["path"].Value.Trim();
                    editedFiles[p] = editedFiles.GetValueOrDefault(p, 0) + 1;
                    if (!IsNonSourceFile(p))
                    {
                        lastActionWasEditOrWrite = true;
                    }
                }
                else
                {
                    lastActionWasEditOrWrite = true;
                }
            }
            else if (name.Equals("bash", StringComparison.OrdinalIgnoreCase))
            {
                var cmdMatch = BashCommandArgRegex().Match(args);
                if (cmdMatch.Success)
                {
                    var cmd = cmdMatch.Groups["cmd"].Value;
                    if (cmd.Contains("dotnet test --no-build") && lastActionWasEditOrWrite && !cmd.Contains("dotnet build"))
                    {
                        ranStaleTest = true;
                    }
                    if (cmd.Contains("dotnet build"))
                    {
                        lastActionWasEditOrWrite = false;
                    }
                }
            }
        }

        // Check bash errors
        var exitCodeMatches = ExitCodeRegex().Matches(content);
        int bashErrors = exitCodeMatches.Count;

        // Check compiler errors
        var compMatches = CompilerErrorRegex().Matches(content);
        int compilerErrors = compMatches.Count;

        // Check test failures
        var testFailMatches = TestFailedLineRegex().Matches(content);
        int testFailures = testFailMatches.Count;
        if (testFailures == 0)
        {
            testFailures = AssertFailureRegex().Matches(content).Count;
        }

        // Check compaction events and history rewrites
        int checkpointCount = CheckpointRegex().Matches(content).Count;
        int compactionEvents = checkpointCount > 0
            ? checkpointCount
            : CompactionHeaderRegex().Matches(content).Count;
        int historyRewrites = HistoryRewriteRegex().Matches(content).Count;

        // Line-by-line checks for edit failures and generic tool errors
        int editFailures = 0;
        int toolErrors = 0;
        var lines = content.Split('\n');
        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');
            if (EditFailureRegex().IsMatch(line))
            {
                editFailures++;
            }
            else if (line.StartsWith("Error: ", StringComparison.OrdinalIgnoreCase) ||
                     (line.Contains("[tool result ") && line.Contains("Error:", StringComparison.OrdinalIgnoreCase)))
            {
                toolErrors++;
            }
        }

        // Identify high-churn files (edited 3 or more times)
        var highChurnFiles = editedFiles.Where(kvp => kvp.Value >= 3)
                                        .OrderByDescending(kvp => kvp.Value)
                                        .ToList();

        var topReadFiles = readFiles.OrderByDescending(kvp => kvp.Value)
                                    .Take(10)
                                    .ToList();

        var topEditedFiles = editedFiles.OrderByDescending(kvp => kvp.Value)
                                        .Take(10)
                                        .ToList();

        // Synthesize friction points
        if (editFailures > 0)
        {
            frictionPoints.Add($"Edit mismatch failures: {editFailures} edit(s) failed to match target content (exact or indentation mismatch).");
            recommendations.Add("Inspect target code with 'read' before editing or break large replacements into smaller, targeted chunks.");
        }

        if (ranStaleTest)
        {
            frictionPoints.Add("Stale test execution: The agent executed 'dotnet test --no-build' immediately after code edits without a preceding build.");
            recommendations.Add("Update stage execution prompt: emphasize running 'dotnet build' before running 'dotnet test --no-build' when changes were made.");
        }

        if (highChurnFiles.Count > 0)
        {
            var churnSummary = string.Join(", ", highChurnFiles.Select(f => $"{Path.GetFileName(f.Key)} ({f.Value} edits)"));
            frictionPoints.Add($"High edit churn detected on: {churnSummary}.");
            recommendations.Add("Consider extracting embedded logic (e.g. HTML/JS strings) or modularizing files to reduce iterative edit cycles.");
        }

        if (compilerErrors > 0)
        {
            frictionPoints.Add($"Compiler diagnostics: {compilerErrors} compiler error(s) occurred during execution.");
        }

        if (testFailures > 0)
        {
            frictionPoints.Add($"Test regressions: {testFailures} test failure(s) occurred during execution.");
        }

        if (bashErrors > 0)
        {
            frictionPoints.Add($"Shell command errors: {bashErrors} non-zero bash exit code(s) occurred.");
        }

        if (compactionEvents > 0)
        {
            frictionPoints.Add($"Context compaction: {compactionEvents} context compaction checkpoint(s) occurred during execution.");
            if (compactionEvents > 5)
            {
                recommendations.Add("High compaction frequency indicates context window saturation. Consider increasing context budget, compacting more aggressively, or breaking down the task scope.");
            }
        }

        if (historyRewrites > 0)
        {
            frictionPoints.Add($"History rewrites: {historyRewrites} call(s) had prior messages mutated/rewritten (prefix cache invalidated).");
            if (historyRewrites > 10)
            {
                recommendations.Add("Inspect historical reasoning pruning or message mutation middleware to prevent breaking LLM prompt prefix (KV) caching on every turn.");
            }
        }

        // Review stage specific friction (e.g. 15+ bash commands for minor edit)
        if (frontmatter?.StageId?.Contains("review", StringComparison.OrdinalIgnoreCase) == true &&
            toolUsage.GetValueOrDefault("bash", 0) > 15 && editedFiles.Count <= 1)
        {
            frictionPoints.Add($"Review exploration overhead: Agent ran {toolUsage["bash"]} bash commands to inspect git diff and grep files for a 1-file edit.");
            recommendations.Add("Pre-inject git diff summary and changed files directly into the review stage prompt to avoid redundant exploratory bash commands.");
        }

        return new TranscriptAnalysis(
            FilePath: filePath,
            FileName: fileName,
            Frontmatter: frontmatter,
            TotalTurns: totalTurns,
            UserTurns: userTurns,
            AssistantTurns: assistantTurns,
            ToolTurns: toolTurns,
            ThinkingTurns: thinkingTurns,
            ToolUsage: toolUsage,
            BashErrors: bashErrors,
            EditFailures: editFailures,
            ToolErrors: toolErrors,
            CompilerErrors: compilerErrors,
            TestFailures: testFailures,
            CompactionEvents: compactionEvents,
            HistoryRewrites: historyRewrites,
            TopReadFiles: topReadFiles,
            TopEditedFiles: topEditedFiles,
            HighChurnFiles: highChurnFiles,
            FrictionPoints: frictionPoints,
            Recommendations: recommendations.Distinct().ToList()
        );
    }

    private static TranscriptFrontmatter? ParseFrontmatter(string content)
    {
        var match = FrontmatterRegex().Match(content);
        if (!match.Success) return null;

        try
        {
            var yaml = match.Groups["frontmatter"].Value;
            return YamlDeserializer.Deserialize<TranscriptFrontmatter>(yaml);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsNonSourceFile(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".md", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".txt", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".json", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".yaml", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".yml", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".gitignore", StringComparison.OrdinalIgnoreCase);
    }
}
