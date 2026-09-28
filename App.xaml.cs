using DellWarrantyScanner.Services;
using System.Windows;
using System.Windows.Threading;

namespace DellWarrantyScanner;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        ThemeService.Initialize();
        new MainWindow().Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ThemeService.Cleanup();
        base.OnExit(e);
    }

    // Last-resort handler so an unexpected error shows a message instead of
    // silently terminating the app (and losing any scan results on screen).
    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show($"An unexpected error occurred:\n\n{e.Exception.Message}",
            "Dell Warranty Scanner", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
