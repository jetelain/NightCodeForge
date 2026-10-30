using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Skills;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Agents.Skills;

public class WorkspaceSkillsService : IWorkspaceSkillsService
{
    private readonly Workspace _workspace;
    private readonly string _workspacePath;
    private readonly int? _taskId;
    private readonly ISkillsService _skillsService;
    private readonly IBundledSkillsProvider _bundledSkillsProvider;
    private readonly ILogger<WorkspaceSkillsService> _logger;

    public WorkspaceSkillsService(
        Workspace workspace,
        string workspacePath,
        int? taskId,
        ISkillsService skillsService,
        IBundledSkillsProvider bundledSkillsProvider,
        ILogger<WorkspaceSkillsService> logger)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        _workspacePath = workspacePath;
        _taskId = taskId;
        _skillsService = skillsService ?? throw new ArgumentNullException(nameof(skillsService));
        _bundledSkillsProvider = bundledSkillsProvider ?? throw new ArgumentNullException(nameof(bundledSkillsProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task<IReadOnlyList<SkillDescriptor>> GetCandidateSkillsAsync(CancellationToken cancellationToken = default)
    {
        var projectConfig = CodeStackPresets.ResolveConfig(_workspacePath);
        var workspaceStack = projectConfig.CodeStack ?? CodeStackPresets.Detect(_workspacePath)?.Name ?? CodeStack.DotNet;

        var candidates = DiscoverCandidateSkills(workspaceStack);
        IReadOnlyList<SkillDescriptor> result = candidates.Values.OrderBy(c => c.Name).ToList();
        return Task.FromResult(result);
    }

    public async Task<IReadOnlyList<SkillDescriptor>> GetActiveSkillsAsync(CancellationToken cancellationToken = default)
    {
        var projectConfig = CodeStackPresets.ResolveConfig(_workspacePath);
        var workspaceStack = projectConfig.CodeStack ?? CodeStackPresets.Detect(_workspacePath)?.Name ?? CodeStack.DotNet;

        var candidates = DiscoverCandidateSkills(workspaceStack);

        // Cascading Policy Evaluation
        var candidateNames = candidates.Keys.ToList();
        var resolution = GlobalSkillsResolver.Resolve(_workspace, projectConfig, candidateNames);

        if (!resolution.IsEnabled)
        {
            return Array.Empty<SkillDescriptor>();
        }

        var activeList = candidates.Values
            .Where(c => resolution.ActiveSkills.Contains(c.Name))
            .OrderBy(c => c.Name)
            .ToList();

        return activeList;
    }

    private Dictionary<string, SkillDescriptor> DiscoverCandidateSkills(string workspaceStack)
    {
        var candidates = new Dictionary<string, SkillDescriptor>(StringComparer.OrdinalIgnoreCase);

        // 1. Local Repository Skills (Highest discovery priority)
        var repoCandidateDirs = new[]
        {
            Path.Combine(_workspacePath, ".nightcodeforge", "skills"),
            Path.Combine(_workspacePath, ".agents", "skills"),
            Path.Combine(_workspacePath, "skills")
        };

        foreach (var baseDir in repoCandidateDirs)
        {
            if (!Directory.Exists(baseDir)) continue;

            try
            {
                foreach (var dir in Directory.EnumerateDirectories(baseDir))
                {
                    var skillFile = Path.Combine(dir, "SKILL.md");
                    if (!File.Exists(skillFile)) continue;

                    var desc = ReadDescriptorFromFile(skillFile, dir, isBuiltIn: false);
                    if (desc != null && !candidates.ContainsKey(desc.Name))
                    {
                        candidates[desc.Name] = desc;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to enumerate repository skills in {BaseDir}", baseDir);
            }
        }

        // 2. Bundled Companion Skills
        var bundled = _bundledSkillsProvider.GetBundledSkills();
        foreach (var b in bundled)
        {
            if (IsStackApplicable(b.Stack, workspaceStack) && !candidates.ContainsKey(b.Name))
            {
                candidates[b.Name] = b;
            }
        }

        // 3. Host User Global Store (~/.nightcodeforge/skills/)
        var globalRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nightcodeforge", "skills");
        if (Directory.Exists(globalRoot))
        {
            var searchDirs = new List<string>
            {
                Path.Combine(globalRoot, NormalizeStack(workspaceStack)),
                Path.Combine(globalRoot, CodeStack.Universal),
                globalRoot
            };

            foreach (var sDir in searchDirs)
            {
                if (!Directory.Exists(sDir)) continue;

                try
                {
                    foreach (var dir in Directory.EnumerateDirectories(sDir))
                    {
                        var skillFile = Path.Combine(dir, "SKILL.md");
                        if (!File.Exists(skillFile)) continue;

                        var desc = ReadDescriptorFromFile(skillFile, dir, isBuiltIn: false);
                        if (desc != null && IsStackApplicable(desc.Stack, workspaceStack) && !candidates.ContainsKey(desc.Name))
                        {
                            candidates[desc.Name] = desc;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to enumerate global skills in {Dir}", sDir);
                }
            }
        }

        return candidates;
    }

    public async Task<string> GetSkillsCatalogPromptAsync(CancellationToken cancellationToken = default)
    {
        var activeSkills = await GetActiveSkillsAsync(cancellationToken);
        return _skillsService.FormatSkillsCatalogPrompt(activeSkills);
    }

    public async Task<string> LoadSkillAsync(string skillName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(skillName))
        {
            return "Error: skill_name parameter is required.";
        }

        var trimmedName = skillName.Trim();

        // 1. Cascading Policy Check
        var projectConfig = CodeStackPresets.ResolveConfig(_workspacePath);
        var resolution = GlobalSkillsResolver.Resolve(_workspace, projectConfig, new[] { trimmedName });
        if (!resolution.IsEnabled || !resolution.ActiveSkills.Contains(trimmedName))
        {
            return $"Error: Skill '{trimmedName}' is disabled by workspace configuration.";
        }

        // 2. Local Repository Override (Highest Precedence)
        var repoCandidateDirs = new[]
        {
            Path.Combine(_workspacePath, ".nightcodeforge", "skills", trimmedName),
            Path.Combine(_workspacePath, ".agents", "skills", trimmedName),
            Path.Combine(_workspacePath, "skills", trimmedName)
        };

        foreach (var dir in repoCandidateDirs)
        {
            var skillFile = Path.Combine(dir, "SKILL.md");
            if (File.Exists(skillFile))
            {
                return await File.ReadAllTextAsync(skillFile, cancellationToken);
            }
        }

        // 3. Bundled Companion Skills
        if (_bundledSkillsProvider.TryGetSkillContent(trimmedName, out var bundledContent) && bundledContent != null)
        {
            return bundledContent;
        }

        // 4. Host User Global Store
        var globalRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nightcodeforge", "skills");
        if (Directory.Exists(globalRoot))
        {
            var workspaceStack = projectConfig.CodeStack ?? CodeStackPresets.Detect(_workspacePath)?.Name ?? CodeStack.DotNet;
            var candidateFiles = new[]
            {
                Path.Combine(globalRoot, NormalizeStack(workspaceStack), trimmedName, "SKILL.md"),
                Path.Combine(globalRoot, CodeStack.Universal, trimmedName, "SKILL.md"),
                Path.Combine(globalRoot, trimmedName, "SKILL.md")
            };

            foreach (var file in candidateFiles)
            {
                if (File.Exists(file))
                {
                    return await File.ReadAllTextAsync(file, cancellationToken);
                }
            }
        }

        // 5. Not found fallback
        var activeSkills = await GetActiveSkillsAsync(cancellationToken);
        var activeNames = string.Join(", ", activeSkills.Select(s => s.Name));
        return $"Error: Skill '{trimmedName}' not found. Available skills: {activeNames}";
    }

    public static bool IsStackApplicable(string? skillStack, string? workspaceStack)
    {
        if (string.IsNullOrWhiteSpace(skillStack) || string.Equals(NormalizeStack(skillStack), CodeStack.Universal, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(workspaceStack))
        {
            return true;
        }

        var normSkill = NormalizeStack(skillStack);
        var normWs = NormalizeStack(workspaceStack);
        return string.Equals(normSkill, normWs, StringComparison.OrdinalIgnoreCase);
    }

    public static string NormalizeStack(string? stack) =>
        CodeStack.NormalizeStack(stack);

    private static SkillDescriptor? ReadDescriptorFromFile(string filePath, string dirPath, bool isBuiltIn)
    {
        try
        {
            var content = File.ReadAllText(filePath);
            var metadata = SkillFrontmatterParser.Parse(content, fallbackName: Path.GetFileName(dirPath));

            return new SkillDescriptor(
                Name: metadata.Name,
                Description: metadata.Description,
                DirectoryPath: dirPath,
                SkillFilePath: filePath,
                Stack: metadata.Stack,
                IsBuiltIn: isBuiltIn);
        }
        catch (Exception)
        {
            // Justified ignore: skip unreadable, locked, or malformed SKILL.md files
            return null;
        }
    }
}

public class WorkspaceSkillsServiceFactory : IWorkspaceSkillsServiceFactory
{
    private readonly ISkillsService _skillsService;
    private readonly IBundledSkillsProvider _bundledSkillsProvider;
    private readonly ILogger<WorkspaceSkillsService> _logger;

    public WorkspaceSkillsServiceFactory(
        ISkillsService skillsService,
        IBundledSkillsProvider bundledSkillsProvider,
        ILogger<WorkspaceSkillsService> logger)
    {
        _skillsService = skillsService ?? throw new ArgumentNullException(nameof(skillsService));
        _bundledSkillsProvider = bundledSkillsProvider ?? throw new ArgumentNullException(nameof(bundledSkillsProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public IWorkspaceSkillsService Create(Workspace workspace, string workspacePath, int? taskId = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);

        return new WorkspaceSkillsService(
            workspace,
            workspacePath,
            taskId,
            _skillsService,
            _bundledSkillsProvider,
            _logger);
    }
}

