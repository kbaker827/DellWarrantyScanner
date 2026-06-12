using DellWarrantyScanner.Services;
using System.Windows;

namespace DellWarrantyScanner;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ThemeService.Initialize();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ThemeService.Cleanup();
        base.OnExit(e);
    }
}
