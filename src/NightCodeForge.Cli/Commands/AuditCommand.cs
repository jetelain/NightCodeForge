namespace NightCodeForge.Cli.Commands;

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NightCodeForge.Cli.Common;
using NightCodeForge.Cli.Services;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Workflow;

public class AuditOptions
{
    public string TargetPath { get; set; } = Directory.GetCurrentDirectory();
}

public enum PillarStatus
{
    Healthy, // 🟢
    Warning, // 🟡
    Blocker  // 🔴
}

public record PillarResult(
    string Name,
    PillarStatus Status,
    string Summary,
    IReadOnlyList<string> Details);

public class AuditCommand
{
    private readonly IProcessRunner _processRunner;

    public AuditCommand(IProcessRunner processRunner)
    {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
    }

    public async Task<int> ExecuteAsync(AuditOptions options, Action<string>? log = null, CancellationToken cancellationToken = default)
    {
        log ??= Console.WriteLine;
        var rawPath = string.IsNullOrWhiteSpace(options.TargetPath) ? Directory.GetCurrentDirectory() : options.TargetPath;
        var targetDir = Path.GetFullPath(rawPath);

        if (!Directory.Exists(targetDir))
        {
            log($"[ncf] Error: Directory not found: {targetDir}");
            return 2;
        }

        log($"[ncf] Running readiness audit against: {targetDir}\n");

        var results = new List<PillarResult>();

        // Load config if available
        var configPath = Path.Combine(targetDir, ".nightcodeforge", "config.json");
        WorkspaceProjectConfig? config = null;
        string? configParseError = null;
        if (File.Exists(configPath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(configPath, cancellationToken);
                config = JsonSerializer.Deserialize<WorkspaceProjectConfig>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (Exception ex)
            {
                configParseError = ex.Message;
            }
        }

        var detectedPreset = CodeStackPresets.Detect(targetDir);

        // Pillar 1: Git Health
        var gitHealth = await AuditGitHealthAsync(targetDir, config, cancellationToken);
        results.Add(gitHealth);

        // Pillar 2: Build Determinism
        var buildDeterminism = await AuditBuildDeterminismAsync(targetDir, config, detectedPreset, cancellationToken);
        results.Add(buildDeterminism);

        // Pillar 3: Test Fast Feedback
        var testFastFeedback = await AuditTestFastFeedbackAsync(targetDir, config, detectedPreset, cancellationToken);
        results.Add(testFastFeedback);

        // Pillar 4: Isolation Readiness
        var isolationReadiness = AuditIsolationReadiness(targetDir);
        results.Add(isolationReadiness);

        // Pillar 5: NCF Configuration
        var ncfConfiguration = AuditNcfConfiguration(targetDir, configPath, config, configParseError);
        results.Add(ncfConfiguration);

        // Determine Overall Status
        var overallStatus = PillarStatus.Healthy;
        if (results.Any(r => r.Status == PillarStatus.Blocker))
        {
            overallStatus = PillarStatus.Blocker;
        }
        else if (results.Any(r => r.Status == PillarStatus.Warning))
        {
            overallStatus = PillarStatus.Warning;
        }

        // Print Console Scorecard
        PrintScorecard(results, overallStatus, log);

        // Write .nightcodeforge/audit-report.md
        var reportDir = Path.Combine(targetDir, ".nightcodeforge");
        if (!Directory.Exists(reportDir))
        {
            Directory.CreateDirectory(reportDir);
        }
        var reportPath = Path.Combine(reportDir, "audit-report.md");
        var reportMarkdown = GenerateMarkdownReport(targetDir, results, overallStatus);
        await File.WriteAllTextAsync(reportPath, reportMarkdown, cancellationToken);
        log($"\n[ncf] Detailed audit scorecard written to: {reportPath}");

        return overallStatus switch
        {
            PillarStatus.Healthy => 0,
            PillarStatus.Warning => 1,
            _ => 2
        };
    }

    private async Task<PillarResult> AuditGitHealthAsync(string targetDir, WorkspaceProjectConfig? config, CancellationToken cancellationToken)
    {
        var details = new List<string>();

        // Check if git repo
        var isGitRepo = Directory.Exists(Path.Combine(targetDir, ".git"));
        if (!isGitRepo)
        {
            var probe = await CliProcessHelper.RunCommandAsync(_processRunner, "git rev-parse --is-inside-work-tree", targetDir, 10, cancellationToken);
            if (probe.ExitCode == 0 && probe.StandardOutput.Trim().Equals("true", StringComparison.OrdinalIgnoreCase))
            {
                isGitRepo = true;
            }
        }

        if (!isGitRepo)
        {
            details.Add("Directory is not a Git repository. Run 'git init'.");
            return new PillarResult("Git Health", PillarStatus.Blocker, "Not a Git repository", details);
        }

        // Check committed HEAD
        var headCheck = await CliProcessHelper.RunCommandAsync(_processRunner, "git rev-parse --verify HEAD", targetDir, 10, cancellationToken);
        if (headCheck.ExitCode != 0)
        {
            details.Add("Repository has no commits (unborn HEAD). Commit your baseline files first.");
            return new PillarResult("Git Health", PillarStatus.Blocker, "Unborn HEAD (no commits)", details);
        }

        // Check current branch
        var branchCheck = await CliProcessHelper.RunCommandAsync(_processRunner, "git branch --show-current", targetDir, 10, cancellationToken);
        var currentBranch = branchCheck.StandardOutput.Trim();

        if (string.IsNullOrWhiteSpace(currentBranch) || currentBranch.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
        {
            details.Add("Repository is in a detached HEAD state. Switch to your default branch.");
            return new PillarResult("Git Health", PillarStatus.Blocker, "Detached HEAD", details);
        }

        bool isPrimaryBranch;
        string expectedBranchDesc;
        if (!string.IsNullOrWhiteSpace(config?.BaseBranch))
        {
            isPrimaryBranch = currentBranch.Equals(config.BaseBranch, StringComparison.OrdinalIgnoreCase);
            expectedBranchDesc = $"configured base branch '{config.BaseBranch}'";
        }
        else
        {
            isPrimaryBranch = currentBranch.Equals("main", StringComparison.OrdinalIgnoreCase) ||
                              currentBranch.Equals("master", StringComparison.OrdinalIgnoreCase);
            expectedBranchDesc = "default branch ('main' or 'master')";
        }

        if (!isPrimaryBranch)
        {
            details.Add($"Current branch is '{currentBranch}', expected {expectedBranchDesc}.");
            return new PillarResult("Git Health", PillarStatus.Blocker, $"Not on primary branch ({currentBranch})", details);
        }

        // Check clean working tree
        var statusCheck = await CliProcessHelper.RunCommandAsync(_processRunner, "git status --porcelain", targetDir, 10, cancellationToken);
        var dirtyLines = statusCheck.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => !l.Replace('\\', '/').EndsWith(".nightcodeforge/audit-report.md", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (dirtyLines.Count > 0)
        {
            details.Add($"Working tree is dirty ({dirtyLines.Count} uncommitted or untracked changes).");
            foreach (var line in dirtyLines.Take(5))
            {
                details.Add($"  {line}");
            }
            if (dirtyLines.Count > 5)
            {
                details.Add($"  ... and {dirtyLines.Count - 5} more.");
            }
            details.Add("Remediation: Commit or stash baseline changes before running automated workflows.");
            return new PillarResult("Git Health", PillarStatus.Blocker, "Dirty working tree", details);
        }

        details.Add($"Committed HEAD on branch '{currentBranch}'. Clean working tree.");
        return new PillarResult("Git Health", PillarStatus.Healthy, $"Clean HEAD on {currentBranch}", details);
    }

    private async Task<PillarResult> AuditBuildDeterminismAsync(
        string targetDir,
        WorkspaceProjectConfig? config,
        CodeStackPreset? preset,
        CancellationToken cancellationToken)
    {
        var details = new List<string>();
        var buildCommand = config?.BuildCommand ?? preset?.BuildCommand;

        if (string.IsNullOrWhiteSpace(buildCommand))
        {
            details.Add("No build command configured and technology stack could not be determined.");
            return new PillarResult("Build Determinism", PillarStatus.Blocker, "No build command configured", details);
        }

        if (buildCommand.StartsWith("dotnet build", StringComparison.OrdinalIgnoreCase) && !buildCommand.Contains("--nologo", StringComparison.OrdinalIgnoreCase))
        {
            buildCommand += " --nologo";
        }

        details.Add($"Executing build command: {buildCommand}");
        var timeout = config?.BuildTimeoutSeconds ?? Constants.DefaultCliCommandTimeoutSeconds;
        var result = await CliProcessHelper.RunCommandAsync(_processRunner, buildCommand, targetDir, timeout, cancellationToken);

        if (result.ExitCode != 0)
        {
            var err = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
            var snippet = Truncate(err.Trim(), 400);
            details.Add($"Build failed with exit code {result.ExitCode}.");
            if (!string.IsNullOrWhiteSpace(snippet))
            {
                details.Add(snippet);
            }
            return new PillarResult("Build Determinism", PillarStatus.Blocker, $"Build failed (exit code {result.ExitCode})", details);
        }

        details.Add("CLI build succeeded with non-interactive execution.");
        return new PillarResult("Build Determinism", PillarStatus.Healthy, "Build succeeded", details);
    }

    private async Task<PillarResult> AuditTestFastFeedbackAsync(
        string targetDir,
        WorkspaceProjectConfig? config,
        CodeStackPreset? preset,
        CancellationToken cancellationToken)
    {
        var details = new List<string>();
        var testCommand = config?.TestCommand ?? preset?.TestCommand;

        if (string.IsNullOrWhiteSpace(testCommand))
        {
            details.Add("No test command configured. Fast feedback test suite cannot be verified.");
            return new PillarResult("Test Fast Feedback", PillarStatus.Warning, "No test command configured", details);
        }

        // Apply fast filter for dotnet
        if (testCommand.StartsWith("dotnet test", StringComparison.OrdinalIgnoreCase))
        {
            if (!testCommand.Contains("--filter", StringComparison.OrdinalIgnoreCase))
            {
                testCommand += $" --filter \"{WorkflowDefaults.FastTestCategoryFilter}\"";
            }
            if (!testCommand.Contains("--nologo", StringComparison.OrdinalIgnoreCase))
            {
                testCommand += " --nologo";
            }
        }

        details.Add($"Executing fast test command: {testCommand}");
        var timeout = config?.TestTimeoutSeconds ?? Constants.DefaultCliCommandTimeoutSeconds;
        var result = await CliProcessHelper.RunCommandAsync(_processRunner, testCommand, targetDir, timeout, cancellationToken);

        if (result.ExitCode != 0)
        {
            var err = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
            var snippet = Truncate(err.Trim(), 400);
            details.Add($"Fast test run failed with exit code {result.ExitCode}.");
            if (!string.IsNullOrWhiteSpace(snippet))
            {
                details.Add(snippet);
            }
            return new PillarResult("Test Fast Feedback", PillarStatus.Blocker, $"Test failure (exit code {result.ExitCode})", details);
        }

        // Check if 0 tests were executed across supported frameworks
        var output = result.StandardOutput;
        var isZeroTests = Regex.IsMatch(output, @"total\s*:\s*0\b", RegexOptions.IgnoreCase) ||
                          Regex.IsMatch(output, @"running\s+0\s+tests", RegexOptions.IgnoreCase) ||
                          Regex.IsMatch(output, @"collected\s+0\s+items", RegexOptions.IgnoreCase) ||
                          output.Contains("no tests ran", StringComparison.OrdinalIgnoreCase) ||
                          output.Contains("No test matches the given testcase filter", StringComparison.OrdinalIgnoreCase) ||
                          output.Contains("Aucun test ne correspond au filtre", StringComparison.OrdinalIgnoreCase) ||
                          output.Contains("[no test files]", StringComparison.OrdinalIgnoreCase) ||
                          output.Contains("No tests found", StringComparison.OrdinalIgnoreCase);

        if (isZeroTests)
        {
            details.Add("Test command succeeded, but 0 test cases were executed matching fast filter.");
            return new PillarResult("Test Fast Feedback", PillarStatus.Warning, "0 fast tests executed", details);
        }

        details.Add("Fast unit test suite passed on baseline branch without long-running E2E tests.");
        return new PillarResult("Test Fast Feedback", PillarStatus.Healthy, "Fast tests passing", details);
    }

    private PillarResult AuditIsolationReadiness(string targetDir)
    {
        var details = new List<string>();
        var sourceExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".cs", ".ts", ".js", ".py", ".rs", ".go"
        };

        var ignoredDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".git", ".vs", ".nightcodeforge", ".agents", "bin", "obj", "node_modules", "TestResults", "target", "dist", "build", "venv", ".venv", "__pycache__"
        };

        var machinePathRegex = new Regex(@"(?:[a-zA-Z]:[/\\]{1,2}(?:Users|home|root)\b|/(?:Users|home|root)/)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        var unmockedNetworkRegex = new Regex(@"(?:new\s+HttpClient\s*\(\)|new\s+TcpClient\s*\(\)|https?://(?!localhost|127\.0\.0\.1|example\.com)[a-zA-Z0-9\-\.]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        var findings = new List<string>();

        try
        {
            ScanDirectory(targetDir, targetDir, sourceExtensions, ignoredDirs, machinePathRegex, unmockedNetworkRegex, findings);
        }
        catch (Exception ex)
        {
            details.Add($"Heuristic scan encountered access issue: {ex.Message}");
        }

        if (findings.Count > 0)
        {
            details.Add($"Detected {findings.Count} potential isolation issues (hardcoded machine paths or unmocked network services):");
            foreach (var f in findings.Take(8))
            {
                details.Add($"  - {f}");
            }
            if (findings.Count > 8)
            {
                details.Add($"  ... and {findings.Count - 8} more.");
            }
            return new PillarResult("Isolation Readiness", PillarStatus.Warning, $"{findings.Count} potential isolation warnings", details);
        }

        details.Add("No hardcoded machine paths or unmocked external network calls detected in source/test files.");
        return new PillarResult("Isolation Readiness", PillarStatus.Healthy, "No isolation issues detected", details);
    }

    private void ScanDirectory(
        string rootDir,
        string currentDir,
        HashSet<string> extensions,
        HashSet<string> ignoredDirs,
        Regex machinePathRegex,
        Regex unmockedNetworkRegex,
        List<string> findings)
    {
        foreach (var file in Directory.EnumerateFiles(currentDir))
        {
            var ext = Path.GetExtension(file);
            if (!extensions.Contains(ext)) continue;

            var relativePath = Path.GetRelativePath(rootDir, file);
            var isTestFile = relativePath.Contains("test", StringComparison.OrdinalIgnoreCase) ||
                             relativePath.Contains("spec", StringComparison.OrdinalIgnoreCase);

            try
            {
                var lines = File.ReadAllLines(file);
                bool inBlockComment = false;
                for (int i = 0; i < lines.Length; i++)
                {
                    var line = lines[i];
                    var trimmed = line.TrimStart();

                    if (inBlockComment)
                    {
                        if (line.Contains("*/"))
                        {
                            inBlockComment = false;
                        }
                        continue;
                    }

                    if (trimmed.StartsWith("/*"))
                    {
                        if (!line.Contains("*/"))
                        {
                            inBlockComment = true;
                        }
                        continue;
                    }

                    // Skip single-line comments in heuristic scan to avoid false positives
                    if (trimmed.StartsWith("//") || trimmed.StartsWith("#") || trimmed.StartsWith("*"))
                    {
                        continue;
                    }

                    // Check hardcoded machine paths in all code
                    if (machinePathRegex.IsMatch(line))
                    {
                        findings.Add($"{relativePath}:{i + 1} (Hardcoded path): {line.Trim()}");
                    }

                    // Check unmocked network in tests
                    if (isTestFile && unmockedNetworkRegex.IsMatch(line))
                    {
                        // Ignore schemas and standard namespace URLs
                        if (line.Contains("schemas.microsoft.com", StringComparison.OrdinalIgnoreCase) ||
                            line.Contains("json.schemastore.org", StringComparison.OrdinalIgnoreCase) ||
                            line.Contains("www.w3.org", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        // Check if file or line has Mock or HttpMessageHandler
                        if (!line.Contains("Mock", StringComparison.OrdinalIgnoreCase) &&
                            !line.Contains("HttpMessageHandler", StringComparison.OrdinalIgnoreCase) &&
                            !line.Contains("fake", StringComparison.OrdinalIgnoreCase) &&
                            !line.Contains("stub", StringComparison.OrdinalIgnoreCase))
                        {
                            findings.Add($"{relativePath}:{i + 1} (Unmocked external client/URL): {line.Trim()}");
                        }
                    }
                }
            }
            catch
            {
                // Skip unreadable files
            }
        }

        IEnumerable<string> subDirs;
        try
        {
            subDirs = Directory.EnumerateDirectories(currentDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var subDir in subDirs)
        {
            var dirName = Path.GetFileName(subDir);
            if (ignoredDirs.Contains(dirName) || dirName.StartsWith('.')) continue;

            ScanDirectory(rootDir, subDir, extensions, ignoredDirs, machinePathRegex, unmockedNetworkRegex, findings);
        }
    }

    private PillarResult AuditNcfConfiguration(string targetDir, string configPath, WorkspaceProjectConfig? config, string? configParseError = null)
    {
        var details = new List<string>();

        if (!File.Exists(configPath) || config == null)
        {
            if (!string.IsNullOrWhiteSpace(configParseError))
            {
                details.Add($".nightcodeforge/config.json is corrupted or invalid JSON: {configParseError}");
            }
            else
            {
                details.Add(".nightcodeforge/config.json is missing or unparseable. Run 'ncf init' to bootstrap configuration.");
            }
            return new PillarResult("NCF Configuration", PillarStatus.Blocker, "Missing or invalid config.json", details);
        }

        bool hasWarnings = false;

        // Check ncfVersion
        if (string.IsNullOrWhiteSpace(config.NcfVersion) || !config.NcfVersion.Equals(Constants.NcfVersion, StringComparison.OrdinalIgnoreCase))
        {
            details.Add($"Config ncfVersion '{config.NcfVersion ?? "none"}' does not match installed CLI version '{Constants.NcfVersion}'. Run 'ncf init' to re-sync.");
            hasWarnings = true;
        }

        // Check baseBranch
        if (string.IsNullOrWhiteSpace(config.BaseBranch))
        {
            details.Add("Config baseBranch is missing. Run 'ncf init' to configure base branch.");
            hasWarnings = true;
        }

        // Check .gitignore and .editorconfig managed blocks
        var gitignorePath = Path.Combine(targetDir, ".gitignore");
        if (!File.Exists(gitignorePath) || !File.ReadAllText(gitignorePath).Contains(ManagedBlockConstants.Header))
        {
            details.Add(".gitignore is missing or lacks the NightCodeForge managed block. Run 'ncf init' to synchronize.");
            hasWarnings = true;
        }

        var editorconfigPath = Path.Combine(targetDir, ".editorconfig");
        if (!File.Exists(editorconfigPath) || !File.ReadAllText(editorconfigPath).Contains(ManagedBlockConstants.Header))
        {
            details.Add(".editorconfig is missing or lacks the NightCodeForge managed block. Run 'ncf init' to synchronize.");
            hasWarnings = true;
        }

        // Check required skills
        var requiredSkills = SkillsInstaller.GetRequiredSkillNames();
        bool missingSkill = false;

        foreach (var skillName in requiredSkills)
        {
            var skillFile = Path.Combine(targetDir, ".agents", "skills", skillName, "SKILL.md");
            if (!File.Exists(skillFile))
            {
                details.Add($"Required skill '{skillName}' not found in .agents/skills/{skillName}/. Run 'ncf init' to install.");
                missingSkill = true;
            }
            else
            {
                var content = File.ReadAllText(skillFile);
                var match = Regex.Match(content, @"^version:\s*(\d+)", RegexOptions.Multiline);
                var embeddedVer = SkillsInstaller.GetEmbeddedSkillVersion(skillName);
                if (match.Success && int.TryParse(match.Groups[1].Value, out var v))
                {
                    if (v != embeddedVer)
                    {
                        details.Add($"Skill '{skillName}' version {v} drifts from embedded version {embeddedVer}. Run 'ncf init' to re-sync.");
                        hasWarnings = true;
                    }
                }
            }
        }

        if (missingSkill)
        {
            return new PillarResult("NCF Configuration", PillarStatus.Blocker, "Missing packaged skills", details);
        }

        if (hasWarnings)
        {
            return new PillarResult("NCF Configuration", PillarStatus.Warning, "Configuration or version drift detected", details);
        }

        details.Add($".nightcodeforge/config.json valid (stack: {config.CodeStack}, v{config.NcfVersion}, branch: {config.BaseBranch}). All packaged skills and managed blocks in sync.");
        return new PillarResult("NCF Configuration", PillarStatus.Healthy, "Configuration and skills in sync", details);
    }

    private static void PrintScorecard(List<PillarResult> results, PillarStatus overall, Action<string> log)
    {
        var overallIcon = StatusIcon(overall);
        var overallLabel = overall switch
        {
            PillarStatus.Healthy => "READY (All checks passed)",
            PillarStatus.Warning => "WARNINGS (Advisory issues present)",
            _ => "BLOCKED (Critical issues must be resolved)"
        };

        log("================================================================================");
        log($"  NIGHTCODEFORGE REPOSITORY READINESS SCORECARD — {overallIcon} {overallLabel}");
        log("================================================================================");
        log(string.Format("  {0,-25} {1,-8} {2}", "Pillar", "Status", "Summary"));
        log("  ------------------------------------------------------------------------------");

        foreach (var r in results)
        {
            log(string.Format("  {0,-25} {1,-8} {2}", r.Name, StatusIcon(r.Status), r.Summary));
        }

        log("================================================================================");

        var nonGreen = results.Where(r => r.Status != PillarStatus.Healthy).ToList();
        if (nonGreen.Count > 0)
        {
            log("\nRemediation Details:");
            foreach (var r in nonGreen)
            {
                log($"\n[{StatusIcon(r.Status)}] {r.Name}:");
                foreach (var d in r.Details)
                {
                    log($"  {d}");
                }
            }
            log("\nTip: Run 'ncf-project-readiness' skill in your AI assistant to guide remediation.");
        }
    }

    private static string GenerateMarkdownReport(string targetDir, List<PillarResult> results, PillarStatus overall)
    {
        var sb = new StringBuilder();
        var overallIcon = StatusIcon(overall);
        var overallText = overall switch
        {
            PillarStatus.Healthy => "🟢 Healthy (Ready for automated workflows)",
            PillarStatus.Warning => "🟡 Warnings (Advisory items detected)",
            _ => "🔴 Blockers (Must be resolved before running agents)"
        };

        sb.AppendLine("# 🌙 NightCodeForge Repository Readiness Audit Report");
        sb.AppendLine();
        sb.AppendLine($"> **Audit Date:** {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC  ");
        sb.AppendLine($"> **CLI Version:** v{Constants.NcfVersion}  ");
        sb.AppendLine($"> **Target Repository:** `{targetDir}`  ");
        sb.AppendLine($"> **Overall Status:** {overallText}");
        sb.AppendLine();
        sb.AppendLine("## Summary Scorecard");
        sb.AppendLine();
        sb.AppendLine("| Pillar | Status | Summary |");
        sb.AppendLine("| :--- | :---: | :--- |");

        foreach (var r in results)
        {
            sb.AppendLine($"| **{r.Name}** | {StatusIcon(r.Status)} | {r.Summary} |");
        }

        sb.AppendLine();
        sb.AppendLine("## Detailed Pillar Findings & Remediation Guidance");
        sb.AppendLine();

        int index = 1;
        foreach (var r in results)
        {
            sb.AppendLine($"### {index++}. {r.Name} ({StatusIcon(r.Status)})");
            sb.AppendLine();
            sb.AppendLine($"- **Status Summary:** {r.Summary}");
            if (r.Details.Count > 0)
            {
                sb.AppendLine("- **Diagnostic Findings:**");
                foreach (var d in r.Details)
                {
                    sb.AppendLine($"  - {d}");
                }
            }
            sb.AppendLine();
        }

        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("*This report was deterministically generated by `ncf audit`. For interactive step-by-step remediation of warnings or blockers, load the `ncf-project-readiness` skill in your AI assistant.*");

        return sb.ToString().Replace("\r\n", "\n");
    }

    private static string StatusIcon(PillarStatus status) => status switch
    {
        PillarStatus.Healthy => "🟢",
        PillarStatus.Warning => "🟡",
        _ => "🔴"
    };

    private static string Truncate(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength) return text;
        return text.Substring(0, maxLength) + "...";
    }
}
