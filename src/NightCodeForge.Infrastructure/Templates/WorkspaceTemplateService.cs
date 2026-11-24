using System.Security;
using Microsoft.Extensions.Logging;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Security;
using NightCodeForge.Core.Services;

namespace NightCodeForge.Infrastructure.Templates;

/// <summary>
/// Orchestrates from-scratch workspace scaffolding using native CLI recipes, repository hygiene generation,
/// baseline Git initialization, and optional remote forge publishing.
/// </summary>
public class WorkspaceTemplateService : IWorkspaceTemplateService
{
    private readonly IProcessRunner _processRunner;
    private readonly IGitCliRunner _gitCliRunner;
    private readonly IVcsHostingProviderRegistry _vcsProviderRegistry;
    private readonly ILocalRepositoryDiscoveryService _localDiscoveryService;
    private readonly ILogger<WorkspaceTemplateService> _logger;

    public WorkspaceTemplateService(
        IProcessRunner processRunner,
        IGitCliRunner gitCliRunner,
        IVcsHostingProviderRegistry vcsProviderRegistry,
        ILocalRepositoryDiscoveryService localDiscoveryService,
        ILogger<WorkspaceTemplateService> logger)
    {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _gitCliRunner = gitCliRunner ?? throw new ArgumentNullException(nameof(gitCliRunner));
        _vcsProviderRegistry = vcsProviderRegistry ?? throw new ArgumentNullException(nameof(vcsProviderRegistry));
        _localDiscoveryService = localDiscoveryService ?? throw new ArgumentNullException(nameof(localDiscoveryService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public IReadOnlyList<WorkspaceTemplateDefinition> GetAvailableTemplates() =>
        WorkspaceTemplateCatalog.BuiltInTemplates;

    /// <inheritdoc />
    public WorkspaceTemplateDefinition? GetTemplate(string templateId)
    {
        if (string.IsNullOrWhiteSpace(templateId))
        {
            return null;
        }

        return WorkspaceTemplateCatalog.BuiltInTemplates.FirstOrDefault(
            t => string.Equals(t.Id, templateId.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <inheritdoc />
    public async Task<TemplateScaffoldingResult> ScaffoldWorkspaceAsync(
        TemplateScaffoldingRequest request,
        IProgress<TemplateScaffoldingProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var template = GetTemplate(request.TemplateId);
        if (template is null)
        {
            throw new WorkspaceTemplateExecutionException(
                request.TemplateId,
                failedCommand: null,
                exitCode: null,
                standardError: null,
                message: $"Workspace template '{request.TemplateId}' was not found.");
        }

        var nameValidation = WorkspaceTemplateProjectNameValidator.Validate(request.ProjectName);
        if (!nameValidation.IsValid)
        {
            throw new WorkspaceTemplateExecutionException(
                template.Id,
                failedCommand: null,
                exitCode: null,
                standardError: null,
                message: nameValidation.ErrorMessage ?? "Invalid project name.");
        }

        var projectName = request.ProjectName.Trim();
        var fullTargetPath = ValidateAndResolveTargetPath(template.Id, request.TargetDirectoryPath);

        string? effectiveRemoteRepoName = null;
        if (request.PublishToRemoteForge)
        {
            if (!request.InitializeGit)
            {
                throw new WorkspaceTemplateExecutionException(
                    template.Id,
                    failedCommand: null,
                    exitCode: null,
                    standardError: null,
                    message: "Cannot publish to a remote forge without initializing Git.");
            }

            if (!request.RemoteProviderType.HasValue)
            {
                throw new VcsHostingProviderException("A remote VCS hosting provider must be selected when publishing.");
            }

            effectiveRemoteRepoName = string.IsNullOrWhiteSpace(request.RemoteRepositoryName)
                ? projectName
                : request.RemoteRepositoryName.Trim();

            var repoNameValidation = WorkspaceTemplateProjectNameValidator.ValidateRepositoryName(effectiveRemoteRepoName);
            if (!repoNameValidation.IsValid)
            {
                throw new VcsHostingProviderException(
                    request.RemoteProviderType.Value,
                    repoNameValidation.ErrorMessage ?? "Invalid remote repository name.");
            }
        }

        Directory.CreateDirectory(fullTargetPath);

        // 1. Execute native CLI recipe steps
        var commands = template.BootstrapCommands;
        for (int i = 0; i < commands.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var command = commands[i];
            var args = WorkspaceTemplateCatalog.RenderArguments(command, projectName);
            var workingDir = ResolveCommandWorkingDirectory(template.Id, fullTargetPath, command.RelativeWorkingDirectory);

            var commandDisplay = $"{command.ToolExecutable} {string.Join(' ', args)}";
            var pct = 10 + (int)Math.Round(((i + 1d) / Math.Max(1, commands.Count)) * 55d);
            progress?.Report(new TemplateScaffoldingProgress(
                CurrentStep: $"Running '{commandDisplay}'...",
                Percentage: pct,
                IsCompleted: false,
                ErrorMessage: null,
                LocalScaffoldCompleted: false));

            _logger.LogInformation("Executing template step {Step}/{Total} for '{TemplateId}': {Command}",
                i + 1, commands.Count, template.Id, commandDisplay);

            var result = await _processRunner.RunAsync(
                new ProcessStartSpec
                {
                    FileName = command.ToolExecutable,
                    ArgumentList = args,
                    WorkingDirectory = workingDir,
                    TimeoutSeconds = WorkspaceTemplateConstants.DefaultCliCommandTimeoutSeconds,
                    OnStandardOutput = line => _logger.LogDebug("[{Tool} stdout] {Line}", command.ToolExecutable, line),
                    OnStandardError = line => _logger.LogWarning("[{Tool} stderr] {Line}", command.ToolExecutable, line)
                },
                cancellationToken).ConfigureAwait(false);

            if (result.ExitCode != 0)
            {
                var errorOutput = string.IsNullOrWhiteSpace(result.StandardError)
                    ? result.StandardOutput
                    : result.StandardError;
                var message = $"Template command '{commandDisplay}' failed with exit code {result.ExitCode}. {errorOutput}".Trim();

                _logger.LogError("Template scaffolding failed for '{TemplateId}' on '{Command}' (exit {ExitCode}): {StdErr}",
                    template.Id, commandDisplay, result.ExitCode, errorOutput);

                throw new WorkspaceTemplateExecutionException(
                    template.Id,
                    commandDisplay,
                    result.ExitCode,
                    errorOutput,
                    message);
            }
        }

        // 2. Write canonical hygiene files & docs/
        progress?.Report(new TemplateScaffoldingProgress(
            CurrentStep: "Writing repository hygiene files (.gitignore, .editorconfig, .gitattributes, docs/)...",
            Percentage: 70,
            IsCompleted: false,
            ErrorMessage: null,
            LocalScaffoldCompleted: false));

        await WorkspaceHygieneWriter.WriteAsync(fullTargetPath, projectName, template, cancellationToken).ConfigureAwait(false);

        // 3. Initialize Git and create clean baseline commit
        if (request.InitializeGit)
        {
            progress?.Report(new TemplateScaffoldingProgress(
                CurrentStep: "Initializing Git repository and creating baseline commit...",
                Percentage: 80,
                IsCompleted: false,
                ErrorMessage: null,
                LocalScaffoldCompleted: false));

            await InitializeGitAndCommitAsync(fullTargetPath, template, cancellationToken).ConfigureAwait(false);
        }

        var localScaffoldCompleted = true;

        progress?.Report(new TemplateScaffoldingProgress(
            CurrentStep: "Local template scaffolding and Git baseline complete.",
            Percentage: request.PublishToRemoteForge ? 85 : 95,
            IsCompleted: false,
            ErrorMessage: null,
            LocalScaffoldCompleted: true));

        // 4. Optional Remote Forge Publishing & discovery cache refresh
        VcsRepositorySummary? publishedRepo = null;
        var discoveryRefreshed = false;
        try
        {
            if (request.PublishToRemoteForge)
            {
                publishedRepo = await PublishToRemoteForgeAsync(
                    fullTargetPath,
                    request.RemoteProviderType!.Value,
                    effectiveRemoteRepoName!,
                    request.RemoteDescription,
                    request.RemoteIsPrivate,
                    progress,
                    cancellationToken).ConfigureAwait(false);
                discoveryRefreshed = true;
            }
        }
        finally
        {
            if (localScaffoldCompleted && !discoveryRefreshed && !cancellationToken.IsCancellationRequested)
            {
                await TryRefreshLocalDiscoveryAsync(fullTargetPath, cancellationToken).ConfigureAwait(false);
            }
        }

        progress?.Report(new TemplateScaffoldingProgress(
            CurrentStep: "Template scaffolding complete.",
            Percentage: 100,
            IsCompleted: true,
            ErrorMessage: null,
            LocalScaffoldCompleted: true));

        return new TemplateScaffoldingResult(
            TargetDirectoryPath: fullTargetPath,
            TemplateId: template.Id,
            ProjectName: projectName,
            LocalScaffoldCompleted: true,
            GitInitialized: request.InitializeGit,
            PublishedRepository: publishedRepo);
    }

    /// <inheritdoc />
    public async Task<VcsRepositorySummary> PublishToRemoteForgeAsync(
        string targetDirectoryPath,
        VcsHostingProviderType providerType,
        string repositoryName,
        string? description,
        bool isPrivate,
        IProgress<TemplateScaffoldingProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectoryPath);

        var fullTargetPath = Path.GetFullPath(targetDirectoryPath.Trim());
        if (!Directory.Exists(fullTargetPath))
        {
            throw new WorkspaceTemplateExecutionException(
                $"Cannot publish to remote forge: directory '{fullTargetPath}' does not exist.");
        }

        var repoValidation = WorkspaceTemplateProjectNameValidator.ValidateRepositoryName(repositoryName);
        if (!repoValidation.IsValid)
        {
            throw new VcsHostingProviderException(
                providerType,
                repoValidation.ErrorMessage ?? "Invalid remote repository name.");
        }

        var provider = _vcsProviderRegistry.GetProvider(providerType);
        if (provider is null)
        {
            throw new VcsHostingProviderException(
                providerType,
                $"VCS hosting provider '{providerType}' is not registered.");
        }

        if (!await provider.IsAuthenticatedAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new VcsHostingProviderException(
                providerType,
                $"{provider.DisplayName} is not authenticated. Please connect your {provider.DisplayName} account in Settings.");
        }

        var authHeader = await provider.GetGitHttpAuthHeaderAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(authHeader))
        {
            throw new VcsHostingProviderException(
                providerType,
                $"Unable to obtain ephemeral Git HTTP credentials for {provider.DisplayName}.");
        }

        progress?.Report(new TemplateScaffoldingProgress(
            CurrentStep: $"Creating {provider.DisplayName} repository '{repositoryName.Trim()}'...",
            Percentage: 90,
            IsCompleted: false,
            ErrorMessage: null,
            LocalScaffoldCompleted: true));

        var createdRepo = await provider.CreateRepositoryAsync(
            repositoryName.Trim(),
            string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            isPrivate,
            cancellationToken).ConfigureAwait(false);

        var trimmedCloneUrl = createdRepo.HttpsCloneUrl?.Trim();
        if (string.IsNullOrWhiteSpace(trimmedCloneUrl) ||
            !Uri.TryCreate(trimmedCloneUrl, UriKind.Absolute, out var cloneUri) ||
            (cloneUri.Scheme != Uri.UriSchemeHttps && cloneUri.Scheme != Uri.UriSchemeHttp))
        {
            throw new VcsHostingProviderException(
                providerType,
                $"{provider.DisplayName} created repository '{createdRepo.FullName}' without a valid HTTP/HTTPS clone URL.");
        }

        progress?.Report(new TemplateScaffoldingProgress(
            CurrentStep: $"Linking origin remote and pushing '{WorkspaceTemplateConstants.DefaultBranch}' to {provider.DisplayName}...",
            Percentage: 95,
            IsCompleted: false,
            ErrorMessage: null,
            LocalScaffoldCompleted: true));

        try
        {
            await EnsureOriginRemoteAsync(fullTargetPath, trimmedCloneUrl, cancellationToken).ConfigureAwait(false);

            var environmentVariables = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [WorkspaceTemplateConstants.GitTerminalPromptEnvVar] = WorkspaceTemplateConstants.GitTerminalPromptDisabledValue
            };

            await _gitCliRunner.ExecuteAsync(
                fullTargetPath,
                [
                    "-c",
                    $"http.extraHeader={authHeader}",
                    "push",
                    "-u",
                    "origin",
                    WorkspaceTemplateConstants.DefaultBranch
                ],
                timeoutSeconds: null,
                environmentVariables: environmentVariables,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (GitCommandException ex)
        {
            var redacted = GitCredentialRedactor.Redact(ex.Message);
            _logger.LogError("Failed to push scaffolded repository '{Repo}' to {Provider}: {Message}",
                createdRepo.FullName, provider.DisplayName, redacted);
            throw new VcsHostingProviderException(
                providerType,
                $"Created remote repository '{createdRepo.FullName}', but pushing branch '{WorkspaceTemplateConstants.DefaultBranch}' failed: {redacted}",
                innerException: ex);
        }

        await TryRefreshLocalDiscoveryAsync(fullTargetPath, cancellationToken).ConfigureAwait(false);

        return createdRepo;
    }

    internal static string ResolveCommandWorkingDirectory(
        string templateId,
        string fullTargetPath,
        string? relativeWorkingDirectory)
    {
        if (string.IsNullOrWhiteSpace(relativeWorkingDirectory))
        {
            return fullTargetPath;
        }

        var trimmedRelative = relativeWorkingDirectory.Trim();
        if (Path.IsPathRooted(trimmedRelative))
        {
            throw new WorkspaceTemplateExecutionException(
                templateId,
                failedCommand: null,
                exitCode: null,
                standardError: null,
                message: $"Template command working directory '{trimmedRelative}' must be a relative path inside the workspace root.");
        }

        var baseFullPath = Path.GetFullPath(fullTargetPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string candidate;
        try
        {
            candidate = Path.GetFullPath(Path.Combine(baseFullPath, trimmedRelative));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or SecurityException)
        {
            throw new WorkspaceTemplateExecutionException(
                templateId,
                failedCommand: null,
                exitCode: null,
                standardError: null,
                message: $"Invalid template command working directory '{trimmedRelative}': {ex.Message}",
                innerException: ex);
        }

        var trimmedCandidate = candidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var isWithinRoot = string.Equals(trimmedCandidate, baseFullPath, comparison) ||
                           trimmedCandidate.StartsWith(baseFullPath + Path.DirectorySeparatorChar, comparison);
        if (!isWithinRoot)
        {
            throw new WorkspaceTemplateExecutionException(
                templateId,
                failedCommand: null,
                exitCode: null,
                standardError: null,
                message: $"Template command working directory '{trimmedRelative}' escapes the target workspace root.");
        }

        return candidate;
    }

    private async Task TryRefreshLocalDiscoveryAsync(string fullTargetPath, CancellationToken cancellationToken)
    {
        try
        {
            await _localDiscoveryService.GetDiscoveredRepositoriesAsync(forceRefresh: true, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh local repository discovery cache after scaffolding '{TargetPath}'.", fullTargetPath);
        }
    }

    private async Task InitializeGitAndCommitAsync(
        string fullTargetPath,
        WorkspaceTemplateDefinition template,
        CancellationToken cancellationToken)
    {
        try
        {
            await _gitCliRunner.ExecuteAsync(
                fullTargetPath,
                ["init", "-b", WorkspaceTemplateConstants.DefaultBranch],
                cancellationToken: cancellationToken).ConfigureAwait(false);

            await _gitCliRunner.ExecuteAsync(
                fullTargetPath,
                ["config", "core.autocrlf", "false"],
                cancellationToken: cancellationToken).ConfigureAwait(false);

            await EnsureGitIdentityConfiguredAsync(fullTargetPath, "user.name", WorkspaceTemplateConstants.DefaultGitUserName, cancellationToken).ConfigureAwait(false);
            await EnsureGitIdentityConfiguredAsync(fullTargetPath, "user.email", WorkspaceTemplateConstants.DefaultGitUserEmail, cancellationToken).ConfigureAwait(false);

            await _gitCliRunner.ExecuteAsync(
                fullTargetPath,
                ["add", "-A"],
                cancellationToken: cancellationToken).ConfigureAwait(false);

            var commitMessage = $"feat: initial commit from NightCodeForge {template.Name} template";
            await _gitCliRunner.ExecuteAsync(
                fullTargetPath,
                ["commit", "-m", commitMessage],
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (GitCommandException ex)
        {
            var redacted = GitCredentialRedactor.Redact(ex.Message);
            _logger.LogError("Git initialization failed during template scaffolding in '{TargetPath}': {Message}",
                fullTargetPath, redacted);
            throw new WorkspaceTemplateExecutionException(
                template.Id,
                $"git {ex.Arguments}",
                ex.ExitCode,
                ex.StandardError,
                $"Git initialization failed in '{fullTargetPath}': {redacted}",
                ex);
        }
    }

    private async Task EnsureGitIdentityConfiguredAsync(
        string fullTargetPath,
        string configKey,
        string fallbackValue,
        CancellationToken cancellationToken)
    {
        string? existingValue = null;
        try
        {
            var queryResult = await _gitCliRunner.ExecuteAsync(
                fullTargetPath,
                ["config", "--get", configKey],
                cancellationToken: cancellationToken).ConfigureAwait(false);
            existingValue = queryResult.StandardOutput.Trim();
        }
        catch (GitCommandException ex)
        {
            // git config --get exits with code 1 when the key is unset; configure a repo-local identity below.
            _logger.LogDebug(ex, "Git config key '{ConfigKey}' not set; applying repo-local default.", configKey);
        }

        if (string.IsNullOrWhiteSpace(existingValue))
        {
            await _gitCliRunner.ExecuteAsync(
                fullTargetPath,
                ["config", configKey, fallbackValue],
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task EnsureOriginRemoteAsync(
        string fullTargetPath,
        string httpsCloneUrl,
        CancellationToken cancellationToken)
    {
        bool originExists = false;
        try
        {
            var remoteResult = await _gitCliRunner.ExecuteAsync(
                fullTargetPath,
                ["remote", "get-url", "origin"],
                cancellationToken: cancellationToken).ConfigureAwait(false);
            originExists = remoteResult.ExitCode == 0;
        }
        catch (GitCommandException ex)
        {
            // git remote get-url exits non-zero when 'origin' does not exist yet.
            _logger.LogDebug(ex, "Remote 'origin' does not exist yet in '{TargetPath}'.", fullTargetPath);
        }

        string[] args = originExists
            ? ["remote", "set-url", "origin", httpsCloneUrl]
            : ["remote", "add", "origin", httpsCloneUrl];

        await _gitCliRunner.ExecuteAsync(fullTargetPath, args, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static string ValidateAndResolveTargetPath(string templateId, string? targetDirectoryPath)
    {
        if (string.IsNullOrWhiteSpace(targetDirectoryPath))
        {
            throw new WorkspaceTemplateExecutionException(
                templateId,
                failedCommand: null,
                exitCode: null,
                standardError: null,
                message: "Target directory path is required.");
        }

        var trimmed = targetDirectoryPath.Trim();
        if (trimmed.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || !Path.IsPathFullyQualified(trimmed))
        {
            throw new WorkspaceTemplateExecutionException(
                templateId,
                failedCommand: null,
                exitCode: null,
                standardError: null,
                message: $"Target directory '{trimmed}' must be a valid absolute path.");
        }

        string fullTargetPath;
        try
        {
            fullTargetPath = Path.GetFullPath(trimmed);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or SecurityException)
        {
            throw new WorkspaceTemplateExecutionException(
                templateId,
                failedCommand: null,
                exitCode: null,
                standardError: null,
                message: $"Invalid target directory path '{trimmed}': {ex.Message}",
                innerException: ex);
        }

        if (File.Exists(fullTargetPath))
        {
            throw new WorkspaceTemplateExecutionException(
                templateId,
                failedCommand: null,
                exitCode: null,
                standardError: null,
                message: $"A file already exists at '{fullTargetPath}'. Choose a new directory path.");
        }

        if (Directory.Exists(fullTargetPath))
        {
            bool hasEntries;
            try
            {
                hasEntries = Directory.EnumerateFileSystemEntries(fullTargetPath).Any();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                throw new WorkspaceTemplateExecutionException(
                    templateId,
                    failedCommand: null,
                    exitCode: null,
                    standardError: null,
                    message: $"Cannot inspect target directory '{fullTargetPath}': {ex.Message}",
                    innerException: ex);
            }

            if (hasEntries)
            {
                throw new WorkspaceTemplateExecutionException(
                    templateId,
                    failedCommand: null,
                    exitCode: null,
                    standardError: null,
                    message: $"Target directory '{fullTargetPath}' already exists and is not empty. Choose an empty or new directory.");
            }
        }

        return fullTargetPath;
    }
}
