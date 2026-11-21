namespace NightCodeForge.Launcher;

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

/// <summary>
/// Abstraction over a running child web server process.
/// </summary>
public interface IChildProcessHandle : IDisposable
{
    int ProcessId { get; }
    bool HasExited { get; }
    Task<int> WaitForExitAsync(CancellationToken cancellationToken);
    void TryRequestGracefulShutdown(PosixSignal signal);
    void TryTerminateTree();
}

/// <summary>
/// Launches the child web server process with inherited console streams.
/// </summary>
public interface IChildProcessStarter
{
    IChildProcessHandle Start(string executablePath, string workingDirectory, IReadOnlyList<string> arguments);
}

/// <summary>
/// System <see cref="Process"/>-backed child process starter preserving console handles for native signal sharing.
/// </summary>
public sealed class SystemChildProcessStarter : IChildProcessStarter
{
    public IChildProcessHandle Start(string executablePath, string workingDirectory, IReadOnlyList<string> arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentNullException.ThrowIfNull(arguments);

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false
        };

        foreach (var arg in arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Process.Start returned null for '{executablePath}'.");

        return new SystemChildProcessHandle(process);
    }

    private sealed class SystemChildProcessHandle(Process process) : IChildProcessHandle
    {
        private const int UnixSigInt = 2;
        private const int UnixSigQuit = 3;
        private const int UnixSigTerm = 15;

        public int ProcessId => process.Id;

        public bool HasExited => process.HasExited;

        public async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return process.ExitCode;
        }

        public void TryRequestGracefulShutdown(PosixSignal signal)
        {
            // Windows console signals (SIGINT/SIGQUIT/SIGTERM/SIGHUP) are natively broadcast to all processes attached to the console.
            if (OperatingSystem.IsWindows())
            {
                return;
            }

            // Map SIGHUP to SIGTERM so ASP.NET Core's ConsoleLifetime initiates graceful shutdown instead of abrupt hangup exit.
            var unixSignal = signal switch
            {
                PosixSignal.SIGINT => UnixSigInt,
                PosixSignal.SIGQUIT => UnixSigQuit,
                _ => UnixSigTerm
            };

            try
            {
                if (!process.HasExited)
                {
                    NativeMethods.Kill(process.Id, unixSignal);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
                // Justification: Child process may exit concurrently before graceful shutdown signal delivery.
                _ = ex;
            }
        }

        public void TryTerminateTree()
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or AggregateException or NotSupportedException)
            {
                // Justification: Child or descendant processes may exit between HasExited check and Kill(entireProcessTree: true) during shutdown race.
                _ = ex;
            }
        }

        public void Dispose() => process.Dispose();

        private static class NativeMethods
        {
            [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
            internal static extern int Kill(int pid, int sig);
        }
    }
}
