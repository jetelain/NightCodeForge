namespace NightCodeForge.Installer.Core.Abstractions;

using System.ComponentModel;
using System.Diagnostics;
using System.Text;

/// <summary>
/// Real OS process runner backed by <see cref="Process"/>.
/// </summary>
public sealed class SystemProcessRunner : IProcessRunner
{
    private const int Win32ErrorCancelled = 1223;
    private const int Win32ErrorFileNotFound = 2;
    private const int Win32ErrorPathNotFound = 3;

    public async Task<ProcessExecutionResult> RunAsync(
        ProcessRunRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.FileName);

        if (request.Detached)
        {
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
        startInfo.UseShellExecute = request.UseShellExecute;
        startInfo.CreateNoWindow = !request.UseShellExecute;

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return new ProcessExecutionResult(1, string.Empty, $"Failed to start detached process '{request.FileName}'.");
            }

            return new ProcessExecutionResult(0, string.Empty, string.Empty);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode is Win32ErrorFileNotFound or Win32ErrorPathNotFound)
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
            startInfo.Verb = "runas";
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return new ProcessExecutionResult(1, string.Empty, $"Failed to start '{request.FileName}'.");
            }
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == Win32ErrorCancelled)
        {
            return new ProcessExecutionResult(Win32ErrorCancelled, string.Empty, ex.Message);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode is Win32ErrorFileNotFound or Win32ErrorPathNotFound)
        {
            return new ProcessExecutionResult(ex.NativeErrorCode, string.Empty, ex.Message);
        }

        using var linkedCts = CreateLinkedTimeoutCts(request.Timeout, cancellationToken);
        try
        {
            await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && request.Timeout.HasValue)
        {
            TryKillProcessTree(process);
            return new ProcessExecutionResult(-1, string.Empty, $"Process '{request.FileName}' timed out after {request.Timeout.Value.TotalSeconds:F0}s.");
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
                return new ProcessExecutionResult(1, string.Empty, $"Failed to start '{request.FileName}'.");
            }
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode is Win32ErrorFileNotFound or Win32ErrorPathNotFound)
        {
            return new ProcessExecutionResult(ex.NativeErrorCode, string.Empty, ex.Message);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

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
                var partialOut = await SafeReadStreamAsync(stdoutTask).ConfigureAwait(false);
                var partialErr = await SafeReadStreamAsync(stderrTask).ConfigureAwait(false);
                var timeoutMessage = $"Process '{request.FileName}' timed out after {request.Timeout.Value.TotalSeconds:F0}s.";
                var combinedErr = string.IsNullOrWhiteSpace(partialErr)
                    ? timeoutMessage
                    : $"{partialErr}{Environment.NewLine}{timeoutMessage}";
                return new ProcessExecutionResult(-1, partialOut, combinedErr);
            }

            _ = SafeReadStreamAsync(stdoutTask);
            _ = SafeReadStreamAsync(stderrTask);
            throw;
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        return new ProcessExecutionResult(process.ExitCode, stdout, stderr);
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
        if (timeout.HasValue && timeout.Value > TimeSpan.Zero)
        {
            cts.CancelAfter(timeout.Value);
        }

        return cts;
    }

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

    private static async Task<string> SafeReadStreamAsync(Task<string> streamTask)
    {
        try
        {
            return await streamTask.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // Justification: Stream readers may abort when process tree is killed or disposed after timeout/cancellation.
            _ = ex;
            return string.Empty;
        }
    }
}
