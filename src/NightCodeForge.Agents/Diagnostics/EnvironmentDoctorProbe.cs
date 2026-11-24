using System.Text;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Execution;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Agents.Diagnostics;

/// <summary>
/// Builds and parses the single-round-trip Environment Doctor diagnostic script.
/// </summary>
internal static class EnvironmentDoctorProbe
{
    internal const string IdentityMarker = "===NCF_DOC:IDENTITY===";
    internal const string TmpCachesMarker = "===NCF_DOC:TMP_CACHES===";
    internal const string MountsMarker = "===NCF_DOC:MOUNTS===";
    internal const string GitMarker = "===NCF_DOC:GIT===";
    internal const string ToolsMarker = "===NCF_DOC:TOOLS===";
    internal const string IcuMarker = "===NCF_DOC:ICU===";

    private static readonly string[] CoreTools = ["git", "rg", "dotnet"];
    private static readonly string[] CompanionTools = ["csnav", "mdfetch", "codecondenser"];
    private static readonly string[] AllProbedTools = [.. CoreTools, .. CompanionTools];
    private static readonly string[] StorageProbePaths = ["/tmp", "/cache/nuget", "/cache/npm"];

    public static string BuildScript(
        SandboxScopeDescriptor scope,
        IReadOnlyList<EnvironmentMountInfo> expectedMounts)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(expectedMounts);

        var sb = new StringBuilder();
        sb.AppendLine("set +e");

        // 1. Identity & $HOME
        sb.AppendLine($"echo \"{IdentityMarker}\"");
        sb.AppendLine("echo \"UID=$(id -u 2>/dev/null || echo unknown)\"");
        sb.AppendLine("echo \"GID=$(id -g 2>/dev/null || echo unknown)\"");
        sb.AppendLine("echo \"USER=$(id -un 2>/dev/null || echo unknown)\"");
        sb.AppendLine("echo \"HOME=${HOME:-unset}\"");
        sb.AppendLine("[ -n \"$HOME\" ] && [ -w \"$HOME\" ] && echo \"HOME_WRITABLE=1\" || echo \"HOME_WRITABLE=0\"");

        // 2. /tmp & package caches
        sb.AppendLine($"echo \"{TmpCachesMarker}\"");
        sb.AppendLine($"for d in {string.Join(' ', StorageProbePaths)}; do");
        sb.AppendLine("  if [ ! -e \"$d\" ]; then echo \"DIR|$d|MISSING\";");
        sb.AppendLine("  elif [ -w \"$d\" ]; then echo \"DIR|$d|RW\";");
        sb.AppendLine("  else echo \"DIR|$d|RO\"; fi");
        sb.AppendLine("done");

        // 3. Virtual mount integrity
        sb.AppendLine($"echo \"{MountsMarker}\"");
        var mountsToCheck = SelectMountsToProbe(scope, expectedMounts);
        foreach (var mount in mountsToCheck)
        {
            var expectedMode = mount.IsReadOnly ? "RO" : "RW";
            var quotedPath = ShellQuoting.QuotePosix(mount.ContainerPath);
            var missingPayload = ShellQuoting.QuotePosix($"MOUNT|{mount.ContainerPath}|{expectedMode}|MISSING");
            var rwPayload = ShellQuoting.QuotePosix($"MOUNT|{mount.ContainerPath}|{expectedMode}|RW");
            var roPayload = ShellQuoting.QuotePosix($"MOUNT|{mount.ContainerPath}|{expectedMode}|RO");
            sb.AppendLine(
                $"if [ ! -e {quotedPath} ]; then echo {missingPayload}; " +
                $"elif [ -w {quotedPath} ]; then echo {rwPayload}; " +
                $"else echo {roPayload}; fi");
        }

        // 4. Git & worktree status
        sb.AppendLine($"echo \"{GitMarker}\"");
        if (scope.ScopeKind == SandboxScopeKind.SystemNightwatch)
        {
            sb.AppendLine("echo \"GIT_SKIPPED=nightwatch\"");
        }
        else
        {
            sb.AppendLine("if git rev-parse --is-inside-work-tree >/dev/null 2>&1; then");
            sb.AppendLine("  echo \"GIT_WORKTREE=true\"");
            sb.AppendLine("  echo \"GIT_DIR=$(git rev-parse --git-dir 2>&1)\"");
            sb.AppendLine("  git status --short --branch 2>&1");
            sb.AppendLine("else");
            sb.AppendLine("  echo \"GIT_WORKTREE=false\"");
            sb.AppendLine("  git status 2>&1");
            sb.AppendLine("fi");
        }

        // 5. SDKs & companion tools
        sb.AppendLine($"echo \"{ToolsMarker}\"");
        sb.AppendLine($"for cmd in {string.Join(' ', AllProbedTools)}; do");
        sb.AppendLine("  loc=$(command -v \"$cmd\" 2>/dev/null || true)");
        sb.AppendLine("  echo \"TOOL|$cmd|$loc\"");
        sb.AppendLine("done");

        // 6. libicu & globalization readiness
        sb.AppendLine($"echo \"{IcuMarker}\"");
        sb.AppendLine("echo \"INVARIANT=${DOTNET_SYSTEM_GLOBALIZATION_INVARIANT:-unset}\"");
        sb.AppendLine("echo \"LANG=${LANG:-unset}\"");
        sb.AppendLine("echo \"LC_ALL=${LC_ALL:-unset}\"");
        sb.AppendLine("icu_lib=$(ldconfig -p 2>/dev/null | grep -i libicu | head -n 3 | tr '\\n' ';')");
        sb.AppendLine("if [ -z \"$icu_lib\" ]; then icu_lib=$(find /usr/lib /lib -maxdepth 3 -name 'libicu*' 2>/dev/null | head -n 3 | tr '\\n' ';'); fi");
        sb.AppendLine("echo \"ICU_LIB=$icu_lib\"");
        sb.AppendLine("dotnet --info 2>&1 | head -n 20");

        return sb.ToString();
    }

    public static IReadOnlyList<EnvironmentDoctorCheckItem> ParseOutput(
        string output,
        string error,
        int exitCode,
        SandboxScopeDescriptor scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var sections = SplitSections(output ?? string.Empty);
        if (sections.Count == 0)
        {
            var diagnostic = string.IsNullOrWhiteSpace(error) ? output : $"{output}\n{error}".Trim();
            return
            [
                new EnvironmentDoctorCheckItem(
                    Category: "Execution",
                    Name: "Doctor Probe",
                    Status: DoctorCheckStatus.Failed,
                    Summary: $"Doctor diagnostic probe produced no structured output (exit code {exitCode}).",
                    DiagnosticOutput: string.IsNullOrWhiteSpace(diagnostic) ? null : diagnostic)
            ];
        }

        var results = new List<EnvironmentDoctorCheckItem>();

        if (sections.TryGetValue(IdentityMarker, out var identityLines))
        {
            results.Add(ParseIdentity(identityLines));
        }

        if (sections.TryGetValue(TmpCachesMarker, out var storageLines))
        {
            results.Add(ParseStorage(storageLines, scope.SecurityTier));
        }

        if (sections.TryGetValue(MountsMarker, out var mountLines))
        {
            results.Add(ParseMounts(mountLines));
        }

        if (sections.TryGetValue(GitMarker, out var gitLines))
        {
            results.Add(ParseGit(gitLines, scope));
        }

        if (sections.TryGetValue(ToolsMarker, out var toolLines))
        {
            results.Add(ParseTools(toolLines));
        }

        if (sections.TryGetValue(IcuMarker, out var icuLines))
        {
            results.Add(ParseIcu(icuLines));
        }

        if (exitCode != 0)
        {
            results.Add(new EnvironmentDoctorCheckItem(
                Category: "Execution",
                Name: "Doctor Probe",
                Status: DoctorCheckStatus.Failed,
                Summary: $"Doctor diagnostic probe exited abnormally (exit code {exitCode}).",
                DiagnosticOutput: string.IsNullOrWhiteSpace(error) ? null : error.Trim()));
        }

        return results;
    }

    private static IReadOnlyList<EnvironmentMountInfo> SelectMountsToProbe(
        SandboxScopeDescriptor scope,
        IReadOnlyList<EnvironmentMountInfo> expectedMounts)
    {
        var virtualMounts = expectedMounts
            .Where(m => m.ContainerPath.StartsWith(SandboxMountTable.WorkspaceVirtualRoot, StringComparison.OrdinalIgnoreCase) ||
                        m.ContainerPath.StartsWith(SandboxMountTable.NightwatchVirtualRoot, StringComparison.OrdinalIgnoreCase) ||
                        m.ContainerPath.StartsWith("/clone", StringComparison.OrdinalIgnoreCase))
            .DistinctBy(m => m.ContainerPath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (virtualMounts.Count > 0)
        {
            return virtualMounts;
        }

        if (scope.MountTable.Entries.Count > 0)
        {
            var isRestricted = scope.SecurityTier == SandboxSecurityTier.RestrictedReadOnly;
            return scope.MountTable.Entries
                .Select(e => new EnvironmentMountInfo(
                    !string.IsNullOrWhiteSpace(e.WslTargetDirectoryPath) ? e.WslTargetDirectoryPath : e.HostDirectoryPath,
                    e.VirtualMountPath,
                    e.IsReadOnly || (isRestricted && !e.AllowWriteInReadOnlyScope)))
                .DistinctBy(m => m.ContainerPath, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return expectedMounts
            .DistinctBy(m => m.ContainerPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static Dictionary<string, List<string>> SplitSections(string output)
    {
        var sections = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        string? currentMarker = null;

        foreach (var rawLine in output.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.TrimEnd();
            if (line is IdentityMarker or TmpCachesMarker or MountsMarker or GitMarker or ToolsMarker or IcuMarker)
            {
                currentMarker = line;
                if (!sections.ContainsKey(currentMarker))
                {
                    sections[currentMarker] = [];
                }
                continue;
            }

            if (currentMarker != null)
            {
                sections[currentMarker].Add(line);
            }
        }

        return sections;
    }

    private static EnvironmentDoctorCheckItem ParseIdentity(List<string> lines)
    {
        var kv = ParseKeyValues(lines);
        var uid = kv.GetValueOrDefault("UID", "unknown");
        var gid = kv.GetValueOrDefault("GID", "unknown");
        var user = kv.GetValueOrDefault("USER", "unknown");
        var home = kv.GetValueOrDefault("HOME", "unset");
        var homeWritable = kv.GetValueOrDefault("HOME_WRITABLE", "0") == "1";

        var status = homeWritable ? DoctorCheckStatus.Passed : DoctorCheckStatus.Failed;
        var summary = homeWritable
            ? $"uid={uid} gid={gid} ({user}), HOME={home} (writable)"
            : $"uid={uid} gid={gid} ({user}), HOME={home} is not writable";

        return new EnvironmentDoctorCheckItem(
            "Identity",
            "User & HOME",
            status,
            summary,
            string.Join('\n', lines));
    }

    private static EnvironmentDoctorCheckItem ParseStorage(List<string> lines, SandboxSecurityTier securityTier)
    {
        var entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            var parts = line.Split('|');
            if (parts.Length >= 3 && parts[0] == "DIR")
            {
                entries[parts[1]] = parts[2];
            }
        }

        var tmpState = entries.GetValueOrDefault("/tmp", "MISSING");
        var warnings = new List<string>();
        var failures = new List<string>();

        if (tmpState == "MISSING")
        {
            failures.Add("/tmp missing");
        }
        else if (tmpState == "RO")
        {
            if (securityTier == SandboxSecurityTier.RestrictedReadOnly)
            {
                warnings.Add("/tmp is read-only (RestrictedReadOnly)");
            }
            else
            {
                failures.Add("/tmp is read-only");
            }
        }

        foreach (var cachePath in StorageProbePaths.Skip(1))
        {
            var state = entries.GetValueOrDefault(cachePath, "MISSING");
            if (state != "RW")
            {
                warnings.Add($"{cachePath}={state}");
            }
        }

        var status = failures.Count > 0
            ? DoctorCheckStatus.Failed
            : (warnings.Count > 0 ? DoctorCheckStatus.Warning : DoctorCheckStatus.Passed);

        var summary = status switch
        {
            DoctorCheckStatus.Passed => "/tmp and package caches (/cache/nuget, /cache/npm) are writable.",
            DoctorCheckStatus.Warning => $"Storage warnings: {string.Join(", ", warnings)}.",
            _ => $"Storage failures: {string.Join(", ", failures.Concat(warnings))}."
        };

        return new EnvironmentDoctorCheckItem(
            "Storage",
            "/tmp & Package Caches",
            status,
            summary,
            string.Join('\n', lines));
    }

    private static EnvironmentDoctorCheckItem ParseMounts(List<string> lines)
    {
        var mismatches = new List<string>();
        var checkedCount = 0;

        foreach (var line in lines)
        {
            var parts = line.Split('|');
            if (parts.Length < 4 || parts[0] != "MOUNT")
            {
                continue;
            }

            checkedCount++;
            var path = parts[1];
            var expected = parts[2];
            var actual = parts[3];

            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            {
                mismatches.Add($"{path} (expected {expected}, actual {actual})");
            }
        }

        if (checkedCount == 0)
        {
            return new EnvironmentDoctorCheckItem(
                "Mounts",
                "Virtual Mount Integrity",
                DoctorCheckStatus.Warning,
                "No virtual mounts were probed.",
                string.Join('\n', lines));
        }

        var status = mismatches.Count == 0 ? DoctorCheckStatus.Passed : DoctorCheckStatus.Failed;
        var summary = mismatches.Count == 0
            ? $"All {checkedCount} virtual mount(s) match expected :ro/:rw permissions."
            : $"Mount mismatch: {string.Join(", ", mismatches)}.";

        return new EnvironmentDoctorCheckItem(
            "Mounts",
            "Virtual Mount Integrity",
            status,
            summary,
            string.Join('\n', lines));
    }

    private static EnvironmentDoctorCheckItem ParseGit(List<string> lines, SandboxScopeDescriptor scope)
    {
        var kv = ParseKeyValues(lines);
        if (scope.ScopeKind == SandboxScopeKind.SystemNightwatch ||
            kv.ContainsKey("GIT_SKIPPED"))
        {
            return new EnvironmentDoctorCheckItem(
                "Git",
                "Git & Worktree Status",
                DoctorCheckStatus.Passed,
                "Git worktree check skipped for System Nightwatch scope.",
                string.Join('\n', lines));
        }

        var isWorktree = string.Equals(kv.GetValueOrDefault("GIT_WORKTREE"), "true", StringComparison.OrdinalIgnoreCase);
        if (!isWorktree)
        {
            return new EnvironmentDoctorCheckItem(
                "Git",
                "Git & Worktree Status",
                DoctorCheckStatus.Failed,
                "Working directory is not a valid Git worktree.",
                string.Join('\n', lines));
        }

        var gitDir = kv.GetValueOrDefault("GIT_DIR", ".git");
        var branchLine = lines.FirstOrDefault(l => l.StartsWith("## ", StringComparison.Ordinal));
        var summary = string.IsNullOrWhiteSpace(branchLine)
            ? $"Valid Git worktree (git-dir: {gitDir})."
            : $"Valid Git worktree ({branchLine[3..].Trim()}, git-dir: {gitDir}).";

        return new EnvironmentDoctorCheckItem(
            "Git",
            "Git & Worktree Status",
            DoctorCheckStatus.Passed,
            summary,
            string.Join('\n', lines));
    }

    private static EnvironmentDoctorCheckItem ParseTools(List<string> lines)
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            var parts = line.Split('|');
            if (parts.Length >= 3 && parts[0] == "TOOL")
            {
                found[parts[1]] = parts[2].Trim();
            }
        }

        var missingCore = CoreTools.Where(t => string.IsNullOrWhiteSpace(found.GetValueOrDefault(t))).ToList();
        var missingCompanion = CompanionTools.Where(t => string.IsNullOrWhiteSpace(found.GetValueOrDefault(t))).ToList();

        var status = missingCore.Count > 0
            ? DoctorCheckStatus.Failed
            : (missingCompanion.Count > 0 ? DoctorCheckStatus.Warning : DoctorCheckStatus.Passed);

        var summary = status switch
        {
            DoctorCheckStatus.Passed => "All core SDKs and companion tools are available on PATH.",
            DoctorCheckStatus.Warning => $"Core tools available; missing companion tool(s): {string.Join(", ", missingCompanion)}.",
            _ => $"Missing core tool(s): {string.Join(", ", missingCore)}." +
                 (missingCompanion.Count > 0 ? $" Missing companion tool(s): {string.Join(", ", missingCompanion)}." : string.Empty)
        };

        return new EnvironmentDoctorCheckItem(
            "Toolchains",
            "SDKs & Companion Tools",
            status,
            summary,
            string.Join('\n', lines));
    }

    private static EnvironmentDoctorCheckItem ParseIcu(List<string> lines)
    {
        var kv = ParseKeyValues(lines);
        var invariant = kv.GetValueOrDefault("INVARIANT", "unset");
        var lang = kv.GetValueOrDefault("LANG", "unset");
        var icuLib = kv.GetValueOrDefault("ICU_LIB", string.Empty);

        var isInvariant = string.Equals(invariant, "1", StringComparison.Ordinal) ||
                          string.Equals(invariant, "true", StringComparison.OrdinalIgnoreCase);
        var hasIcuError = lines.Any(l =>
            l.Contains("Couldn't find a valid ICU package", StringComparison.OrdinalIgnoreCase) ||
            l.Contains("globalization-invariant mode", StringComparison.OrdinalIgnoreCase));
        var hasDotnetInfo = lines.Any(l =>
            l.Contains(".NET SDK", StringComparison.OrdinalIgnoreCase) ||
            l.Contains("Host:", StringComparison.OrdinalIgnoreCase) ||
            l.Contains("RID:", StringComparison.OrdinalIgnoreCase));

        DoctorCheckStatus status;
        string summary;
        if (isInvariant || hasIcuError)
        {
            status = DoctorCheckStatus.Failed;
            summary = isInvariant
                ? $"DOTNET_SYSTEM_GLOBALIZATION_INVARIANT={invariant} disables ICU globalization."
                : "dotnet reported a missing ICU globalization package.";
        }
        else if (!string.IsNullOrWhiteSpace(icuLib))
        {
            status = DoctorCheckStatus.Passed;
            summary = $"libicu detected ({icuLib.TrimEnd(';')}), LANG={lang}.";
        }
        else if (hasDotnetInfo)
        {
            status = DoctorCheckStatus.Warning;
            summary = $"dotnet --info succeeded (LANG={lang}), but libicu was not found via ldconfig/standard paths.";
        }
        else
        {
            status = DoctorCheckStatus.Failed;
            summary = "Neither libicu nor a working dotnet runtime could be verified.";
        }

        return new EnvironmentDoctorCheckItem(
            "Globalization",
            ".NET & ICU Readiness",
            status,
            summary,
            string.Join('\n', lines));
    }

    private static Dictionary<string, string> ParseKeyValues(IEnumerable<string> lines)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            var idx = line.IndexOf('=');
            if (idx > 0)
            {
                dict[line[..idx].Trim()] = line[(idx + 1)..].Trim();
            }
        }
        return dict;
    }
}
