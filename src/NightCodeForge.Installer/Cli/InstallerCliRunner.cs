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

        if (!plan.RunHeadless)
        {
            throw new InvalidOperationException("InstallerCliRunner.RunHeadlessAsync cannot run a GUI startup plan.");
        }

        var progress = new TextWriterProgress(
            output,
            error,
            quietInfo: plan.CliOptions.UninstallOptions.Quiet &&
                       plan.HeadlessAction is InstallerLaunchMode.Uninstall or InstallerLaunchMode.Upgrade);

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

        output.WriteLine($"{InstallerConstants.ApplicationDisplayName} Installer");
        output.WriteLine("Usage:");
        output.WriteLine("  NightCodeForge.Installer [uninstall | upgrade] [options]");
        output.WriteLine("Options:");
        output.WriteLine($"  {InstallerCliParser.VerbUninstall}, {InstallerCliParser.FlagUninstall}       Open Interactive Uninstaller (or run headlessly with {InstallerCliParser.FlagQuiet})");
        output.WriteLine($"  {InstallerCliParser.VerbUpgrade}, {InstallerCliParser.FlagUpgrade}           Open Maintenance Hub focused on upgrading (or run headlessly with {InstallerCliParser.FlagQuiet})");
        output.WriteLine($"  {InstallerCliParser.FlagWizard}                     Force {InstallerConstants.WizardStepCount}-step OOBE Setup Wizard (Upgrade/Repair mode if installed)");
        output.WriteLine($"  {InstallerCliParser.FlagKeepData}                  Preserve {InstallerConstants.ConfigDirectoryName}/, {InstallerConstants.DataDirectoryName}/, and {InstallerConstants.LogsDirectoryName}/ during uninstall");
        output.WriteLine($"  {InstallerCliParser.FlagRemoveTools}               Also uninstall companion .NET global tools");
        output.WriteLine($"  {InstallerCliParser.FlagRemoveOllamaRouter}        Also uninstall {InstallerConstants.ComponentOllamaRouter} and {InstallerConstants.OllamaRouterProtocolScheme}:// handler");
        output.WriteLine($"  {InstallerCliParser.FlagQuiet}                      Execute uninstall or upgrade headlessly without opening a UI");
        output.WriteLine($"  {InstallerCliParser.FlagBundle} <dir>               Use an air-gapped local bundle directory");
        output.WriteLine($"  {InstallerCliParser.FlagAirGapped}                 Prefer air-gapped local bundle source");
        output.WriteLine($"  {InstallerCliParser.FlagInstallDir} <dir>          Override target installation directory");
        output.WriteLine($"  {InstallerCliParser.FlagWslDistro} <name>          Override target WSL2 distribution (default: {InstallerConstants.DefaultWslDistro})");
        output.WriteLine($"  {InstallerCliParser.FlagHelpShort}, {InstallerCliParser.FlagHelp}                   Show this help message");
    }

    private sealed class TextWriterProgress(TextWriter output, TextWriter error, bool quietInfo)
        : IProgress<InstallerProgressEvent>
    {
        private readonly object _syncLock = new();

        public void Report(InstallerProgressEvent value)
        {
            if (quietInfo && value.Level != InstallerLogLevel.Error)
            {
                return;
            }

            var writer = value.Level == InstallerLogLevel.Error ? error : output;
            lock (_syncLock)
            {
                writer.WriteLine($"[{value.Step}] {value.Message}");
            }
        }
    }
}
