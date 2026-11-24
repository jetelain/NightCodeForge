using System.ComponentModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Globalization;
using NightCodeForge.Core.Workflow.Filtering;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Infrastructure.Execution;

namespace NightCodeForge.Agents.Tools;

/// <summary>
/// Structured outcome of executing a command through <see cref="SandboxBashToolModule"/>.
/// </summary>
public sealed record BashToolExecutionResult(
    string FormattedOutput,
    string EffectiveWorkingDirectory,
    int ExitCode,
    string Error,
    TimeSpan Duration,
    bool IsTruncated);

/// <summary>
/// Composable tool module encapsulating sandbox command execution (<c>bash</c>),
/// virtual path translation via <see cref="ISandboxPathResolver"/>, <see cref="ReadOnlyCommandValidator"/>,
/// dynamic build/test timeout escalation, /tmp raw output offloading, and semantic CLI output filtering.
/// </summary>
public sealed class SandboxBashToolModule : IAgentToolModule
{
    public const string MissingModuleInstanceMessage = IAgentToolModule.MissingBoundModuleInstanceMessage;

    private readonly IExecutionEnvironment _executionEnv;
    private readonly ICliOutputFilterRegistry _cliFilterRegistry;
    private readonly int? _buildTimeoutSeconds;
    private readonly int? _testTimeoutSeconds;
    private readonly bool _isReadOnly;
    private readonly string _workspaceRoot;
    private readonly LocalOptimizationFlags _optimizationFlags;
    private readonly Action<BashExecutionAuditEntry>? _onBashExecutionAudited;
    private readonly ConcurrentQueue<(BashExecutionAuditEntry Audit, string RenderedOutput)> _auditEntries = new();
    private readonly ILogger _logger;

    public ISandboxPathResolver SandboxPathResolver { get; set; }

    private bool IsEffectiveReadOnly => _isReadOnly || SandboxPathResolver?.Scope?.IsReadOnly == true;

    public SandboxBashToolModule(
        CanonicalToolScope scope,
        ISandboxPathResolver sandboxPathResolver,
        IExecutionEnvironment executionEnv,
        ICliOutputFilterRegistry cliFilterRegistry,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(scope);
        SandboxPathResolver = sandboxPathResolver ?? throw new ArgumentNullException(nameof(sandboxPathResolver));
        _executionEnv = executionEnv ?? throw new ArgumentNullException(nameof(executionEnv));
        _cliFilterRegistry = cliFilterRegistry ?? throw new ArgumentNullException(nameof(cliFilterRegistry));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _buildTimeoutSeconds = scope.BuildTimeoutSeconds;
        _testTimeoutSeconds = scope.TestTimeoutSeconds;
        _isReadOnly = scope.IsReadOnly;
        _workspaceRoot = scope.WorkspaceRoot ?? string.Empty;
        _optimizationFlags = scope.OptimizationFlags ?? new LocalOptimizationFlags();
        _onBashExecutionAudited = scope.OnBashExecutionAudited;
    }

    /// <summary>
    /// Attempts to claim the <see cref="BashExecutionAuditEntry"/> corresponding to a rendered <c>bash</c> tool result.
    /// </summary>
    public bool TryTakeAuditEntry(string renderedResult, out BashExecutionAuditEntry? auditEntry)
    {
        var remaining = new List<(BashExecutionAuditEntry Audit, string RenderedOutput)>();
        BashExecutionAuditEntry? matched = null;

        while (_auditEntries.TryDequeue(out var item))
        {
            if (matched is null && string.Equals(item.RenderedOutput, renderedResult, StringComparison.Ordinal))
            {
                matched = item.Audit;
            }
            else
            {
                remaining.Add(item);
            }
        }

        foreach (var item in remaining)
        {
            _auditEntries.Enqueue(item);
        }

        if (matched is null && remaining.Count > 0 && _auditEntries.TryDequeue(out var fallback))
        {
            auditEntry = fallback.Audit;
            return true;
        }

        auditEntry = matched;
        return matched is not null;
    }

    public IReadOnlyList<AIFunction> CreateFunctions() => CreateFunctions(this);

    public static IReadOnlyList<AIFunction> CreateFunctions(SandboxBashToolModule? module) =>
    [
        AIFunctionFactory.Create(
            async ([Description("Shell command line to execute")] string command,
             [Description("Timeout in seconds")] int? timeout = null,
             [Description("Working directory relative to repo root")] string? working_directory = null,
             [Description("Optional. Set to true to bypass smart output filtering and receive raw, uncompressed CLI output")] bool? disable_filter = null,
             CancellationToken ct = default) =>
            {
                if (module == null)
                {
                    throw new InvalidOperationException(MissingModuleInstanceMessage);
                }

                try
                {
                    return await module.BashAsync(command, timeout, working_directory, disable_filter, ct);
                }
                catch (UnauthorizedAccessException ex)
                {
                    return $"Error: {ex.Message}";
                }
            },
            CanonicalToolNames.Bash,
            "Executes a bash or shell command within the workspace sandbox.")
    ];

    public Task<string> BashAsync(
        string command,
        int? timeout = null,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default) =>
        BashAsync(command, timeout, workingDirectory, disableFilter: null, cancellationToken);

    public async Task<string> BashAsync(
        string command,
        int? timeout,
        string? workingDirectory,
        bool? disableFilter,
        CancellationToken cancellationToken = default) =>
        (await ExecuteStructuredAsync(command, timeout, workingDirectory, disableFilter, cancellationToken)).FormattedOutput;

    /// <summary>
    /// Executes a command through the full <c>bash</c> tool pipeline (path resolution, read-only validation,
    /// dynamic timeout escalation, /tmp offloading, and CLI output filtering) and returns structured metadata alongside the rendered output.
    /// </summary>
    public async Task<BashToolExecutionResult> ExecuteStructuredAsync(
        string command,
        int? timeout = null,
        string? workingDirectory = null,
        bool? disableFilter = null,
        CancellationToken cancellationToken = default)
    {
        var fallbackWorkDir = SandboxPathResolver?.Scope?.DefaultWorkingDirectory ?? string.Empty;

        if (string.IsNullOrWhiteSpace(command))
        {
            return new BashToolExecutionResult("[Empty bash command]", fallbackWorkDir, 0, string.Empty, TimeSpan.Zero, false);
        }

        var originalCommand = command;

        try
        {
            ReadOnlyCommandValidator.EnsureMountAccessAllowed(
                command,
                SandboxPathResolver?.MountTable,
                SandboxPathResolver?.Scope?.ScopeCacheKey,
                workingDirectory,
                SandboxPathResolver?.Scope?.DefaultWorkingDirectory);
        }
        catch (UnauthorizedAccessException ex) when (
            !Core.Interfaces.SandboxPathResolver.IsAttachmentsPathOrReference(
                workingDirectory,
                SandboxPathResolver?.MountTable,
                SandboxPathResolver?.Scope?.DefaultWorkingDirectory) &&
            !Core.Interfaces.SandboxPathResolver.ContainsAttachmentsReference(command, SandboxPathResolver?.MountTable))
        {
            return new BashToolExecutionResult($"Error: {ex.Message}", workingDirectory ?? fallbackWorkDir, 1, ex.Message, TimeSpan.Zero, false);
        }

        if (IsEffectiveReadOnly)
        {
            var (isAllowed, errorMsg) = ReadOnlyCommandValidator.ValidateCommand(command);
            if (!isAllowed)
            {
                _logger.LogWarning("CanonicalTools.bash: Read-only command violation: '{Command}'. Reason: {Error}", command, errorMsg);
                return new BashToolExecutionResult($"[READ-ONLY VIOLATION]: {errorMsg}", workingDirectory ?? fallbackWorkDir, 1, errorMsg ?? string.Empty, TimeSpan.Zero, false);
            }
        }

        string? workDir;
        try
        {
            var resolvedWorkDir = SandboxPathResolver!.ResolveBashWorkingDirectory(workingDirectory, _executionEnv.Tier);
            workDir = string.IsNullOrEmpty(resolvedWorkDir) ? null : resolvedWorkDir;
            command = SandboxPathResolver.TranslateBashCommand(command, _executionEnv.Tier);
        }
        catch (UnauthorizedAccessException ex) when (
            !Core.Interfaces.SandboxPathResolver.IsAttachmentsPathOrReference(
                workingDirectory,
                SandboxPathResolver?.MountTable,
                SandboxPathResolver?.Scope?.DefaultWorkingDirectory) &&
            !Core.Interfaces.SandboxPathResolver.ContainsAttachmentsReference(command, SandboxPathResolver?.MountTable))
        {
            return new BashToolExecutionResult($"Error: {ex.Message}", workingDirectory ?? fallbackWorkDir, 1, ex.Message, TimeSpan.Zero, false);
        }

        var effectiveWorkDir = workDir ?? fallbackWorkDir;

        int configuredBuildTimeout = _buildTimeoutSeconds ?? WorkflowDefaults.DefaultBuildTimeoutSeconds;
        int configuredTestTimeout = _testTimeoutSeconds ?? WorkflowDefaults.DefaultTestTimeoutSeconds;
        int combinedSuiteTimeout = Math.Max(WorkflowDefaults.MinimumBashToolTimeoutSeconds, configuredBuildTimeout + configuredTestTimeout);

        int effectiveTimeout = timeout is > 0
            ? Math.Max(timeout.Value, combinedSuiteTimeout)
            : combinedSuiteTimeout;

        CommandResult res;
        try
        {
            res = await _executionEnv.ExecuteAsync(command, workDir, effectiveTimeout, null, cancellationToken);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("CanonicalTools.bash: Command timed out after {Timeout}s: '{Command}' in '{WorkDir}'", effectiveTimeout, command, workDir);
            var timeoutOutput = $"Exit Code: {WorkflowDefaults.ProcessTimeoutExitCode}\n[TIMEOUT]: Command execution timed out after {effectiveTimeout}s. Execution was aborted to prevent resource lockup. Check for hanging tests, infinite loops, or deadlocks.";
            return new BashToolExecutionResult(timeoutOutput, effectiveWorkDir, WorkflowDefaults.ProcessTimeoutExitCode, $"Command execution timed out after {effectiveTimeout}s.", TimeSpan.FromSeconds(effectiveTimeout), false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CanonicalTools.bash: Execution failed for '{Command}' in '{WorkDir}'", command, workDir);
            return new BashToolExecutionResult($"Error: Command execution failed: {ex.Message}", effectiveWorkDir, -1, ex.Message, TimeSpan.Zero, false);
        }

        var rawOutput = string.IsNullOrWhiteSpace(res.StandardOutput) ? res.StandardError : res.StandardOutput;
        if (!string.IsNullOrWhiteSpace(res.StandardError) && !string.IsNullOrWhiteSpace(res.StandardOutput))
        {
            rawOutput = res.StandardOutput + "\n[STDERR]\n" + res.StandardError;
        }

        bool isTimeout = res.ExitCode == WorkflowDefaults.ProcessTimeoutExitCode ||
                         (res.Truncated && (res.StandardError.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
                                            res.StandardOutput.Contains("timed out", StringComparison.OrdinalIgnoreCase)));

        if (isTimeout)
        {
            _logger.LogWarning("CanonicalTools.bash: Command timed out (detected exit code {ExitCode} or buffer): '{Command}'", WorkflowDefaults.ProcessTimeoutExitCode, command);
            var timeoutOutput = $"Exit Code: {WorkflowDefaults.ProcessTimeoutExitCode}\n[TIMEOUT]: Command execution timed out after {effectiveTimeout}s. Execution was aborted to prevent resource lockup. Check for hanging tests, infinite loops, or deadlocks.";
            return new BashToolExecutionResult(timeoutOutput, effectiveWorkDir, WorkflowDefaults.ProcessTimeoutExitCode, res.StandardError, res.Duration, res.Truncated);
        }

        if (res.ExitCode != 0)
        {
            _logger.LogWarning("CanonicalTools.bash: Command returned exit code {ExitCode}: '{Command}'. Output summary: {Output}",
                res.ExitCode, command, rawOutput.Length > WorkflowDefaults.DefaultOutputSummaryTruncationLength ? rawOutput[..WorkflowDefaults.DefaultOutputSummaryTruncationLength] + "..." : rawOutput);
        }

        var virtualTmpPath = TrySaveRawOutputToTmp(rawOutput);
        var formatted = FormatBashOutput(originalCommand, command, res.ExitCode, rawOutput, virtualTmpPath, disableFilter == true);

        var audit = new BashExecutionAuditEntry(
            originalCommand,
            res.ExitCode,
            virtualTmpPath,
            rawOutput,
            rawOutput.Length,
            formatted.FinalOutput.Length,
            formatted.WasSmartFiltered,
            formatted.WasTruncated);

        _auditEntries.Enqueue((audit, formatted.FinalOutput));
        _onBashExecutionAudited?.Invoke(audit);

        return new BashToolExecutionResult(
            formatted.FinalOutput,
            effectiveWorkDir,
            res.ExitCode,
            res.StandardError,
            res.Duration,
            res.Truncated || formatted.WasTruncated);
    }

    private (string FinalOutput, bool WasSmartFiltered, bool WasTruncated) FormatBashOutput(
        string originalCommand,
        string translatedCommand,
        int exitCode,
        string rawOutput,
        string? virtualTmpPath,
        bool disableFilter)
    {
        var prefix = $"Exit Code: {exitCode}\n";
        var footer = virtualTmpPath != null
            ? $"\n{CliOutputFilterConstants.FormatFullOutputFooter(virtualTmpPath, rawOutput.Length)}"
            : string.Empty;

        return CliOutputFilterHelpers.FormatCommandOutput(
            _cliFilterRegistry,
            originalCommand,
            translatedCommand,
            isSuccess: exitCode == 0,
            prefix,
            rawOutput,
            footer,
            _workspaceRoot,
            enableSmartFilter: _optimizationFlags.SmartCliOutputFiltering && !disableFilter,
            WorkflowDefaults.MaxToolInlineChars);
    }

    internal static string TruncateWithHeadAndFooter(
        string prefix,
        string bodyText,
        string footer,
        int maxTotalChars) =>
        CliOutputFilterHelpers.TruncateWithHeadAndFooter(prefix, bodyText, footer, maxTotalChars);

    private string? TrySaveRawOutputToTmp(string rawOutput)
    {
        if (SandboxPathResolver is null)
        {
            return null;
        }

        var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
        var shortId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8];
        var virtualTmpPath = $"/tmp/bash-{timestamp}-{shortId}.txt";

        try
        {
            // ResolveHostPath denies forWrite: true in read-only scopes; offloading uses the writable host backing directory
            // for /tmp (which is mounted :ro inside RestrictedReadOnly sandboxes).
            var hostTmpFile = SandboxPathResolver.ResolveHostPath(virtualTmpPath, forWrite: false);
            if (string.IsNullOrWhiteSpace(hostTmpFile))
            {
                return null;
            }

            var directory = Path.GetDirectoryName(hostTmpFile);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(hostTmpFile, rawOutput);
            return virtualTmpPath;
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogDebug(ex, "CanonicalTools.bash: /tmp mount unavailable for raw bash offload ({TmpPath}).", virtualTmpPath);
            return null;
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "CanonicalTools.bash: Failed to write raw bash output to {TmpPath}.", virtualTmpPath);
            return null;
        }
    }
}
