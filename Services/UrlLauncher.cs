using System.ComponentModel;
using System.Diagnostics;
using System.Windows;

namespace DellWarrantyScanner.Services;

public static class UrlLauncher
{
    // Opens a URL in the default browser, reporting (rather than crashing on)
    // machines with no browser registered.
    public static void Open(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            MessageBox.Show($"Could not open your web browser.\n\nPlease visit:\n{url}",
                "Open Link", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
