namespace NightCodeForge.Core.Services;

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NightCodeForge.Core.Domain;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

public partial class TranscriptAnalysisService : ITranscriptAnalysisService
{
    private const int MaxSampleErrorLength = 160;
    private const int MaxCompilerMessageLength = 200;
    private const int MaxNormalizedArgsLength = 120;
    private const int MaxHotspotsPerGroup = 10;

    private static readonly HashSet<string> ExcludedNonTranscriptFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "AGENTS.md",
        "README.md",
        "CLAUDE.md",
        "CONTRIBUTING.md",
        "CHANGELOG.md",
        "LICENSE.md",
        "ARCHITECTURE.md",
        "SKILL.md"
    };

    private static readonly HashSet<string> MultiSubcommandExecutables = new(StringComparer.OrdinalIgnoreCase)
    {
        "dotnet", "git", "npm", "pnpm", "yarn", "npx", "csnav", "cargo", "go", "docker", "kubectl", "az", "gh", "uv", "poetry", "pip"
    };

    private static readonly HashSet<string> SearchTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "grep", "glob", "grep_search", "find_files", "search", "find"
    };

    private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    [GeneratedRegex(@"^---\r?\n(?<frontmatter>[\s\S]*?)\r?\n---\r?\n?", RegexOptions.Multiline)]
    private static partial Regex FrontmatterRegex();

    [GeneratedRegex(@"^###\s+Model calls\b", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex ModelCallsSectionRegex();

    [GeneratedRegex(@"^####\s+\[\s*[0-9:]+\s*\]\s+Call\s+\d+", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex ModelCallHeaderRegex();

    [GeneratedRegex(@"^###\s+", RegexOptions.Multiline)]
    private static partial Regex H3SectionHeaderRegex();

    private static readonly HashSet<string> KnownTurnRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        "USER",
        "ASSISTANT",
        "TOOL",
        "TOOL_RESULT",
        "TOOL_ERROR",
        "THINKING",
        "COMPACTION",
        "SYSTEM"
    };

    [GeneratedRegex(@"^#{3,4}[ \t]+(?:\[[ \t]*(?<offset>[0-9:]+)[ \t]*\][ \t]+)?(?:[^\sA-Za-z0-9_*]+[ \t]+)*(?:\*\*)?(?<role>[A-Za-z0-9_]+)(?:\*\*)?[ \t]*\r?$", RegexOptions.Multiline)]
    private static partial Regex TurnHeaderRegex();

    [GeneratedRegex(@"\*\*Tool Invocation:\*\*\s*`(?<name>[^`]+)`")]
    private static partial Regex ToolInvocationRegex();

    [GeneratedRegex(@"\*\*Tool:\*\*\s*`(?<name>[^`]+)`")]
    private static partial Regex ToolResultHeaderRegex();

    [GeneratedRegex(@"\*\*Tool Output:\*\*", RegexOptions.IgnoreCase)]
    private static partial Regex ToolOutputMarkerRegex();

    [GeneratedRegex(@"""(?:path|file_path|TargetFile|AbsolutePath)""\s*:\s*""(?<path>(?:\\.|[^""\\])*)""")]
    private static partial Regex FilePathArgRegex();

    [GeneratedRegex(@"""(?:command|CommandLine)""\s*:\s*""(?<cmd>(?:\\.|[^""\\])*)""")]
    private static partial Regex BashCommandArgRegex();

    [GeneratedRegex(@"(?:^|[;&|\r\n]\s*|\$\()\s*git(?:\s+--?[^\s]+)*\s+diff\b", RegexOptions.IgnoreCase)]
    private static partial Regex GitDiffCommandRegex();

    [GeneratedRegex(@"(?:^|[;&|\r\n]\s*)\s*git(?:\s+--?[^\s]+)*\s+(?:checkout\s+--\s+|restore\b|reset\s+--hard\b)", RegexOptions.IgnoreCase)]
    private static partial Regex SelfRevertCommandRegex();

    [GeneratedRegex(@"Exit Code:\s*(?<code>-?[1-9]\d*)")]
    private static partial Regex ExitCodeRegex();

    [GeneratedRegex(@"Exit Code:\s*0\b")]
    private static partial Regex ZeroExitCodeRegex();

    [GeneratedRegex(@"(?i)\b(?:timed\s*out|timeout\s+expired|sentinel\s+timeout|SIGINT|SIGTERM|SIGKILL|process\s+(?:hung|killed|terminated\s+due\s+to\s+timeout)|command\s+hung)\b")]
    private static partial Regex BashHangOrTimeoutRegex();

    [GeneratedRegex(@"(?i)(?:could not locate target chunk|neither exact nor fuzzy indentation match succeeded|did not match target content|Error:\s*Edit\s*#\d+\s*failed)")]
    private static partial Regex EditFailureRegex();

    [GeneratedRegex(@"\berror\s+(?<code>(?:CS|MSB|NU|TS)\d+)\s*:\s*(?<msg>[^\r\n]+)")]
    private static partial Regex CompilerErrorRegex();

    [GeneratedRegex(@"^\s*(?:\d+>)?\s*(?<file>(?:[A-Za-z]:[\\/])?[^\r\n:]+?)(?:\(\d+(?:,\d+(?:,\d+,\d+)?)?\)|:\d+(?::\d+)?)?\s*[-:]\s*$")]
    private static partial Regex DiagnosticFilePrefixRegex();

    [GeneratedRegex(@"(?:^|\r?\n)\s*Failed\s+(?<test>[A-Za-z0-9_]+(?:\.[A-Za-z0-9_<>]+)+(?:\([^\r\n]*\))?)")]
    private static partial Regex TestFailedLineRegex();

    [GeneratedRegex(@"Assert\.[A-Za-z]+\(\)\s+Failure")]
    private static partial Regex AssertFailureRegex();

    [GeneratedRegex(@"(?i)(?:Test Run Failed|Failed!\s+-\s+Failed:\s*[1-9]\d*)")]
    private static partial Regex TestRunFailedMarkerRegex();

    [GeneratedRegex(@"(?i)(?:Passed!\s+-\s+Failed:\s*0|Test Run Successful|Passed!\s+All\s+\d+\s+tests\s+passed)")]
    private static partial Regex TestRunPassedMarkerRegex();

    [GeneratedRegex(@"\[CONTEXT COMPACTION CHECKPOINT\]")]
    private static partial Regex CheckpointRegex();

    [GeneratedRegex(@"✂️\s*\*\*COMPACTION\*\*")]
    private static partial Regex CompactionHeaderRegex();

    [GeneratedRegex(@"History rewritten since the previous call")]
    private static partial Regex HistoryRewriteRegex();

    [GeneratedRegex(@"\[Output truncated;\s*(?<total>[0-9,]+)\s+chars total")]
    private static partial Regex TruncatedOutputRegex();

    [GeneratedRegex(@"(?i)(?:file\s+not\s+found|could\s+not\s+find\s+file|does\s+not\s+exist|no\s+such\s+file|path\s+not\s+found|cannot\s+find\s+the\s+path)")]
    private static partial Regex FileNotFoundRegex();

    [GeneratedRegex(@"(?i)(?:0\s+matches|no\s+matches\s+found|found\s+0\s+(?:matches|files|results)|no\s+files\s+found|""?totalMatches""?\s*:\s*0\b)")]
    private static partial Regex ZeroSearchMatchesRegex();

    [GeneratedRegex(@"(?i)(?:JSON\s+parse\s+error|invalid\s+tool\s+arguments|malformed\s+tool\s+call|failed\s+to\s+parse\s+tool\s+call|truncated\s+tool\s+call)")]
    private static partial Regex MalformedToolErrorRegex();

    [GeneratedRegex(@"^(?:(?<date>\d{4}-\d{2}-\d{2})-)?(?:task-(?<taskId>\d+)-)?.*?(?:-run(?<run>\d+))?(?:-[A-Za-z0-9_]+)?\.md$", RegexOptions.IgnoreCase)]
    private static partial Regex TranscriptFileNameMetadataRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    public Task<IReadOnlyList<TranscriptAnalysis>> AnalyzeDirectoryAsync(string directoryPath, CancellationToken cancellationToken = default) =>
        AnalyzeDirectoryAsync(directoryPath, TranscriptAnalysisDefaults.DefaultRecentLimit, cancellationToken);

    public async Task<IReadOnlyList<TranscriptAnalysis>> AnalyzeDirectoryAsync(
        string directoryPath,
        int? maxTranscripts,
        CancellationToken cancellationToken = default)
    {
        var window = await AnalyzeDirectoryWindowAsync(directoryPath, maxTranscripts, cancellationToken).ConfigureAwait(false);
        return window.Items;
    }

    public async Task<TranscriptWindowResult<TranscriptAnalysis>> AnalyzeDirectoryWindowAsync(
        string directoryPath,
        int? maxTranscripts = TranscriptAnalysisDefaults.DefaultRecentLimit,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
        {
            return new TranscriptWindowResult<TranscriptAnalysis>(Array.Empty<TranscriptAnalysis>(), 0, NormalizeLimit(maxTranscripts));
        }

        var orderedFiles = DiscoverOrderedTranscriptFiles(new[] { directoryPath });
        var totalAvailableCount = orderedFiles.Count;
        var appliedLimit = NormalizeLimit(maxTranscripts);

        var filesToRead = appliedLimit.HasValue
            ? orderedFiles.Take(appliedLimit.Value)
            : orderedFiles;

        var results = new List<TranscriptAnalysis>();
        foreach (var file in filesToRead)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var analysis = await AnalyzeFileAsync(file, cancellationToken).ConfigureAwait(false);
            results.Add(analysis);
        }

        return new TranscriptWindowResult<TranscriptAnalysis>(results, totalAvailableCount, appliedLimit);
    }

    public async Task<WorkspaceFrictionHotspots> GetWorkspaceFrictionHotspotsAsync(
        string workspacePath,
        int? maxTranscripts = TranscriptAnalysisDefaults.DefaultRecentLimit,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath))
        {
            return WorkspaceFrictionHotspots.Empty;
        }

        var directories = GetWorkspaceTranscriptDirectories(workspacePath);
        bool isFallbackRoot = directories.Count == 0;
        if (isFallbackRoot)
        {
            directories = new[] { workspacePath };
        }

        var orderedFiles = DiscoverOrderedTranscriptFiles(directories);
        if (orderedFiles.Count == 0)
        {
            return WorkspaceFrictionHotspots.Empty;
        }

        var appliedLimit = NormalizeLimit(maxTranscripts);
        var filesToRead = appliedLimit.HasValue
            ? orderedFiles.Take(appliedLimit.Value)
            : orderedFiles;

        var analyses = new List<TranscriptAnalysis>();
        foreach (var file in filesToRead)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TranscriptAnalysis analysis;
            try
            {
                analysis = await AnalyzeFileAsync(file, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // Justification: concurrent active pipeline stages (or parallel test workers) may hold an exclusive write lock
                // on an in-flight transcript file or delete a temporary transcript while scanning; skip rather than failing prompt context.
                continue;
            }

            if (analysis.Frontmatter == null && analysis.TotalTurns == 0)
            {
                continue;
            }

            analyses.Add(analysis);
        }

        return analyses.Count == 0
            ? WorkspaceFrictionHotspots.Empty
            : BuildWorkspaceFrictionHotspots(analyses);
    }

    public async Task<TranscriptAnalysis> AnalyzeFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var content = await ReadAllTextSharedAsync(filePath, cancellationToken).ConfigureAwait(false);
        var fileName = Path.GetFileName(filePath);
        return Analyze(content, fileName, filePath);
    }

    public static async Task<string> ReadAllTextSharedAsync(string filePath, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            useAsync: true);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    public TranscriptAnalysis Analyze(string content, string fileName, string filePath = "")
    {
        content ??= string.Empty;
        var frontmatter = ParseFrontmatter(content);

        // 1. Section Boundary Splitting (avoid double-counting from Model calls / Environment / MAF config)
        var (conversationBody, modelCallsSection) = SplitSections(content);

        // 2. Parse conversation turns and count roles
        var turns = ParseTurns(conversationBody);
        int totalTurns = turns.Count;
        int userTurns = 0;
        int assistantTurns = 0;
        int toolTurns = 0;
        int thinkingTurns = 0;

        foreach (var turn in turns)
        {
            var upperRole = turn.Role.ToUpperInvariant();
            if (upperRole.Contains("USER", StringComparison.Ordinal)) userTurns++;
            else if (upperRole.Contains("ASSISTANT", StringComparison.Ordinal)) assistantTurns++;
            else if (upperRole.Contains("TOOL", StringComparison.Ordinal)) toolTurns++;
            else if (upperRole.Contains("THINKING", StringComparison.Ordinal)) thinkingTurns++;
        }

        // 3. Correlate tool invocations & outputs across the three transcript patterns
        var (correlatedCalls, malformedFenceCount) = CorrelateToolCalls(turns, conversationBody);

        // 4. Compute per-tool, per-file, bash command, compiler error, and behavioral breakdowns
        var toolUsage = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var fileTrackers = new Dictionary<string, FileFrictionTracker>(StringComparer.OrdinalIgnoreCase);
        var toolTrackers = new Dictionary<string, ToolFrictionTracker>(StringComparer.OrdinalIgnoreCase);
        var bashTrackers = new Dictionary<string, BashCommandTracker>(StringComparer.OrdinalIgnoreCase);
        var loopBuckets = new Dictionary<string, ToolLoopTracker>(StringComparer.Ordinal);

        bool lastActionWasEditOrWrite = false;
        bool ranStaleTest = false;
        int gitDiffCommands = 0;
        int deadEndSearchCount = 0;
        int testRunCycles = 0;
        int failedTestRuns = 0;
        int selfRevertCount = 0;
        int malformedOrTruncatedToolCount = malformedFenceCount;
        int hangOrTimeoutBashErrorsWithoutExitCode = 0;
        int errorTurnWithoutLineMatchCount = 0;
        int correlatedBashExitCodeErrors = 0;
        int correlatedEditFailures = 0;
        int correlatedGenericToolErrors = 0;
        var diagnosticToolOutputs = new List<string>();
        var slowToolExecutions = new List<SlowTurnEntry>();

        foreach (var call in correlatedCalls)
        {
            toolUsage[call.ToolName] = toolUsage.GetValueOrDefault(call.ToolName, 0) + 1;

            if (!toolTrackers.TryGetValue(call.ToolName, out var toolTracker))
            {
                toolTracker = new ToolFrictionTracker(call.ToolName);
                toolTrackers[call.ToolName] = toolTracker;
            }
            toolTracker.InvocationCount++;

            if (call.HasMalformedArgs || MalformedToolErrorRegex().IsMatch(call.OutputText))
            {
                malformedOrTruncatedToolCount++;
            }

            bool isOversized = IsOversizedOutput(call.OutputText);
            if (isOversized)
            {
                toolTracker.OversizedOutputCount++;
            }

            // Tool latency check (> 60s between invocation turn and result turn)
            if (call.StartOffsetSeconds.HasValue && call.EndOffsetSeconds.HasValue &&
                call.StartOffset != null && call.EndOffset != null)
            {
                var delta = call.EndOffsetSeconds.Value - call.StartOffsetSeconds.Value;
                if (delta > TranscriptAnalysisDefaults.SlowToolExecutionThresholdSeconds)
                {
                    var label = BuildSlowToolLabel(call);
                    slowToolExecutions.Add(new SlowTurnEntry(call.StartOffset, call.EndOffset, label, delta));
                }
            }

            // Loop tracking by (ToolName, NormalizedArgs)
            var (canonicalArgsKey, argsSummary) = NormalizeToolArguments(call.ArgsJson);
            var loopKey = $"{call.ToolName.ToLowerInvariant()}\u001f{canonicalArgsKey}";
            if (!loopBuckets.TryGetValue(loopKey, out var loopTracker))
            {
                loopTracker = new ToolLoopTracker(call.ToolName, argsSummary);
                loopBuckets[loopKey] = loopTracker;
            }
            loopTracker.Count++;

            bool isRead = IsReadTool(call.ToolName);
            bool isEditOrWrite = IsEditOrWriteTool(call.ToolName);
            bool isBash = IsBashTool(call.ToolName);
            var bashCmd = isBash ? ExtractBashCommandArg(call.ArgsJson) : null;
            bool isSourceInspectionBash = isBash && !string.IsNullOrWhiteSpace(bashCmd) && IsBashSourceInspectionCommand(bashCmd);
            bool isFileOrDiffContentOutput = isRead || isEditOrWrite || SearchTools.Contains(call.ToolName) || isSourceInspectionBash;

            if (!isFileOrDiffContentOutput && !string.IsNullOrWhiteSpace(call.OutputText))
            {
                diagnosticToolOutputs.Add(call.OutputText);
            }

            int callEditFailureLineCount = isEditOrWrite ? CountEditFailureLines(call.OutputText) : 0;
            correlatedEditFailures += callEditFailureLineCount;
            bool isEditFailure = isEditOrWrite && (callEditFailureLineCount > 0 || call.IsErrorTurn);

            int callExitCodeErrorCount = isBash && !isSourceInspectionBash ? ExitCodeRegex().Matches(call.OutputText).Count : 0;
            correlatedBashExitCodeErrors += callExitCodeErrorCount;
            bool hasExitCodeError = callExitCodeErrorCount > 0;
            bool hasHangOrTimeout = isBash && !isSourceInspectionBash && ContainsBashHangOrTimeout(call.OutputText);
            int callGenericToolErrorLines = CountGenericToolErrorLines(call.OutputText, isFileOrDiffContentOutput);
            correlatedGenericToolErrors += callGenericToolErrorLines;
            bool hasLineToolError = callGenericToolErrorLines > 0;
            bool hasCompilerDiagnostics = !isFileOrDiffContentOutput && CompilerErrorRegex().IsMatch(call.OutputText);
            bool isCallFailure = call.IsErrorTurn || isEditFailure || hasExitCodeError || hasHangOrTimeout || hasLineToolError || hasCompilerDiagnostics;

            if (isCallFailure)
            {
                toolTracker.FailureCount++;
                toolTracker.SampleError ??= ExtractSampleErrorLine(call.OutputText, call.IsErrorTurn ? "Tool returned TOOL_ERROR." : null);
            }

            if (call.IsErrorTurn && !isEditFailure && !hasExitCodeError && !hasHangOrTimeout && !hasLineToolError && !hasCompilerDiagnostics)
            {
                errorTurnWithoutLineMatchCount++;
            }

            if (isRead)
            {
                var path = ExtractFilePathArg(call.ArgsJson);
                if (!string.IsNullOrWhiteSpace(path))
                {
                    var ft = GetOrCreateFileTracker(fileTrackers, path);
                    ft.ReadCount++;
                    ft.CurrentConsecutiveReads++;
                    ft.MaxConsecutiveReads = Math.Max(ft.MaxConsecutiveReads, ft.CurrentConsecutiveReads);
                }

                if (call.HasResultTurn &&
                    (call.IsErrorTurn || hasLineToolError || IsShortReadErrorOutput(call.OutputText)) &&
                    FileNotFoundRegex().IsMatch(call.OutputText))
                {
                    deadEndSearchCount++;
                }
            }
            else if (isEditOrWrite)
            {
                var path = ExtractFilePathArg(call.ArgsJson);
                if (!string.IsNullOrWhiteSpace(path))
                {
                    var ft = GetOrCreateFileTracker(fileTrackers, path);
                    if (IsEditTool(call.ToolName) && ft.ReadCount == 0 && !ft.HasWritten)
                    {
                        ft.EditedWithoutReadCount++;
                    }
                    ft.EditCount++;
                    if (IsWriteTool(call.ToolName))
                    {
                        ft.HasWritten = true;
                    }
                    ft.CurrentConsecutiveReads = 0;

                    if (isEditFailure)
                    {
                        ft.EditFailureCount++;
                    }

                    if (!IsNonSourceFile(path))
                    {
                        lastActionWasEditOrWrite = true;
                    }
                }
                else
                {
                    lastActionWasEditOrWrite = true;
                }
            }
            else if (isBash)
            {
                var cmd = bashCmd;
                if (!string.IsNullOrWhiteSpace(cmd))
                {
                    if (cmd.Contains("dotnet test --no-build", StringComparison.OrdinalIgnoreCase) &&
                        lastActionWasEditOrWrite &&
                        !cmd.Contains("dotnet build", StringComparison.OrdinalIgnoreCase))
                    {
                        ranStaleTest = true;
                    }

                    if (cmd.Contains("dotnet build", StringComparison.OrdinalIgnoreCase))
                    {
                        lastActionWasEditOrWrite = false;
                    }

                    var diffMatches = GitDiffCommandRegex().Matches(cmd);
                    if (diffMatches.Count > 0)
                    {
                        gitDiffCommands += diffMatches.Count;
                    }

                    if (SelfRevertCommandRegex().IsMatch(cmd))
                    {
                        selfRevertCount++;
                    }
                }

                if (!string.IsNullOrWhiteSpace(cmd) || hasExitCodeError || hasHangOrTimeout || call.IsErrorTurn || isOversized)
                {
                    var pattern = !string.IsNullOrWhiteSpace(cmd) ? NormalizeBashCommandPattern(cmd) : CanonicalToolNames.Bash;
                    if (!bashTrackers.TryGetValue(pattern, out var bt))
                    {
                        bt = new BashCommandTracker(pattern);
                        bashTrackers[pattern] = bt;
                    }
                    bt.ExecutionCount++;
                    if (isOversized)
                    {
                        bt.OversizedOutputCount++;
                    }
                    if (hasHangOrTimeout)
                    {
                        bt.HangOrTimeoutCount++;
                        if (!hasExitCodeError)
                        {
                            hangOrTimeoutBashErrorsWithoutExitCode++;
                        }
                    }

                    var exitCodeMatch = !isSourceInspectionBash ? ExitCodeRegex().Match(call.OutputText) : Match.Empty;
                    if (exitCodeMatch.Success && int.TryParse(exitCodeMatch.Groups["code"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var exitCode))
                    {
                        bt.LastExitCode = exitCode;
                        bt.FailureCount++;
                    }
                    else if (ZeroExitCodeRegex().IsMatch(call.OutputText))
                    {
                        bt.LastExitCode = 0;
                    }
                    else if (hasHangOrTimeout || call.IsErrorTurn)
                    {
                        bt.FailureCount++;
                    }
                }

                if (!string.IsNullOrWhiteSpace(cmd))
                {
                    if (IsBashTestCommand(cmd))
                    {
                        testRunCycles++;
                        if (hasExitCodeError || hasHangOrTimeout || call.IsErrorTurn ||
                            TestFailedLineRegex().IsMatch(call.OutputText) ||
                            AssertFailureRegex().IsMatch(call.OutputText) ||
                            TestRunFailedMarkerRegex().IsMatch(call.OutputText))
                        {
                            failedTestRuns++;
                        }
                    }

                    if (call.HasResultTurn && IsBashSearchCommand(cmd) && IsZeroSearchOutput(call.OutputText, isBash: true))
                    {
                        deadEndSearchCount++;
                    }
                }
            }
            else if (call.HasResultTurn && SearchTools.Contains(call.ToolName) && IsZeroSearchOutput(call.OutputText, isBash: false))
            {
                deadEndSearchCount++;
            }
        }

        // 5. Slow LLM inference turns (> 30s delta from User / Tool output -> Thinking / Assistant / Tool invocation)
        var slowLlmTurns = ExtractSlowLlmTurns(turns);

        // 6. Aggregate line/body errors (bash, compiler, test, compaction, history rewrites, edit/tool errors)
        string diagnosticScanBody;
        int bashErrors;
        int editFailures;
        int toolErrors;

        if (correlatedCalls.Count > 0)
        {
            diagnosticScanBody = string.Join("\n", diagnosticToolOutputs);
            bashErrors = correlatedBashExitCodeErrors + hangOrTimeoutBashErrorsWithoutExitCode;
            editFailures = correlatedEditFailures;
            toolErrors = errorTurnWithoutLineMatchCount + correlatedGenericToolErrors;
        }
        else
        {
            var rawToolTurnBodies = turns
                .Where(t => t.Role.Contains("TOOL", StringComparison.OrdinalIgnoreCase))
                .Select(t => t.Body)
                .ToList();
            diagnosticScanBody = rawToolTurnBodies.Count > 0
                ? string.Join("\n", rawToolTurnBodies)
                : conversationBody;

            bashErrors = ExitCodeRegex().Matches(diagnosticScanBody).Count + hangOrTimeoutBashErrorsWithoutExitCode;
            editFailures = CountEditFailureLines(diagnosticScanBody);
            toolErrors = errorTurnWithoutLineMatchCount + CountGenericToolErrorLines(diagnosticScanBody, isFileOrDiffContent: false);
        }

        var compilerErrorBreakdown = ExtractCompilerErrorBreakdown(diagnosticScanBody, out int compilerErrors);

        var testFailMatches = TestFailedLineRegex().Matches(diagnosticScanBody);
        var failingTests = testFailMatches
            .Select(m => m.Groups["test"].Value.Trim())
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        int testFailures = testFailMatches.Count;
        if (testFailures == 0)
        {
            testFailures = AssertFailureRegex().Matches(diagnosticScanBody).Count;
        }

        int checkpointCount = CheckpointRegex().Matches(conversationBody).Count;
        int compactionEvents = checkpointCount > 0
            ? checkpointCount
            : CompactionHeaderRegex().Matches(conversationBody).Count;
        int historyRewrites = HistoryRewriteRegex().Matches(modelCallsSection).Count;

        // 7. Materialize ordered breakdown collections
        var fileFriction = fileTrackers.Values
            .Select(ft => new FileFrictionEntry(
                ft.FilePath,
                ft.ReadCount,
                ft.EditCount,
                ft.EditFailureCount,
                ft.EditedWithoutReadCount,
                ft.MaxConsecutiveReads))
            .OrderByDescending(f => f.EditFailureCount * 3 + f.EditCount * 2 + f.ReadCount)
            .ThenBy(f => f.FilePath, StringComparer.OrdinalIgnoreCase)
            .Take(TranscriptAnalysisDefaults.MaxFileFrictionEntriesPerSession)
            .ToList();

        var toolFriction = toolTrackers.Values
            .Where(t => t.FailureCount > 0 || t.OversizedOutputCount > 0)
            .Select(t => new ToolFrictionEntry(
                t.ToolName,
                t.InvocationCount,
                t.FailureCount,
                t.InvocationCount > 0 ? Math.Round(t.FailureCount * 100.0 / t.InvocationCount, 1) : 0.0,
                t.OversizedOutputCount,
                t.SampleError))
            .OrderByDescending(t => t.FailureCount)
            .ThenByDescending(t => t.OversizedOutputCount)
            .ThenByDescending(t => t.InvocationCount)
            .ThenBy(t => t.ToolName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var bashCommandFriction = bashTrackers.Values
            .Select(b => new BashCommandFrictionEntry(
                b.CommandPattern,
                b.ExecutionCount,
                b.FailureCount,
                b.HangOrTimeoutCount,
                b.OversizedOutputCount,
                b.LastExitCode))
            .OrderByDescending(b => b.FailureCount + b.HangOrTimeoutCount * 2 + b.OversizedOutputCount)
            .ThenByDescending(b => b.ExecutionCount)
            .ThenBy(b => b.CommandPattern, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var repeatedToolLoops = loopBuckets.Values
            .Where(b => b.Count >= 3)
            .Select(b => new RepeatedToolLoopEntry(b.ToolName, b.ArgsSummary, b.Count))
            .OrderByDescending(l => l.RepeatCount)
            .ThenBy(l => l.ToolName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        bool hasTestOscillation = failedTestRuns >= 2;
        var behavioralSignals = new TranscriptBehavioralSignals(
            repeatedToolLoops,
            deadEndSearchCount,
            testRunCycles,
            failingTests,
            hasTestOscillation,
            selfRevertCount,
            malformedOrTruncatedToolCount,
            slowToolExecutions,
            slowLlmTurns);

        // Legacy summary lists
        var highChurnFiles = fileTrackers.Values
            .Where(f => f.EditCount >= 3 && !IsNonSourceFile(f.FilePath))
            .OrderByDescending(f => f.EditCount)
            .Select(f => new KeyValuePair<string, int>(f.FilePath, f.EditCount))
            .ToList();

        var topReadFiles = fileTrackers.Values
            .Where(f => f.ReadCount > 0)
            .OrderByDescending(f => f.ReadCount)
            .Take(10)
            .Select(f => new KeyValuePair<string, int>(f.FilePath, f.ReadCount))
            .ToList();

        var topEditedFiles = fileTrackers.Values
            .Where(f => f.EditCount > 0)
            .OrderByDescending(f => f.EditCount)
            .Take(10)
            .Select(f => new KeyValuePair<string, int>(f.FilePath, f.EditCount))
            .ToList();

        // 8. Synthesize friction points & categorized recommendations
        var frictionPoints = new List<string>();
        var recommendations = new List<FrictionRecommendation>();

        SynthesizeFrictionAndRecommendations(
            frontmatter,
            fileFriction,
            toolFriction,
            bashCommandFriction,
            compilerErrorBreakdown,
            behavioralSignals,
            highChurnFiles,
            bashErrors,
            editFailures,
            toolErrors,
            compilerErrors,
            testFailures,
            compactionEvents,
            historyRewrites,
            ranStaleTest,
            gitDiffCommands,
            editedFileCount: fileTrackers.Values.Count(f => f.EditCount > 0),
            frictionPoints,
            recommendations);

        var deduplicatedRecommendations = recommendations
            .GroupBy(r => (r.Target, r.Category, Key: r.Subject ?? r.Title))
            .Select(g => g.First())
            .ToList();

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
            Recommendations: deduplicatedRecommendations,
            FileFriction: fileFriction,
            ToolFriction: toolFriction,
            BashCommandFriction: bashCommandFriction,
            CompilerErrorBreakdown: compilerErrorBreakdown,
            BehavioralSignals: behavioralSignals);
    }

    public static IReadOnlyList<string> DiscoverOrderedTranscriptFiles(IEnumerable<string> directories)
    {
        var seenFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<TranscriptFileCandidate>();

        foreach (var dir in directories)
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            {
                continue;
            }

            foreach (var file in Directory.GetFiles(dir, "*.md", SearchOption.TopDirectoryOnly))
            {
                var fileName = Path.GetFileName(file);
                if (ExcludedNonTranscriptFileNames.Contains(fileName) || !seenFileNames.Add(fileName))
                {
                    continue;
                }

                var lastWriteUtc = File.GetLastWriteTimeUtc(file);
                var (parsedDate, taskId, runNumber) = ParseFileNameSortKey(fileName);
                candidates.Add(new TranscriptFileCandidate(
                    file,
                    fileName,
                    parsedDate ?? lastWriteUtc.Date,
                    taskId,
                    runNumber,
                    lastWriteUtc));
            }
        }

        return candidates
            .OrderByDescending(c => c.EffectiveDate)
            .ThenByDescending(c => c.TaskId)
            .ThenByDescending(c => c.RunNumber)
            .ThenByDescending(c => c.LastWriteUtc)
            .ThenByDescending(c => c.FileName, StringComparer.OrdinalIgnoreCase)
            .Select(c => c.FullPath)
            .ToList();
    }

    public static bool IsNonSourceFile(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".md", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".txt", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".json", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".yaml", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".yml", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".gitignore", StringComparison.OrdinalIgnoreCase);
    }

    public static string FormatDuration(long? durationMs)
    {
        if (!durationMs.HasValue || durationMs < 0) return "-";
        var ts = TimeSpan.FromMilliseconds(durationMs.Value);
        var totalHours = (int)ts.TotalHours;
        return totalHours > 0
            ? $"{totalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes:D2}:{ts.Seconds:D2}";
    }

    internal static int? NormalizeLimit(int? maxTranscripts) =>
        maxTranscripts is > 0 ? maxTranscripts : null;

    internal static string NormalizeBashCommandPattern(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return "bash";
        }

        var segments = SplitShellSegments(command);
        string? primarySegment = null;
        foreach (var segment in segments)
        {
            var trimmed = segment.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                continue;
            }

            var tokens = TokenizeCommand(trimmed);
            if (tokens.Count == 0)
            {
                continue;
            }

            // Skip leading directory navigation or export/set wrappers
            var head = Path.GetFileName(tokens[0]);
            if (head.Equals("cd", StringComparison.OrdinalIgnoreCase) ||
                head.Equals("pushd", StringComparison.OrdinalIgnoreCase) ||
                head.Equals("export", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            primarySegment = trimmed;
            break;
        }

        primarySegment ??= command.Trim();
        var cmdTokens = TokenizeCommand(primarySegment);

        // Strip leading ENV=value assignments
        int idx = 0;
        while (idx < cmdTokens.Count && cmdTokens[idx].Contains('=') && !cmdTokens[idx].StartsWith('-'))
        {
            idx++;
        }

        if (idx >= cmdTokens.Count)
        {
            return "bash";
        }

        var executable = Path.GetFileName(cmdTokens[idx]);
        idx++;

        if (executable.Equals("git", StringComparison.OrdinalIgnoreCase))
        {
            while (idx < cmdTokens.Count)
            {
                var tok = cmdTokens[idx];
                if (tok is "-C" or "-c" or "--git-dir" or "--work-tree")
                {
                    idx += 2;
                    continue;
                }
                if (tok.StartsWith('-'))
                {
                    idx++;
                    continue;
                }
                return $"git {tok.ToLowerInvariant()}";
            }
            return "git";
        }

        if (executable.Equals("npm", StringComparison.OrdinalIgnoreCase) ||
            executable.Equals("pnpm", StringComparison.OrdinalIgnoreCase) ||
            executable.Equals("yarn", StringComparison.OrdinalIgnoreCase))
        {
            while (idx < cmdTokens.Count && cmdTokens[idx].StartsWith('-'))
            {
                idx++;
            }
            if (idx < cmdTokens.Count)
            {
                var sub = cmdTokens[idx];
                if (sub.Equals("run", StringComparison.OrdinalIgnoreCase) && idx + 1 < cmdTokens.Count && !cmdTokens[idx + 1].StartsWith('-'))
                {
                    return $"{executable.ToLowerInvariant()} run {cmdTokens[idx + 1]}";
                }
                return $"{executable.ToLowerInvariant()} {sub}";
            }
            return executable.ToLowerInvariant();
        }

        if (MultiSubcommandExecutables.Contains(executable))
        {
            while (idx < cmdTokens.Count && cmdTokens[idx].StartsWith('-'))
            {
                idx++;
            }
            if (idx < cmdTokens.Count && !LooksLikePathToken(cmdTokens[idx]))
            {
                return $"{executable.ToLowerInvariant()} {cmdTokens[idx].ToLowerInvariant()}";
            }
        }

        return executable;
    }

    internal static bool TryParseOffsetSeconds(string? offset, out int totalSeconds)
    {
        totalSeconds = 0;
        if (string.IsNullOrWhiteSpace(offset))
        {
            return false;
        }

        var parts = offset.Trim().Split(':');
        if (parts.Length == 2 &&
            int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var mins) &&
            int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var secs) &&
            mins >= 0 && secs >= 0 && secs < 60)
        {
            totalSeconds = mins * 60 + secs;
            return true;
        }

        if (parts.Length == 3 &&
            int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hrs) &&
            int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var m3) &&
            int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var s3) &&
            hrs >= 0 && m3 >= 0 && s3 >= 0)
        {
            totalSeconds = hrs * 3600 + m3 * 60 + s3;
            return true;
        }

        return false;
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
        catch (YamlDotNet.Core.YamlException)
        {
            // Justification: malformed frontmatter in historical or partial transcripts falls back to null metadata.
            return null;
        }
    }

    private static (string ConversationBody, string ModelCallsSection) SplitSections(string content)
    {
        var body = content;
        var fmMatch = FrontmatterRegex().Match(content);
        if (fmMatch.Success)
        {
            body = content.Substring(fmMatch.Length);
        }

        var closedFences = BuildClosedFenceRanges(body);
        string modelCallsSection;
        var modelCallsMatch = ModelCallsSectionRegex()
            .Matches(body)
            .Cast<Match>()
            .FirstOrDefault(m => !IsIndexInsideFence(m.Index, closedFences));

        if (modelCallsMatch is { Success: true })
        {
            modelCallsSection = body.Substring(modelCallsMatch.Index);
            body = body.Substring(0, modelCallsMatch.Index);
        }
        else
        {
            modelCallsSection = content;
            var callMatch = ModelCallHeaderRegex()
                .Matches(body)
                .Cast<Match>()
                .FirstOrDefault(m => !IsIndexInsideFence(m.Index, closedFences));
            if (callMatch is { Success: true })
            {
                body = body.Substring(0, callMatch.Index);
            }
        }

        body = StripMetadataSections(body);
        return (body, modelCallsSection);
    }

    private static string StripMetadataSections(string body)
    {
        var closedFences = BuildClosedFenceRanges(body);
        var headerMatches = H3SectionHeaderRegex()
            .Matches(body)
            .Cast<Match>()
            .Where(m => !IsIndexInsideFence(m.Index, closedFences))
            .ToList();
        if (headerMatches.Count == 0)
        {
            return body;
        }

        var sb = new StringBuilder(body.Length);
        int cursor = 0;

        for (int i = 0; i < headerMatches.Count; i++)
        {
            var headerStart = headerMatches[i].Index;
            var nextHeaderStart = (i + 1 < headerMatches.Count) ? headerMatches[i + 1].Index : body.Length;

            if (headerStart > cursor)
            {
                sb.Append(body, cursor, headerStart - cursor);
            }

            var lineEnd = body.IndexOf('\n', headerStart);
            var headerLine = lineEnd >= 0
                ? body.Substring(headerStart, lineEnd - headerStart).Trim()
                : body.Substring(headerStart).Trim();

            bool isMetadataSection =
                headerLine.StartsWith("### Environment", StringComparison.OrdinalIgnoreCase) ||
                headerLine.StartsWith("### MAF Pipeline Configuration", StringComparison.OrdinalIgnoreCase);

            if (!isMetadataSection)
            {
                sb.Append(body, headerStart, nextHeaderStart - headerStart);
            }

            cursor = nextHeaderStart;
        }

        if (cursor < body.Length)
        {
            sb.Append(body, cursor, body.Length - cursor);
        }

        return sb.ToString();
    }

    private static List<ParsedTurn> ParseTurns(string conversationBody)
    {
        var closedFences = BuildClosedFenceRanges(conversationBody);
        var rawMatches = TurnHeaderRegex().Matches(conversationBody);
        var headerMatches = new List<(Match Match, string Offset, string Role)>(rawMatches.Count);

        foreach (Match match in rawMatches)
        {
            if (IsIndexInsideFence(match.Index, closedFences))
            {
                continue;
            }

            var offset = match.Groups["offset"].Value.Trim();
            var role = match.Groups["role"].Value.Trim();
            if (string.IsNullOrEmpty(offset))
            {
                if (!KnownTurnRoles.Contains(role))
                {
                    continue;
                }
            }
            else if (role.Equals("Call", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            headerMatches.Add((match, offset, role));
        }

        var turns = new List<ParsedTurn>(headerMatches.Count);

        for (int i = 0; i < headerMatches.Count; i++)
        {
            var (match, offset, role) = headerMatches[i];
            int bodyStart = match.Index + match.Length;
            int bodyEnd = (i + 1 < headerMatches.Count) ? headerMatches[i + 1].Match.Index : conversationBody.Length;
            var turnBody = conversationBody.Substring(bodyStart, bodyEnd - bodyStart).Trim();
            int? offsetSeconds = TryParseOffsetSeconds(offset, out var sec) ? sec : null;

            turns.Add(new ParsedTurn(offset, offsetSeconds, role, turnBody));
        }

        return turns;
    }

    private static List<(int Start, int End)> BuildClosedFenceRanges(string text)
    {
        var ranges = new List<(int Start, int End)>();
        if (string.IsNullOrEmpty(text))
        {
            return ranges;
        }

        string? activeFence = null;
        int openStart = 0;
        int pos = 0;

        while (pos <= text.Length)
        {
            int lineEnd = text.IndexOf('\n', pos);
            int nextPos = lineEnd >= 0 ? lineEnd + 1 : text.Length + 1;
            var rawLine = lineEnd >= 0 ? text.Substring(pos, lineEnd - pos) : text.Substring(pos);
            var trimmed = rawLine.TrimEnd('\r').Trim();

            if (activeFence == null)
            {
                var opening = DetectOpeningFence(trimmed);
                if (opening != null)
                {
                    activeFence = opening;
                    openStart = pos;
                }
            }
            else if (string.Equals(trimmed, activeFence, StringComparison.Ordinal))
            {
                ranges.Add((openStart, lineEnd >= 0 ? lineEnd : text.Length));
                activeFence = null;
            }

            if (lineEnd < 0)
            {
                break;
            }
            pos = nextPos;
        }

        return ranges;
    }

    private static bool IsIndexInsideFence(int index, List<(int Start, int End)> closedFences)
    {
        for (int i = 0; i < closedFences.Count; i++)
        {
            var (start, end) = closedFences[i];
            if (index > start && index < end)
            {
                return true;
            }
            if (start >= index)
            {
                break;
            }
        }

        return false;
    }

    private static (List<CorrelatedToolCall> Calls, int MalformedFenceCount) CorrelateToolCalls(
        List<ParsedTurn> turns,
        string conversationBody)
    {
        var correlated = new List<CorrelatedToolCall>();
        var pending = new List<PendingToolInvocation>();
        int malformedFenceCount = 0;

        if (turns.Count == 0)
        {
            var fallbackInvocations = ExtractToolInvocations(conversationBody, out var malformed);
            malformedFenceCount += malformed;
            foreach (var inv in fallbackInvocations)
            {
                correlated.Add(new CorrelatedToolCall(
                    inv.ToolName,
                    inv.ArgsJson,
                    inv.InlineOutput ?? inv.RemainingTextAfterArgs ?? string.Empty,
                    IsErrorTurn: false,
                    StartOffset: null,
                    StartOffsetSeconds: null,
                    EndOffset: null,
                    EndOffsetSeconds: null,
                    HasMalformedArgs: inv.HasMalformedArgs,
                    HasResultTurn: inv.InlineOutput != null || !string.IsNullOrWhiteSpace(inv.RemainingTextAfterArgs)));
            }
            return (correlated, malformedFenceCount);
        }

        foreach (var turn in turns)
        {
            var upperRole = turn.Role.ToUpperInvariant();
            bool isToolResultOrError = upperRole == "TOOL_RESULT" || upperRole == "TOOL_ERROR";
            bool isErrorTurn = upperRole == "TOOL_ERROR";

            if (isToolResultOrError)
            {
                var toolHeaderMatch = ToolResultHeaderRegex().Match(turn.Body);
                var resultToolName = toolHeaderMatch.Success
                    ? toolHeaderMatch.Groups["name"].Value.Trim()
                    : null;
                if (string.IsNullOrWhiteSpace(resultToolName))
                {
                    var echoedInvMatch = ToolInvocationRegex().Match(turn.Body);
                    if (echoedInvMatch.Success)
                    {
                        resultToolName = echoedInvMatch.Groups["name"].Value.Trim();
                    }
                }

                var outputText = ExtractToolResultBody(turn.Body);
                var matchedPending = PopPendingInvocation(pending, resultToolName);
                if (matchedPending != null)
                {
                    correlated.Add(new CorrelatedToolCall(
                        matchedPending.ToolName,
                        matchedPending.ArgsJson,
                        outputText,
                        isErrorTurn,
                        matchedPending.StartOffset,
                        matchedPending.StartOffsetSeconds,
                        turn.Offset,
                        turn.OffsetSeconds,
                        matchedPending.HasMalformedArgs,
                        HasResultTurn: true));
                }
                else if (!string.IsNullOrWhiteSpace(resultToolName))
                {
                    correlated.Add(new CorrelatedToolCall(
                        resultToolName,
                        ArgsJson: null,
                        outputText,
                        isErrorTurn,
                        turn.Offset,
                        turn.OffsetSeconds,
                        turn.Offset,
                        turn.OffsetSeconds,
                        HasMalformedArgs: false,
                        HasResultTurn: true));
                }

                continue;
            }

            if (upperRole.Contains("TOOL", StringComparison.Ordinal))
            {
                var invocations = ExtractToolInvocations(turn.Body, out var malformed);
                malformedFenceCount += malformed;
                var hasOutputMarker = ToolOutputMarkerRegex().IsMatch(turn.Body);

                if (invocations.Count > 0)
                {
                    foreach (var inv in invocations)
                    {
                        if (hasOutputMarker || inv.InlineOutput != null)
                        {
                            // Pattern A result turn (echoed Tool Invocation + Tool Output) or Pattern C inline TOOL turn
                            var outputText = inv.InlineOutput ?? ExtractToolOutputSection(turn.Body) ?? string.Empty;
                            var matchedPending = PopPendingInvocation(pending, inv.ToolName, inv.ArgsJson);

                            if (matchedPending != null)
                            {
                                correlated.Add(new CorrelatedToolCall(
                                    matchedPending.ToolName,
                                    matchedPending.ArgsJson ?? inv.ArgsJson,
                                    outputText,
                                    isErrorTurn,
                                    matchedPending.StartOffset,
                                    matchedPending.StartOffsetSeconds,
                                    turn.Offset,
                                    turn.OffsetSeconds,
                                    matchedPending.HasMalformedArgs || inv.HasMalformedArgs,
                                    HasResultTurn: true));
                            }
                            else
                            {
                                correlated.Add(new CorrelatedToolCall(
                                    inv.ToolName,
                                    inv.ArgsJson,
                                    outputText,
                                    isErrorTurn,
                                    turn.Offset,
                                    turn.OffsetSeconds,
                                    turn.Offset,
                                    turn.OffsetSeconds,
                                    inv.HasMalformedArgs,
                                    HasResultTurn: true));
                            }
                        }
                        else
                        {
                            // Check if this TOOL turn echoes a pending ASSISTANT tool invocation without a **Tool Output:** marker
                            var matchedAssistantPending = PopPendingInvocation(
                                pending,
                                inv.ToolName,
                                inv.ArgsJson,
                                requireFromAssistantTurn: true);

                            if (matchedAssistantPending != null)
                            {
                                correlated.Add(new CorrelatedToolCall(
                                    matchedAssistantPending.ToolName,
                                    matchedAssistantPending.ArgsJson ?? inv.ArgsJson,
                                    inv.RemainingTextAfterArgs ?? string.Empty,
                                    isErrorTurn,
                                    matchedAssistantPending.StartOffset,
                                    matchedAssistantPending.StartOffsetSeconds,
                                    turn.Offset,
                                    turn.OffsetSeconds,
                                    matchedAssistantPending.HasMalformedArgs || inv.HasMalformedArgs,
                                    HasResultTurn: true));
                            }
                            else
                            {
                                // Pattern B invocation turn (TOOL turn waiting for a subsequent TOOL_RESULT / TOOL_ERROR turn)
                                pending.Add(new PendingToolInvocation(
                                    inv.ToolName,
                                    inv.ArgsJson,
                                    turn.Offset,
                                    turn.OffsetSeconds,
                                    inv.HasMalformedArgs,
                                    FromAssistantTurn: false,
                                    FallbackOutputText: inv.RemainingTextAfterArgs));
                            }
                        }
                    }
                }
                else
                {
                    // TOOL turn containing raw output only (e.g. legacy test transcripts)
                    var matchedPending = PopPendingInvocation(pending, toolName: null);
                    if (matchedPending != null)
                    {
                        correlated.Add(new CorrelatedToolCall(
                            matchedPending.ToolName,
                            matchedPending.ArgsJson,
                            turn.Body,
                            isErrorTurn,
                            matchedPending.StartOffset,
                            matchedPending.StartOffsetSeconds,
                            turn.Offset,
                            turn.OffsetSeconds,
                            matchedPending.HasMalformedArgs,
                            HasResultTurn: true));
                    }
                }

                continue;
            }

            // ASSISTANT / other turn: collect any tool invocations
            var assistantInvocations = ExtractToolInvocations(turn.Body, out var assistantMalformed);
            malformedFenceCount += assistantMalformed;
            foreach (var inv in assistantInvocations)
            {
                if (inv.InlineOutput != null)
                {
                    correlated.Add(new CorrelatedToolCall(
                        inv.ToolName,
                        inv.ArgsJson,
                        inv.InlineOutput,
                        IsErrorTurn: false,
                        turn.Offset,
                        turn.OffsetSeconds,
                        turn.Offset,
                        turn.OffsetSeconds,
                        inv.HasMalformedArgs,
                        HasResultTurn: true));
                }
                else
                {
                    pending.Add(new PendingToolInvocation(
                        inv.ToolName,
                        inv.ArgsJson,
                        turn.Offset,
                        turn.OffsetSeconds,
                        inv.HasMalformedArgs,
                        FromAssistantTurn: true));
                }
            }
        }

        // Flush any pending invocations that never received a result turn (interrupted or synthetic tests)
        foreach (var leftover in pending)
        {
            bool hasFallbackResult = !string.IsNullOrWhiteSpace(leftover.FallbackOutputText);
            correlated.Add(new CorrelatedToolCall(
                leftover.ToolName,
                leftover.ArgsJson,
                leftover.FallbackOutputText ?? string.Empty,
                IsErrorTurn: false,
                leftover.StartOffset,
                leftover.StartOffsetSeconds,
                EndOffset: null,
                EndOffsetSeconds: null,
                leftover.HasMalformedArgs,
                HasResultTurn: hasFallbackResult));
        }

        return (correlated, malformedFenceCount);
    }

    private static PendingToolInvocation? PopPendingInvocation(
        List<PendingToolInvocation> pending,
        string? toolName,
        string? echoedArgsJson = null,
        bool requireFromAssistantTurn = false)
    {
        if (pending.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(toolName))
        {
            var idx = pending.FindIndex(p =>
                (!requireFromAssistantTurn || p.FromAssistantTurn) &&
                p.ToolName.Equals(toolName, StringComparison.OrdinalIgnoreCase) &&
                (echoedArgsJson == null ||
                 p.ArgsJson == null ||
                 string.Equals(p.ArgsJson, echoedArgsJson, StringComparison.Ordinal)));
            if (idx >= 0)
            {
                var found = pending[idx];
                pending.RemoveAt(idx);
                return found;
            }

            return null;
        }

        if (requireFromAssistantTurn)
        {
            var assistantIdx = pending.FindIndex(p => p.FromAssistantTurn);
            if (assistantIdx < 0)
            {
                return null;
            }

            var matched = pending[assistantIdx];
            pending.RemoveAt(assistantIdx);
            return matched;
        }

        var first = pending[0];
        pending.RemoveAt(0);
        return first;
    }

    private static List<ExtractedToolInvocation> ExtractToolInvocations(string body, out int malformedFenceCount)
    {
        malformedFenceCount = 0;
        var closedFences = BuildClosedFenceRanges(body);
        var matches = ToolInvocationRegex()
            .Matches(body)
            .Cast<Match>()
            .Where(m => !IsIndexInsideFence(m.Index, closedFences))
            .ToList();
        if (matches.Count == 0)
        {
            return new List<ExtractedToolInvocation>();
        }

        var results = new List<ExtractedToolInvocation>(matches.Count);
        for (int i = 0; i < matches.Count; i++)
        {
            var match = matches[i];
            var name = match.Groups["name"].Value.Trim();
            int afterHeader = match.Index + match.Length;
            int nextBoundary = (i + 1 < matches.Count) ? matches[i + 1].Index : body.Length;

            var tail = body.Substring(afterHeader, nextBoundary - afterHeader);
            var trimmedStart = tail.TrimStart();
            string? argsJson = null;
            string? inlineOutput = null;
            string? remainingTextAfterArgs = null;
            bool hasMalformedArgs = false;

            var fencePrefix = DetectOpeningFence(trimmedStart);
            if (fencePrefix != null)
            {
                int headerEnd = trimmedStart.IndexOf('\n');
                if (headerEnd < 0)
                {
                    hasMalformedArgs = true;
                    malformedFenceCount++;
                }
                else
                {
                    var afterOpeningLine = trimmedStart.Substring(headerEnd + 1);
                    var closingIdx = FindClosingFenceIndex(afterOpeningLine, fencePrefix);
                    if (closingIdx >= 0)
                    {
                        argsJson = afterOpeningLine.Substring(0, closingIdx).Trim();
                        int closeLineEnd = afterOpeningLine.IndexOf('\n', closingIdx);
                        var afterClosing = closeLineEnd >= 0
                            ? afterOpeningLine.Substring(closeLineEnd + 1)
                            : afterOpeningLine.Substring(closingIdx + fencePrefix.Length);
                        inlineOutput = ExtractToolOutputSection(afterClosing);
                        if (inlineOutput == null)
                        {
                            var trimmedAfterClosing = afterClosing.Trim();
                            if (!string.IsNullOrEmpty(trimmedAfterClosing))
                            {
                                remainingTextAfterArgs = trimmedAfterClosing;
                            }
                        }
                    }
                    else
                    {
                        hasMalformedArgs = true;
                        malformedFenceCount++;
                        var outMatch = ToolOutputMarkerRegex().Match(afterOpeningLine);
                        argsJson = outMatch.Success
                            ? afterOpeningLine.Substring(0, outMatch.Index).Trim()
                            : afterOpeningLine.Trim();
                        if (outMatch.Success)
                        {
                            inlineOutput = ExtractToolOutputSection(afterOpeningLine.Substring(outMatch.Index));
                        }
                    }
                }
            }
            else
            {
                inlineOutput = ExtractToolOutputSection(tail);
                if (inlineOutput == null)
                {
                    var trimmedTail = tail.Trim();
                    if (!string.IsNullOrEmpty(trimmedTail))
                    {
                        remainingTextAfterArgs = trimmedTail;
                    }
                }
            }

            if (argsJson != null && !IsValidJsonObject(argsJson))
            {
                hasMalformedArgs = true;
            }

            results.Add(new ExtractedToolInvocation(name, argsJson, inlineOutput, remainingTextAfterArgs, hasMalformedArgs));
        }

        return results;
    }

    private static string? DetectOpeningFence(string text)
    {
        if (text.StartsWith("````", StringComparison.Ordinal))
        {
            return "````";
        }
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            return "```";
        }
        return null;
    }

    private static int FindClosingFenceIndex(string text, string fence)
    {
        int pos = 0;
        while (pos <= text.Length)
        {
            int lineEnd = text.IndexOf('\n', pos);
            var line = (lineEnd >= 0 ? text.Substring(pos, lineEnd - pos) : text.Substring(pos)).TrimEnd('\r').Trim();
            if (string.Equals(line, fence, StringComparison.Ordinal))
            {
                return pos;
            }
            if (lineEnd < 0)
            {
                break;
            }
            pos = lineEnd + 1;
        }
        return -1;
    }

    private static string? ExtractToolOutputSection(string text)
    {
        var markerMatch = ToolOutputMarkerRegex().Match(text);
        if (!markerMatch.Success)
        {
            return null;
        }

        var afterMarker = text.Substring(markerMatch.Index + markerMatch.Length).Trim();
        var fence = DetectOpeningFence(afterMarker);
        if (fence != null)
        {
            int nl = afterMarker.IndexOf('\n');
            if (nl >= 0)
            {
                var inside = afterMarker.Substring(nl + 1);
                var closingIdx = FindClosingFenceIndex(inside, fence);
                return closingIdx >= 0
                    ? inside.Substring(0, closingIdx).Trim()
                    : inside.Trim();
            }
        }

        return afterMarker;
    }

    private static string ExtractToolResultBody(string turnBody)
    {
        var outputSection = ExtractToolOutputSection(turnBody);
        if (outputSection != null)
        {
            return outputSection;
        }

        var working = turnBody;
        var toolHeader = ToolResultHeaderRegex().Match(working);
        if (toolHeader.Success)
        {
            working = working.Substring(toolHeader.Index + toolHeader.Length).TrimStart();
        }

        var fence = DetectOpeningFence(working);
        if (fence != null)
        {
            int nl = working.IndexOf('\n');
            if (nl >= 0)
            {
                var inside = working.Substring(nl + 1);
                var closingIdx = FindClosingFenceIndex(inside, fence);
                return closingIdx >= 0
                    ? inside.Substring(0, closingIdx).Trim()
                    : inside.Trim();
            }
        }

        return RemoveToolInvocationMarkers(working).Trim();
    }

    private static string RemoveToolInvocationMarkers(string text)
    {
        var match = ToolInvocationRegex().Match(text);
        if (!match.Success)
        {
            return text;
        }
        return text.Remove(match.Index, match.Length).Trim();
    }

    private static bool IsValidJsonObject(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith('{'))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            return doc.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            // Justification: truncated or malformed tool JSON arguments are surfaced as MalformedOrTruncatedToolCount.
            return false;
        }
    }

    private static string? ExtractFilePathArg(string? argsJson)
    {
        if (string.IsNullOrWhiteSpace(argsJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in new[] { "path", "file_path", "TargetFile", "AbsolutePath" })
                {
                    if (doc.RootElement.TryGetProperty(prop, out var el) && el.ValueKind == JsonValueKind.String)
                    {
                        var val = el.GetString()?.Trim();
                        if (!string.IsNullOrWhiteSpace(val))
                        {
                            return val;
                        }
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Justification: malformed JSON falls back to regex extraction so partial transcripts still attribute file friction.
        }

        var match = FilePathArgRegex().Match(argsJson);
        return match.Success ? UnescapeJsonSnippet(match.Groups["path"].Value.Trim()) : null;
    }

    private static string? ExtractBashCommandArg(string? argsJson)
    {
        if (string.IsNullOrWhiteSpace(argsJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in new[] { "command", "CommandLine" })
                {
                    if (doc.RootElement.TryGetProperty(prop, out var el) && el.ValueKind == JsonValueKind.String)
                    {
                        var val = el.GetString()?.Trim();
                        if (!string.IsNullOrWhiteSpace(val))
                        {
                            return val;
                        }
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Justification: malformed JSON falls back to regex extraction so partial transcripts still attribute command friction.
        }

        var match = BashCommandArgRegex().Match(argsJson);
        return match.Success ? UnescapeJsonSnippet(match.Groups["cmd"].Value) : null;
    }

    private static string UnescapeJsonSnippet(string raw) =>
        raw.Replace("\\\"", "\"").Replace("\\\\", "\\").Replace("\\n", "\n").Replace("\\r", "\r").Replace("\\t", "\t");

    private static (string CanonicalKey, string Summary) NormalizeToolArguments(string? argsJson)
    {
        if (string.IsNullOrWhiteSpace(argsJson))
        {
            return ("{}", "(no arguments)");
        }

        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                var ordered = new SortedDictionary<string, string>(StringComparer.Ordinal);
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    ordered[prop.Name] = prop.Value.GetRawText();
                }

                var canonical = "{" + string.Join(",", ordered.Select(kvp => $"{JsonSerializer.Serialize(kvp.Key)}:{kvp.Value}")) + "}";
                var summary = canonical.Length <= MaxNormalizedArgsLength
                    ? canonical
                    : canonical.Substring(0, MaxNormalizedArgsLength) + "...";
                return (canonical, summary);
            }
        }
        catch (JsonException)
        {
            // Justification: malformed JSON falls back to whitespace-collapsed normalization for loop detection.
        }

        var collapsed = WhitespaceRegex().Replace(argsJson.Trim(), " ");
        var truncatedSummary = collapsed.Length <= MaxNormalizedArgsLength
            ? collapsed
            : collapsed.Substring(0, MaxNormalizedArgsLength) + "...";
        return (collapsed, truncatedSummary);
    }

    private static List<SlowTurnEntry> ExtractSlowLlmTurns(List<ParsedTurn> turns)
    {
        var slowTurns = new List<SlowTurnEntry>();
        for (int i = 1; i < turns.Count; i++)
        {
            var prev = turns[i - 1];
            var curr = turns[i];
            if (!prev.OffsetSeconds.HasValue || !curr.OffsetSeconds.HasValue)
            {
                continue;
            }

            ParsedTurn? priorToPrev = i >= 2 ? turns[i - 2] : null;
            if (!IsPromptOrToolResultTurn(prev, priorToPrev) || !IsLlmOutputTurn(curr, prev))
            {
                continue;
            }

            int delta = curr.OffsetSeconds.Value - prev.OffsetSeconds.Value;
            if (delta > TranscriptAnalysisDefaults.SlowLlmTurnThresholdSeconds)
            {
                var label = $"{prev.Role.ToUpperInvariant()} -> {curr.Role.ToUpperInvariant()}";
                slowTurns.Add(new SlowTurnEntry(prev.Offset, curr.Offset, label, delta));
            }
        }

        return slowTurns;
    }

    private static bool IsPromptOrToolResultTurn(ParsedTurn turn, ParsedTurn? precedingTurn)
    {
        var role = turn.Role.ToUpperInvariant();
        if (role.Contains("USER", StringComparison.Ordinal) ||
            role == "TOOL_RESULT" ||
            role == "TOOL_ERROR" ||
            role == "COMPACTION")
        {
            return true;
        }

        if (role == "TOOL")
        {
            if (ToolOutputMarkerRegex().IsMatch(turn.Body) || !ToolInvocationRegex().IsMatch(turn.Body))
            {
                return true;
            }

            // If a TOOL turn echoes Tool Invocation without Tool Output directly after an ASSISTANT tool invocation, it is the tool result turn
            if (precedingTurn != null &&
                precedingTurn.Role.Contains("ASSISTANT", StringComparison.OrdinalIgnoreCase) &&
                ToolInvocationRegex().IsMatch(precedingTurn.Body))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsLlmOutputTurn(ParsedTurn turn, ParsedTurn precedingTurn)
    {
        var role = turn.Role.ToUpperInvariant();
        if (role.Contains("THINKING", StringComparison.Ordinal) ||
            role.Contains("ASSISTANT", StringComparison.Ordinal))
        {
            return true;
        }

        if (role == "TOOL")
        {
            if (precedingTurn.Role.Contains("ASSISTANT", StringComparison.OrdinalIgnoreCase) &&
                ToolInvocationRegex().IsMatch(precedingTurn.Body))
            {
                return false;
            }

            // A TOOL turn is an LLM-initiated tool invocation if it carries Tool Invocation without Tool Output
            return ToolInvocationRegex().IsMatch(turn.Body) && !ToolOutputMarkerRegex().IsMatch(turn.Body);
        }

        return false;
    }

    private static List<CompilerErrorEntry> ExtractCompilerErrorBreakdown(string conversationBody, out int totalCount)
    {
        totalCount = 0;
        var trackers = new Dictionary<string, CompilerErrorTracker>(StringComparer.OrdinalIgnoreCase);

        var lines = conversationBody.Split('\n');
        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');
            foreach (Match m in CompilerErrorRegex().Matches(line))
            {
                totalCount++;
                var code = m.Groups["code"].Value.ToUpperInvariant();
                var rawMsg = m.Groups["msg"].Value.Trim();

                // Strip trailing MSBuild project path suffix e.g. [C:\repo\Foo.csproj]
                var projectBracketIdx = rawMsg.LastIndexOf(" [", StringComparison.Ordinal);
                if (projectBracketIdx > 0 && rawMsg.EndsWith(']') &&
                    (rawMsg.Contains(".csproj", StringComparison.OrdinalIgnoreCase) || rawMsg.Contains(".fsproj", StringComparison.OrdinalIgnoreCase)))
                {
                    rawMsg = rawMsg.Substring(0, projectBracketIdx).Trim();
                }

                if (rawMsg.Length > MaxCompilerMessageLength)
                {
                    rawMsg = rawMsg.Substring(0, MaxCompilerMessageLength) + "...";
                }

                string? sampleFile = null;
                if (m.Index > 0)
                {
                    var prefix = line.Substring(0, m.Index);
                    var fileMatch = DiagnosticFilePrefixRegex().Match(prefix);
                    if (fileMatch.Success)
                    {
                        var candidate = fileMatch.Groups["file"].Value.Trim();
                        if (LooksLikeDiagnosticFilePath(candidate))
                        {
                            sampleFile = candidate;
                        }
                    }
                }

                if (!trackers.TryGetValue(code, out var tracker))
                {
                    tracker = new CompilerErrorTracker(code, rawMsg, sampleFile);
                    trackers[code] = tracker;
                }
                else if (tracker.SampleFilePath == null && sampleFile != null)
                {
                    tracker.SampleFilePath = sampleFile;
                }
                tracker.Count++;
            }
        }

        return trackers.Values
            .OrderByDescending(t => t.Count)
            .ThenBy(t => t.DiagnosticCode, StringComparer.OrdinalIgnoreCase)
            .Select(t => new CompilerErrorEntry(t.DiagnosticCode, t.Count, t.SampleMessage, t.SampleFilePath))
            .ToList();
    }

    private static bool LooksLikeDiagnosticFilePath(string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate) || candidate.Length > 260)
        {
            return false;
        }

        return candidate.Contains('/') ||
               candidate.Contains('\\') ||
               Path.HasExtension(candidate);
    }

    private static void SynthesizeFrictionAndRecommendations(
        TranscriptFrontmatter? frontmatter,
        IReadOnlyList<FileFrictionEntry> fileFriction,
        IReadOnlyList<ToolFrictionEntry> toolFriction,
        IReadOnlyList<BashCommandFrictionEntry> bashCommandFriction,
        IReadOnlyList<CompilerErrorEntry> compilerBreakdown,
        TranscriptBehavioralSignals behavioralSignals,
        IReadOnlyList<KeyValuePair<string, int>> highChurnFiles,
        int bashErrors,
        int editFailures,
        int toolErrors,
        int compilerErrors,
        int testFailures,
        int compactionEvents,
        int historyRewrites,
        bool ranStaleTest,
        int gitDiffCommands,
        int editedFileCount,
        List<string> frictionPoints,
        List<FrictionRecommendation> recommendations)
    {
        // --- User Codebase Attribution ---
        var splitCandidates = fileFriction.Where(f => f.IsSplitCandidate).ToList();
        if (splitCandidates.Count > 0)
        {
            foreach (var file in splitCandidates)
            {
                var fileNameOnly = Path.GetFileName(file.FilePath);
                recommendations.Add(new FrictionRecommendation(
                    Target: RecommendationTarget.UserCodebase,
                    Category: FrictionCategory.FileArchitecture,
                    Title: $"Split high-friction source file: {fileNameOnly}",
                    ActionableDetail: $"Modularize '{file.FilePath}' ({file.EditCount} edits, {file.EditFailureCount} edit failure(s), {file.ConsecutiveReReadCount} consecutive re-read(s)) into smaller single-responsibility files to reduce churn.",
                    Subject: file.FilePath));
            }
        }

        if (highChurnFiles.Count > 0)
        {
            var churnSummary = string.Join(", ", highChurnFiles.Select(f => $"{Path.GetFileName(f.Key)} ({f.Value} edits)"));
            frictionPoints.Add($"High edit churn detected on: {churnSummary}.");
        }

        var reReadCandidates = splitCandidates
            .Where(f => f.ConsecutiveReReadCount >= 3 && f.EditCount < 3)
            .ToList();
        if (reReadCandidates.Count > 0)
        {
            var reReadSummary = string.Join(", ", reReadCandidates.Select(f => $"{Path.GetFileName(f.FilePath)} ({f.ConsecutiveReReadCount} consecutive reads)"));
            frictionPoints.Add($"Repeated consecutive file re-reads without edits on: {reReadSummary}.");
        }

        if (compilerErrors > 0)
        {
            var topCodes = string.Join(", ", compilerBreakdown.Take(3).Select(c => $"{c.DiagnosticCode} ({c.Count}x)"));
            var suffix = string.IsNullOrEmpty(topCodes) ? string.Empty : $" [{topCodes}]";
            frictionPoints.Add($"Compiler diagnostics: {compilerErrors} compiler error(s){suffix} occurred during execution.");

            foreach (var err in compilerBreakdown.Take(3))
            {
                var fileContext = string.IsNullOrWhiteSpace(err.SampleFilePath) ? string.Empty : $" in '{err.SampleFilePath}'";
                recommendations.Add(new FrictionRecommendation(
                    Target: RecommendationTarget.UserCodebase,
                    Category: FrictionCategory.CompilerDiagnostic,
                    Title: $"Resolve recurring {err.DiagnosticCode} diagnostic ({err.Count}x)",
                    ActionableDetail: $"Fix {err.DiagnosticCode}{fileContext}: {err.SampleMessage}",
                    Subject: err.DiagnosticCode));
            }
        }

        if (testFailures > 0 || behavioralSignals.HasTestOscillation)
        {
            var testList = behavioralSignals.FailingTests.Count > 0
                ? string.Join(", ", behavioralSignals.FailingTests.Take(5))
                : "failing test suite";
            frictionPoints.Add(behavioralSignals.HasTestOscillation
                ? $"Test oscillation detected across {behavioralSignals.TestRunCycles} test run(s): {testFailures} failure(s) ({testList})."
                : $"Test regressions: {testFailures} test failure(s) occurred during execution.");

            var subjectTest = behavioralSignals.FailingTests.FirstOrDefault() ?? "TestSuite";
            recommendations.Add(new FrictionRecommendation(
                Target: RecommendationTarget.UserCodebase,
                Category: FrictionCategory.TestSuite,
                Title: behavioralSignals.HasTestOscillation ? "Stabilize oscillating test suite" : "Fix failing test assertions",
                ActionableDetail: $"Investigate and repair failing test(s): {testList}.",
                Subject: subjectTest));
        }

        // --- NightCodeForge Harness & Prompt Attribution ---
        if (editFailures > 0)
        {
            var editedWithoutReadFiles = fileFriction.Where(f => f.EditedWithoutReadCount > 0).Select(f => Path.GetFileName(f.FilePath)).ToList();
            var withoutReadHint = editedWithoutReadFiles.Count > 0
                ? $" ({editedWithoutReadFiles.Count} file(s) edited before reading: {string.Join(", ", editedWithoutReadFiles.Take(3))})"
                : string.Empty;
            frictionPoints.Add($"Edit mismatch failures: {editFailures} edit(s) failed to match target content (exact or indentation mismatch){withoutReadHint}.");
            recommendations.Add(new FrictionRecommendation(
                Target: RecommendationTarget.NightCodeForge,
                Category: FrictionCategory.Tooling,
                Title: "Enforce read-before-edit and smaller edit chunks",
                ActionableDetail: $"Strengthen edit tool prompt guidance and pre-edit validation{withoutReadHint} so the agent reads target lines before editing and keeps replacement chunks compact.",
                Subject: CanonicalToolNames.Edit));
        }

        if (ranStaleTest)
        {
            frictionPoints.Add("Stale test execution: The agent executed 'dotnet test --no-build' immediately after code edits without a preceding build.");
            recommendations.Add(new FrictionRecommendation(
                Target: RecommendationTarget.NightCodeForge,
                Category: FrictionCategory.PromptOrStage,
                Title: "Require 'dotnet build' before 'dotnet test --no-build'",
                ActionableDetail: "Update stage execution prompt directives to forbid running 'dotnet test --no-build' after source file edits without an intervening 'dotnet build'.",
                Subject: "dotnet test --no-build"));
        }

        if (bashErrors > 0)
        {
            frictionPoints.Add($"Shell command errors: {bashErrors} non-zero bash exit code(s) occurred.");
        }

        var hangingCommands = bashCommandFriction.Where(b => b.HangOrTimeoutCount > 0).ToList();
        if (hangingCommands.Count > 0)
        {
            var cmdSummary = string.Join(", ", hangingCommands.Select(b => $"{b.CommandPattern} ({b.HangOrTimeoutCount}x)"));
            frictionPoints.Add($"Command hangs/timeouts detected on: {cmdSummary}.");
            recommendations.Add(new FrictionRecommendation(
                Target: RecommendationTarget.NightCodeForge,
                Category: FrictionCategory.ShellCommand,
                Title: "Add non-interactive flags and command timeout guardrails",
                ActionableDetail: $"Harden bash execution rules and prompt flags for {cmdSummary} (non-interactive options, explicit timeouts, and hang detection).",
                Subject: hangingCommands[0].CommandPattern));
        }

        if (toolErrors > 0)
        {
            frictionPoints.Add($"Tool execution errors: {toolErrors} generic tool error(s) occurred.");
        }

        var failingNonEditTools = toolFriction
            .Where(t => !IsEditOrWriteTool(t.ToolName) && t.FailureCount > 0)
            .ToList();
        if (failingNonEditTools.Count > 0)
        {
            var topTool = failingNonEditTools[0];
            recommendations.Add(new FrictionRecommendation(
                Target: RecommendationTarget.NightCodeForge,
                Category: FrictionCategory.Tooling,
                Title: $"Improve '{topTool.ToolName}' tool ergonomics & validation",
                ActionableDetail: $"'{topTool.ToolName}' failed {topTool.FailureCount}/{topTool.InvocationCount} time(s) ({topTool.FailureRatePercent.ToString("F0", CultureInfo.InvariantCulture)}%). Review tool schema guidance and error hints{(string.IsNullOrWhiteSpace(topTool.SampleError) ? string.Empty : $": {topTool.SampleError}")}.",
                Subject: topTool.ToolName));
        }

        var oversizedTools = toolFriction.Where(t => t.OversizedOutputCount > 0).ToList();
        if (oversizedTools.Count > 0)
        {
            var toolList = string.Join(", ", oversizedTools.Select(t => $"{t.ToolName} ({t.OversizedOutputCount}x)"));
            frictionPoints.Add($"Oversized tool outputs (> {TranscriptAnalysisDefaults.OversizedToolOutputCharThreshold:N0} chars) detected on: {toolList}.");
            recommendations.Add(new FrictionRecommendation(
                Target: RecommendationTarget.NightCodeForge,
                Category: FrictionCategory.Tooling,
                Title: "Filter or truncate oversized tool outputs",
                ActionableDetail: $"Apply CLI output filtering or stricter truncation caps on {toolList} to prevent premature context saturation.",
                Subject: oversizedTools[0].ToolName));
        }

        if (behavioralSignals.MalformedOrTruncatedToolCount > 0)
        {
            frictionPoints.Add($"Malformed or truncated tool invocations: {behavioralSignals.MalformedOrTruncatedToolCount} detected.");
            recommendations.Add(new FrictionRecommendation(
                Target: RecommendationTarget.NightCodeForge,
                Category: FrictionCategory.Tooling,
                Title: "Harden tool call JSON schema & truncation recovery",
                ActionableDetail: $"Detected {behavioralSignals.MalformedOrTruncatedToolCount} malformed/truncated tool call(s); verify guided grammar decoding or tool call argument limits.",
                Subject: "ToolSchema"));
        }

        if (compactionEvents > 0)
        {
            frictionPoints.Add($"Context compaction: {compactionEvents} context compaction checkpoint(s) occurred during execution.");
            if (compactionEvents > 5)
            {
                recommendations.Add(new FrictionRecommendation(
                    Target: RecommendationTarget.NightCodeForge,
                    Category: FrictionCategory.ContextAndCache,
                    Title: "Tune context budget and compaction thresholds",
                    ActionableDetail: $"High compaction frequency ({compactionEvents} checkpoints) indicates context window saturation. Increase context budget, tune compaction trigger thresholds, or decompose stage scope.",
                    Subject: "Compaction"));
            }
        }

        if (historyRewrites > 10)
        {
            frictionPoints.Add($"History rewrites: {historyRewrites} call(s) had prior messages mutated/rewritten (prefix cache invalidated).");
            recommendations.Add(new FrictionRecommendation(
                Target: RecommendationTarget.NightCodeForge,
                Category: FrictionCategory.ContextAndCache,
                Title: "Stabilize prompt prefix to preserve LLM KV-cache",
                ActionableDetail: $"Inspect historical reasoning pruning or message mutation middleware ({historyRewrites} history rewrites) to stop invalidating LLM prompt prefix KV-cache.",
                Subject: "KvCachePrefix"));
        }
        else if (historyRewrites > 0)
        {
            frictionPoints.Add($"History rewrites: {historyRewrites} call(s) had prior messages mutated/rewritten (prefix cache invalidated).");
        }

        if (IsCodeReviewStage(frontmatter?.StageId) && gitDiffCommands >= 3 && editedFileCount <= 1)
        {
            var editSummary = editedFileCount == 0 ? "zero-file" : $"{editedFileCount}-file";
            frictionPoints.Add($"Code review exploration overhead: Agent ran {gitDiffCommands} 'git diff' command(s) to inspect changes for a {editSummary} edit.");
            recommendations.Add(new FrictionRecommendation(
                Target: RecommendationTarget.NightCodeForge,
                Category: FrictionCategory.PromptOrStage,
                Title: "Pre-inject git diff summary into code review stage prompt",
                ActionableDetail: $"Pre-inject git diff summary and changed files directly into the code review stage prompt to avoid {gitDiffCommands} redundant exploratory bash commands.",
                Subject: "code-review-diff"));
        }

        if (behavioralSignals.RepeatedToolLoops.Count > 0)
        {
            var topLoop = behavioralSignals.RepeatedToolLoops[0];
            frictionPoints.Add($"Repeated identical tool loop: '{topLoop.ToolName}' invoked {topLoop.RepeatCount}x with identical arguments.");
            recommendations.Add(new FrictionRecommendation(
                Target: RecommendationTarget.NightCodeForge,
                Category: FrictionCategory.PromptOrStage,
                Title: "Mitigate repeated identical tool call loops",
                ActionableDetail: $"'{topLoop.ToolName}' was invoked {topLoop.RepeatCount}x with identical arguments ({topLoop.NormalizedArgsSummary}). Tighten loop-circuit-breaker thresholds or stage instructions.",
                Subject: topLoop.ToolName));
        }

        if (behavioralSignals.DeadEndSearchCount > 0)
        {
            frictionPoints.Add($"Dead-end searches or missing-file reads: {behavioralSignals.DeadEndSearchCount} zero-match/not-found call(s).");
            if (behavioralSignals.DeadEndSearchCount >= 3)
            {
                recommendations.Add(new FrictionRecommendation(
                    Target: RecommendationTarget.NightCodeForge,
                    Category: FrictionCategory.PromptOrStage,
                    Title: "Improve symbol map & repo structure pre-injection",
                    ActionableDetail: $"Agent hit {behavioralSignals.DeadEndSearchCount} dead-end searches/missing reads; enrich DeterministicSymbolMap and top-level directory context in the stage prompt.",
                    Subject: "SymbolMap"));
            }
        }

        if (behavioralSignals.SelfRevertCount > 0)
        {
            frictionPoints.Add($"Self-reverted edits: {behavioralSignals.SelfRevertCount} git checkout/restore revert(s) executed.");
        }

        if (behavioralSignals.SlowLlmTurns.Count > 0)
        {
            var maxDelay = behavioralSignals.SlowLlmTurns.Max(s => s.DurationSeconds);
            frictionPoints.Add($"Slow LLM inference turns (> {TranscriptAnalysisDefaults.SlowLlmTurnThresholdSeconds}s): {behavioralSignals.SlowLlmTurns.Count} turn(s), peak {maxDelay}s.");
            recommendations.Add(new FrictionRecommendation(
                Target: RecommendationTarget.NightCodeForge,
                Category: FrictionCategory.ContextAndCache,
                Title: "Investigate slow LLM turn latency (cold loads / KV-cache misses)",
                ActionableDetail: $"Detected {behavioralSignals.SlowLlmTurns.Count} LLM turn(s) exceeding {TranscriptAnalysisDefaults.SlowLlmTurnThresholdSeconds}s (peak {maxDelay}s). Check JIT model preload and prefix KV-cache stability.",
                Subject: "LlmLatency"));
        }

        if (behavioralSignals.SlowToolExecutions.Count > 0)
        {
            var slowest = behavioralSignals.SlowToolExecutions.OrderByDescending(s => s.DurationSeconds).First();
            frictionPoints.Add($"Slow tool executions (> {TranscriptAnalysisDefaults.SlowToolExecutionThresholdSeconds}s): {behavioralSignals.SlowToolExecutions.Count} call(s), slowest '{slowest.Label}' ({slowest.DurationSeconds}s).");
            recommendations.Add(new FrictionRecommendation(
                Target: RecommendationTarget.NightCodeForge,
                Category: FrictionCategory.Tooling,
                Title: "Scope slow tool or build/test commands",
                ActionableDetail: $"'{slowest.Label}' took {slowest.DurationSeconds}s (threshold {TranscriptAnalysisDefaults.SlowToolExecutionThresholdSeconds}s). Scope test filters or enforce command timeouts.",
                Subject: slowest.Label));
        }
    }

    private static WorkspaceFrictionHotspots BuildWorkspaceFrictionHotspots(IReadOnlyList<TranscriptAnalysis> analyses)
    {
        if (analyses.Count == 0)
        {
            return WorkspaceFrictionHotspots.Empty;
        }

        var mergedFiles = new Dictionary<string, FileFrictionEntry>(StringComparer.OrdinalIgnoreCase);
        var mergedErrors = new Dictionary<string, CompilerErrorTracker>(StringComparer.OrdinalIgnoreCase);
        var failingTestSessions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var oscillatingTests = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var analysis in analyses)
        {
            foreach (var file in analysis.FileFriction)
            {
                if (mergedFiles.TryGetValue(file.FilePath, out var existing))
                {
                    mergedFiles[file.FilePath] = new FileFrictionEntry(
                        existing.FilePath,
                        existing.ReadCount + file.ReadCount,
                        existing.EditCount + file.EditCount,
                        existing.EditFailureCount + file.EditFailureCount,
                        existing.EditedWithoutReadCount + file.EditedWithoutReadCount,
                        Math.Max(existing.ConsecutiveReReadCount, file.ConsecutiveReReadCount));
                }
                else
                {
                    mergedFiles[file.FilePath] = file;
                }
            }

            foreach (var err in analysis.CompilerErrorBreakdown)
            {
                if (!mergedErrors.TryGetValue(err.DiagnosticCode, out var tracker))
                {
                    tracker = new CompilerErrorTracker(err.DiagnosticCode, err.SampleMessage, err.SampleFilePath);
                    mergedErrors[err.DiagnosticCode] = tracker;
                }
                else if (tracker.SampleFilePath == null && err.SampleFilePath != null)
                {
                    tracker.SampleFilePath = err.SampleFilePath;
                }
                tracker.Count += err.Count;
            }

            foreach (var test in analysis.BehavioralSignals.FailingTests)
            {
                failingTestSessions[test] = failingTestSessions.GetValueOrDefault(test, 0) + 1;
                if (analysis.BehavioralSignals.HasTestOscillation)
                {
                    oscillatingTests.Add(test);
                }
            }
        }

        var splitCandidateFiles = mergedFiles.Values
            .Where(f => f.IsSplitCandidate)
            .OrderByDescending(f => f.EditFailureCount * 3 + f.EditCount * 2 + f.ReadCount)
            .ThenBy(f => f.FilePath, StringComparer.OrdinalIgnoreCase)
            .Take(MaxHotspotsPerGroup)
            .ToList();

        var topCompilerErrors = mergedErrors.Values
            .OrderByDescending(e => e.Count)
            .ThenBy(e => e.DiagnosticCode, StringComparer.OrdinalIgnoreCase)
            .Take(MaxHotspotsPerGroup)
            .Select(e => new CompilerErrorEntry(e.DiagnosticCode, e.Count, e.SampleMessage, e.SampleFilePath))
            .ToList();

        var recurringFailingTests = failingTestSessions
            .Where(kvp => kvp.Value >= 2 || oscillatingTests.Contains(kvp.Key))
            .OrderByDescending(kvp => kvp.Value)
            .ThenBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
            .Take(MaxHotspotsPerGroup)
            .Select(kvp => kvp.Key)
            .ToList();

        return new WorkspaceFrictionHotspots(
            analyses.Count,
            splitCandidateFiles,
            topCompilerErrors,
            recurringFailingTests);
    }

    private static IReadOnlyList<string> GetWorkspaceTranscriptDirectories(string workspacePath)
    {
        var dirs = new List<string>();
        var docsDir = Path.Combine(workspacePath, "docs", "transcripts");
        if (Directory.Exists(docsDir))
        {
            dirs.Add(docsDir);
        }

        var ncfDir = Path.Combine(workspacePath, ".nightcodeforge", "transcripts");
        if (Directory.Exists(ncfDir))
        {
            dirs.Add(ncfDir);
        }

        var worktreesRoot = Path.Combine(workspacePath, ".nightcodeforge", "worktrees");
        if (Directory.Exists(worktreesRoot))
        {
            foreach (var wt in Directory.GetDirectories(worktreesRoot))
            {
                var wtTranscripts = Path.Combine(wt, "docs", "transcripts");
                if (Directory.Exists(wtTranscripts))
                {
                    dirs.Add(wtTranscripts);
                }
            }
        }

        return dirs;
    }

    private static (DateTime? ParsedDate, int TaskId, int RunNumber) ParseFileNameSortKey(string fileName)
    {
        var match = TranscriptFileNameMetadataRegex().Match(fileName);
        if (!match.Success)
        {
            return (null, 0, 1);
        }

        DateTime? parsedDate = null;
        if (match.Groups["date"].Success &&
            DateTime.TryParseExact(match.Groups["date"].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt))
        {
            parsedDate = dt.Date;
        }

        int taskId = match.Groups["taskId"].Success && int.TryParse(match.Groups["taskId"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tid)
            ? tid
            : 0;

        int runNumber = match.Groups["run"].Success && int.TryParse(match.Groups["run"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var run)
            ? run
            : 1;

        return (parsedDate, taskId, runNumber);
    }

    private static FileFrictionTracker GetOrCreateFileTracker(Dictionary<string, FileFrictionTracker> trackers, string path)
    {
        if (!trackers.TryGetValue(path, out var tracker))
        {
            tracker = new FileFrictionTracker(path);
            trackers[path] = tracker;
        }
        return tracker;
    }

    private static string BuildSlowToolLabel(CorrelatedToolCall call)
    {
        if (IsBashTool(call.ToolName))
        {
            var cmd = ExtractBashCommandArg(call.ArgsJson);
            if (!string.IsNullOrWhiteSpace(cmd))
            {
                return $"{call.ToolName} ({NormalizeBashCommandPattern(cmd)})";
            }
        }
        else if (IsReadTool(call.ToolName) || IsEditOrWriteTool(call.ToolName))
        {
            var path = ExtractFilePathArg(call.ArgsJson);
            if (!string.IsNullOrWhiteSpace(path))
            {
                return $"{call.ToolName} ({Path.GetFileName(path)})";
            }
        }

        return call.ToolName;
    }

    private static bool IsOversizedOutput(string outputText)
    {
        if (string.IsNullOrEmpty(outputText))
        {
            return false;
        }

        if (outputText.Length > TranscriptAnalysisDefaults.OversizedToolOutputCharThreshold)
        {
            return true;
        }

        var match = TruncatedOutputRegex().Match(outputText);
        if (match.Success &&
            int.TryParse(match.Groups["total"].Value.Replace(",", string.Empty, StringComparison.Ordinal), NumberStyles.Integer, CultureInfo.InvariantCulture, out var totalChars))
        {
            return totalChars > TranscriptAnalysisDefaults.OversizedToolOutputCharThreshold;
        }

        return false;
    }

    private static string? ExtractSampleErrorLine(string outputText, string? fallback)
    {
        if (string.IsNullOrWhiteSpace(outputText))
        {
            return fallback;
        }

        var lines = outputText.Split('\n');
        string? firstNonEmpty = null;
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line))
            {
                continue;
            }

            firstNonEmpty ??= line;
            if (EditFailureRegex().IsMatch(line) ||
                IsGenericToolErrorLine(line) ||
                CompilerErrorRegex().IsMatch(line) ||
                ExitCodeRegex().IsMatch(line) ||
                BashHangOrTimeoutRegex().IsMatch(line))
            {
                return TruncateText(line, MaxSampleErrorLength);
            }
        }

        return firstNonEmpty != null ? TruncateText(firstNonEmpty, MaxSampleErrorLength) : fallback;
    }

    private static string TruncateText(string text, int maxLength) =>
        text.Length <= maxLength ? text : text.Substring(0, maxLength) + "...";

    private static int CountEditFailureLines(string outputText)
    {
        if (string.IsNullOrWhiteSpace(outputText))
        {
            return 0;
        }

        int count = 0;
        foreach (var rawLine in outputText.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (EditFailureRegex().IsMatch(line))
            {
                count++;
            }
        }

        return count;
    }

    private static int CountGenericToolErrorLines(string outputText, bool isFileOrDiffContent)
    {
        if (string.IsNullOrWhiteSpace(outputText))
        {
            return 0;
        }

        int count = 0;
        bool checkedFirstNonEmpty = false;
        foreach (var rawLine in outputText.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (EditFailureRegex().IsMatch(line))
            {
                checkedFirstNonEmpty = true;
                continue;
            }

            if (isFileOrDiffContent)
            {
                if (!checkedFirstNonEmpty && line.TrimStart().StartsWith("Error: ", StringComparison.OrdinalIgnoreCase))
                {
                    count++;
                }
                else if (line.Contains("[tool result ", StringComparison.OrdinalIgnoreCase) &&
                         line.Contains("Error:", StringComparison.OrdinalIgnoreCase))
                {
                    count++;
                }

                checkedFirstNonEmpty = true;
                continue;
            }

            checkedFirstNonEmpty = true;
            if (IsGenericToolErrorLine(line))
            {
                count++;
            }
        }

        return count;
    }

    private static bool ContainsBashHangOrTimeout(string outputText)
    {
        if (string.IsNullOrWhiteSpace(outputText) || ZeroExitCodeRegex().IsMatch(outputText))
        {
            return false;
        }

        foreach (var rawLine in outputText.Split('\n'))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line) ||
                line.StartsWith('+') ||
                line.StartsWith('-') ||
                line.StartsWith("//", StringComparison.Ordinal) ||
                line.StartsWith('#'))
            {
                continue;
            }

            if (BashHangOrTimeoutRegex().IsMatch(line))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsShortReadErrorOutput(string outputText)
    {
        if (string.IsNullOrWhiteSpace(outputText) || outputText.Length > 240)
        {
            return false;
        }

        int nonEmptyLines = 0;
        foreach (var rawLine in outputText.Split('\n'))
        {
            var trimmed = rawLine.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                continue;
            }

            nonEmptyLines++;
            if (nonEmptyLines > 2 || char.IsDigit(trimmed[0]))
            {
                return false;
            }
        }

        return nonEmptyLines > 0;
    }

    private static bool IsGenericToolErrorLine(string line) =>
        line.StartsWith("Error: ", StringComparison.OrdinalIgnoreCase) ||
        (line.Contains("[tool result ", StringComparison.OrdinalIgnoreCase) && line.Contains("Error:", StringComparison.OrdinalIgnoreCase));

    private static bool IsReadTool(string toolName) =>
        toolName.Equals(CanonicalToolNames.Read, StringComparison.OrdinalIgnoreCase) ||
        toolName.Equals("ReadFile", StringComparison.OrdinalIgnoreCase) ||
        toolName.Equals("read_file", StringComparison.OrdinalIgnoreCase);

    private static bool IsEditTool(string toolName) =>
        toolName.Equals(CanonicalToolNames.Edit, StringComparison.OrdinalIgnoreCase) ||
        toolName.Equals("EditFile", StringComparison.OrdinalIgnoreCase) ||
        toolName.Equals("edit_file", StringComparison.OrdinalIgnoreCase);

    private static bool IsWriteTool(string toolName) =>
        toolName.Equals(CanonicalToolNames.Write, StringComparison.OrdinalIgnoreCase) ||
        toolName.Equals("WriteFile", StringComparison.OrdinalIgnoreCase) ||
        toolName.Equals("write_file", StringComparison.OrdinalIgnoreCase);

    private static bool IsEditOrWriteTool(string toolName) =>
        IsEditTool(toolName) || IsWriteTool(toolName);

    private static bool IsBashTool(string toolName) =>
        toolName.Equals(CanonicalToolNames.Bash, StringComparison.OrdinalIgnoreCase);

    private static bool IsBashTestCommand(string cmd) =>
        cmd.Contains("dotnet test", StringComparison.OrdinalIgnoreCase) ||
        cmd.Contains("npm test", StringComparison.OrdinalIgnoreCase) ||
        cmd.Contains("npm run test", StringComparison.OrdinalIgnoreCase) ||
        cmd.Contains("pytest", StringComparison.OrdinalIgnoreCase) ||
        cmd.Contains("vitest", StringComparison.OrdinalIgnoreCase) ||
        cmd.Contains("jest", StringComparison.OrdinalIgnoreCase) ||
        cmd.Contains("cargo test", StringComparison.OrdinalIgnoreCase) ||
        cmd.Contains("go test", StringComparison.OrdinalIgnoreCase);

    private static bool IsBashSearchCommand(string cmd)
    {
        var pattern = NormalizeBashCommandPattern(cmd);
        return pattern.Equals("rg", StringComparison.OrdinalIgnoreCase) ||
               pattern.Equals("grep", StringComparison.OrdinalIgnoreCase) ||
               pattern.Equals("find", StringComparison.OrdinalIgnoreCase) ||
               pattern.Equals("fd", StringComparison.OrdinalIgnoreCase) ||
               pattern.Equals("git grep", StringComparison.OrdinalIgnoreCase) ||
               pattern.Equals("csnav search", StringComparison.OrdinalIgnoreCase) ||
               pattern.Equals("csnav api", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBashSourceInspectionCommand(string cmd)
    {
        if (IsBashSearchCommand(cmd))
        {
            return true;
        }

        var pattern = NormalizeBashCommandPattern(cmd);
        return pattern.Equals("git diff", StringComparison.OrdinalIgnoreCase) ||
               pattern.Equals("git show", StringComparison.OrdinalIgnoreCase) ||
               pattern.Equals("git log", StringComparison.OrdinalIgnoreCase) ||
               pattern.Equals("git status", StringComparison.OrdinalIgnoreCase) ||
               pattern.Equals("cat", StringComparison.OrdinalIgnoreCase) ||
               pattern.Equals("head", StringComparison.OrdinalIgnoreCase) ||
               pattern.Equals("tail", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsZeroSearchOutput(string outputText, bool isBash)
    {
        if (string.IsNullOrWhiteSpace(outputText))
        {
            return true;
        }

        if (ZeroSearchMatchesRegex().IsMatch(outputText))
        {
            return true;
        }

        if (isBash)
        {
            var trimmed = outputText.Trim();
            if (ExitCodeRegex().IsMatch(trimmed) || ZeroExitCodeRegex().IsMatch(trimmed))
            {
                var withoutExitCode = ExitCodeRegex().Replace(trimmed, string.Empty);
                withoutExitCode = ZeroExitCodeRegex().Replace(withoutExitCode, string.Empty).Trim();
                return string.IsNullOrEmpty(withoutExitCode);
            }
        }

        return false;
    }

    private static IReadOnlyList<string> SplitShellSegments(string command)
    {
        var segments = new List<string>();
        var current = new StringBuilder();
        bool inSingleQuote = false;
        bool inDoubleQuote = false;

        for (int i = 0; i < command.Length; i++)
        {
            char c = command[i];
            if (c == '\'' && !inDoubleQuote)
            {
                inSingleQuote = !inSingleQuote;
                current.Append(c);
                continue;
            }
            if (c == '"' && !inSingleQuote && (i == 0 || command[i - 1] != '\\'))
            {
                inDoubleQuote = !inDoubleQuote;
                current.Append(c);
                continue;
            }

            if (!inSingleQuote && !inDoubleQuote)
            {
                if (c == ';' || c == '\n')
                {
                    segments.Add(current.ToString());
                    current.Clear();
                    continue;
                }
                if ((c == '&' || c == '|') && i + 1 < command.Length && command[i + 1] == c)
                {
                    segments.Add(current.ToString());
                    current.Clear();
                    i++;
                    continue;
                }
            }

            current.Append(c);
        }

        if (current.Length > 0)
        {
            segments.Add(current.ToString());
        }

        return segments;
    }

    private static List<string> TokenizeCommand(string segment)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        bool inSingleQuote = false;
        bool inDoubleQuote = false;

        for (int i = 0; i < segment.Length; i++)
        {
            char c = segment[i];
            if (c == '\'' && !inDoubleQuote)
            {
                inSingleQuote = !inSingleQuote;
                continue;
            }
            if (c == '"' && !inSingleQuote && (i == 0 || segment[i - 1] != '\\'))
            {
                inDoubleQuote = !inDoubleQuote;
                continue;
            }
            if (!inSingleQuote && !inDoubleQuote && char.IsWhiteSpace(c))
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }

    private static bool LooksLikePathToken(string token) =>
        token.Contains('/') || token.Contains('\\') || token.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || token.EndsWith(".sln", StringComparison.OrdinalIgnoreCase);

    private static bool IsCodeReviewStage(string? stageId)
    {
        if (string.IsNullOrWhiteSpace(stageId))
        {
            return false;
        }

        if (string.Equals(stageId, StandardStageIds.CodeReviewAndFix, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (stageId.Contains("task-review", StringComparison.OrdinalIgnoreCase) ||
            stageId.Contains("task_review", StringComparison.OrdinalIgnoreCase) ||
            stageId.Contains("post-review", StringComparison.OrdinalIgnoreCase) ||
            stageId.Contains("deterministic", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return stageId.Contains("code-review", StringComparison.OrdinalIgnoreCase) ||
               stageId.Contains("code_review", StringComparison.OrdinalIgnoreCase) ||
               stageId.Contains("codereview", StringComparison.OrdinalIgnoreCase) ||
               stageId.Contains("review-code", StringComparison.OrdinalIgnoreCase) ||
               stageId.Contains("review_code", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record ParsedTurn(string Offset, int? OffsetSeconds, string Role, string Body);

    private sealed record ExtractedToolInvocation(
        string ToolName,
        string? ArgsJson,
        string? InlineOutput,
        string? RemainingTextAfterArgs,
        bool HasMalformedArgs);

    private sealed record PendingToolInvocation(
        string ToolName,
        string? ArgsJson,
        string? StartOffset,
        int? StartOffsetSeconds,
        bool HasMalformedArgs,
        bool FromAssistantTurn,
        string? FallbackOutputText = null);

    private sealed record CorrelatedToolCall(
        string ToolName,
        string? ArgsJson,
        string OutputText,
        bool IsErrorTurn,
        string? StartOffset,
        int? StartOffsetSeconds,
        string? EndOffset,
        int? EndOffsetSeconds,
        bool HasMalformedArgs,
        bool HasResultTurn = true);

    private sealed record TranscriptFileCandidate(
        string FullPath,
        string FileName,
        DateTime EffectiveDate,
        int TaskId,
        int RunNumber,
        DateTime LastWriteUtc);

    private sealed class FileFrictionTracker(string filePath)
    {
        public string FilePath { get; } = filePath;
        public int ReadCount { get; set; }
        public int EditCount { get; set; }
        public int EditFailureCount { get; set; }
        public int EditedWithoutReadCount { get; set; }
        public int CurrentConsecutiveReads { get; set; }
        public int MaxConsecutiveReads { get; set; }
        public bool HasWritten { get; set; }
    }

    private sealed class ToolFrictionTracker(string toolName)
    {
        public string ToolName { get; } = toolName;
        public int InvocationCount { get; set; }
        public int FailureCount { get; set; }
        public int OversizedOutputCount { get; set; }
        public string? SampleError { get; set; }
    }

    private sealed class BashCommandTracker(string commandPattern)
    {
        public string CommandPattern { get; } = commandPattern;
        public int ExecutionCount { get; set; }
        public int FailureCount { get; set; }
        public int HangOrTimeoutCount { get; set; }
        public int OversizedOutputCount { get; set; }
        public int? LastExitCode { get; set; }
    }

    private sealed class CompilerErrorTracker(string diagnosticCode, string sampleMessage, string? sampleFilePath)
    {
        public string DiagnosticCode { get; } = diagnosticCode;
        public string SampleMessage { get; } = sampleMessage;
        public string? SampleFilePath { get; set; } = sampleFilePath;
        public int Count { get; set; }
    }

    private sealed class ToolLoopTracker(string toolName, string argsSummary)
    {
        public string ToolName { get; } = toolName;
        public string ArgsSummary { get; } = argsSummary;
        public int Count { get; set; }
    }
}
