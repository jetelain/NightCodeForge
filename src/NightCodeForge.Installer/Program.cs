namespace NightCodeForge.Installer;

using NightCodeForge.Installer.Cli;
using NightCodeForge.Installer.Core.Constants;
using NightCodeForge.Installer.Core.Models;

/// <summary>
/// Native AOT entrypoint for <c>NightCodeForge.Installer</c> (CLI uninstall/detection foundation;
/// Avalonia OOBE Wizard &amp; Maintenance Hub UI added in Step 12.18C).
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        InstallerCliOptions cliOptions;
        try
        {
            cliOptions = InstallerCliParser.Parse(args);
        }
        catch (ArgumentException ex)
        {
            await Console.Error.WriteLineAsync($"Error: {ex.Message}").ConfigureAwait(false);
            return InstallerConstants.ExitCodeError;
        }

        if (cliOptions.Mode == InstallerLaunchMode.Help)
        {
            PrintUsage();
            return InstallerConstants.ExitCodeSuccess;
        }

        using var services = InstallerServiceContainer.CreateDefault();
        var progress = new SynchronousProgress<InstallerProgressEvent>(ev =>
        {
            if (!cliOptions.UninstallOptions.Quiet || ev.Level == InstallerLogLevel.Error)
            {
                var target = ev.Level == InstallerLogLevel.Error ? Console.Error : Console.Out;
                target.WriteLine($"[{ev.Step}] {ev.Message}");
            }
        });

        if (cliOptions.Mode == InstallerLaunchMode.Uninstall)
        {
            var result = await services.UninstallService.UninstallAsync(
                cliOptions.UninstallOptions,
                cliOptions.InstallDirectory,
                cliOptions.WslDistroName,
                progress,
                CancellationToken.None).ConfigureAwait(false);

            if (!result.Succeeded)
            {
                await Console.Error.WriteLineAsync($"Uninstallation failed: {result.ErrorMessage}").ConfigureAwait(false);
                return InstallerConstants.ExitCodeError;
            }

            return InstallerConstants.ExitCodeSuccess;
        }

        var discovery = services.InstallationDetector.Detect(cliOptions.BundleDirectory);
        Console.WriteLine($"NightCodeForge Installer ({services.Platform.CurrentRuntimeIdentifier})");
        Console.WriteLine($"  NightCodeForge Installed : {discovery.NightCodeForge.IsInstalled} ({discovery.NightCodeForge.InstallDirectory})");
        Console.WriteLine($"  OllamaRouter Installed   : {discovery.OllamaRouter.IsInstalled} ({discovery.OllamaRouter.InstallDirectory})");
        Console.WriteLine($"  Recommended Source       : {discovery.RecommendedSourceMode}");
        if (discovery.DetectedBundleDirectory is not null)
        {
            Console.WriteLine($"  Local Bundle             : {discovery.DetectedBundleDirectory}");
        }

        Console.WriteLine("Headless uninstall is ready ('uninstall --quiet'). Avalonia OOBE UI is scheduled for Step 12.18C.");
        return InstallerConstants.ExitCodeSuccess;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("NightCodeForge Installer");
        Console.WriteLine("Usage:");
        Console.WriteLine("  NightCodeForge.Installer [uninstall | upgrade] [options]");
        Console.WriteLine("Options:");
        Console.WriteLine("  uninstall / --uninstall      Uninstall NightCodeForge");
        Console.WriteLine("  --keep-data                  Preserve config/, data/, and logs/ during uninstall");
        Console.WriteLine("  --remove-tools               Also uninstall companion .NET global tools");
        Console.WriteLine("  --remove-ollamarouter        Also uninstall OllamaRouter and ollamarouter:// handler");
        Console.WriteLine("  --quiet                      Execute uninstall without interactive prompts");
        Console.WriteLine("  --bundle <dir>               Use an air-gapped local bundle directory");
        Console.WriteLine("  --air-gapped                 Prefer air-gapped local bundle source");
        Console.WriteLine("  --install-dir <dir>          Override target installation directory");
        Console.WriteLine("  --wsl-distro <name>          Override target WSL2 distribution (default: Ubuntu)");
        Console.WriteLine("  -h, --help                   Show this help message");
    }

    private sealed class SynchronousProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;

        public SynchronousProgress(Action<T> handler)
        {
            _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        }

        public void Report(T value) => _handler(value);
    }
}
