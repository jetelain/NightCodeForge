namespace NightCodeForge.Installer;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using NightCodeForge.Installer.Cli;
using NightCodeForge.Installer.ViewModels;
using NightCodeForge.Installer.Views;

/// <summary>
/// Avalonia desktop application shell hosting the 3 installer modes (OOBE Wizard,
/// Maintenance &amp; Updater Hub, and Interactive Uninstaller).
/// </summary>
public partial class App : Application
{
    private InstallerServiceContainer? _services;
    private MainWindowViewModel? _viewModel;
    private MainWindow? _mainWindow;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _services = InstallerServiceContainer.CreateDefault();
            var discovery = _services.InstallationDetector.Detect(
                Program.LaunchOptions.BundleDirectory,
                Program.LaunchOptions.InstallDirectory);
            var startupPlan = InstallerStartupRouter.Resolve(Program.LaunchOptions, discovery, _services.Platform);

            var dispatcher = new AvaloniaUiDispatcher();
            var uiHost = new AvaloniaInstallerUiHost(
                () => _mainWindow,
                exitCode => desktop.TryShutdown(exitCode));

            _viewModel = new MainWindowViewModel(_services, startupPlan, uiHost, dispatcher);
            _mainWindow = new MainWindow
            {
                DataContext = _viewModel
            };

            desktop.MainWindow = _mainWindow;
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            desktop.Exit += OnDesktopExit;

            _ = InitializeViewModelAsync(_viewModel);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static async Task InitializeViewModelAsync(MainWindowViewModel viewModel)
    {
        try
        {
            await viewModel.InitializeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await Console.Error.WriteLineAsync($"Installer initialization error: {ex.Message}").ConfigureAwait(false);
            viewModel.ReportInitializationError($"Installer initialization error: {ex.Message}");
        }
    }

    private void OnDesktopExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        _viewModel?.Dispose();
        _viewModel = null;
        _services?.Dispose();
        _services = null;
    }
}
