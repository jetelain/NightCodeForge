namespace NightCodeForge.Cli.Services;

using System.Reflection;
using System.Text.RegularExpressions;
using NightCodeForge.Cli.Common;

public class SkillsInstaller
{
    private static readonly Assembly Assembly = typeof(SkillsInstaller).Assembly;
    public static readonly string[] RequiredSkills = ["ncf-backlog-migrator", "ncf-ideation", "ncf-project-readiness"];

    public static IReadOnlyList<string> GetRequiredSkillNames() => RequiredSkills;

    public static int GetEmbeddedSkillVersion(string skillName)
    {
        var resourceName = $"skills/{skillName}/SKILL.md";
        var content = ReadEmbeddedResource(resourceName);
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
        var normalizedTarget = logicalName.Replace('\\', '/');
        var names = Assembly.GetManifestResourceNames();
        var match = names.FirstOrDefault(n => string.Equals(n.Replace('\\', '/'), normalizedTarget, StringComparison.OrdinalIgnoreCase));
        if (match == null) return null;

        using var stream = Assembly.GetManifestResourceStream(match);
        if (stream == null) return null;

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static byte[]? ReadEmbeddedResourceBytes(string logicalName)
    {
        var normalizedTarget = logicalName.Replace('\\', '/');
        var names = Assembly.GetManifestResourceNames();
        var match = names.FirstOrDefault(n => string.Equals(n.Replace('\\', '/'), normalizedTarget, StringComparison.OrdinalIgnoreCase));
        if (match == null) return null;

        using var stream = Assembly.GetManifestResourceStream(match);
        if (stream == null) return null;

        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    public static void InstallSkills(string workspaceRoot, bool dryRun = false, Action<string>? log = null)
    {
        var targetBaseDir = Path.Combine(workspaceRoot, ".agents", "skills");
        var allResourceNames = Assembly.GetManifestResourceNames()
            .Where(n => n.Replace('\\', '/').StartsWith("skills/", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (allResourceNames.Count == 0)
        {
            throw new InvalidOperationException("No embedded skills found in NightCodeForge.Cli assembly.");
        }

        foreach (var rawResourceName in allResourceNames)
        {
            var normalizedResourceName = rawResourceName.Replace('\\', '/');
            var relativePath = normalizedResourceName.Substring("skills/".Length);
            var targetFilePath = Path.Combine(targetBaseDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
            var targetDir = Path.GetDirectoryName(targetFilePath);

            if (relativePath.EndsWith("SKILL.md", StringComparison.OrdinalIgnoreCase))
            {
                var embeddedContent = ReadEmbeddedResource(rawResourceName);
                if (embeddedContent == null) continue;

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
                    log?.Invoke($"[ncf] Installed skill: {relativePath}");
                }
            }
            else
            {
                // Non-SKILL.md assets (e.g. TASK_TEMPLATE.md)
                var bytes = ReadEmbeddedResourceBytes(rawResourceName);
                if (bytes == null) continue;

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
                    log?.Invoke($"[ncf] Installed skill asset: {relativePath}");
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
