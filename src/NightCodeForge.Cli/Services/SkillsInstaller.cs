namespace NightCodeForge.Cli.Services;

using System.Reflection;
using System.Text.RegularExpressions;
using NightCodeForge.Cli.Common;

public class SkillsInstaller
{
    public const string WorkspaceAgentsFolder = ".agents";
    public const string SkillsFolder = "skills";
    public const string GeminiFolder = ".gemini";
    public const string ConfigFolder = "config";

    private const string SkillsResourcePrefix = "skills/";
    private const string SkillFileName = "SKILL.md";

    private static readonly Assembly Assembly = typeof(SkillsInstaller).Assembly;
    public static readonly string[] RequiredSkills = ["ncf-backlog", "ncf-backlog-migrator", "ncf-project-readiness"];

    public static IReadOnlyList<string> GetRequiredSkillNames() => RequiredSkills;

    public static IReadOnlyList<string> GetEmbeddedSkillNames()
    {
        var skillNames = Assembly.GetManifestResourceNames()
            .Select(n => n.Replace('\\', '/'))
            .Where(n => n.StartsWith(SkillsResourcePrefix, StringComparison.OrdinalIgnoreCase))
            .Select(n => n.Substring(SkillsResourcePrefix.Length))
            .Where(IsTopLevelSkillFile)
            .Select(relative => relative.Substring(0, relative.IndexOf('/')))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (skillNames.Count == 0)
        {
            throw new InvalidOperationException("No embedded skills found in NightCodeForge.Cli assembly.");
        }

        return skillNames;
    }

    public static string? GetEmbeddedSkillContent(string skillName)
    {
        if (string.IsNullOrWhiteSpace(skillName))
        {
            return null;
        }

        var trimmed = skillName.Trim();
        if (trimmed.Contains('/') || trimmed.Contains('\\'))
        {
            return null;
        }

        return ReadEmbeddedResource($"{SkillsResourcePrefix}{trimmed}/{SkillFileName}");
    }

    public static IReadOnlyList<string> GetGlobalSkillDirectories()
    {
        return GetGlobalSkillDirectories(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }

    public static IReadOnlyList<string> GetGlobalSkillDirectories(string userProfilePath)
    {
        if (string.IsNullOrWhiteSpace(userProfilePath))
        {
            throw new InvalidOperationException("Could not resolve user profile directory for global skill installation.");
        }

        var trimmedProfile = userProfilePath.Trim();
        return
        [
            Path.Combine(trimmedProfile, GeminiFolder, ConfigFolder, SkillsFolder),
            Path.Combine(trimmedProfile, WorkspaceAgentsFolder, SkillsFolder)
        ];
    }

    public static int GetEmbeddedSkillVersion(string skillName)
    {
        var content = GetEmbeddedSkillContent(skillName);
        if (content == null) return 1;

        var match = Regex.Match(content, @"^version:\s*(\d+)", RegexOptions.Multiline);
        if (match.Success && int.TryParse(match.Groups[1].Value, out var v))
        {
            return v;
        }

        return 1;
    }

    public static string? ReadEmbeddedResource(string logicalName)
    {
        using var stream = OpenEmbeddedResourceStream(logicalName);
        if (stream == null) return null;

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static byte[]? ReadEmbeddedResourceBytes(string logicalName)
    {
        using var stream = OpenEmbeddedResourceStream(logicalName);
        if (stream == null) return null;

        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    private static Stream? OpenEmbeddedResourceStream(string logicalName)
    {
        var normalizedTarget = logicalName.Replace('\\', '/');
        var names = Assembly.GetManifestResourceNames();
        var match = names.FirstOrDefault(n => string.Equals(n.Replace('\\', '/'), normalizedTarget, StringComparison.OrdinalIgnoreCase));
        return match == null ? null : Assembly.GetManifestResourceStream(match);
    }

    private static bool IsTopLevelSkillFile(string relativePath)
    {
        var slashIndex = relativePath.IndexOf('/');
        return slashIndex > 0 &&
               relativePath.IndexOf('/', slashIndex + 1) < 0 &&
               relativePath.AsSpan(slashIndex + 1).Equals(SkillFileName, StringComparison.OrdinalIgnoreCase);
    }

    public static void InstallSkills(string workspaceRoot, bool dryRun = false, Action<string>? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        var targetBaseDir = Path.Combine(workspaceRoot, WorkspaceAgentsFolder, SkillsFolder);
        InstallSkillsToDirectory(targetBaseDir, dryRun, log);
    }

    public static IReadOnlyList<string> InstallGlobalSkills(bool dryRun = false, Action<string>? log = null)
    {
        return InstallGlobalSkills(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), dryRun, log);
    }

    public static IReadOnlyList<string> InstallGlobalSkills(string userProfilePath, bool dryRun = false, Action<string>? log = null)
    {
        var targetDirs = GetGlobalSkillDirectories(userProfilePath);
        foreach (var dir in targetDirs)
        {
            InstallSkillsToDirectory(dir, dryRun, log);
        }

        return targetDirs;
    }

    public static void InstallSkillsToDirectory(string targetBaseDir, bool dryRun = false, Action<string>? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetBaseDir);

        var allResourceNames = Assembly.GetManifestResourceNames()
            .Where(n => n.Replace('\\', '/').StartsWith(SkillsResourcePrefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (allResourceNames.Count == 0)
        {
            throw new InvalidOperationException("No embedded skills found in NightCodeForge.Cli assembly.");
        }

        foreach (var rawResourceName in allResourceNames)
        {
            var normalizedResourceName = rawResourceName.Replace('\\', '/');
            var relativePath = normalizedResourceName.Substring(SkillsResourcePrefix.Length);
            var targetFilePath = Path.Combine(targetBaseDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
            var targetDir = Path.GetDirectoryName(targetFilePath);

            if (IsTopLevelSkillFile(relativePath))
            {
                var embeddedContent = ReadEmbeddedResource(rawResourceName)
                    ?? throw new InvalidOperationException($"Failed to read embedded resource '{rawResourceName}'.");

                var transformed = PrepareSkillContent(embeddedContent, targetFilePath);

                if (dryRun)
                {
                    log?.Invoke($"[dry-run] Would write {targetFilePath}");
                }
                else
                {
                    if (targetDir != null && !Directory.Exists(targetDir))
                    {
                        Directory.CreateDirectory(targetDir);
                    }

                    File.WriteAllText(targetFilePath, transformed);
                    log?.Invoke($"[ncf] Installed skill: {targetFilePath}");
                }
            }
            else
            {
                // Non-SKILL.md assets (e.g. TASK_TEMPLATE.md)
                var bytes = ReadEmbeddedResourceBytes(rawResourceName)
                    ?? throw new InvalidOperationException($"Failed to read embedded resource '{rawResourceName}'.");

                if (dryRun)
                {
                    log?.Invoke($"[dry-run] Would write asset {targetFilePath}");
                }
                else
                {
                    if (targetDir != null && !Directory.Exists(targetDir))
                    {
                        Directory.CreateDirectory(targetDir);
                    }

                    File.WriteAllBytes(targetFilePath, bytes);
                    log?.Invoke($"[ncf] Installed skill asset: {targetFilePath}");
                }
            }
        }
    }

    public static string PrepareSkillContent(string embeddedContent, string? existingFilePath = null)
    {
        var normalizedEmbedded = embeddedContent.Replace("\r\n", "\n").Trim();

        // Match frontmatter and body
        var fmMatch = Regex.Match(normalizedEmbedded, @"\A---\s*\n(.*?)\n---\s*\n(.*)\z", RegexOptions.Singleline);
        string frontmatter;
        string body;

        if (fmMatch.Success)
        {
            frontmatter = $"---\n{fmMatch.Groups[1].Value.Trim()}\n---";
            body = fmMatch.Groups[2].Value.Trim();
        }
        else
        {
            frontmatter = string.Empty;
            body = normalizedEmbedded;
        }

        string? existingContent = null;
        if (!string.IsNullOrWhiteSpace(existingFilePath) && File.Exists(existingFilePath))
        {
            existingContent = File.ReadAllText(existingFilePath).Replace("\r\n", "\n");
        }

        if (string.IsNullOrWhiteSpace(existingContent))
        {
            var sb = new System.Text.StringBuilder();
            if (!string.IsNullOrWhiteSpace(frontmatter))
            {
                sb.Append(frontmatter).Append("\n\n");
            }
            sb.Append(Constants.ManagedBlockHeader).Append('\n');
            sb.Append(body).Append('\n');
            sb.Append(Constants.ManagedBlockFooter).Append('\n');
            return sb.ToString();
        }

        // Existing file exists: preserve any user modifications outside the managed block
        var startIndex = existingContent.IndexOf(Constants.ManagedBlockHeader, StringComparison.Ordinal);
        var endIndex = startIndex >= 0
            ? existingContent.IndexOf(Constants.ManagedBlockFooter, startIndex, StringComparison.Ordinal)
            : -1;

        if (startIndex >= 0 && endIndex >= startIndex)
        {
            var before = existingContent.Substring(0, startIndex);
            var afterIndex = endIndex + Constants.ManagedBlockFooter.Length;
            var after = afterIndex < existingContent.Length ? existingContent.Substring(afterIndex) : string.Empty;

            var existingFmMatch = Regex.Match(before, @"\A---\s*\n(.*?)\n---\s*(.*)\z", RegexOptions.Singleline);
            var userBefore = existingFmMatch.Success
                ? existingFmMatch.Groups[2].Value.Trim()
                : before.Trim();
            var userAfter = after.Trim();

            var sb = new System.Text.StringBuilder();
            if (!string.IsNullOrWhiteSpace(frontmatter))
            {
                sb.Append(frontmatter).Append("\n\n");
            }

            if (!string.IsNullOrWhiteSpace(userBefore))
            {
                sb.Append(userBefore).Append("\n\n");
            }

            sb.Append(Constants.ManagedBlockHeader).Append('\n');
            sb.Append(body).Append('\n');
            sb.Append(Constants.ManagedBlockFooter).Append('\n');

            if (!string.IsNullOrWhiteSpace(userAfter))
            {
                sb.Append('\n').Append(userAfter).Append('\n');
            }

            return sb.ToString();
        }

        // No managed block in existing file: wrap embedded body with managed block and append user's existing body
        {
            var existingFmMatch = Regex.Match(existingContent, @"\A---\s*\n(.*?)\n---\s*\n(.*)\z", RegexOptions.Singleline);
            var userExistingBody = existingFmMatch.Success
                ? existingFmMatch.Groups[2].Value.Trim()
                : existingContent.Trim();

            var sb = new System.Text.StringBuilder();
            if (!string.IsNullOrWhiteSpace(frontmatter))
            {
                sb.Append(frontmatter).Append("\n\n");
            }
            sb.Append(Constants.ManagedBlockHeader).Append('\n');
            sb.Append(body).Append('\n');
            sb.Append(Constants.ManagedBlockFooter).Append('\n');

            if (!string.IsNullOrWhiteSpace(userExistingBody))
            {
                sb.Append("\n# Custom Repository Instructions\n");
                sb.Append(userExistingBody).Append('\n');
            }

            return sb.ToString();
        }
    }
}
