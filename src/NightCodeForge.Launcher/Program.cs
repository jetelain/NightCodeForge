namespace NightCodeForge.Launcher;

using System.Runtime.InteropServices;
using System.Text;

/// <summary>
/// Native AOT launcher entrypoint supervising <c>NightCodeForge.Web</c> with automatic crash restart
/// and clean OS signal forwarding without cmd.exe batch prompts.
/// </summary>
public static class Program
{
    private const string ConsoleWindowTitle = "NightCodeForge";
    private const string SigIntDescription = "SIGINT (Ctrl+C)";
    private const string SigQuitDescription = "SIGQUIT (Ctrl+Break)";
    private const string SigTermDescription = "SIGTERM";
    private const string SigHupWindowsDescription = "SIGHUP (console window closed)";
    private const string SigHupUnixDescription = "SIGHUP";

    public static async Task<int> Main(string[] args)
    {
        ConfigureConsoleWindow();

        var processDir = Path.GetDirectoryName(Environment.ProcessPath);
        var launcherDirectory = string.IsNullOrWhiteSpace(processDir) ? AppContext.BaseDirectory : processDir;

        LauncherConfiguration config;
        try
        {
            config = LauncherConfigurationResolver.Resolve(
                args,
                launcherDirectory,
                Environment.GetEnvironmentVariable,
                File.Exists,
                File.ReadAllLines,
                OperatingSystem.IsWindows());
        }
        catch (Exception ex) when (ex is FileNotFoundException or ArgumentException)
        {
            await Console.Error.WriteLineAsync($"{ConsoleAndFileLauncherLogger.SourceTag} Error: {ex.Message}").ConfigureAwait(false);
            return ExitCodeClassifier.ExitCodeConfigurationError;
        }

        var logger = new ConsoleAndFileLauncherLogger(
            config.LauncherLogPath,
            Console.Out,
            Console.Error,
            TimeProvider.System);

        using var supervisor = new LauncherSupervisor(
            new SystemChildProcessStarter(),
            logger,
            TimeProvider.System,
            OperatingSystem.IsWindows());

        var signalRegistrations = RegisterSignals(supervisor);
        try
        {
            return await supervisor.RunAsync(config, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            foreach (var registration in signalRegistrations)
            {
                registration.Dispose();
            }
        }
    }

    private static void ConfigureConsoleWindow()
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch (Exception ex) when (ex is IOException or PlatformNotSupportedException)
        {
            // Justification: Non-interactive/detached hosts or redirected streams may lack a console handle or disallow encoding changes.
            _ = ex;
        }

        try
        {
            Console.Title = ConsoleWindowTitle;
        }
        catch (Exception ex) when (ex is IOException or PlatformNotSupportedException)
        {
            // Justification: Non-interactive hosts or restricted terminals may not support setting Console.Title.
            _ = ex;
        }
    }

    private static List<PosixSignalRegistration> RegisterSignals(LauncherSupervisor supervisor)
    {
        var registrations = new List<PosixSignalRegistration>(4);
        var isWindows = OperatingSystem.IsWindows();

        TryRegisterSignal(registrations, PosixSignal.SIGINT, context =>
        {
            context.Cancel = true;
            supervisor.RequestShutdown(SigIntDescription, PosixSignal.SIGINT);
        });

        TryRegisterSignal(registrations, PosixSignal.SIGQUIT, context =>
        {
            context.Cancel = true;
            supervisor.RequestShutdown(SigQuitDescription, PosixSignal.SIGQUIT);
        });

        TryRegisterSignal(registrations, PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            supervisor.RequestShutdown(SigTermDescription, PosixSignal.SIGTERM);
        });

        TryRegisterSignal(registrations, PosixSignal.SIGHUP, context =>
        {
            context.Cancel = true;
            supervisor.RequestShutdown(isWindows ? SigHupWindowsDescription : SigHupUnixDescription, PosixSignal.SIGHUP);
        });

        return registrations;
    }

    private static void TryRegisterSignal(
        List<PosixSignalRegistration> registrations,
        PosixSignal signal,
        Action<PosixSignalContext> handler)
    {
        try
        {
            registrations.Add(PosixSignalRegistration.Create(signal, handler));
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or IOException)
        {
            // Justification: Certain platforms/sandboxes omit specific POSIX signals (e.g., SIGQUIT/SIGHUP).
            _ = ex;
        }
    }
}
