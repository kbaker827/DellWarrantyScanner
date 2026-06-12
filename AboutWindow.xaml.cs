using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Navigation;

namespace DellWarrantyScanner;

public partial class AboutWindow : Window
{
    private const string ReleasesUrl = "https://github.com/kbaker827/DellWarrantyScanner/releases";

    public AboutWindow()
    {
        InitializeComponent();

        var ver = Assembly.GetExecutingAssembly().GetName().Version;
        _txtVersion.Text = ver is null ? "v1.0.0" : $"v{ver.Major}.{ver.Minor}.{ver.Build}";

        _txtCopyright.Text = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright
            ?? "Copyright © 2026 kbaker827";
    }

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo { FileName = e.Uri.AbsoluteUri, UseShellExecute = true });
        e.Handled = true;
    }

    private void ViewReleases_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo { FileName = ReleasesUrl, UseShellExecute = true });

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
