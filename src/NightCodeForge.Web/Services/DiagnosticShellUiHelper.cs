using System.Globalization;
using System.Text;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Web.Services;

/// <summary>
/// Target execution context scope selectable in the Diagnostic Bash Console UI.
/// </summary>
public enum DiagnosticScopeSelection
{
    WorkspaceCanonical = 0,
    TaskWorktree = 1,
    SystemNightwatch = 2
}

/// <summary>
/// One-click diagnostic preset command definition.
/// </summary>
public record DiagnosticShellPreset(
    string Id,
    string Name,
    string Command,
    string Purpose);

/// <summary>
/// Aggregate counts for an Environment Doctor report.
/// </summary>
public record DiagnosticDoctorSummary(
    int Passed,
    int Warnings,
    int Failed,
    int Total);

/// <summary>
/// Single stdout or stderr streaming chunk captured from live shell telemetry.
/// </summary>
public record DiagnosticStreamChunk(
    string Text,
    bool IsError);

/// <summary>
/// Immutable snapshot of currently active live stream output for the diagnostic console.
/// </summary>
public record DiagnosticLiveStreamSnapshot(
    bool IsActive,
    string? TrackedCommandId,
    string Command,
    IReadOnlyList<DiagnosticStreamChunk> Chunks);

/// <summary>
/// Completed command transcript entry rendered in the diagnostic console session log.
/// </summary>
public record DiagnosticTranscriptEntry(
    Guid Id,
    DiagnosticScopeSelection Scope,
    DiagnosticExecutionPipeline Pipeline,
    SandboxSecurityTier SecurityTier,
    DiagnosticCommandResult Result);

/// <summary>
/// Label and CSS class pair for status/metadata badges.
/// </summary>
public record DiagnosticBadgeInfo(
    string Label,
    string CssClass);

/// <summary>
/// Presentation metadata for an Environment Doctor check status.
/// </summary>
public record DiagnosticDoctorStatusDisplay(
    string Icon,
    string Label,
    string BadgeCssClass,
    string CardCssClass);

/// <summary>
/// Pure helper providing presets, target construction, validation, and presentation formatting
/// for <c>EnvironmentDiagnosticShell.razor</c>.
/// </summary>
public static class DiagnosticShellUiHelper
{
    public const int SigIntExitCode = 130;
    public const int CancelledExitCode = -1;
    public const int MinTimeoutSeconds = 1;
    public const int MaxTimeoutSeconds = 600;
    public const int MaxTranscriptEntries = 200;
    public const string DefaultFallbackWorkingDirectory = "/";
    public const string DefaultCancelledErrorMessage = "Command execution was cancelled.";
    public const string KeyEnter = "Enter";
    public const string KeyArrowUp = "ArrowUp";
    public const string KeyArrowDown = "ArrowDown";

    /// <summary>
    /// Built-in 1-click diagnostic presets specified in Step 12.22B.
    /// </summary>
    public static IReadOnlyList<DiagnosticShellPreset> Presets { get; } =
    [
        new(
            Id: "identity-directories",
            Name: "Identity & Directories",
            Command: "id && pwd && echo \"HOME=$HOME\" && ls -la",
            Purpose: "Verifies UID/GID mapping, working directory, $HOME fallback, and file ownership."),
        new(
            Id: "environment-path",
            Name: "Environment & PATH",
            Command: "echo \"PATH=$PATH\" && env | sort",
            Purpose: "Inspects deduplicated PATH, locale (LANG, LC_ALL), ICU, and cache env vars."),
        new(
            Id: "sdks-companion-tools",
            Name: "SDKs & Companion Tools",
            Command: "for cmd in git rg dotnet node npm python3 csnav mdfetch codecondenser; do printf \"%-15s: %s\\n\" \"$cmd\" \"$(which $cmd 2>/dev/null || echo MISSING)\"; done",
            Purpose: "Checks availability of all core toolchains and Phase 12 companion tools on PATH."),
        new(
            Id: "mount-write-probe",
            Name: "Mount & Write Probe",
            Command: "df -h && for dir in /workspace /workspace/clone \"$HOME\" /tmp /cache/nuget /cache/npm /opt/ncf-dotnet-tools /workspace/skills; do [ -e \"$dir\" ] && ([ -w \"$dir\" ] && echo \"RW: $dir\" || echo \"RO: $dir\") || echo \"MISSING: $dir\"; done",
            Purpose: "Verifies which mounted directories exist and whether they are writable (RW) or read-only (RO)."),
        new(
            Id: "git-worktree-status",
            Name: "Git & Worktree Status",
            Command: "git status && git rev-parse --git-dir --git-common-dir && git branch -a && git worktree list",
            Purpose: "Diagnoses Git repository validity, relative worktree pointers, and safe.directory."),
        new(
            Id: "dotnet-icu-probe",
            Name: ".NET & ICU Probe",
            Command: "dotnet --info",
            Purpose: "Verifies mounted host .NET SDKs, runtime packs, and libicu globalization readiness.")
    ];

    /// <summary>
    /// Builds an <see cref="EnvironmentDiagnosticTarget"/> from UI selections, enforcing scope invariants.
    /// </summary>
    public static EnvironmentDiagnosticTarget BuildTarget(
        DiagnosticScopeSelection scope,
        int? workspaceId,
        int? taskId,
        SandboxSecurityTier securityTier,
        DiagnosticExecutionPipeline pipeline,
        string? workingDirectoryOverride = null,
        int? timeoutSecondsOverride = null)
    {
        var trimmedWorkDir = string.IsNullOrWhiteSpace(workingDirectoryOverride)
            ? null
            : workingDirectoryOverride.Trim();

        int? effectiveTimeout = timeoutSecondsOverride is > 0
            ? Math.Clamp(timeoutSecondsOverride.Value, MinTimeoutSeconds, MaxTimeoutSeconds)
            : null;

        return scope switch
        {
            DiagnosticScopeSelection.SystemNightwatch => new EnvironmentDiagnosticTarget(
                WorkspaceId: null,
                TaskId: null,
                SecurityTier: SandboxSecurityTier.RestrictedReadOnly,
                Pipeline: pipeline,
                WorkingDirectoryOverride: trimmedWorkDir,
                TimeoutSecondsOverride: effectiveTimeout,
                ScopeKindOverride: SandboxScopeKind.SystemNightwatch),

            DiagnosticScopeSelection.TaskWorktree => new EnvironmentDiagnosticTarget(
                WorkspaceId: workspaceId,
                TaskId: taskId,
                SecurityTier: securityTier,
                Pipeline: pipeline,
                WorkingDirectoryOverride: trimmedWorkDir,
                TimeoutSecondsOverride: effectiveTimeout,
                ScopeKindOverride: SandboxScopeKind.TaskWorktree),

            _ => new EnvironmentDiagnosticTarget(
                WorkspaceId: workspaceId,
                TaskId: null,
                SecurityTier: securityTier,
                Pipeline: pipeline,
                WorkingDirectoryOverride: trimmedWorkDir,
                TimeoutSecondsOverride: effectiveTimeout,
                ScopeKindOverride: SandboxScopeKind.WorkspaceClone)
        };
    }

    /// <summary>
    /// Validates whether the selected diagnostic target has the required workspace/task identifiers.
    /// </summary>
    public static (bool IsValid, string? ErrorMessage) ValidateTarget(
        DiagnosticScopeSelection scope,
        int? workspaceId,
        int? taskId)
    {
        if (scope == DiagnosticScopeSelection.SystemNightwatch)
        {
            return (true, null);
        }

        if (workspaceId is not > 0)
        {
            return (false, "Select a workspace or switch to System Nightwatch scope.");
        }

        if (scope == DiagnosticScopeSelection.TaskWorktree && taskId is not > 0)
        {
            return (false, "Select a task for Task Worktree scope.");
        }

        return (true, null);
    }

    public static DiagnosticBadgeInfo FormatContainerLiveness(bool? isRunning) => isRunning switch
    {
        true => new DiagnosticBadgeInfo("Running", "badge-teal"),
        false => new DiagnosticBadgeInfo("Stopped", "badge-crimson"),
        null => new DiagnosticBadgeInfo("N/A", "badge-gray")
    };

    public static DiagnosticBadgeInfo FormatExecutionTier(ExecutionTier tier) => tier switch
    {
        ExecutionTier.ContainerSandbox => new DiagnosticBadgeInfo("ContainerSandbox", "badge-indigo"),
        ExecutionTier.WslDirect => new DiagnosticBadgeInfo("WslDirect", "badge-amber"),
        ExecutionTier.BareHost => new DiagnosticBadgeInfo("BareHost", "badge-gray"),
        _ => new DiagnosticBadgeInfo(tier.ToString(), "badge-gray")
    };

    public static string FormatWslBadge(bool useWsl, string? wslDistribution)
    {
        if (!useWsl)
        {
            return "Native Host";
        }

        return !string.IsNullOrWhiteSpace(wslDistribution)
            ? $"WSL2: {wslDistribution.Trim()}"
            : "WSL2";
    }

    public static DiagnosticBadgeInfo FormatMountMode(bool isReadOnly) => isReadOnly
        ? new DiagnosticBadgeInfo("RO", "badge-amber")
        : new DiagnosticBadgeInfo("RW", "badge-teal");

    public static DiagnosticDoctorStatusDisplay FormatDoctorStatus(DoctorCheckStatus status) => status switch
    {
        DoctorCheckStatus.Passed => new DiagnosticDoctorStatusDisplay("✅", "Passed", "badge-teal", "doctor-card-passed"),
        DoctorCheckStatus.Warning => new DiagnosticDoctorStatusDisplay("⚠️", "Warning", "badge-amber", "doctor-card-warning"),
        DoctorCheckStatus.Failed => new DiagnosticDoctorStatusDisplay("❌", "Failed", "badge-crimson", "doctor-card-failed"),
        _ => new DiagnosticDoctorStatusDisplay("•", status.ToString(), "badge-gray", string.Empty)
    };

    public static DiagnosticDoctorSummary SummarizeDoctorChecks(IReadOnlyList<EnvironmentDoctorCheckItem>? checks)
    {
        if (checks == null || checks.Count == 0)
        {
            return new DiagnosticDoctorSummary(0, 0, 0, 0);
        }

        var passed = 0;
        var warnings = 0;
        var failed = 0;

        foreach (var check in checks)
        {
            switch (check.Status)
            {
                case DoctorCheckStatus.Passed:
                    passed++;
                    break;
                case DoctorCheckStatus.Warning:
                    warnings++;
                    break;
                case DoctorCheckStatus.Failed:
                    failed++;
                    break;
            }
        }

        return new DiagnosticDoctorSummary(passed, warnings, failed, checks.Count);
    }

    public static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        if (duration.TotalSeconds < 1.0)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{Math.Round(duration.TotalMilliseconds):0} ms");
        }

        if (duration.TotalMinutes < 1.0)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{duration.TotalSeconds:0.00} s");
        }

        var minutes = (int)duration.TotalMinutes;
        var seconds = duration.Seconds;
        return string.Create(CultureInfo.InvariantCulture, $"{minutes}m {seconds:00}s");
    }

    public static DiagnosticBadgeInfo FormatExitCodeBadge(int exitCode) => exitCode switch
    {
        0 => new DiagnosticBadgeInfo("Exit 0", "badge-teal"),
        SigIntExitCode => new DiagnosticBadgeInfo("SIGINT (130)", "badge-amber"),
        CancelledExitCode => new DiagnosticBadgeInfo("Cancelled (-1)", "badge-amber"),
        _ => new DiagnosticBadgeInfo($"Exit {exitCode}", "badge-crimson")
    };

    public static IReadOnlyList<string> SplitSynthesizedPath(string? synthesizedPath)
    {
        if (string.IsNullOrWhiteSpace(synthesizedPath))
        {
            return Array.Empty<string>();
        }

        return synthesizedPath
            .Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public static string FormatScopeLabel(DiagnosticScopeSelection scope) => scope switch
    {
        DiagnosticScopeSelection.WorkspaceCanonical => "Workspace Canonical",
        DiagnosticScopeSelection.TaskWorktree => "Task Worktree",
        DiagnosticScopeSelection.SystemNightwatch => "System Nightwatch",
        _ => scope.ToString()
    };

    public static string FormatPipelineLabel(DiagnosticExecutionPipeline pipeline) => pipeline switch
    {
        DiagnosticExecutionPipeline.AgentBashTool => "Agent 'bash' Tool",
        DiagnosticExecutionPipeline.RawExecutionEnvironment => "Raw IExecutionEnvironment",
        _ => pipeline.ToString()
    };

    /// <summary>
    /// Builds a stable cache key representing the currently inspected target configuration.
    /// </summary>
    public static string BuildInspectionKey(
        DiagnosticScopeSelection scope,
        int? workspaceId,
        int? taskId,
        SandboxSecurityTier effectiveSecurityTier) =>
        $"{scope}:{(scope == DiagnosticScopeSelection.SystemNightwatch ? 0 : workspaceId)}:{(scope == DiagnosticScopeSelection.TaskWorktree ? taskId : 0)}:{effectiveSecurityTier}";

    /// <summary>
    /// Resolves an optional working directory override against the environment's default working directory,
    /// collapsing relative <c>.</c> and <c>..</c> segments for accurate live-stream correlation.
    /// </summary>
    public static string? ResolveExpectedWorkingDirectory(
        string? workingDirectoryOverride,
        string? defaultWorkingDirectory)
    {
        var trimmedDefault = string.IsNullOrWhiteSpace(defaultWorkingDirectory)
            ? null
            : defaultWorkingDirectory.Trim();

        if (string.IsNullOrWhiteSpace(workingDirectoryOverride))
        {
            return trimmedDefault;
        }

        var trimmedOverride = workingDirectoryOverride.Trim();
        var normalizedOverride = trimmedOverride.Replace('\\', '/');

        if (IsRootedPath(normalizedOverride))
        {
            return NormalizePosixOrDrivePath(normalizedOverride);
        }

        if (string.IsNullOrEmpty(trimmedDefault))
        {
            return normalizedOverride;
        }

        var normalizedDefault = trimmedDefault.Replace('\\', '/').TrimEnd('/');
        var combined = string.IsNullOrEmpty(normalizedDefault)
            ? "/" + normalizedOverride
            : $"{normalizedDefault}/{normalizedOverride}";

        return NormalizePosixOrDrivePath(combined);
    }

    /// <summary>
    /// Builds a fallback <see cref="DiagnosticCommandResult"/> when caller cancellation aborts execution
    /// before <see cref="IEnvironmentDiagnosticShellService.ExecuteCommandAsync"/> returns.
    /// </summary>
    public static DiagnosticCommandResult BuildFallbackCancelledResult(
        string command,
        string? effectiveWorkingDirectory,
        DiagnosticLiveStreamSnapshot snapshot,
        TimeSpan duration,
        DateTimeOffset? executedAt = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var stdout = string.Concat(snapshot.Chunks.Where(c => !c.IsError).Select(c => c.Text));
        var stderr = string.Concat(snapshot.Chunks.Where(c => c.IsError).Select(c => c.Text));

        return new DiagnosticCommandResult(
            CommandLine: command,
            EffectiveWorkingDirectory: string.IsNullOrWhiteSpace(effectiveWorkingDirectory)
                ? DefaultFallbackWorkingDirectory
                : effectiveWorkingDirectory,
            ExitCode: CancelledExitCode,
            Output: stdout,
            Error: string.IsNullOrEmpty(stderr) ? DefaultCancelledErrorMessage : stderr,
            Duration: duration < TimeSpan.Zero ? TimeSpan.Zero : duration,
            IsTruncated: false,
            ExecutedAt: executedAt ?? DateTimeOffset.UtcNow);
    }

    private static bool IsRootedPath(string normalizedPath)
    {
        if (normalizedPath.StartsWith('/') || normalizedPath == "~" || normalizedPath.StartsWith("~/", StringComparison.Ordinal))
        {
            return true;
        }

        return normalizedPath.Length >= 2 &&
               char.IsAsciiLetter(normalizedPath[0]) &&
               normalizedPath[1] == ':' &&
               (normalizedPath.Length == 2 || normalizedPath[2] == '/');
    }

    private static string NormalizePosixOrDrivePath(string path)
    {
        string prefix;
        string remainder;

        if (path.StartsWith('/'))
        {
            prefix = "/";
            remainder = path[1..];
        }
        else if (path == "~" || path.StartsWith("~/", StringComparison.Ordinal))
        {
            prefix = "~";
            remainder = path.Length > 2 ? path[2..] : string.Empty;
        }
        else if (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':')
        {
            prefix = path[..2];
            remainder = path.Length > 2 ? path[2..].TrimStart('/') : string.Empty;
        }
        else
        {
            prefix = string.Empty;
            remainder = path;
        }

        var stack = new List<string>();
        foreach (var segment in remainder.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (stack.Count > 0 && stack[^1] != "..")
                {
                    stack.RemoveAt(stack.Count - 1);
                }
                else if (string.IsNullOrEmpty(prefix))
                {
                    stack.Add("..");
                }
                continue;
            }

            stack.Add(segment);
        }

        if (prefix == "/")
        {
            return stack.Count == 0 ? "/" : "/" + string.Join('/', stack);
        }

        if (prefix == "~")
        {
            return stack.Count == 0 ? "~" : "~/" + string.Join('/', stack);
        }

        if (!string.IsNullOrEmpty(prefix))
        {
            return stack.Count == 0 ? prefix + "/" : $"{prefix}/{string.Join('/', stack)}";
        }

        return stack.Count == 0 ? "." : string.Join('/', stack);
    }
}

/// <summary>
/// Bounded bash-style command history supporting Up/Down arrow navigation with draft preservation.
/// </summary>
public sealed class DiagnosticCommandHistory
{
    public const int DefaultMaxCapacity = 200;

    private readonly int _maxCapacity;
    private readonly List<string> _entries = new();
    private int _cursor;
    private string _draft = string.Empty;

    public DiagnosticCommandHistory(int maxCapacity = DefaultMaxCapacity)
    {
        if (maxCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCapacity), "Capacity must be positive.");
        }

        _maxCapacity = maxCapacity;
        _cursor = 0;
    }

    public IReadOnlyList<string> Entries => _entries;

    public void Record(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return;
        }

        var trimmed = command.Trim();
        if (_entries.Count == 0 || !string.Equals(_entries[^1], trimmed, StringComparison.Ordinal))
        {
            _entries.Add(trimmed);
            if (_entries.Count > _maxCapacity)
            {
                _entries.RemoveRange(0, _entries.Count - _maxCapacity);
            }
        }

        _cursor = _entries.Count;
        _draft = string.Empty;
    }

    public string NavigateUp(string? currentInput)
    {
        if (_entries.Count == 0)
        {
            return currentInput ?? string.Empty;
        }

        if (_cursor >= _entries.Count)
        {
            _draft = currentInput ?? string.Empty;
            _cursor = _entries.Count - 1;
        }
        else if (_cursor > 0)
        {
            _cursor--;
        }

        return _entries[_cursor];
    }

    public string NavigateDown(string? currentInput)
    {
        if (_entries.Count == 0 || _cursor >= _entries.Count)
        {
            return currentInput ?? string.Empty;
        }

        _cursor++;
        if (_cursor >= _entries.Count)
        {
            _cursor = _entries.Count;
            return _draft;
        }

        return _entries[_cursor];
    }

    public void ResetNavigation()
    {
        _cursor = _entries.Count;
        _draft = string.Empty;
    }
}

/// <summary>
/// Thread-safe telemetry correlator that isolates live stdout/stderr streaming for the active
/// diagnostic shell execution so concurrent background agent commands do not cross-talk.
/// </summary>
public sealed class DiagnosticLiveStreamCorrelator
{
    public const int MaxBufferedChars = 100_000;

    private readonly object _lock = new();
    private readonly List<DiagnosticStreamChunk> _chunks = new();
    private bool _isActive;
    private string _rawCommand = string.Empty;
    private string _normalizedCommand = string.Empty;
    private string? _normalizedExpectedWorkDir;
    private string? _trackedCommandId;
    private int _bufferedCharCount;

    public void BeginExecution(string command, string? expectedWorkingDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        lock (_lock)
        {
            _isActive = true;
            _rawCommand = command;
            _normalizedCommand = NormalizeCommand(command);
            _normalizedExpectedWorkDir = NormalizeWorkingDirectory(expectedWorkingDirectory);
            _trackedCommandId = null;
            _chunks.Clear();
            _bufferedCharCount = 0;
        }
    }

    public bool HandleShellCommandChanged(ShellCommandRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        lock (_lock)
        {
            if (!_isActive)
            {
                return false;
            }

            if (_trackedCommandId == null)
            {
                if (record.Status == ShellCommandStatus.Running && MatchesExpectedCommand(record))
                {
                    _trackedCommandId = record.CommandId;
                    return true;
                }

                return false;
            }

            return string.Equals(record.CommandId, _trackedCommandId, StringComparison.Ordinal);
        }
    }

    public bool HandleShellCommandOutput(string commandId, string chunk, bool isError)
    {
        if (string.IsNullOrEmpty(chunk))
        {
            return false;
        }

        lock (_lock)
        {
            if (!_isActive ||
                _trackedCommandId == null ||
                !string.Equals(commandId, _trackedCommandId, StringComparison.Ordinal))
            {
                return false;
            }

            return AppendChunkLocked(chunk, isError);
        }
    }

    public void EndExecution()
    {
        lock (_lock)
        {
            _isActive = false;
            _rawCommand = string.Empty;
            _normalizedCommand = string.Empty;
            _normalizedExpectedWorkDir = null;
            _trackedCommandId = null;
            _chunks.Clear();
            _bufferedCharCount = 0;
        }
    }

    public DiagnosticLiveStreamSnapshot GetSnapshot()
    {
        lock (_lock)
        {
            return new DiagnosticLiveStreamSnapshot(
                IsActive: _isActive,
                TrackedCommandId: _trackedCommandId,
                Command: _rawCommand,
                Chunks: _chunks.ToArray());
        }
    }

    private bool MatchesExpectedCommand(ShellCommandRecord record)
    {
        var recCmd = NormalizeCommand(record.CommandLine);
        if (string.Equals(recCmd, _normalizedCommand, StringComparison.Ordinal))
        {
            return MatchesWorkingDirectory(record.WorkingDirectory);
        }

        if (TryExtractWrappedShellCommand(recCmd, out var innerCommand, out var embeddedWorkDir) &&
            string.Equals(innerCommand, _normalizedCommand, StringComparison.Ordinal))
        {
            return !string.IsNullOrEmpty(embeddedWorkDir)
                ? MatchesWorkingDirectory(embeddedWorkDir)
                : MatchesWorkingDirectory(record.WorkingDirectory);
        }

        return false;
    }

    private bool MatchesWorkingDirectory(string? candidateWorkDir)
    {
        var recWorkDir = NormalizeWorkingDirectory(candidateWorkDir);
        if (string.IsNullOrEmpty(_normalizedExpectedWorkDir) || string.IsNullOrEmpty(recWorkDir))
        {
            return true;
        }

        if (string.Equals(recWorkDir, _normalizedExpectedWorkDir, StringComparison.Ordinal))
        {
            return true;
        }

        var wslRec = TryConvertWindowsDriveToWsl(recWorkDir);
        var wslExpected = TryConvertWindowsDriveToWsl(_normalizedExpectedWorkDir);
        if (wslRec != null && wslExpected != null)
        {
            return string.Equals(wslRec, wslExpected, StringComparison.Ordinal);
        }

        return false;
    }

    private static bool TryExtractWrappedShellCommand(
        string commandLine,
        out string innerCommand,
        out string? embeddedWorkDir)
    {
        innerCommand = string.Empty;
        embeddedWorkDir = null;

        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return false;
        }

        string? payload = null;
        if (commandLine.StartsWith("wsl.exe ", StringComparison.OrdinalIgnoreCase) ||
            commandLine.StartsWith("wsl ", StringComparison.OrdinalIgnoreCase))
        {
            const string wslBashMarker = " bash -c ";
            var idx = commandLine.IndexOf(wslBashMarker, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                payload = commandLine[(idx + wslBashMarker.Length)..];
            }
        }
        else if (commandLine.StartsWith("/bin/bash -c ", StringComparison.OrdinalIgnoreCase))
        {
            payload = commandLine["/bin/bash -c ".Length..];
        }
        else if (commandLine.StartsWith("bash -c ", StringComparison.OrdinalIgnoreCase))
        {
            payload = commandLine["bash -c ".Length..];
        }

        if (payload == null)
        {
            return false;
        }

        var remaining = payload.Trim();

        if (remaining.StartsWith("mkdir -p ", StringComparison.Ordinal))
        {
            var andIdx = remaining.IndexOf(" && ", StringComparison.Ordinal);
            if (andIdx < 0)
            {
                return false;
            }

            remaining = remaining[(andIdx + 4)..].TrimStart();
        }

        if (remaining.StartsWith("cd ", StringComparison.Ordinal))
        {
            var andIdx = remaining.IndexOf(" && ", StringComparison.Ordinal);
            if (andIdx < 0)
            {
                return false;
            }

            var rawDir = remaining[3..andIdx].Trim();
            if (rawDir.Length >= 2 &&
                ((rawDir[0] == '"' && rawDir[^1] == '"') || (rawDir[0] == '\'' && rawDir[^1] == '\'')))
            {
                rawDir = rawDir[1..^1];
            }

            embeddedWorkDir = rawDir;
            innerCommand = remaining[(andIdx + 4)..].Trim();
            return !string.IsNullOrEmpty(innerCommand);
        }

        innerCommand = remaining;
        return !string.IsNullOrEmpty(innerCommand);
    }

    private bool AppendChunkLocked(string chunk, bool isError)
    {
        var remainingBudget = MaxBufferedChars - _bufferedCharCount;
        if (remainingBudget <= 0)
        {
            return false;
        }

        var effectiveChunk = chunk.Length <= remainingBudget
            ? chunk
            : chunk[..remainingBudget];

        if (_chunks.Count > 0 && _chunks[^1].IsError == isError)
        {
            var last = _chunks[^1];
            _chunks[^1] = last with { Text = last.Text + effectiveChunk };
        }
        else
        {
            _chunks.Add(new DiagnosticStreamChunk(effectiveChunk, isError));
        }

        _bufferedCharCount += effectiveChunk.Length;
        return true;
    }

    private static string NormalizeCommand(string? command) =>
        (command ?? string.Empty).Replace("\r", string.Empty).Trim();

    private static string? NormalizeWorkingDirectory(string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            return null;
        }

        var normalized = workingDirectory.Trim().Replace('\\', '/');
        return normalized.Length > 1 ? normalized.TrimEnd('/') : normalized;
    }

    private static string? TryConvertWindowsDriveToWsl(string normalizedPath)
    {
        if (normalizedPath.StartsWith("/mnt/", StringComparison.Ordinal) &&
            normalizedPath.Length >= 6 &&
            char.IsAsciiLetter(normalizedPath[5]) &&
            (normalizedPath.Length == 6 || normalizedPath[6] == '/'))
        {
            return $"/mnt/{char.ToLowerInvariant(normalizedPath[5])}{normalizedPath[6..]}";
        }

        if (normalizedPath.Length >= 2 &&
            char.IsAsciiLetter(normalizedPath[0]) &&
            normalizedPath[1] == ':' &&
            (normalizedPath.Length == 2 || normalizedPath[2] == '/'))
        {
            var rest = normalizedPath.Length > 2 ? normalizedPath[2..] : string.Empty;
            return $"/mnt/{char.ToLowerInvariant(normalizedPath[0])}{rest}";
        }

        return null;
    }
}
