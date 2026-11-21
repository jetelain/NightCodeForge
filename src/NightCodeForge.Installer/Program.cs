namespace NightCodeForge.Installer;

using Avalonia;
using Avalonia.Win32;
using Avalonia.X11;
using NightCodeForge.Installer.Cli;
using NightCodeForge.Installer.Core.Constants;

/// <summary>
/// Native AOT desktop &amp; headless CLI entrypoint for <c>NightCodeForge.Installer</c>.
/// Configures strict pure software rendering (<see cref="Win32RenderingMode.Software"/> and
/// <see cref="X11RenderingMode.Software"/>) so 0 MB of GPU VRAM is allocated.
/// </summary>
public static class Program
{
    internal static InstallerCliOptions LaunchOptions { get; private set; } = InstallerCliParser.Parse([]);

    [STAThread]
    public static int Main(string[] args)
    {
        InstallerCliOptions cliOptions;
        try
        {
            cliOptions = InstallerCliParser.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return InstallerConstants.ExitCodeError;
        }

        LaunchOptions = cliOptions;

        if (cliOptions.Mode == InstallerLaunchMode.Help)
        {
            InstallerCliRunner.WriteUsage(Console.Out);
            return InstallerConstants.ExitCodeSuccess;
        }

        if (cliOptions.Mode is InstallerLaunchMode.Uninstall or InstallerLaunchMode.Upgrade &&
            cliOptions.UninstallOptions.Quiet)
        {
            using var services = InstallerServiceContainer.CreateDefault();
            var discovery = services.InstallationDetector.Detect(cliOptions.BundleDirectory);
            var startupPlan = InstallerStartupRouter.Resolve(cliOptions, discovery, services.Platform);
            return InstallerCliRunner.RunHeadlessAsync(
                startupPlan,
                services,
                Console.Out,
                Console.Error,
                CancellationToken.None).GetAwaiter().GetResult();
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new Win32PlatformOptions { RenderingMode = [Win32RenderingMode.Software] })
            .With(new X11PlatformOptions { RenderingMode = [X11RenderingMode.Software] });
}
