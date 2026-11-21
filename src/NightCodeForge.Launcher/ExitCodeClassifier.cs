namespace NightCodeForge.Launcher;

/// <summary>
/// Classifies and formats child process exit codes (including signed 32-bit Windows NTSTATUS crash codes).
/// </summary>
public static class ExitCodeClassifier
{
    public const int ExitCodeSuccess = 0;
    public const int ExitCodeConfigurationError = 2;
    public const int ExitCodeLaunchFailure = 3;
    public const int MaxPosixExitCode = 255;
    public const int ExitCodeControlCWindows = unchecked((int)0xC000013A);
    public const int ExitCodeSigIntUnix = 130;
    public const int ExitCodeSigQuitUnix = 131;
    public const int ExitCodeSigTermUnix = 143;

    public static bool IsIntentionalShutdownExitCode(int exitCode, bool isWindows)
    {
        if (exitCode == ExitCodeSuccess)
        {
            return true;
        }

        if (isWindows && exitCode == ExitCodeControlCWindows)
        {
            return true;
        }

        return !isWindows && exitCode is ExitCodeSigIntUnix or ExitCodeSigQuitUnix or ExitCodeSigTermUnix;
    }

    public static string? TryDescribeKnownCrashCode(int exitCode)
    {
        return unchecked((uint)exitCode) switch
        {
            0xC0000409u => "STATUS_STACK_BUFFER_OVERRUN / FastFail",
            0xC0000005u => "STATUS_ACCESS_VIOLATION",
            0xC00000FDu => "STATUS_STACK_OVERFLOW",
            0xE0434352u => "CLR unhandled managed exception",
            0x80131623u => "Environment.FailFast",
            0xC000001Du => "STATUS_ILLEGAL_INSTRUCTION",
            0xC0000374u => "STATUS_HEAP_CORRUPTION",
            0x40000015u => "STATUS_FATAL_APP_EXIT",
            unchecked((uint)ExitCodeControlCWindows) => "STATUS_CONTROL_C_EXIT",
            _ => null
        };
    }

    public static string FormatExitCode(int exitCode)
    {
        if (exitCode < 0 || exitCode > MaxPosixExitCode)
        {
            var hex = unchecked((uint)exitCode).ToString("X8", System.Globalization.CultureInfo.InvariantCulture);
            var description = TryDescribeKnownCrashCode(exitCode);
            return description is null
                ? $"{exitCode} (0x{hex})"
                : $"{exitCode} (0x{hex}: {description})";
        }

        return exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
