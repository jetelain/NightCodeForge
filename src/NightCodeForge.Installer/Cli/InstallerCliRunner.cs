namespace NightCodeForge.Installer.Cli;

using NightCodeForge.Installer.Core.Constants;
using NightCodeForge.Installer.Core.Models;

/// <summary>
/// Executes non-GUI CLI startup paths (<c>--help</c>, <c>uninstall --quiet</c>, <c>upgrade --quiet</c>).
/// </summary>
public static class InstallerCliRunner
{
    public static async Task<int> RunHeadlessAsync(
        InstallerStartupPlan plan,
        InstallerServiceContainer services,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        if (plan.HeadlessAction == InstallerLaunchMode.Help)
        {
            WriteUsage(output);
            return InstallerConstants.ExitCodeSuccess;
        }

        var progress = new TextWriterProgress(
            output,
            error,
            quietInfo: plan.CliOptions.UninstallOptions.Quiet && plan.HeadlessAction == InstallerLaunchMode.Uninstall);

        if (plan.HeadlessAction == InstallerLaunchMode.Uninstall)
        {
            var result = await services.UninstallService.UninstallAsync(
                plan.CliOptions.UninstallOptions,
                plan.EffectiveInstallDirectory,
                plan.EffectiveWslDistroName,
                progress,
                cancellationToken).ConfigureAwait(false);

            if (!result.Succeeded)
            {
                await error.WriteLineAsync($"Uninstallation failed: {result.ErrorMessage}").ConfigureAwait(false);
                return InstallerConstants.ExitCodeError;
            }

            return InstallerConstants.ExitCodeSuccess;
        }

        if (plan.HeadlessAction == InstallerLaunchMode.Upgrade)
        {
            var result = await services.UpgradeService.UpgradeAllAsync(
                plan.EffectiveSource,
                plan.EffectiveInstallDirectory,
                plan.EffectiveWslDistroName,
                createDesktopShortcut: false,
                progress,
                cancellationToken).ConfigureAwait(false);

            if (!result.Succeeded)
            {
                await error.WriteLineAsync($"Upgrade failed: {result.ErrorMessage}").ConfigureAwait(false);
                return InstallerConstants.ExitCodeError;
            }

            await output.WriteLineAsync(
                $"Upgrade complete. NightCodeForge {result.NightCodeForgeDeployment?.Version} at '{result.NightCodeForgeDeployment?.InstallDirectory}'.").ConfigureAwait(false);
            return InstallerConstants.ExitCodeSuccess;
        }

        return InstallerConstants.ExitCodeSuccess;
    }

    public static void WriteUsage(TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);

        output.WriteLine("NightCodeForge Installer");
        output.WriteLine("Usage:");
        output.WriteLine("  NightCodeForge.Installer [uninstall | upgrade] [options]");
        output.WriteLine("Options:");
        output.WriteLine("  uninstall, --uninstall       Open Interactive Uninstaller (or run headlessly with --quiet)");
        output.WriteLine("  upgrade, --upgrade           Open Maintenance Hub focused on upgrading");
        output.WriteLine("  --wizard                     Force 5-step OOBE Setup Wizard (Upgrade/Repair mode if installed)");
        output.WriteLine("  --keep-data                  Preserve config/, data/, and logs/ during uninstall");
        output.WriteLine("  --remove-tools               Also uninstall companion .NET global tools");
        output.WriteLine("  --remove-ollamarouter        Also uninstall OllamaRouter and ollamarouter:// handler");
        output.WriteLine("  --quiet                      Execute uninstall or upgrade headlessly without opening a UI");
        output.WriteLine("  --bundle <dir>               Use an air-gapped local bundle directory");
        output.WriteLine("  --air-gapped                 Prefer air-gapped local bundle source");
        output.WriteLine("  --install-dir <dir>          Override target installation directory");
        output.WriteLine("  --wsl-distro <name>          Override target WSL2 distribution (default: Ubuntu)");
        output.WriteLine("  -h, --help                   Show this help message");
    }

    private sealed class TextWriterProgress(TextWriter output, TextWriter error, bool quietInfo)
        : IProgress<InstallerProgressEvent>
    {
        public void Report(InstallerProgressEvent value)
        {
            if (quietInfo && value.Level != InstallerLogLevel.Error)
            {
                return;
            }

            var writer = value.Level == InstallerLogLevel.Error ? error : output;
            writer.WriteLine($"[{value.Step}] {value.Message}");
        }
    }
}
