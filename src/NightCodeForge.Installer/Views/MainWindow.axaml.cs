namespace NightCodeForge.Installer.Views;

using Avalonia.Controls;
using NightCodeForge.Installer.ViewModels;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Closing += OnWindowClosing;
        Closed += OnWindowClosed;
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && vm.HasActiveDestructiveOperation)
        {
            e.Cancel = true;
            vm.NotifyCloseBlockedDuringOperation();
        }
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        if (DataContext is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
