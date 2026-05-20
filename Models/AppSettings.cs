using System.Text.Json;

namespace DellWarrantyScanner.Models;

public class AppSettings
{
    public string DellClientId { get; set; } = "";
    public string DellClientSecret { get; set; } = "";
    public string IpRange { get; set; } = "";
    public string WmiUsername { get; set; } = "";
    public string WmiPassword { get; set; } = "";
    public bool UseCurrentCredentials { get; set; } = true;

    private static string SettingsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DellWarrantyScanner", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            if (!File.Exists(SettingsPath)) return new AppSettings();
            var json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save()
    {
        var dir = Path.GetDirectoryName(SettingsPath)!;
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
