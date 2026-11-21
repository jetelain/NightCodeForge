namespace NightCodeForge.Installer.Core.Abstractions;

using System.ComponentModel;
using System.Diagnostics;

/// <summary>
/// Live OS implementation of <see cref="IProcessControlService"/> using <see cref="Process"/>.
/// </summary>
public sealed class SystemProcessControlService : IProcessControlService
{
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

        var currentPid = CurrentProcessId;
        var targets = new List<Process>();

        foreach (var rawName in processNames)
        {
            if (string.IsNullOrWhiteSpace(rawName))
            {
                continue;
            }

            var matches = Process.GetProcessesByName(NormalizeProcessName(rawName));
            foreach (var process in matches)
            {
                if (process.Id == currentPid)
                {
                    process.Dispose();
                    continue;
                }

                targets.Add(process);
            }
        }

        if (targets.Count == 0)
        {
            return false;
        }

        try
        {
            var killedAny = false;
            foreach (var target in targets)
            {
                try
                {
                    if (!target.HasExited)
                    {
                        target.Kill(entireProcessTree: true);
                        killedAny = true;
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
                {
                    // Justification: Target process may exit concurrently or deny termination handle during teardown.
                    _ = ex;
                }
            }

            if (killedAny && waitTimeout > TimeSpan.Zero)
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(waitTimeout);

                var waitTasks = targets.Select(target => WaitForExitBestEffortAsync(target, timeoutCts.Token));
                await Task.WhenAll(waitTasks).ConfigureAwait(false);
            }

            return killedAny;
        }
        finally
        {
            DisposeAll(targets);
        }
    }

    private static async Task WaitForExitBestEffortAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            if (!process.HasExited)
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or OperationCanceledException)
        {
            // Justification: Bounded process-exit wait must not crash upgrades when OS wait times out or process already exited.
            _ = ex;
        }
    }

    private static bool SafeHasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // Justification: Process access may fail if process terminated between enumeration and inspection.
            _ = ex;
            return true;
        }
    }

    private static string NormalizeProcessName(string rawName)
    {
        var trimmed = rawName.Trim();
        return trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^4]
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
