namespace NightCodeForge.Web.Lifecycle;

using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Probes whether a previously recorded PID still represents the same live OS process.
/// </summary>
public interface IProcessLivenessChecker
{
    bool IsProcessRunning(int processId, DateTimeOffset expectedStartTimeUtc);
}

/// <summary>
/// OS-backed liveness checker comparing process ID and start time to avoid PID reuse false positives.
/// </summary>
public sealed class SystemProcessLivenessChecker : IProcessLivenessChecker
{
    private const double StartTimeToleranceSeconds = 5.0;

    public bool IsProcessRunning(int processId, DateTimeOffset expectedStartTimeUtc)
    {
        if (processId <= 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited)
            {
                return false;
            }

            var actualStartUtc = process.StartTime.ToUniversalTime();
            return Math.Abs((actualStartUtc - expectedStartTimeUtc).TotalSeconds) <= StartTimeToleranceSeconds;
        }
        catch (ArgumentException)
        {
            // Justification: Process with the recorded PID is no longer running.
            return false;
        }
        catch (InvalidOperationException)
        {
            // Justification: Process exited while querying its state or start time.
            return false;
        }
        catch (Win32Exception)
        {
            // Justification: Process is inaccessible or belongs to another security context (PID reused by OS/system process).
            return false;
        }
        catch (NotSupportedException)
        {
            // Justification: Querying process state/start time is unsupported on some restricted host environments.
            return false;
        }
    }
}

/// <summary>
/// Atomically persists and inspects the host's <c>instance-lifecycle.json</c> marker file
/// so hard crashes (such as native fast-fail / SIGKILL / power loss) are detected on next startup.
/// </summary>
public sealed class InstanceLifecycleStateStore
{
    public const string StateFileName = "instance-lifecycle.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _stateFilePath;
    private readonly IProcessLivenessChecker _processLivenessChecker;
    private readonly object _writeLock = new();

    public InstanceLifecycleStateStore(string stateFilePath, IProcessLivenessChecker processLivenessChecker)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateFilePath);
        _processLivenessChecker = processLivenessChecker ?? throw new ArgumentNullException(nameof(processLivenessChecker));
        _stateFilePath = stateFilePath;
    }

    public string StateFilePath => _stateFilePath;

    /// <summary>
    /// Reads and classifies the previous instance's lifecycle marker, failing open if corrupt or unreadable.
    /// </summary>
    public PreviousInstanceAssessment InspectPreviousRun(int currentProcessId)
    {
        if (!File.Exists(_stateFilePath))
        {
            return new PreviousInstanceAssessment(PreviousInstanceAssessmentKind.None, PreviousState: null, DiagnosticError: null);
        }

        InstanceLifecycleState? previous;
        try
        {
            var json = File.ReadAllText(_stateFilePath);
            previous = JsonSerializer.Deserialize<InstanceLifecycleState>(json, SerializerOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new PreviousInstanceAssessment(
                PreviousInstanceAssessmentKind.CorruptedStateFile,
                PreviousState: null,
                DiagnosticError: ex.Message);
        }

        if (previous is null)
        {
            return new PreviousInstanceAssessment(
                PreviousInstanceAssessmentKind.CorruptedStateFile,
                PreviousState: null,
                DiagnosticError: "Lifecycle state file deserialized to null.");
        }

        return previous.Status switch
        {
            InstanceLifecycleStatus.StoppedCleanly => new PreviousInstanceAssessment(
                PreviousInstanceAssessmentKind.StoppedCleanly, previous, DiagnosticError: null),
            InstanceLifecycleStatus.Crashed => new PreviousInstanceAssessment(
                PreviousInstanceAssessmentKind.Crashed, previous, DiagnosticError: null),
            InstanceLifecycleStatus.ExitedWithoutGracefulShutdown => new PreviousInstanceAssessment(
                PreviousInstanceAssessmentKind.ExitedWithoutGracefulShutdown, previous, DiagnosticError: null),
            InstanceLifecycleStatus.Stopping => AssessActiveOrAbandonedState(
                previous, currentProcessId, PreviousInstanceAssessmentKind.TerminatedDuringShutdown),
            InstanceLifecycleStatus.Running => AssessActiveOrAbandonedState(
                previous, currentProcessId, PreviousInstanceAssessmentKind.UncleanTerminationDetected),
            _ => new PreviousInstanceAssessment(
                PreviousInstanceAssessmentKind.CorruptedStateFile,
                previous,
                DiagnosticError: $"Unknown lifecycle status '{previous.Status}'.")
        };
    }

    /// <summary>
    /// Writes the lifecycle state via a temporary file + atomic overwrite move.
    /// </summary>
    public bool TryWriteState(InstanceLifecycleState state, out Exception? error)
    {
        ArgumentNullException.ThrowIfNull(state);

        lock (_writeLock)
        {
            var tempPath = $"{_stateFilePath}.{state.ProcessId}.{Guid.NewGuid():N}.tmp";
            try
            {
                var directory = Path.GetDirectoryName(_stateFilePath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var json = JsonSerializer.Serialize(state, SerializerOptions);
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, _stateFilePath, overwrite: true);
                error = null;
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                TryDeleteTempFile(tempPath);
                error = ex;
                return false;
            }
        }
    }

    private PreviousInstanceAssessment AssessActiveOrAbandonedState(
        InstanceLifecycleState previous,
        int currentProcessId,
        PreviousInstanceAssessmentKind abandonedKind)
    {
        if (previous.ProcessId != currentProcessId &&
            _processLivenessChecker.IsProcessRunning(previous.ProcessId, previous.ProcessStartedAtUtc))
        {
            return new PreviousInstanceAssessment(
                PreviousInstanceAssessmentKind.AnotherInstanceStillRunning,
                previous,
                DiagnosticError: null);
        }

        return new PreviousInstanceAssessment(abandonedKind, previous, DiagnosticError: null);
    }

    private static void TryDeleteTempFile(string tempPath)
    {
        try
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Justification: Best-effort cleanup of failed atomic write staging file; primary failure is already surfaced to caller.
            _ = ex;
        }
    }
}
