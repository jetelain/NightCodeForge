using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using NightCodeForge.Agents.Memory;
using NightCodeForge.Agents.Tools.EditorConfig;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Execution;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Memory;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Infrastructure.Execution;

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
    private readonly IAgentMemoryWriteNotifier _memoryNotifier;
    private readonly ILogger<AcpHostRpcHandler> _logger;

    // Scoped per ACP session so concurrent sessions never share or leak terminal IDs.
    private readonly ConcurrentDictionary<(string SessionId, string TerminalId), (int ExitCode, string Output)> _terminals = new();

    public AcpHostRpcHandler(
        IExecutionEnvironmentProvider envProvider,
        IEditorConfigService editorConfigService,
        IEditorConfigFileApplier editorConfigFileApplier,
        IWorkspacePathResolver pathResolver,
        IAgentMemoryWriteNotifier memoryNotifier,
        ILogger<AcpHostRpcHandler> logger)
    {
        _memoryNotifier = memoryNotifier ?? throw new ArgumentNullException(nameof(memoryNotifier));
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

    /// <summary>
    /// A read-only session may only write <c>/workspace/memory/*.md</c>, and only when its scope mounts it with
    /// <see cref="VirtualMountEntry.AllowWriteInReadOnlyScope"/> (never for untrusted triage).
    /// </summary>
    private static bool IsReadOnlyMemoryWriteAllowed(string filePath, AcpHostRpcContext context)
    {
        if (context.SandboxPathResolver == null ||
            AgentMemoryPathClassifier.Classify(filePath) != AgentMemoryScope.LocalWorkspace)
        {
            return false;
        }

        return context.SandboxPathResolver.Scope.MountTable.Entries.Any(e =>
            !e.IsReadOnly &&
            e.AllowWriteInReadOnlyScope &&
            string.Equals(e.VirtualMountPath, AgentMemoryConstants.VirtualLocalMemoryPath, StringComparison.OrdinalIgnoreCase));
    }

    private static string ReadOnlyWriteDeniedMessage(string filePath, AcpHostRpcContext context) =>
        context.SandboxPathResolver?.Scope.MountTable.Entries.Any(e => !e.IsReadOnly && e.AllowWriteInReadOnlyScope) == true
            ? $"Access denied: Mount '{filePath}' is read-only. In read-only mode, only '{AgentMemoryConstants.VirtualLocalMemoryPath}/*.md' is writable."
            : "Access denied: Write operations are not permitted in a read-only agent session.";

    private async Task NotifyMemoryWriteAsync(
        AcpHostRpcContext context,
        string requestedPath,
        string resolvedPath,
        bool created,
        AgentMemoryScope scope,
        string? contentSha256,
        CancellationToken cancellationToken)
    {
        var workspaceId = context.Session.Workspace?.Id ?? 0;
        if (workspaceId <= 0 || scope == AgentMemoryScope.None)
        {
            return;
        }

        await _memoryNotifier.NotifyWriteAsync(
            workspaceId,
            context.Session.ForgeTask.Id,
            context.Session.StageId,
            context.SessionId,
            new AgentMemoryWriteEvent(
                requestedPath,
                resolvedPath,
                scope,
                created ? AgentMemoryChangeKind.Created : AgentMemoryChangeKind.Updated,
                contentSha256),
            cancellationToken);
    }

    private static (string SessionId, string TerminalId) TerminalKey(string sessionId, JsonNode? parameters)
        => (sessionId, (parameters?["terminalId"]?.ToString() ?? "").ToUpperInvariant());

    private AcpHostRpcResult ReadFile(JsonNode? parameters, AcpHostRpcContext context)
    {
        var filePath = parameters?["path"]?.ToString() ?? "";
        try
        {
            int? offset = parameters?["offset"]?.GetValue<int?>();
            int? limit = parameters?["limit"]?.GetValue<int?>();
            var content = ReadWorkspaceFile(
                context.LocalWorkspaceRoot,
                filePath,
                offset,
                limit,
                context.Session.Workspace?.Id,
                context.Session.ForgeTask.Id,
                context.Session.Workspace?.ExecutionMode,
                context.SandboxPathResolver);
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
        var filePath = parameters?["path"]?.ToString() ?? "";
        if (IsReadOnlySession(context) && !IsReadOnlyMemoryWriteAllowed(filePath, context))
        {
            return AcpHostRpcResult.Error(ReadOnlyWriteDeniedMessage(filePath, context));
        }

        var content = parameters?["content"]?.ToString() ?? "";
        try
        {
            var (resolvedPath, created, memoryScope, contentSha256) = await WriteWorkspaceFileAsync(
                context.LocalWorkspaceRoot,
                filePath,
                content,
                context.Session.Workspace?.Id,
                context.Session.ForgeTask.Id,
                context.Session.Workspace?.ExecutionMode,
                context.SandboxPathResolver,
                cancellationToken);
            await NotifyMemoryWriteAsync(context, filePath, resolvedPath, created, memoryScope, contentSha256, cancellationToken);
            return AcpHostRpcResult.Success(new { });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ACP fs/write_text_file failed for '{Path}'", filePath);
            return AcpHostRpcResult.Error(ex.Message);
        }
    }

    private string ResolveFilePath(
        string workspacePath,
        string filePath,
        int? workspaceId,
        int? taskId,
        ExecutionMode? executionMode,
        ISandboxPathResolver? sandboxPathResolver = null,
        bool forWrite = false)
    {
        if (sandboxPathResolver != null)
        {
            var sandboxResolved = sandboxPathResolver.ResolveHostPath(filePath, forWrite);
            RejectSharedMemorySymlink(workspacePath, filePath, sandboxResolved);
            return sandboxResolved;
        }

        if (AgentMemoryPathResolution.IsLocalMemoryPath(filePath, out _))
        {
            var memoryDir = ResolveLocalMemoryDirectory(workspaceId);
            var memoryFile = AgentMemoryPathResolution.ResolveLocalMemoryFile(filePath, workspaceId ?? 0, memoryDir);
            return memoryFile!;
        }

        var trimmed = (filePath ?? string.Empty).Trim();
        var normalized = trimmed.Replace('\\', '/');

        if (forWrite && !string.IsNullOrWhiteSpace(trimmed) &&
            (normalized.Equals("/workspace/skills", StringComparison.OrdinalIgnoreCase) ||
             normalized.StartsWith("/workspace/skills/", StringComparison.OrdinalIgnoreCase) ||
             normalized.Equals("/nightcodeforge", StringComparison.OrdinalIgnoreCase) ||
             normalized.StartsWith("/nightcodeforge/", StringComparison.OrdinalIgnoreCase)))
        {
            throw new UnauthorizedAccessException($"Access denied: Mount '{filePath}' is read-only.");
        }

        if ((normalized.Equals("/tmp", StringComparison.OrdinalIgnoreCase) ||
             normalized.StartsWith("/tmp/", StringComparison.OrdinalIgnoreCase)) &&
            !WorkspacePathResolver.TryResolveTmpSubPath(filePath, out _))
        {
            throw new UnauthorizedAccessException($"Access denied: Path '{filePath}' resolves outside the allowed workspace sandbox.");
        }

        var resolvedPath = ResolveRawHostFilePath(workspacePath, filePath ?? string.Empty, workspaceId, taskId, executionMode);
        ValidateWithinWorkspace(resolvedPath, filePath ?? string.Empty, workspacePath, workspaceId, taskId, executionMode);
        RejectSharedMemorySymlink(workspacePath, filePath ?? string.Empty, resolvedPath);
        return resolvedPath;
    }

    private string ResolveLocalMemoryDirectory(int? workspaceId)
    {
        if (!workspaceId.HasValue || workspaceId.Value <= 0)
        {
            throw new UnauthorizedAccessException("Access denied: Local memory requires a workspace.");
        }

        return _pathResolver.ToHostPath(_pathResolver.GetWorkspaceMemoryPath(workspaceId.Value));
    }

    /// <summary>A malicious repository symlink under <c>AGENTS.md</c> or <c>.agents/memory</c> must never exfiltrate host files.</summary>
    private static void RejectSharedMemorySymlink(string workspacePath, string requestedPath, string resolvedPath)
    {
        if (string.IsNullOrWhiteSpace(workspacePath) ||
            !SandboxPathResolver.IsPathInsideDirectory(resolvedPath, workspacePath))
        {
            return;
        }

        var isShared =
            AgentMemoryPathClassifier.Classify(requestedPath, workspacePath) == AgentMemoryScope.SharedRepository ||
            AgentMemoryPathClassifier.Classify(resolvedPath, workspacePath) == AgentMemoryScope.SharedRepository;
        if (isShared && AgentMemoryFileSystem.HasReparsePointInChain(workspacePath, resolvedPath))
        {
            throw new UnauthorizedAccessException($"Access denied: Path '{requestedPath}' traverses a symbolic link or reparse point inside agent memory.");
        }
    }

    private string ResolveRawHostFilePath(
        string workspacePath,
        string filePath,
        int? workspaceId,
        int? taskId,
        ExecutionMode? executionMode)
    {
        var effectiveTaskId = (taskId.HasValue && taskId.Value > 0) ? taskId.Value : (int?)null;

        if (workspaceId.HasValue && executionMode != ExecutionMode.BareHost)
        {
            var rootCrucibleDir = _pathResolver.ResolveCruciblePath(workspaceId.Value, string.Empty, effectiveTaskId);
            if (!string.IsNullOrWhiteSpace(rootCrucibleDir) && Directory.Exists(rootCrucibleDir))
            {
                var resolvedFromCrucibleRoot = _pathResolver.ResolveCruciblePath(workspaceId.Value, filePath, effectiveTaskId);
                if (!string.IsNullOrWhiteSpace(resolvedFromCrucibleRoot))
                {
                    return resolvedFromCrucibleRoot;
                }
            }

            if (WorkspacePathResolver.TryResolveTmpSubPath(filePath, out _))
            {
                var crucibleTmpDir = _pathResolver.ResolveCruciblePath(workspaceId.Value, "/tmp", effectiveTaskId);
                if (!string.IsNullOrWhiteSpace(crucibleTmpDir) && Directory.Exists(crucibleTmpDir))
                {
                    var resolvedTmp = _pathResolver.ResolveCruciblePath(workspaceId.Value, filePath, effectiveTaskId);
                    if (!string.IsNullOrWhiteSpace(resolvedTmp))
                    {
                        return resolvedTmp;
                    }
                }
            }

            var crucibleCandidate = _pathResolver.ResolveCruciblePath(workspaceId.Value, filePath, effectiveTaskId);
            if (!string.IsNullOrWhiteSpace(crucibleCandidate) &&
                (File.Exists(crucibleCandidate) || Directory.Exists(crucibleCandidate)))
            {
                return crucibleCandidate;
            }
        }

        if (!string.IsNullOrWhiteSpace(workspacePath))
        {
            var resolvedByRoot = _pathResolver.ResolveCruciblePath(workspacePath, filePath, effectiveTaskId);
            if (!string.IsNullOrWhiteSpace(resolvedByRoot))
            {
                return resolvedByRoot;
            }
        }

        return ResolveLocalWorkspaceCandidate(workspacePath, filePath, effectiveTaskId);
    }

    private static string ResolveLocalWorkspaceCandidate(string workspacePath, string filePath, int? taskId)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return string.IsNullOrWhiteSpace(workspacePath) ? workspacePath : NormalizeLocalPath(workspacePath);
        }

        var normalized = filePath.Trim().Replace('\\', '/');
        if (string.Equals(normalized, WorkspaceRoot, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, WorkspaceName, StringComparison.OrdinalIgnoreCase))
        {
            return NormalizeLocalPath(workspacePath);
        }

        if (normalized.StartsWith(WorkspaceRootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var rel = normalized[WorkspaceRootPrefix.Length..].TrimStart('/');
            if (taskId.HasValue)
            {
                var taskPrefix = $"task-{taskId.Value}/";
                if (rel.StartsWith(taskPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    rel = rel[taskPrefix.Length..];
                }
            }
            return NormalizeLocalPath(Path.Combine(workspacePath, rel));
        }

        if (normalized.StartsWith(WorkspaceNamePrefix, StringComparison.OrdinalIgnoreCase))
        {
            var rel = normalized[WorkspaceNamePrefix.Length..].TrimStart('/');
            return NormalizeLocalPath(Path.Combine(workspacePath, rel));
        }

        if (WorkspacePathResolver.TryResolveTmpSubPath(filePath, out var tmpSubPath))
        {
            var tmpBase = Path.Combine(workspacePath, "tmp");
            return string.IsNullOrEmpty(tmpSubPath)
                ? NormalizeLocalPath(tmpBase)
                : NormalizeLocalPath(Path.Combine(tmpBase, tmpSubPath));
        }

        if (filePath.StartsWith(@"\\") || (filePath.Length >= 2 && filePath[1] == ':') || Path.IsPathRooted(filePath))
        {
            return NormalizeLocalPath(filePath);
        }

        return NormalizeLocalPath(Path.Combine(workspacePath, filePath));
    }

    private void ValidateWithinWorkspace(
        string fullPath,
        string requestedPath,
        string workspacePath,
        int? workspaceId,
        int? taskId,
        ExecutionMode? executionMode)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            throw new UnauthorizedAccessException("Access denied: Empty path.");
        }

        var effectiveTaskId = (taskId.HasValue && taskId.Value > 0) ? taskId.Value : (int?)null;
        var allowedRoots = new List<string>();

        if (!string.IsNullOrWhiteSpace(workspacePath))
        {
            allowedRoots.Add(workspacePath);
            allowedRoots.Add(Path.Combine(workspacePath, "tmp"));

            var rootTmp = _pathResolver.ResolveCruciblePath(workspacePath, "/tmp", effectiveTaskId);
            if (!string.IsNullOrWhiteSpace(rootTmp))
            {
                allowedRoots.Add(rootTmp);
            }

            var rootClone = _pathResolver.ResolveCruciblePath(workspacePath, "/workspace/clone", effectiveTaskId);
            if (!string.IsNullOrWhiteSpace(rootClone))
            {
                allowedRoots.Add(rootClone);
            }
        }

        if (workspaceId.HasValue && executionMode != ExecutionMode.BareHost)
        {
            var crucibleRoot = _pathResolver.ResolveCruciblePath(workspaceId.Value, string.Empty, effectiveTaskId);
            if (!string.IsNullOrWhiteSpace(crucibleRoot))
            {
                allowedRoots.Add(crucibleRoot);
            }

            var crucibleTmp = _pathResolver.ResolveCruciblePath(workspaceId.Value, "/tmp", effectiveTaskId);
            if (!string.IsNullOrWhiteSpace(crucibleTmp))
            {
                allowedRoots.Add(crucibleTmp);
            }

            var crucibleClone = _pathResolver.ResolveCruciblePath(workspaceId.Value, "/workspace/clone", effectiveTaskId);
            if (!string.IsNullOrWhiteSpace(crucibleClone))
            {
                allowedRoots.Add(crucibleClone);
            }
        }

        if (allowedRoots.Count == 0)
        {
            throw new UnauthorizedAccessException($"Access denied: Path '{requestedPath}' has no workspace boundary.");
        }

        var normFullPath = NormalizeForSandbox(fullPath);
        foreach (var root in allowedRoots)
        {
            var normRoot = NormalizeForSandbox(root);
            if (!string.IsNullOrEmpty(normRoot) &&
                (normFullPath.Equals(normRoot, StringComparison.OrdinalIgnoreCase) ||
                 normFullPath.StartsWith(normRoot + "/", StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }
        }

        _logger.LogWarning("AcpHostRpcHandler: Path traversal outside workspace boundary detected: '{Path}' (resolved: '{FullPath}')", requestedPath, fullPath);
        throw new UnauthorizedAccessException($"Access denied: Path '{requestedPath}' resolves outside the allowed workspace sandbox.");
    }

    private static string NormalizeLocalPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    private static string NormalizeForSandbox(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var normalized = path.Replace('\\', '/').TrimEnd('/');
        if (Path.IsPathRooted(path) && !normalized.StartsWith("//") && !normalized.StartsWith(@"\\") &&
            !normalized.StartsWith("/workspace", StringComparison.OrdinalIgnoreCase) &&
            !normalized.StartsWith("/clone", StringComparison.OrdinalIgnoreCase) &&
            !normalized.StartsWith("/tmp", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                normalized = Path.GetFullPath(path).Replace('\\', '/').TrimEnd('/');
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Retain normalized representation on non-standard virtual paths.
            }
        }

        return normalized;
    }

    private string ReadWorkspaceFile(string workspacePath, string filePath, int? offset, int? limit, int? workspaceId, int? taskId, ExecutionMode? executionMode, ISandboxPathResolver? sandboxPathResolver = null)
    {
        var resolvedPath = ResolveFilePath(workspacePath, filePath, workspaceId, taskId, executionMode, sandboxPathResolver, forWrite: false);
        if (!File.Exists(resolvedPath))
        {
            throw new FileNotFoundException($"File not found: {filePath}", filePath);
        }

        using var stream = new FileStream(resolvedPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        if (!offset.HasValue && !limit.HasValue)
        {
            return reader.ReadToEnd();
        }

        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            lines.Add(line);
        }

        int skip = offset.HasValue && offset.Value > 0 ? offset.Value - 1 : 0;
        int take = limit.HasValue && limit.Value > 0 ? limit.Value : Math.Max(0, lines.Count - skip);
        return string.Join(Environment.NewLine, lines.Skip(skip).Take(take));
    }

    private async Task<(string ResolvedPath, bool Created, AgentMemoryScope MemoryScope, string? ContentSha256)> WriteWorkspaceFileAsync(
        string workspacePath,
        string filePath,
        string content,
        int? workspaceId,
        int? taskId,
        ExecutionMode? executionMode,
        ISandboxPathResolver? sandboxPathResolver,
        CancellationToken ct)
    {
        var resolvedPath = ResolveFilePath(workspacePath, filePath, workspaceId, taskId, executionMode, sandboxPathResolver, forWrite: true);
        var memoryScope = AgentMemoryPathClassifier.Classify(filePath, workspacePath);
        if (memoryScope == AgentMemoryScope.None)
        {
            memoryScope = AgentMemoryPathClassifier.Classify(resolvedPath, workspacePath);
        }

        var isMemoryWrite = memoryScope != AgentMemoryScope.None && workspaceId is > 0;
        var dir = Path.GetDirectoryName(resolvedPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using var memoryLock = isMemoryWrite
            ? await AgentMemoryFileWriter.AcquireLockAsync(workspaceId!.Value, ct)
            : null;

        if (isMemoryWrite && AgentMemoryReparseGuard.IsReparsePoint(resolvedPath))
        {
            throw new UnauthorizedAccessException($"Access denied: Path '{filePath}' traverses a symbolic link or reparse point inside agent memory.");
        }

        var editorConfig = await _editorConfigService.GetSettingsForPathAsync(resolvedPath, ct);
        string? existing = File.Exists(resolvedPath)
            ? (isMemoryWrite
                ? await AgentMemoryFileSystem.ReadAllTextSharedAsync(resolvedPath, ct)
                : await File.ReadAllTextAsync(resolvedPath, ct))
            : null;
        var bytes = _editorConfigFileApplier.ApplyFormattingAndEncode(content, editorConfig, existing, resolvedPath);
        if (isMemoryWrite)
        {
            var created = await AgentMemoryFileWriter.WriteAtomicAsync(resolvedPath, bytes, ct);
            var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
            return (resolvedPath, created, memoryScope, sha);
        }

        await File.WriteAllBytesAsync(resolvedPath, bytes, ct);
        return (resolvedPath, existing == null, memoryScope, null);
    }
}
