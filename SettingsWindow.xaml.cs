using DellWarrantyScanner.Models;
using DellWarrantyScanner.Services;
using System.IO;
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

        try
        {
            _settings.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show($"Could not save settings:\n\n{ex.Message}", "Settings",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        UrlLauncher.Open(e.Uri.AbsoluteUri);
        e.Handled = true;
    }
}
