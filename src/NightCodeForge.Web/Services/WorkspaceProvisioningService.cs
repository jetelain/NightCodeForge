using System.Runtime.InteropServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Services;
using NightCodeForge.Web.Models;

namespace NightCodeForge.Web.Services;

/// <summary>
/// Orchestrates workspace onboarding: filesystem probing, Git repository discovery/initialization,
/// database record persistence, isolated Ext4 clone mirroring, directory scaffolding,
/// and agent profile configuration.
/// </summary>
public class WorkspaceProvisioningService : IWorkspaceProvisioningService
{
    private readonly IWorkspaceRepository _workspaceRepository;
    private readonly IGitMirrorSyncService _gitMirrorSyncService;
    private readonly IWorkspacePathResolver _pathResolver;
    private readonly IWslMetadataService _wslMetadataService;
    private readonly IGitCliRunner _gitCliRunner;
    private readonly ITaskMarkdownSyncService _markdownSyncService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<WorkspaceProvisioningService> _logger;

    public WorkspaceProvisioningService(
        IWorkspaceRepository workspaceRepository,
        IGitMirrorSyncService gitMirrorSyncService,
        IWorkspacePathResolver pathResolver,
        IWslMetadataService wslMetadataService,
        IGitCliRunner gitCliRunner,
        ITaskMarkdownSyncService markdownSyncService,
        IConfiguration configuration,
        ILogger<WorkspaceProvisioningService>? logger = null)
    {
        _workspaceRepository = workspaceRepository ?? throw new ArgumentNullException(nameof(workspaceRepository));
        _gitMirrorSyncService = gitMirrorSyncService ?? throw new ArgumentNullException(nameof(gitMirrorSyncService));
        _pathResolver = pathResolver ?? throw new ArgumentNullException(nameof(pathResolver));
        _wslMetadataService = wslMetadataService ?? throw new ArgumentNullException(nameof(wslMetadataService));
        _gitCliRunner = gitCliRunner ?? throw new ArgumentNullException(nameof(gitCliRunner));
        _markdownSyncService = markdownSyncService ?? throw new ArgumentNullException(nameof(markdownSyncService));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? NullLogger<WorkspaceProvisioningService>.Instance;
    }

    /// <inheritdoc />
    public async Task<RepositoryProbeResult> ProbeRepositoryAsync(string directoryPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            return new RepositoryProbeResult(
                Exists: false,
                IsGitRepository: false,
                SuggestedName: null,
                AvailableBranches: Array.Empty<string>(),
                DetectedBaseBranch: "main",
                DetectedTechStack: "Unknown",
                HasAgentsMarkdown: false,
                WarningMessage: "Please specify a directory path.");
        }

        var trimmedPath = directoryPath.Trim();
        if (!Directory.Exists(trimmedPath))
        {
            return new RepositoryProbeResult(
                Exists: false,
                IsGitRepository: false,
                SuggestedName: null,
                AvailableBranches: Array.Empty<string>(),
                DetectedBaseBranch: "main",
                DetectedTechStack: "Unknown",
                HasAgentsMarkdown: false,
                WarningMessage: $"Directory '{trimmedPath}' does not exist.");
        }

        string suggestedName;
        try
        {
            var dirInfo = new DirectoryInfo(trimmedPath);
            suggestedName = dirInfo.Name;
        }
        catch
        {
            suggestedName = "workspace";
        }

        var isGit = CheckIfGitRepository(trimmedPath);
        if (!isGit)
        {
            try
            {
                var checkResult = await _gitCliRunner.ExecuteAsync(trimmedPath, ["rev-parse", "--is-inside-work-tree"], cancellationToken: cancellationToken);
                if (checkResult.ExitCode == 0 && checkResult.StandardOutput.Trim().Equals("true", StringComparison.OrdinalIgnoreCase))
                {
                    isGit = true;
                }
            }
            catch
            {
                isGit = false;
            }
        }

        var techStack = DetectTechStack(trimmedPath);
        var hasGuidelines = CheckHasAgentGuidelines(trimmedPath);

        if (!isGit)
        {
            return new RepositoryProbeResult(
                Exists: true,
                IsGitRepository: false,
                SuggestedName: suggestedName,
                AvailableBranches: Array.Empty<string>(),
                DetectedBaseBranch: "main",
                DetectedTechStack: techStack,
                HasAgentsMarkdown: hasGuidelines,
                WarningMessage: "Directory exists but is not a Git repository. You can initialize one below.");
        }

        // Git repository detected: probe branches
        var branches = await ProbeBranchesAsync(trimmedPath, cancellationToken);
        var detectedBaseBranch = ResolveDefaultBaseBranch(branches);

        // Probe cross-platform line-ending hygiene (.gitattributes, .editorconfig, core.autocrlf)
        var gitattributesPath = Path.Combine(trimmedPath, ".gitattributes");
        bool hasGitAttributes = File.Exists(gitattributesPath);
        bool hasLfGitAttributes = false;
        if (hasGitAttributes)
        {
            try
            {
                var content = await File.ReadAllTextAsync(gitattributesPath, cancellationToken);
                hasLfGitAttributes = content.Contains("eol=lf", StringComparison.OrdinalIgnoreCase);
            }
            catch { }
        }

        var editorConfigPath = Path.Combine(trimmedPath, ".editorconfig");
        bool hasEditorConfig = File.Exists(editorConfigPath);
        bool hasLfEditorConfig = false;
        if (hasEditorConfig)
        {
            try
            {
                var content = await File.ReadAllTextAsync(editorConfigPath, cancellationToken);
                hasLfEditorConfig = content.Contains("end_of_line = lf", StringComparison.OrdinalIgnoreCase) ||
                                    content.Contains("end_of_line=lf", StringComparison.OrdinalIgnoreCase);
            }
            catch { }
        }

        string? coreAutoCrlf = null;
        try
        {
            var configResult = await _gitCliRunner.ExecuteAsync(trimmedPath, ["config", "--get", "core.autocrlf"], cancellationToken: cancellationToken);
            if (configResult.ExitCode == 0)
            {
                coreAutoCrlf = configResult.StandardOutput.Trim();
            }
        }
        catch { }

        string? lineEndingRecommendation = null;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var missingItems = new List<string>();
            if (!hasLfGitAttributes) missingItems.Add(".gitattributes (* text=auto eol=lf)");
            if (!hasLfEditorConfig) missingItems.Add(".editorconfig (end_of_line = lf)");
            if (!string.Equals(coreAutoCrlf, "false", StringComparison.OrdinalIgnoreCase)) missingItems.Add("git config core.autocrlf false");

            if (missingItems.Count > 0)
            {
                lineEndingRecommendation = $"Cross-platform hygiene recommended: configure {string.Join(", ", missingItems)} to avoid CRLF/LF push conflicts between Windows and WSL2.";
            }
        }

        return new RepositoryProbeResult(
            Exists: true,
            IsGitRepository: true,
            SuggestedName: suggestedName,
            AvailableBranches: branches,
            DetectedBaseBranch: detectedBaseBranch,
            DetectedTechStack: techStack,
            HasAgentsMarkdown: hasGuidelines,
            WarningMessage: null,
            HasGitAttributes: hasGitAttributes,
            HasLfGitAttributes: hasLfGitAttributes,
            HasEditorConfig: hasEditorConfig,
            HasLfEditorConfig: hasLfEditorConfig,
            CoreAutoCrlf: coreAutoCrlf,
            LineEndingRecommendation: lineEndingRecommendation);
    }

    /// <inheritdoc />
    public async Task<bool> InitializeGitRepositoryAsync(string directoryPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        var trimmedPath = directoryPath.Trim();

        try
        {
            if (!Directory.Exists(trimmedPath))
            {
                Directory.CreateDirectory(trimmedPath);
            }

            try
            {
                var result = await _gitCliRunner.ExecuteAsync(trimmedPath, ["init", "-b", "main"], cancellationToken: cancellationToken);
                return result.ExitCode == 0;
            }
            catch
            {
                var fallback = await _gitCliRunner.ExecuteAsync(trimmedPath, ["init"], cancellationToken: cancellationToken);
                return fallback.ExitCode == 0;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize Git repository in '{DirectoryPath}'", trimmedPath);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<Workspace> ProvisionWorkspaceAsync(
        WorkspaceCreationModel model,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(model.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(model.RepositoryPath);

        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var defaultWslDistro = isWindows ? _wslMetadataService.GetDefaultDistributionName() : null;
        var wslDistro = !string.IsNullOrWhiteSpace(model.WslDistribution) ? model.WslDistribution : defaultWslDistro;

        progress?.Report("Creating workspace database record...");

        var workspace = new Workspace
        {
            Name = model.Name.Trim(),
            RepositoryPath = Path.GetFullPath(model.RepositoryPath.Trim()),
            DefaultBaseBranch = string.IsNullOrWhiteSpace(model.DefaultBaseBranch) ? "main" : model.DefaultBaseBranch.Trim(),
            ExecutionMode = model.ExecutionMode,
            ContainerImage = model.ContainerImage,
            WslDistribution = wslDistro,
            UseWslForContainers = isWindows,
            BuildTimeoutSeconds = model.BuildTimeoutSeconds,
            TestTimeoutSeconds = model.TestTimeoutSeconds,
            CreatedAt = DateTime.UtcNow
        };

        workspace = await _workspaceRepository.CreateAsync(workspace, cancellationToken);

        // 2. Ext4 Mirror Setup
        try
        {
            progress?.Report("Configuring host repository and initializing Ext4 mirror...");
            var targetMirrorDir = _pathResolver.GetAgentWorkingClonePath(workspace.Id);
            var ext4Path = await _gitMirrorSyncService.InitializeMirrorAsync(workspace, targetMirrorDir, cancellationToken);
            workspace.Ext4MirrorPath = ext4Path;
            await _workspaceRepository.UpdateAsync(workspace, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ext4 mirror setup encountered an issue for workspace {WorkspaceId}. Continuing provisioning.", workspace.Id);
        }

        // 3. Directory Scaffolding & GitIgnore
        progress?.Report("Scaffolding docs/tasks/ and ensuring .gitignore entries...");
        try
        {
            var docsTasksPath = Path.Combine(workspace.RepositoryPath, "docs", "tasks");
            if (!Directory.Exists(docsTasksPath))
            {
                Directory.CreateDirectory(docsTasksPath);
            }

            var docsTranscriptsPath = Path.Combine(workspace.RepositoryPath, "docs", "transcripts");
            if (!Directory.Exists(docsTranscriptsPath))
            {
                Directory.CreateDirectory(docsTranscriptsPath);
            }

            await EnsureGitIgnoreEntriesAsync(workspace.RepositoryPath, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create docs/tasks directory or update .gitignore in {RepositoryPath}", workspace.RepositoryPath);
        }

        // 4. Markdown Sync Watcher Registration
        progress?.Report("Registering task markdown sync watcher...");
        try
        {
            _markdownSyncService.StartWatching(workspace.Id, workspace.RepositoryPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to start markdown sync watcher for workspace {WorkspaceId}", workspace.Id);
        }

        // 5. Agent Profiles Configuration
        progress?.Report("Configuring agent profiles...");
        try
        {
            var defaultModel = _configuration["LlmProvider:ModelId"] ?? "Qwen3.8-27B:latest";

            if (model.UseDedicatedAgentProfiles)
            {
                var dedicatedModel = !string.IsNullOrWhiteSpace(model.DedicatedModelName)
                    ? model.DedicatedModelName.Trim()
                    : defaultModel;

                var standardProfiles = AgentProfile.CreateStandardGlobalProfiles(dedicatedModel);
                var dedicatedProfiles = standardProfiles.Select(p => new AgentProfile
                {
                    Id = $"ws-{workspace.Id}:{p.Id}",
                    WorkspaceId = workspace.Id,
                    Name = $"{p.Name} ({workspace.Name})",
                    HarnessId = p.HarnessId,
                    ModelName = dedicatedModel,
                    Mode = p.Mode,
                    SecurityTier = p.SecurityTier,
                    PermittedTools = p.PermittedTools,
                    OptimizationFlags = p.OptimizationFlags,
                    Temperature = p.Temperature,
                    ThinkingEffort = p.ThinkingEffort,
                    SystemPrompt = p.SystemPrompt,
                    StageDirectives = new Dictionary<string, string> { [p.Id] = "Dedicated workspace override" }
                }).ToList();

                workspace.AgentProfiles = dedicatedProfiles;
                await _workspaceRepository.UpdateAsync(workspace, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Agent profiles configuration encountered an issue for workspace {WorkspaceId}. Continuing provisioning.", workspace.Id);
        }

        progress?.Report("Workspace provisioning complete!");
        return workspace;
    }

    private static bool CheckIfGitRepository(string directoryPath)
    {
        try
        {
            var gitDir = Path.Combine(directoryPath, ".git");
            return Directory.Exists(gitDir) || File.Exists(gitDir);
        }
        catch
        {
            return false;
        }
    }

    private async Task<IReadOnlyList<string>> ProbeBranchesAsync(string directoryPath, CancellationToken cancellationToken)
    {
        var branches = new List<string>();
        try
        {
            var branchResult = await _gitCliRunner.ExecuteAsync(
                directoryPath,
                ["branch", "--format=%(refname:short)"],
                cancellationToken: cancellationToken);

            if (branchResult.ExitCode == 0 && !string.IsNullOrWhiteSpace(branchResult.StandardOutput))
            {
                var lines = branchResult.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (var line in lines)
                {
                    var b = line.Trim('*', ' ');
                    if (!string.IsNullOrWhiteSpace(b) && !branches.Contains(b))
                    {
                        branches.Add(b);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to query Git branches in '{DirectoryPath}'", directoryPath);
        }

        return branches;
    }

    private static string ResolveDefaultBaseBranch(IReadOnlyList<string> branches)
    {
        if (branches.Count == 0)
        {
            return "main";
        }

        if (branches.Contains("main", StringComparer.OrdinalIgnoreCase))
            return "main";

        if (branches.Contains("master", StringComparer.OrdinalIgnoreCase))
            return "master";

        if (branches.Contains("develop", StringComparer.OrdinalIgnoreCase))
            return "develop";

        return branches[0];
    }

    private static string DetectTechStack(string directoryPath)
    {
        try
        {
            if (Directory.EnumerateFiles(directoryPath, "*.sln").Any() ||
                Directory.EnumerateFiles(directoryPath, "*.csproj").Any())
            {
                return ".NET / C#";
            }

            foreach (var subDir in Directory.EnumerateDirectories(directoryPath))
            {
                var name = Path.GetFileName(subDir);
                if (name.StartsWith('.') ||
                    name.Equals("node_modules", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("obj", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (Directory.EnumerateFiles(subDir, "*.csproj").Any() || Directory.EnumerateFiles(subDir, "*.sln").Any())
                {
                    return ".NET / C#";
                }
            }

            if (File.Exists(Path.Combine(directoryPath, "package.json")))
                return "Node.js / TypeScript";

            if (File.Exists(Path.Combine(directoryPath, "Cargo.toml")))
                return "Rust";

            if (File.Exists(Path.Combine(directoryPath, "pyproject.toml")) ||
                File.Exists(Path.Combine(directoryPath, "requirements.txt")))
                return "Python";

            if (File.Exists(Path.Combine(directoryPath, "go.mod")))
                return "Go";
        }
        catch
        {
            // Ignore access exceptions
        }

        return "General / Polyglot";
    }

    private static bool CheckHasAgentGuidelines(string directoryPath)
    {
        try
        {
            return File.Exists(Path.Combine(directoryPath, "AGENTS.md")) ||
                   File.Exists(Path.Combine(directoryPath, ".agents", "AGENTS.md")) ||
                   File.Exists(Path.Combine(directoryPath, "CLAUDE.md"));
        }
        catch
        {
            return false;
        }
    }

    private async Task EnsureGitIgnoreEntriesAsync(string repoPath, CancellationToken cancellationToken)
    {
        try
        {
            var gitignorePath = Path.Combine(repoPath, ".gitignore");
            var requiredEntries = new[] { "docs/tasks/", "docs/transcripts/" };

            if (File.Exists(gitignorePath))
            {
                var content = await File.ReadAllTextAsync(gitignorePath, cancellationToken);
                var lines = content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var missing = requiredEntries.Where(e => !lines.Contains(e) && !lines.Contains(e.TrimEnd('/'))).ToList();

                if (missing.Count > 0)
                {
                    var textToAppend = "\n# NightCodeForge task artifacts and transcripts\n" + string.Join("\n", missing) + "\n";
                    await File.AppendAllTextAsync(gitignorePath, textToAppend, cancellationToken);
                    _logger.LogInformation("Appended {Count} NightCodeForge entries to '{GitIgnorePath}'", missing.Count, gitignorePath);
                }
            }
            else
            {
                var initialContent = "# NightCodeForge task artifacts and transcripts\ndocs/tasks/\ndocs/transcripts/\n";
                await File.WriteAllTextAsync(gitignorePath, initialContent, cancellationToken);
                _logger.LogInformation("Created '{GitIgnorePath}' with NightCodeForge entries", gitignorePath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to ensure .gitignore entries in '{RepoPath}'", repoPath);
        }
    }
}
