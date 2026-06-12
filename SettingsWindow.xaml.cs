using DellWarrantyScanner.Models;
using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;

namespace DellWarrantyScanner;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        LoadSettings();
    }

    private void LoadSettings()
    {
        _txtClientId.Text         = _settings.DellClientId;
        _txtClientSecret.Password = _settings.DellClientSecret;
        _rbCurrentUser.IsChecked  = _settings.UseCurrentCredentials;
        _rbSpecifiedUser.IsChecked = !_settings.UseCurrentCredentials;
        _txtWmiUser.Text          = _settings.WmiUsername;
        _txtWmiPass.Password      = _settings.WmiPassword;
        _wmiCredPanel.IsEnabled   = !_settings.UseCurrentCredentials;
    }

    private void SpecifiedUser_Checked(object sender, RoutedEventArgs e) =>
        _wmiCredPanel.IsEnabled = true;

    private void SpecifiedUser_Unchecked(object sender, RoutedEventArgs e) =>
        _wmiCredPanel.IsEnabled = false;

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _settings.DellClientId          = _txtClientId.Text.Trim();
        _settings.DellClientSecret      = _txtClientSecret.Password.Trim();
        _settings.UseCurrentCredentials = _rbCurrentUser.IsChecked == true;
        _settings.WmiUsername           = _txtWmiUser.Text.Trim();
        _settings.WmiPassword           = _txtWmiPass.Password;
        _settings.Save();
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo { FileName = e.Uri.AbsoluteUri, UseShellExecute = true });
        e.Handled = true;
    }
}
