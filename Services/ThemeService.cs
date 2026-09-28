using Microsoft.Win32;
using System.Windows;

namespace DellWarrantyScanner.Services;

public static class ThemeService
{
    public static event EventHandler? ThemeChanged;

    public static void Initialize()
    {
        Apply(IsDarkMode());
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    public static void Cleanup()
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
    }

    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        // Raised on a background thread; BeginInvoke avoids blocking it on the UI thread.
        if (e.Category == UserPreferenceCategory.General)
            Application.Current?.Dispatcher.BeginInvoke(() => Apply(IsDarkMode()));
    }

    private static bool IsDarkMode()
    {
        using var key = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        var value = key?.GetValue("AppsUseLightTheme");
        return value is int i && i == 0;
    }

    private static ResourceDictionary? _activeTheme;

    private static void Apply(bool dark)
    {
        var dict = Application.Current.Resources.MergedDictionaries;
        if (_activeTheme != null) dict.Remove(_activeTheme);
        var uri = new Uri(dark ? "Themes/Dark.xaml" : "Themes/Light.xaml", UriKind.Relative);
        _activeTheme = new ResourceDictionary { Source = uri };
        dict.Add(_activeTheme);
        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }
}
