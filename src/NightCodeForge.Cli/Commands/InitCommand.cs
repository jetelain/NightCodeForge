namespace NightCodeForge.Cli.Commands;

using NightCodeForge.Cli.Common;
using NightCodeForge.Cli.Services;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;

public class InitOptions
{
    public string TargetPath { get; set; } = Directory.GetCurrentDirectory();
    public bool DryRun { get; set; }
    public bool SkipFormat { get; set; }
    public string? Stack { get; set; }
}

public class InitCommand
{
    private readonly IProcessRunner _processRunner;

    public InitCommand(IProcessRunner processRunner)
    {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
    }

    public async Task<int> ExecuteAsync(InitOptions options, Action<string>? log = null, CancellationToken cancellationToken = default)
    {
        log ??= Console.WriteLine;
        var rawPath = string.IsNullOrWhiteSpace(options.TargetPath) ? Directory.GetCurrentDirectory() : options.TargetPath;
        var targetDir = Path.GetFullPath(rawPath);

        if (!Directory.Exists(targetDir))
        {
            log($"[ncf] Error: Directory not found: {targetDir}");
            return 2;
        }

        // 1. Git repository check
        var isGitRepo = Directory.Exists(Path.Combine(targetDir, ".git"));
        if (!isGitRepo)
        {
            var gitCheck = await CliProcessHelper.RunCommandAsync(_processRunner, "git rev-parse --is-inside-work-tree", targetDir, 10, cancellationToken);
            if (gitCheck.ExitCode == 0 && gitCheck.StandardOutput.Trim().Equals("true", StringComparison.OrdinalIgnoreCase))
            {
                isGitRepo = true;
            }
        }

        if (!isGitRepo)
        {
            log("[ncf] Error: Target directory is not a Git repository. Run 'git init' before initializing NightCodeForge.");
            return 2;
        }

        // 2. Stack detection
        CodeStackPreset? preset = null;
        if (!string.IsNullOrWhiteSpace(options.Stack))
        {
            if (!CodeStackPresets.TryResolve(options.Stack, out preset) || preset == null)
            {
                log($"[ncf] Error: Unknown tech stack '{options.Stack}'. Supported stacks: dotnet, node, python, rust, go.");
                return 2;
            }
        }
        else
        {
            preset = CodeStackPresets.Detect(targetDir);
            if (preset == null)
            {
                log("[ncf] Error: Could not automatically detect project technology stack.\nPlease specify --stack <dotnet|node|python|rust|go>.");
                return 2;
            }
        }

        log($"[ncf] Detected technology stack: {preset.Name}");

        // 3. Resolve Git base branch
        var branchResult = await CliProcessHelper.RunCommandAsync(_processRunner, "git branch --show-current", targetDir, 10, cancellationToken);
        var baseBranch = branchResult.StandardOutput.Trim();
        if (string.IsNullOrWhiteSpace(baseBranch) || baseBranch.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
        {
            // Check if master exists, otherwise default to main
            var masterCheck = await CliProcessHelper.RunCommandAsync(_processRunner, "git rev-parse --verify master", targetDir, 10, cancellationToken);
            baseBranch = masterCheck.ExitCode == 0 ? "master" : "main";
        }
        log($"[ncf] Resolved base branch: {baseBranch}");

        // 4. Update .gitignore
        var gitignorePath = Path.Combine(targetDir, ".gitignore");
        var existingGitignore = File.Exists(gitignorePath) ? await File.ReadAllTextAsync(gitignorePath, cancellationToken) : null;
        var updatedGitignore = GitignorePreparer.Prepare(existingGitignore);

        if (options.DryRun)
        {
            log("[dry-run] Would update .gitignore with managed block");
        }
        else
        {
            await File.WriteAllTextAsync(gitignorePath, updatedGitignore, cancellationToken);
            if (existingGitignore != null && string.Equals(existingGitignore.Replace("\r\n", "\n"), updatedGitignore, StringComparison.Ordinal))
            {
                log("[ncf] .gitignore is already up to date.");
            }
            else
            {
                log("[ncf] .gitignore updated with managed block.");
            }
        }

        // 5. Update .editorconfig
        var editorconfigPath = Path.Combine(targetDir, ".editorconfig");
        var existingEditorconfig = File.Exists(editorconfigPath) ? await File.ReadAllTextAsync(editorconfigPath, cancellationToken) : null;
        var updatedEditorconfig = EditorConfigPreparer.Prepare(existingEditorconfig);

        if (options.DryRun)
        {
            log("[dry-run] Would update .editorconfig with managed block");
        }
        else
        {
            await File.WriteAllTextAsync(editorconfigPath, updatedEditorconfig, cancellationToken);
            if (existingEditorconfig != null && string.Equals(existingEditorconfig.Replace("\r\n", "\n"), updatedEditorconfig, StringComparison.Ordinal))
            {
                log("[ncf] .editorconfig is already up to date.");
            }
            else
            {
                log("[ncf] .editorconfig updated with managed block (LF, utf-8, indent).");
            }
        }

        // 6. Install packaged skills
        SkillsInstaller.InstallSkills(targetDir, options.DryRun, log);

        // 7. Bootstrap .nightcodeforge/config.json
        ConfigBootstrapper.BootstrapConfig(targetDir, preset, baseBranch, options.DryRun, log);

        // 8. Initial format pass
        if (options.DryRun)
        {
            log($"[dry-run] Would run initial format command: {preset.FormatCommand ?? "none"}");
        }
        else if (options.SkipFormat)
        {
            log("[ncf] Skipping initial format pass (--skip-format specified).");
        }
        else if (!string.IsNullOrWhiteSpace(preset.FormatCommand))
        {
            var formatCmd = preset.Name == "dotnet" ? "dotnet format --no-restore" : preset.FormatCommand;
            log($"[ncf] Running initial format pass ({formatCmd})...");
            var formatResult = await CliProcessHelper.RunCommandAsync(_processRunner, formatCmd, targetDir, Constants.DefaultCliCommandTimeoutSeconds, cancellationToken);
            if (formatResult.ExitCode == 0)
            {
                log("[ncf] Initial format pass succeeded.");
            }
            else
            {
                log($"[ncf] Warning: Initial format command exited with code {formatResult.ExitCode}.");
            }
        }

        log("[ncf] Initialization complete! Run 'ncf audit' to verify repository readiness.");
        return 0;
    }
}
