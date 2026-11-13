using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using NightCodeForge.Agents.Tools.EditorConfig;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Execution;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Agents.Acp;

/// <summary>Context of the ACP session on whose behalf a host-side RPC callback is executed.</summary>
public sealed record AcpHostRpcContext(
    string SessionId,
    AgentSessionContext Session,
    string WorkspacePath,
    string LocalWorkspaceRoot,
    ISandboxPathResolver? SandboxPathResolver = null);

/// <summary>Outcome of a host RPC callback: either a result payload or an error message.</summary>
public sealed record AcpHostRpcResult(object? Result, string? ErrorMessage)
{
    public bool IsError => ErrorMessage != null;
    public static AcpHostRpcResult Success(object result) => new(result, null);
    public static AcpHostRpcResult Error(string message) => new(null, message);
}

/// <summary>Executes ACP host-side JSON-RPC callbacks (<c>terminal/*</c> and <c>fs/*</c>).</summary>
public interface IAcpHostRpcHandler
{
    bool CanHandle(string method);

    /// <summary>True when the method starts new agent activity (ends the current assistant text turn).</summary>
    bool EndsAssistantTurn(string method);

    Task<AcpHostRpcResult> HandleAsync(string method, JsonNode? parameters, AcpHostRpcContext context, CancellationToken cancellationToken);

    /// <summary>Drops terminal state owned by the given ACP session.</summary>
    void EndSession(string sessionId);
}

public static class AcpHostMethods
{
    public const string TerminalCreate = "terminal/create";
    public const string TerminalRun = "terminal/run";
    public const string TerminalOutput = "terminal/output";
    public const string TerminalWaitForExit = "terminal/wait_for_exit";
    public const string TerminalRelease = "terminal/release";
    public const string TerminalKill = "terminal/kill";
    public const string FsReadTextFile = "fs/read_text_file";
    public const string FsWriteTextFile = "fs/write_text_file";
}

public class AcpHostRpcHandler : IAcpHostRpcHandler
{
    private const int TerminalTimeoutSeconds = 300;
    private const string WorkspaceRoot = "/workspace";
    private const string WorkspaceRootPrefix = "/workspace/";
    private const string WorkspaceName = "workspace";
    private const string WorkspaceNamePrefix = "workspace/";

    private readonly IExecutionEnvironmentProvider _envProvider;
    private readonly IEditorConfigService _editorConfigService;
    private readonly IEditorConfigFileApplier _editorConfigFileApplier;
    private readonly IWorkspacePathResolver _pathResolver;
    private readonly ILogger<AcpHostRpcHandler> _logger;

    // Scoped per ACP session so concurrent sessions never share or leak terminal IDs.
    private readonly ConcurrentDictionary<(string SessionId, string TerminalId), (int ExitCode, string Output)> _terminals = new();

    public AcpHostRpcHandler(
        IExecutionEnvironmentProvider envProvider,
        IEditorConfigService editorConfigService,
        IEditorConfigFileApplier editorConfigFileApplier,
        IWorkspacePathResolver pathResolver,
        ILogger<AcpHostRpcHandler> logger)
    {
        _envProvider = envProvider ?? throw new ArgumentNullException(nameof(envProvider));
        _editorConfigService = editorConfigService ?? throw new ArgumentNullException(nameof(editorConfigService));
        _editorConfigFileApplier = editorConfigFileApplier ?? throw new ArgumentNullException(nameof(editorConfigFileApplier));
        _pathResolver = pathResolver ?? throw new ArgumentNullException(nameof(pathResolver));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public bool CanHandle(string method) => method is
        AcpHostMethods.TerminalCreate or AcpHostMethods.TerminalRun or AcpHostMethods.TerminalOutput
        or AcpHostMethods.TerminalWaitForExit or AcpHostMethods.TerminalRelease or AcpHostMethods.TerminalKill
        or AcpHostMethods.FsReadTextFile or AcpHostMethods.FsWriteTextFile;

    public bool EndsAssistantTurn(string method) => method is
        AcpHostMethods.TerminalCreate or AcpHostMethods.TerminalRun
        or AcpHostMethods.FsReadTextFile or AcpHostMethods.FsWriteTextFile;

    public void EndSession(string sessionId)
    {
        foreach (var key in _terminals.Keys.Where(k => k.SessionId == sessionId).ToList())
        {
            _terminals.TryRemove(key, out _);
        }
    }

    public async Task<AcpHostRpcResult> HandleAsync(string method, JsonNode? parameters, AcpHostRpcContext context, CancellationToken cancellationToken)
    {
        switch (method)
        {
            case AcpHostMethods.TerminalCreate:
            case AcpHostMethods.TerminalRun:
                return await RunTerminalAsync(parameters, context, cancellationToken);
            case AcpHostMethods.TerminalOutput:
            {
                var key = TerminalKey(context.SessionId, parameters);
                var output = _terminals.TryGetValue(key, out var cached) ? cached.Output : "";
                return AcpHostRpcResult.Success(new { output });
            }
            case AcpHostMethods.TerminalWaitForExit:
            {
                var key = TerminalKey(context.SessionId, parameters);
                var exitCode = _terminals.TryGetValue(key, out var cached) ? cached.ExitCode : 0;
                return AcpHostRpcResult.Success(new { exitCode });
            }
            case AcpHostMethods.TerminalRelease:
            case AcpHostMethods.TerminalKill:
            {
                _terminals.TryRemove(TerminalKey(context.SessionId, parameters), out _);
                return AcpHostRpcResult.Success(new { });
            }
            case AcpHostMethods.FsReadTextFile:
                return ReadFile(parameters, context);
            case AcpHostMethods.FsWriteTextFile:
                return await WriteFileAsync(parameters, context, cancellationToken);
            default:
                throw new InvalidOperationException($"Unsupported ACP host method '{method}'.");
        }
    }

    private async Task<AcpHostRpcResult> RunTerminalAsync(JsonNode? parameters, AcpHostRpcContext context, CancellationToken cancellationToken)
    {
        var session = context.Session;
        IExecutionEnvironment env;
        if (context.SandboxPathResolver != null)
        {
            env = await _envProvider.GetEnvironmentAsync(context.SandboxPathResolver.Scope, cancellationToken)
                ?? throw new InvalidOperationException($"No execution environment could be resolved for scope '{context.SandboxPathResolver.Scope.ScopeCacheKey}'.");
        }
        else
        {
            var workspace = session.Workspace ?? throw new InvalidOperationException("Workspace is required for ACP harness execution.");
            env = await _envProvider.GetEnvironmentAsync(workspace, session.Profile.SecurityTier, session.ForgeTask.Id, cancellationToken)
                ?? throw new InvalidOperationException($"No execution environment could be resolved for workspace #{workspace.Id}.");
        }

        var cmd = parameters?["command"]?.ToString() ?? "";
        if (IsReadOnlySession(context) && !string.IsNullOrWhiteSpace(cmd))
        {
            var (isAllowed, errorMsg) = Tools.ReadOnlyCommandValidator.ValidateCommand(cmd);
            if (!isAllowed)
            {
                _logger.LogWarning("ACP terminal/run blocked in read-only session '{SessionId}': '{Command}'. Reason: {Error}",
                    context.SessionId, cmd, errorMsg);
                return AcpHostRpcResult.Error($"[READ-ONLY VIOLATION]: {errorMsg}");
            }
        }

        var rawCwd = parameters?["cwd"]?.ToString();
        string cwd;
        if (context.SandboxPathResolver != null)
        {
            try
            {
                cwd = context.SandboxPathResolver.ResolveBashWorkingDirectory(rawCwd, env.Tier);
                cmd = context.SandboxPathResolver.TranslateBashCommand(cmd, env.Tier);
            }
            catch (UnauthorizedAccessException ex)
            {
                return AcpHostRpcResult.Error(ex.Message);
            }
        }
        else if (string.IsNullOrWhiteSpace(rawCwd) ||
            string.Equals(rawCwd, WorkspaceRoot, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(rawCwd, WorkspaceName, StringComparison.OrdinalIgnoreCase))
        {
            cwd = context.WorkspacePath;
        }
        else if (rawCwd.StartsWith(WorkspaceRootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var rel = rawCwd.Substring(WorkspaceRootPrefix.Length);
            cwd = Path.Combine(context.WorkspacePath, rel).Replace('\\', '/');
        }
        else
        {
            cwd = rawCwd;
        }

        var execRes = await env.ExecuteAsync(cmd, cwd, TerminalTimeoutSeconds, null, cancellationToken);
        var output = execRes.StandardOutput;
        if (!string.IsNullOrEmpty(execRes.StandardError))
        {
            output = string.IsNullOrEmpty(output) ? execRes.StandardError : output + Environment.NewLine + execRes.StandardError;
        }
        var exitCode = execRes.ExitCode;

        var terminalId = Guid.NewGuid().ToString("N");
        _terminals[(context.SessionId, terminalId.ToUpperInvariant())] = (exitCode, output);
        return AcpHostRpcResult.Success(new { terminalId, exitCode, output });
    }

    private static bool IsReadOnlySession(AcpHostRpcContext context) =>
        context.SandboxPathResolver?.Scope.IsReadOnly == true ||
        context.Session.Profile.Mode == AgentMode.ReadOnly ||
        context.Session.Profile.SecurityTier == SandboxSecurityTier.RestrictedReadOnly ||
        context.Session.RequiredMode == AgentMode.ReadOnly;

    private static (string SessionId, string TerminalId) TerminalKey(string sessionId, JsonNode? parameters)
        => (sessionId, (parameters?["terminalId"]?.ToString() ?? "").ToUpperInvariant());

    private AcpHostRpcResult ReadFile(JsonNode? parameters, AcpHostRpcContext context)
    {
        var filePath = parameters?["path"]?.ToString() ?? "";
        try
        {
            int? offset = parameters?["offset"]?.GetValue<int?>();
            int? limit = parameters?["limit"]?.GetValue<int?>();
            var content = ReadWorkspaceFile(context.LocalWorkspaceRoot, filePath, offset, limit, context.Session.Workspace?.Id, context.Session.ForgeTask.Id, context.SandboxPathResolver);
            return AcpHostRpcResult.Success(new { content });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ACP fs/read_text_file failed for '{Path}'", filePath);
            return AcpHostRpcResult.Error(ex.Message);
        }
    }

    private async Task<AcpHostRpcResult> WriteFileAsync(JsonNode? parameters, AcpHostRpcContext context, CancellationToken cancellationToken)
    {
        if (IsReadOnlySession(context))
        {
            return AcpHostRpcResult.Error("Access denied: Write operations are not permitted in a read-only agent session.");
        }

        var filePath = parameters?["path"]?.ToString() ?? "";
        var content = parameters?["content"]?.ToString() ?? "";
        try
        {
            await WriteWorkspaceFileAsync(context.LocalWorkspaceRoot, filePath, content, context.Session.Workspace?.Id, context.Session.ForgeTask.Id, context.SandboxPathResolver, cancellationToken);
            return AcpHostRpcResult.Success(new { });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ACP fs/write_text_file failed for '{Path}'", filePath);
            return AcpHostRpcResult.Error(ex.Message);
        }
    }

    private string ResolveFilePath(string workspacePath, string filePath, int? workspaceId, int? taskId, ISandboxPathResolver? sandboxPathResolver = null, bool forWrite = false)
    {
        if (sandboxPathResolver != null)
        {
            return sandboxPathResolver.ResolveHostPath(filePath, forWrite);
        }

        if (string.IsNullOrWhiteSpace(filePath))
        {
            return workspaceId.HasValue
                ? _pathResolver.ResolveCruciblePath(workspaceId.Value, string.Empty, taskId)
                : workspacePath;
        }

        if (filePath.StartsWith(@"\\") || (filePath.Length >= 2 && filePath[1] == ':'))
        {
            return Path.GetFullPath(filePath);
        }

        if (workspaceId.HasValue)
        {
            var cruciblePath = _pathResolver.ResolveCruciblePath(workspaceId.Value, filePath, taskId);
            if (!string.IsNullOrWhiteSpace(cruciblePath))
            {
                return cruciblePath;
            }
        }

        var normalized = filePath.Replace('\\', '/');
        if (normalized.StartsWith(WorkspaceRootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFullPath(Path.Combine(workspacePath, normalized.Substring(WorkspaceRootPrefix.Length)));
        }
        if (string.Equals(normalized, WorkspaceRoot, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, WorkspaceName, StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFullPath(workspacePath);
        }
        if (normalized.StartsWith(WorkspaceNamePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFullPath(Path.Combine(workspacePath, normalized.Substring(WorkspaceNamePrefix.Length)));
        }

        return Path.IsPathRooted(filePath)
            ? Path.GetFullPath(filePath)
            : Path.GetFullPath(Path.Combine(workspacePath, filePath));
    }

    private string ReadWorkspaceFile(string workspacePath, string filePath, int? offset, int? limit, int? workspaceId, int? taskId, ISandboxPathResolver? sandboxPathResolver = null)
    {
        var resolvedPath = ResolveFilePath(workspacePath, filePath, workspaceId, taskId, sandboxPathResolver, forWrite: false);
        if (!File.Exists(resolvedPath))
        {
            throw new FileNotFoundException($"File not found: {filePath}", filePath);
        }

        if (!offset.HasValue && !limit.HasValue)
        {
            return File.ReadAllText(resolvedPath);
        }

        var lines = File.ReadAllLines(resolvedPath);
        int skip = offset.HasValue && offset.Value > 0 ? offset.Value - 1 : 0;
        int take = limit.HasValue && limit.Value > 0 ? limit.Value : lines.Length - skip;
        return string.Join(Environment.NewLine, lines.Skip(skip).Take(take));
    }

    private async Task WriteWorkspaceFileAsync(string workspacePath, string filePath, string content, int? workspaceId, int? taskId, ISandboxPathResolver? sandboxPathResolver, CancellationToken ct)
    {
        var resolvedPath = ResolveFilePath(workspacePath, filePath, workspaceId, taskId, sandboxPathResolver, forWrite: true);
        var dir = Path.GetDirectoryName(resolvedPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var editorConfig = await _editorConfigService.GetSettingsForPathAsync(resolvedPath, ct);
        string? existing = File.Exists(resolvedPath) ? await File.ReadAllTextAsync(resolvedPath, ct) : null;
        var bytes = _editorConfigFileApplier.ApplyFormattingAndEncode(content, editorConfig, existing, resolvedPath);
        await File.WriteAllBytesAsync(resolvedPath, bytes, ct);
    }
}
