namespace NightCodeForge.Cli.Commands;

using NightCodeForge.Cli.Services;

public enum SkillsAction
{
    Install,
    Show,
    List
}

public class SkillsOptions
{
    public SkillsAction Action { get; set; } = SkillsAction.Install;
    public bool Global { get; set; }
    public string? CustomDirectory { get; set; }
    public string TargetPath { get; set; } = Directory.GetCurrentDirectory();
    public bool DryRun { get; set; }
    public string? SkillName { get; set; }
}

public class SkillsCommand
{
    private const int ExitCodeSuccess = 0;
    private const int ExitCodeValidationError = 2;

    private readonly Func<string> _userProfileProvider;

    public SkillsCommand()
        : this(() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
    {
    }

    public SkillsCommand(Func<string> userProfileProvider)
    {
        _userProfileProvider = userProfileProvider ?? throw new ArgumentNullException(nameof(userProfileProvider));
    }

    public Task<int> ExecuteAsync(SkillsOptions options, Action<string>? log = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();

        log ??= Console.WriteLine;

        switch (options.Action)
        {
            case SkillsAction.List:
                foreach (var skillName in SkillsInstaller.GetEmbeddedSkillNames())
                {
                    var version = SkillsInstaller.GetEmbeddedSkillVersion(skillName);
                    log($"{skillName} (v{version})");
                }
                return Task.FromResult(ExitCodeSuccess);

            case SkillsAction.Show:
                if (string.IsNullOrWhiteSpace(options.SkillName))
                {
                    var skillNames = SkillsInstaller.GetEmbeddedSkillNames();
                    for (int i = 0; i < skillNames.Count; i++)
                    {
                        var name = skillNames[i];
                        var content = SkillsInstaller.GetEmbeddedSkillContent(name)
                            ?? throw new InvalidOperationException($"Embedded skill '{name}' could not be read.");

                        if (i > 0)
                        {
                            log(string.Empty);
                        }

                        log($"=== {name} ===");
                        log(content.TrimEnd());
                    }
                    return Task.FromResult(ExitCodeSuccess);
                }

                var requestedContent = SkillsInstaller.GetEmbeddedSkillContent(options.SkillName);
                if (requestedContent == null)
                {
                    var available = string.Join(", ", SkillsInstaller.GetEmbeddedSkillNames());
                    log($"[ncf] Error: Unknown skill '{options.SkillName}'. Available skills: {available}");
                    return Task.FromResult(ExitCodeValidationError);
                }

                log(requestedContent.TrimEnd());
                return Task.FromResult(ExitCodeSuccess);

            case SkillsAction.Install:
                if (options.Global && !string.IsNullOrWhiteSpace(options.CustomDirectory))
                {
                    log("[ncf] Error: Cannot combine '--global' with '--dir'.");
                    return Task.FromResult(ExitCodeValidationError);
                }

                if (!string.IsNullOrWhiteSpace(options.CustomDirectory))
                {
                    var customDir = Path.GetFullPath(options.CustomDirectory);
                    if (File.Exists(customDir))
                    {
                        log($"[ncf] Error: Custom directory path points to an existing file: {customDir}");
                        return Task.FromResult(ExitCodeValidationError);
                    }

                    SkillsInstaller.InstallSkillsToDirectory(customDir, options.DryRun, log);
                    return Task.FromResult(ExitCodeSuccess);
                }

                if (options.Global)
                {
                    SkillsInstaller.InstallGlobalSkills(_userProfileProvider(), options.DryRun, log);
                    return Task.FromResult(ExitCodeSuccess);
                }

                var rawPath = string.IsNullOrWhiteSpace(options.TargetPath) ? Directory.GetCurrentDirectory() : options.TargetPath;
                var targetDir = Path.GetFullPath(rawPath);
                if (!Directory.Exists(targetDir))
                {
                    log($"[ncf] Error: Directory not found: {targetDir}");
                    return Task.FromResult(ExitCodeValidationError);
                }

                SkillsInstaller.InstallSkills(targetDir, options.DryRun, log);
                return Task.FromResult(ExitCodeSuccess);

            default:
                throw new ArgumentOutOfRangeException(nameof(options), options.Action, "Unsupported skills action.");
        }
    }
}
