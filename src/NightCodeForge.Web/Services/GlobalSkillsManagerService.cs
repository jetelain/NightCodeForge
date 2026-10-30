using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using NightCodeForge.Agents.Skills;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Skills;

namespace NightCodeForge.Web.Services;

public record GlobalSkillViewModel(
    string Name,
    string Description,
    string Stack,
    string Instructions,
    string FilePath,
    bool IsBuiltIn);

public record CreateSkillRequest(
    string Name,
    string Stack,
    string Description,
    string Instructions);

public record UpdateSkillRequest(
    string Stack,
    string Description,
    string Instructions);

public interface IGlobalSkillsManagerService
{
    Task<IReadOnlyList<GlobalSkillViewModel>> GetGlobalSkillsAsync(CancellationToken ct = default);
    Task CreateCustomSkillAsync(CreateSkillRequest request, CancellationToken ct = default);
    Task UpdateCustomSkillAsync(string skillName, UpdateSkillRequest request, CancellationToken ct = default);
    Task DeleteCustomSkillAsync(string skillName, CancellationToken ct = default);
}

public class GlobalSkillsManagerService : IGlobalSkillsManagerService
{
    private static readonly Regex ValidSlugRegex = new(@"^[a-zA-Z0-9_\-]+$", RegexOptions.Compiled);
    private readonly IBundledSkillsProvider _bundledSkillsProvider;
    private readonly ILogger<GlobalSkillsManagerService> _logger;
    private readonly string _globalSkillsDirectory;

    public GlobalSkillsManagerService(
        IBundledSkillsProvider bundledSkillsProvider,
        ILogger<GlobalSkillsManagerService> logger)
        : this(bundledSkillsProvider, null, logger)
    {
    }

    public GlobalSkillsManagerService(
        IBundledSkillsProvider bundledSkillsProvider,
        string? globalSkillsDirectory,
        ILogger<GlobalSkillsManagerService> logger)
    {
        _bundledSkillsProvider = bundledSkillsProvider ?? throw new ArgumentNullException(nameof(bundledSkillsProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _globalSkillsDirectory = !string.IsNullOrWhiteSpace(globalSkillsDirectory)
            ? globalSkillsDirectory
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nightcodeforge", "skills");
    }

    public async Task<IReadOnlyList<GlobalSkillViewModel>> GetGlobalSkillsAsync(CancellationToken ct = default)
    {
        var result = new Dictionary<string, GlobalSkillViewModel>(StringComparer.OrdinalIgnoreCase);

        // 1. Bundled Skills (Built-In)
        var bundled = _bundledSkillsProvider.GetBundledEntries();
        foreach (var b in bundled)
        {
            var parsed = SkillFrontmatterParser.Parse(b.Content);
            result[b.Descriptor.Name] = new GlobalSkillViewModel(
                Name: b.Descriptor.Name,
                Description: b.Descriptor.Description,
                Stack: b.Descriptor.Stack ?? CodeStack.Universal,
                Instructions: parsed.Instructions,
                FilePath: b.Descriptor.SkillFilePath,
                IsBuiltIn: true);
        }

        // 2. Host User Global Store (~/.nightcodeforge/skills/)
        if (Directory.Exists(_globalSkillsDirectory))
        {
            try
            {
                var skillFiles = Directory.EnumerateFiles(_globalSkillsDirectory, "SKILL.md", SearchOption.AllDirectories);
                foreach (var file in skillFiles)
                {
                    ct.ThrowIfCancellationRequested();
                    var content = await File.ReadAllTextAsync(file, ct);
                    var dirName = Path.GetFileName(Path.GetDirectoryName(file));
                    var metadata = SkillFrontmatterParser.Parse(content, fallbackName: dirName);

                    if (string.IsNullOrWhiteSpace(metadata.Name)) continue;

                    var isBuiltIn = _bundledSkillsProvider.GetBundledSkills().Any(k =>
                        string.Equals(k.Name, metadata.Name, StringComparison.OrdinalIgnoreCase));

                    result[metadata.Name] = new GlobalSkillViewModel(
                        Name: metadata.Name,
                        Description: metadata.Description,
                        Stack: metadata.Stack,
                        Instructions: metadata.Instructions,
                        FilePath: file,
                        IsBuiltIn: isBuiltIn);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to enumerate global skills in {GlobalDir}", _globalSkillsDirectory);
            }
        }

        return result.Values.OrderBy(s => s.Stack).ThenBy(s => s.Name).ToList();
    }

    public async Task CreateCustomSkillAsync(CreateSkillRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateSkillName(request.Name);

        var isBuiltIn = _bundledSkillsProvider.GetBundledSkills().Any(k =>
            string.Equals(k.Name, request.Name.Trim(), StringComparison.OrdinalIgnoreCase));
        if (isBuiltIn)
        {
            throw new InvalidOperationException($"Cannot create skill '{request.Name}' because a built-in skill with that name already exists.");
        }

        var stack = SkillFrontmatterParser.NormalizeStack(request.Stack);
        var targetDir = Path.Combine(_globalSkillsDirectory, stack, request.Name.Trim());
        ValidatePathConfinement(targetDir);

        if (!Directory.Exists(targetDir))
        {
            Directory.CreateDirectory(targetDir);
        }

        var skillFile = Path.Combine(targetDir, "SKILL.md");
        var fileContent = SkillFrontmatterParser.BuildMarkdown(request.Name.Trim(), request.Description, stack, request.Instructions);
        await File.WriteAllTextAsync(skillFile, fileContent, ct);
        _logger.LogInformation("Created custom global skill '{SkillName}' in stack '{Stack}'.", request.Name, stack);
    }

    public async Task UpdateCustomSkillAsync(string skillName, UpdateSkillRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateSkillName(skillName);

        var isBuiltIn = _bundledSkillsProvider.GetBundledSkills().Any(k =>
            string.Equals(k.Name, skillName.Trim(), StringComparison.OrdinalIgnoreCase));
        if (isBuiltIn)
        {
            throw new InvalidOperationException($"Built-in skill '{skillName}' cannot be modified directly.");
        }

        var existingFile = FindCustomSkillFile(skillName.Trim());
        if (existingFile == null)
        {
            throw new FileNotFoundException($"Custom skill '{skillName}' was not found.");
        }

        var newStack = SkillFrontmatterParser.NormalizeStack(request.Stack);
        var currentDir = Path.GetDirectoryName(existingFile)!;
        var targetDir = Path.Combine(_globalSkillsDirectory, newStack, skillName.Trim());
        ValidatePathConfinement(targetDir);

        if (!string.Equals(Path.GetFullPath(currentDir), Path.GetFullPath(targetDir), StringComparison.OrdinalIgnoreCase))
        {
            // Stack changed, move folder
            if (Directory.Exists(targetDir))
            {
                Directory.Delete(targetDir, true);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(targetDir)!);
            Directory.Move(currentDir, targetDir);
        }

        var skillFile = Path.Combine(targetDir, "SKILL.md");
        var fileContent = SkillFrontmatterParser.BuildMarkdown(skillName.Trim(), request.Description, newStack, request.Instructions);
        await File.WriteAllTextAsync(skillFile, fileContent, ct);
        _logger.LogInformation("Updated custom global skill '{SkillName}'.", skillName);
    }

    public async Task DeleteCustomSkillAsync(string skillName, CancellationToken ct = default)
    {
        ValidateSkillName(skillName);

        var isBuiltIn = _bundledSkillsProvider.GetBundledSkills().Any(k =>
            string.Equals(k.Name, skillName.Trim(), StringComparison.OrdinalIgnoreCase));
        if (isBuiltIn)
        {
            throw new InvalidOperationException($"Built-in skill '{skillName}' cannot be deleted.");
        }

        var existingFile = FindCustomSkillFile(skillName.Trim());
        if (existingFile == null)
        {
            throw new FileNotFoundException($"Custom skill '{skillName}' was not found.");
        }

        var dir = Path.GetDirectoryName(existingFile)!;
        ValidatePathConfinement(dir);

        await Task.Run(() =>
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }, ct);

        _logger.LogInformation("Deleted custom global skill '{SkillName}'.", skillName);
    }

    private string? FindCustomSkillFile(string skillName)
    {
        if (!Directory.Exists(_globalSkillsDirectory)) return null;

        var files = Directory.EnumerateFiles(_globalSkillsDirectory, "SKILL.md", SearchOption.AllDirectories);
        foreach (var file in files)
        {
            var dirName = Path.GetFileName(Path.GetDirectoryName(file));
            if (string.Equals(dirName, skillName, StringComparison.OrdinalIgnoreCase))
            {
                return file;
            }

            try
            {
                var content = File.ReadAllText(file);
                var metadata = SkillFrontmatterParser.Parse(content, dirName);
                if (string.Equals(metadata.Name, skillName, StringComparison.OrdinalIgnoreCase))
                {
                    return file;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Skipped unreadable skill file at {FilePath}", file);
            }
        }

        return null;
    }

    private void ValidateSkillName(string skillName)
    {
        if (string.IsNullOrWhiteSpace(skillName))
        {
            throw new ArgumentException("Skill name cannot be empty.", nameof(skillName));
        }

        var trimmed = skillName.Trim();
        if (!ValidSlugRegex.IsMatch(trimmed) || trimmed.Contains(".."))
        {
            throw new ArgumentException($"Invalid skill name '{skillName}'. Only alphanumeric characters, hyphens, and underscores are allowed.", nameof(skillName));
        }
    }

    private void ValidatePathConfinement(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var fullGlobal = Path.GetFullPath(_globalSkillsDirectory);

        if (!fullPath.StartsWith(fullGlobal, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException($"Path traversal detected. Access denied to path: {path}");
        }
    }
}

