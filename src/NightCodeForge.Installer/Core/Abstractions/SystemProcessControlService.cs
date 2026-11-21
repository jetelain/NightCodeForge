namespace NightCodeForge.Installer.Core.Abstractions;

using System.ComponentModel;
using System.Diagnostics;
using NightCodeForge.Installer.Core.Constants;

/// <summary>
/// Live OS implementation of <see cref="IProcessControlService"/> using <see cref="Process"/>.
/// </summary>
public sealed class SystemProcessControlService : IProcessControlService
{
    private const int Win32ErrorAccessDenied = 5;

    public int CurrentProcessId => Environment.ProcessId;

    public string? CurrentProcessPath => Environment.ProcessPath;

    public bool IsAnyProcessRunning(IEnumerable<string> processNames)
    {
        ArgumentNullException.ThrowIfNull(processNames);

        var currentPid = CurrentProcessId;
        foreach (var rawName in processNames)
        {
            if (string.IsNullOrWhiteSpace(rawName))
            {
                continue;
            }

            var processes = Process.GetProcessesByName(NormalizeProcessName(rawName));
            try
            {
                foreach (var process in processes)
                {
                    if (process.Id != currentPid && !SafeHasExited(process))
                    {
                        return true;
                    }
                }
            }
            finally
            {
                DisposeAll(processes);
            }
        }

        return false;
    }

    public async Task<bool> StopProcessesAsync(
        IEnumerable<string> processNames,
        TimeSpan waitTimeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(processNames);
        cancellationToken.ThrowIfCancellationRequested();

        var currentPid = CurrentProcessId;
        var seenPids = new HashSet<int>();
        var targets = new List<Process>();

        try
        {
            foreach (var rawName in processNames)
            {
                if (string.IsNullOrWhiteSpace(rawName))
                {
                    continue;
                }

                var matches = Process.GetProcessesByName(NormalizeProcessName(rawName));
                foreach (var process in matches)
                {
                    var keepHandle = false;
                    try
                    {
                        var pid = process.Id;
                        if (pid != currentPid && seenPids.Add(pid))
                        {
                            targets.Add(process);
                            keepHandle = true;
                        }
                    }
                    finally
                    {
                        if (!keepHandle)
                        {
                            process.Dispose();
                        }
                    }
                }
            }

            if (targets.Count == 0)
            {
                return false;
            }

            var killedTargets = new List<Process>();
            foreach (var target in targets)
            {
                try
                {
                    if (!target.HasExited)
                    {
                        target.Kill(entireProcessTree: true);
                        killedTargets.Add(target);
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
                {
                    // Justification: Target process may exit concurrently or deny termination handle during teardown.
                    _ = ex;
                }
            }

            if (killedTargets.Count > 0 && waitTimeout > TimeSpan.Zero)
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(waitTimeout);

                var waitTasks = killedTargets.Select(target => WaitForExitBestEffortAsync(target, timeoutCts.Token, cancellationToken));
                await Task.WhenAll(waitTasks).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return killedTargets.Count > 0;
        }
        finally
        {
            DisposeAll(targets);
        }
    }

    private static async Task WaitForExitBestEffortAsync(
        Process process,
        CancellationToken linkedToken,
        CancellationToken callerToken)
    {
        try
        {
            if (!process.HasExited)
            {
                await process.WaitForExitAsync(linkedToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
        {
            // Justification: Bounded process-exit wait must not crash upgrades when OS wait times out.
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Justification: Process may exit concurrently or deny wait handle during teardown.
            _ = ex;
        }
    }

    private static bool SafeHasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (Win32Exception ex) when (OperatingSystem.IsWindows() && ex.NativeErrorCode == Win32ErrorAccessDenied)
        {
            // Process is still active (e.g., elevated/system process), not exited.
            return false;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // Justification: Process access may fail if process terminated between enumeration and inspection.
            _ = ex;
            return true;
        }
    }

    internal static string NormalizeProcessName(string rawName)
    {
        var trimmed = rawName.Trim();
        return trimmed.EndsWith(InstallerConstants.ExeExtension, StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^InstallerConstants.ExeExtension.Length]
            : trimmed;
    }

    private static void DisposeAll(IEnumerable<Process> processes)
    {
        foreach (var process in processes)
        {
            process.Dispose();
        }
    }
}
