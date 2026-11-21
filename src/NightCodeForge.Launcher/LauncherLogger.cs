namespace NightCodeForge.Launcher;

using System.Globalization;
using System.Text;

/// <summary>
/// Lightweight logging abstraction for the Native AOT launcher shim.
/// </summary>
public interface ILauncherLogger
{
    void Info(string message);
    void Warn(string message);
    void Error(string message);
}

/// <summary>
/// Thread-safe launcher logger writing formatted entries to stdout/stderr and appending to <c>launcher.log</c>.
/// </summary>
public sealed class ConsoleAndFileLauncherLogger : ILauncherLogger
{
    public const string LevelInfo = "INF";
    public const string LevelWarn = "WRN";
    public const string LevelError = "ERR";
    public const string SourceTag = "[Launcher]";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _logFilePath;
    private readonly TextWriter _stdout;
    private readonly TextWriter _stderr;
    private readonly TimeProvider _timeProvider;
    private readonly object _gate = new();
    private bool _fileWriteWarningEmitted;

    public ConsoleAndFileLauncherLogger(
        string logFilePath,
        TextWriter stdout,
        TextWriter stderr,
        TimeProvider timeProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logFilePath);
        _logFilePath = logFilePath;
        _stdout = stdout ?? throw new ArgumentNullException(nameof(stdout));
        _stderr = stderr ?? throw new ArgumentNullException(nameof(stderr));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public void Info(string message) => Write(LevelInfo, message, useErrorStream: false);

    public void Warn(string message) => Write(LevelWarn, message, useErrorStream: false);

    public void Error(string message) => Write(LevelError, message, useErrorStream: true);

    private void Write(string level, string message, bool useErrorStream)
    {
        var timestamp = _timeProvider.GetLocalNow().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        var formatted = $"[{timestamp} {level}] {SourceTag} {message}";

        lock (_gate)
        {
            var targetWriter = useErrorStream ? _stderr : _stdout;
            try
            {
                targetWriter.WriteLine(formatted);
                targetWriter.Flush();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // Justification: Console handle/pipe may be closed on SIGHUP or detached execution; log entry must still persist to launcher.log and must not throw out of a signal handler.
                _ = ex;
            }

            try
            {
                var parentDir = Path.GetDirectoryName(_logFilePath);
                if (!string.IsNullOrWhiteSpace(parentDir))
                {
                    Directory.CreateDirectory(parentDir);
                }

                using var stream = new FileStream(_logFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var writer = new StreamWriter(stream, Utf8NoBom);
                writer.WriteLine(formatted);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (!_fileWriteWarningEmitted)
                {
                    _fileWriteWarningEmitted = true;
                    try
                    {
                        _stderr.WriteLine($"[{timestamp} {LevelWarn}] {SourceTag} Failed to write launcher log '{_logFilePath}': {ex.Message}");
                        _stderr.Flush();
                    }
                    catch (Exception stderrEx) when (stderrEx is IOException or ObjectDisposedException)
                    {
                        // Justification: Stderr handle may also be closed during console teardown or detached execution.
                        _ = stderrEx;
                    }
                }
            }
        }
    }
}
