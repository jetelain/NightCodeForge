using System.ComponentModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NightCodeForge.Agents.Optimizations;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Infrastructure.Execution;

namespace NightCodeForge.Agents.Tools;

/// <summary>
/// Composable tool module encapsulating sandbox command execution (<c>bash</c>),
/// virtual path translation via <see cref="ISandboxPathResolver"/>, <see cref="ReadOnlyCommandValidator"/>,
/// dynamic build/test timeout escalation, and <see cref="StructuredDiagnosticExtractor"/>.
/// </summary>
public sealed class SandboxBashToolModule : IAgentToolModule
{
    public const string MissingModuleInstanceMessage = IAgentToolModule.MissingBoundModuleInstanceMessage;

    private readonly IExecutionEnvironment _executionEnv;
    private readonly int? _buildTimeoutSeconds;
    private readonly int? _testTimeoutSeconds;
    private readonly bool _isReadOnly;
    private readonly ILogger _logger;

    public ISandboxPathResolver SandboxPathResolver { get; set; }

    private bool IsEffectiveReadOnly => _isReadOnly || SandboxPathResolver?.Scope?.IsReadOnly == true;

    public SandboxBashToolModule(
        CanonicalToolScope scope,
        ISandboxPathResolver sandboxPathResolver,
        IExecutionEnvironment executionEnv,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(scope);
        SandboxPathResolver = sandboxPathResolver ?? throw new ArgumentNullException(nameof(sandboxPathResolver));
        _executionEnv = executionEnv ?? throw new ArgumentNullException(nameof(executionEnv));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _buildTimeoutSeconds = scope.BuildTimeoutSeconds;
        _testTimeoutSeconds = scope.TestTimeoutSeconds;
        _isReadOnly = scope.IsReadOnly;
    }

    public IReadOnlyList<AIFunction> CreateFunctions() => CreateFunctions(this);

    public static IReadOnlyList<AIFunction> CreateFunctions(SandboxBashToolModule? module) =>
    [
        AIFunctionFactory.Create(
            ([Description("Shell command line to execute")] string command,
             [Description("Timeout in seconds")] int? timeout = null,
             [Description("Working directory relative to repo root")] string? working_directory = null,
             CancellationToken ct = default) =>
                module != null
                    ? module.BashAsync(command, timeout, working_directory, ct)
                    : throw new InvalidOperationException(MissingModuleInstanceMessage),
            CanonicalToolNames.Bash,
            "Executes a bash or shell command within the workspace sandbox.")
    ];

    public async Task<string> BashAsync(string command, int? timeout = null, string? workingDirectory = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return "[Empty bash command]";
        }

        if (IsEffectiveReadOnly)
        {
            var (isAllowed, errorMsg) = ReadOnlyCommandValidator.ValidateCommand(command);
            if (!isAllowed)
            {
                _logger.LogWarning("CanonicalTools.bash: Read-only command violation: '{Command}'. Reason: {Error}", command, errorMsg);
                return $"[READ-ONLY VIOLATION]: {errorMsg}";
            }
        }

        string? workDir;
        try
        {
            var resolvedWorkDir = SandboxPathResolver.ResolveBashWorkingDirectory(workingDirectory, _executionEnv.Tier);
            workDir = string.IsNullOrEmpty(resolvedWorkDir) ? null : resolvedWorkDir;
            command = SandboxPathResolver.TranslateBashCommand(command, _executionEnv.Tier);
        }
        catch (UnauthorizedAccessException ex)
        {
            return $"Error: {ex.Message}";
        }

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
            return $"Exit Code: {WorkflowDefaults.ProcessTimeoutExitCode}\n[TIMEOUT]: Command execution timed out after {effectiveTimeout}s. Execution was aborted to prevent resource lockup. Check for hanging tests, infinite loops, or deadlocks.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CanonicalTools.bash: Execution failed for '{Command}' in '{WorkDir}'", command, workDir);
            return $"Error: Command execution failed: {ex.Message}";
        }

        var output = string.IsNullOrWhiteSpace(res.StandardOutput) ? res.StandardError : res.StandardOutput;
        if (!string.IsNullOrWhiteSpace(res.StandardError) && !string.IsNullOrWhiteSpace(res.StandardOutput))
        {
            output = res.StandardOutput + "\n[STDERR]\n" + res.StandardError;
        }

        bool isTimeout = res.ExitCode == WorkflowDefaults.ProcessTimeoutExitCode ||
                         (res.Truncated && (res.StandardError.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
                                            res.StandardOutput.Contains("timed out", StringComparison.OrdinalIgnoreCase)));

        if (isTimeout)
        {
            _logger.LogWarning("CanonicalTools.bash: Command timed out (detected exit code {ExitCode} or buffer): '{Command}'", WorkflowDefaults.ProcessTimeoutExitCode, command);
            return $"Exit Code: {WorkflowDefaults.ProcessTimeoutExitCode}\n[TIMEOUT]: Command execution timed out after {effectiveTimeout}s. Execution was aborted to prevent resource lockup. Check for hanging tests, infinite loops, or deadlocks.";
        }

        if (res.ExitCode != 0)
        {
            _logger.LogWarning("CanonicalTools.bash: Command returned exit code {ExitCode}: '{Command}'. Output summary: {Output}",
                res.ExitCode, command, output.Length > WorkflowDefaults.DefaultOutputSummaryTruncationLength ? output[..WorkflowDefaults.DefaultOutputSummaryTruncationLength] + "..." : output);
        }

        if (command.Contains("build", StringComparison.OrdinalIgnoreCase) || command.Contains("test", StringComparison.OrdinalIgnoreCase))
        {
            var extractor = new StructuredDiagnosticExtractor();
            var diag = extractor.ExtractDiagnostics(output);
            if (diag.CompilerErrors.Count > 0 || diag.TestFailures.Count > 0)
            {
                output = $"{output}\n\n[Structured Diagnostics Extracted]:\n{diag.FormattedMarkdown}";
            }
        }

        if (output.Length > WorkflowDefaults.MaxStageOutputChars)
        {
            output = WorkflowDefaults.TruncateStageOutput(output);
        }

        return $"Exit Code: {res.ExitCode}\n{output}".TrimEnd();
    }
}
