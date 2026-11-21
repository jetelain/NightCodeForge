namespace NightCodeForge.Installer.Core.Abstractions;

using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using NightCodeForge.Installer.Core.Constants;

/// <summary>
/// Real OS process runner backed by <see cref="Process"/>.
/// </summary>
public sealed class SystemProcessRunner : IProcessRunner
{
    private const int Win32ErrorCancelled = 1223;
    private const int Win32ErrorFileNotFound = 2;
    private const int Win32ErrorPathNotFound = 3;
    private const int UnixErrnoNoEnt = 2;
    private const int UnixErrnoNotDir = 20;

    public async Task<ProcessExecutionResult> RunAsync(
        ProcessRunRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.FileName);

        if (request.Timeout.HasValue && request.Timeout.Value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Process timeout must be positive when specified.");
        }

        if (request.RunAsAdmin && !OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("RunAsAdmin elevation via ShellExecute 'runas' is only supported on Windows.");
        }

        if ((request.UseShellExecute || request.RunAsAdmin) && request.EnvironmentVariables is { Count: > 0 })
        {
            throw new ArgumentException(
                "ProcessRunRequest cannot specify EnvironmentVariables when UseShellExecute or RunAsAdmin is enabled.",
                nameof(request));
        }

        if (request.Detached)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return StartDetached(request);
        }

        if (request.RunAsAdmin || request.UseShellExecute)
        {
            return await RunWithShellExecuteAsync(request, cancellationToken).ConfigureAwait(false);
        }

        return await RunCapturedAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static ProcessExecutionResult StartDetached(ProcessRunRequest request)
    {
        var startInfo = CreateBaseStartInfo(request);
        var useShellExecute = request.UseShellExecute || request.RunAsAdmin;
        startInfo.UseShellExecute = useShellExecute;
        startInfo.CreateNoWindow = !useShellExecute;
        if (request.RunAsAdmin)
        {
            startInfo.Verb = InstallerConstants.RunAsVerb;
        }

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                // Windows ShellExecuteEx legitimately returns a null Process handle (hProcess == 0)
                // when handing a URI or file to an already-running browser/protocol handler via DDE/COM.
                return useShellExecute && !request.RunAsAdmin
                    ? new ProcessExecutionResult(InstallerConstants.ExitCodeSuccess, string.Empty, string.Empty)
                    : new ProcessExecutionResult(
                        InstallerConstants.ExitCodeError,
                        string.Empty,
                        $"Failed to start detached process '{request.FileName}'.");
            }

            return new ProcessExecutionResult(InstallerConstants.ExitCodeSuccess, string.Empty, string.Empty);
        }
        catch (Win32Exception ex) when ((useShellExecute && ex.NativeErrorCode == Win32ErrorCancelled) || IsFileOrPathNotFound(ex))
        {
            return new ProcessExecutionResult(ex.NativeErrorCode, string.Empty, ex.Message);
        }
    }

    private static async Task<ProcessExecutionResult> RunWithShellExecuteAsync(
        ProcessRunRequest request,
        CancellationToken cancellationToken)
    {
        var startInfo = CreateBaseStartInfo(request);
        startInfo.UseShellExecute = true;
        startInfo.CreateNoWindow = false;
        if (request.RunAsAdmin)
        {
            startInfo.Verb = InstallerConstants.RunAsVerb;
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return !request.RunAsAdmin
                    ? new ProcessExecutionResult(InstallerConstants.ExitCodeSuccess, string.Empty, string.Empty)
                    : new ProcessExecutionResult(
                        InstallerConstants.ExitCodeError,
                        string.Empty,
                        $"Failed to start '{request.FileName}'.");
            }
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == Win32ErrorCancelled || IsFileOrPathNotFound(ex))
        {
            return new ProcessExecutionResult(ex.NativeErrorCode, string.Empty, ex.Message);
        }

        using var linkedCts = CreateLinkedTimeoutCts(request.Timeout, cancellationToken);
        try
        {
            await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKillProcessTree(process);
            if (!cancellationToken.IsCancellationRequested && request.Timeout.HasValue)
            {
                return new ProcessExecutionResult(
                    InstallerConstants.ExitCodeTimeout,
                    string.Empty,
                    $"Process '{request.FileName}' timed out after {request.Timeout.Value.TotalSeconds:F0}s.");
            }

            throw;
        }

        return new ProcessExecutionResult(process.ExitCode, string.Empty, string.Empty);
    }

    private static async Task<ProcessExecutionResult> RunCapturedAsync(
        ProcessRunRequest request,
        CancellationToken cancellationToken)
    {
        var startInfo = CreateBaseStartInfo(request);
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.StandardOutputEncoding = Encoding.UTF8;
        startInfo.StandardErrorEncoding = Encoding.UTF8;

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return new ProcessExecutionResult(
                    InstallerConstants.ExitCodeError,
                    string.Empty,
                    $"Failed to start '{request.FileName}'.");
            }
        }
        catch (Win32Exception ex) when (IsFileOrPathNotFound(ex))
        {
            return new ProcessExecutionResult(ex.NativeErrorCode, string.Empty, ex.Message);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
        var drainTimeout = TimeSpan.FromMilliseconds(InstallerConstants.ProcessPostExitDrainTimeoutMs);

        using var linkedCts = CreateLinkedTimeoutCts(request.Timeout, cancellationToken);
        try
        {
            await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKillProcessTree(process);
            if (!cancellationToken.IsCancellationRequested && request.Timeout.HasValue)
            {
                var partialOut = await DrainStreamWithTimeoutAsync(stdoutTask, process.StandardOutput, drainTimeout).ConfigureAwait(false);
                var partialErr = await DrainStreamWithTimeoutAsync(stderrTask, process.StandardError, drainTimeout).ConfigureAwait(false);
                var timeoutMessage = $"Process '{request.FileName}' timed out after {request.Timeout.Value.TotalSeconds:F0}s.";
                var combinedErr = string.IsNullOrWhiteSpace(partialErr)
                    ? timeoutMessage
                    : $"{partialErr}{Environment.NewLine}{timeoutMessage}";
                return new ProcessExecutionResult(InstallerConstants.ExitCodeTimeout, partialOut, combinedErr);
            }

            CloseStreamBestEffort(process.StandardOutput);
            CloseStreamBestEffort(process.StandardError);
            ObserveFaultedReadTask(stdoutTask);
            ObserveFaultedReadTask(stderrTask);
            throw;
        }

        var stdout = await DrainStreamWithTimeoutAsync(stdoutTask, process.StandardOutput, drainTimeout).ConfigureAwait(false);
        var stderr = await DrainStreamWithTimeoutAsync(stderrTask, process.StandardError, drainTimeout).ConfigureAwait(false);
        return new ProcessExecutionResult(process.ExitCode, stdout, stderr);
    }

    private static async Task<string> DrainStreamWithTimeoutAsync(
        Task<string> streamTask,
        StreamReader reader,
        TimeSpan drainTimeout)
    {
        if (!streamTask.IsCompleted)
        {
            try
            {
                await streamTask.WaitAsync(drainTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Justification: A detached grandchild inherited the pipe write handle; close reader to unblock and proceed.
                CloseStreamBestEffort(reader);
                ObserveFaultedReadTask(streamTask);
                return string.Empty;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
            {
                // Justification: Stream reader aborted when process tree was killed or disposed.
                _ = ex;
                return string.Empty;
            }
        }

        try
        {
            return await streamTask.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // Justification: Stream reader aborted when process tree was killed or disposed.
            _ = ex;
            return string.Empty;
        }
    }

    private static void ObserveFaultedReadTask(Task<string> task)
    {
        if (task.IsCompleted)
        {
            _ = task.Exception;
            return;
        }

        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static void CloseStreamBestEffort(StreamReader reader)
    {
        try
        {
            reader.Close();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Justification: Best-effort pipe closure during teardown/timeout.
            _ = ex;
        }
    }

    private static ProcessStartInfo CreateBaseStartInfo(ProcessRunRequest request)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = request.FileName
        };

        if (!string.IsNullOrWhiteSpace(request.WorkingDirectory))
        {
            startInfo.WorkingDirectory = request.WorkingDirectory;
        }

        if (request.RawArguments is not null)
        {
            if (request.Arguments.Count > 0)
            {
                throw new ArgumentException("ProcessRunRequest cannot specify both Arguments and RawArguments.", nameof(request));
            }

            startInfo.Arguments = request.RawArguments;
        }
        else
        {
            foreach (var arg in request.Arguments)
            {
                startInfo.ArgumentList.Add(arg);
            }
        }

        if (!request.UseShellExecute && !request.RunAsAdmin && request.EnvironmentVariables is not null)
        {
            foreach (var (key, value) in request.EnvironmentVariables)
            {
                startInfo.Environment[key] = value;
            }
        }

        return startInfo;
    }

    private static CancellationTokenSource CreateLinkedTimeoutCts(TimeSpan? timeout, CancellationToken cancellationToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout.HasValue)
        {
            cts.CancelAfter(timeout.Value);
        }

        return cts;
    }

    private static bool IsFileOrPathNotFound(Win32Exception ex) =>
        OperatingSystem.IsWindows()
            ? ex.NativeErrorCode is Win32ErrorFileNotFound or Win32ErrorPathNotFound
            : ex.NativeErrorCode is UnixErrnoNoEnt or UnixErrnoNotDir;

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Justification: Process may exit concurrently before Kill completes.
            _ = ex;
        }
    }
}
