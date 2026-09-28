using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DellWarrantyScanner.Models;

public class AppSettings
{
    public string DellClientId { get; set; } = "";
    public string DellClientSecret { get; set; } = "";
    public string IpRange { get; set; } = "";
    public string WmiUsername { get; set; } = "";
    public string WmiPassword { get; set; } = "";
    public bool UseCurrentCredentials { get; set; } = true;

    public bool HasApiCredentials =>
        !string.IsNullOrWhiteSpace(DellClientId) && !string.IsNullOrWhiteSpace(DellClientSecret);

    private static string SettingsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DellWarrantyScanner", "settings.json");

    // Secrets are encrypted with DPAPI so only the current Windows user can read them.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("DellWarrantyScanner");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // On-disk shape. The plaintext secret fields are only read, to migrate settings
    // files written by older versions, and are never written back.
    private sealed class StoredSettings
    {
        public string DellClientId { get; set; } = "";
        public string? DellClientSecret { get; set; }
        public string? DellClientSecretProtected { get; set; }
        public string IpRange { get; set; } = "";
        public string WmiUsername { get; set; } = "";
        public string? WmiPassword { get; set; }
        public string? WmiPasswordProtected { get; set; }
        public bool UseCurrentCredentials { get; set; } = true;
    }

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new AppSettings();
            var stored = JsonSerializer.Deserialize<StoredSettings>(File.ReadAllText(SettingsPath));
            if (stored == null) return new AppSettings();

            return new AppSettings
            {
                DellClientId          = stored.DellClientId,
                DellClientSecret      = Unprotect(stored.DellClientSecretProtected) ?? stored.DellClientSecret ?? "",
                IpRange               = stored.IpRange,
                WmiUsername           = stored.WmiUsername,
                WmiPassword           = Unprotect(stored.WmiPasswordProtected) ?? stored.WmiPassword ?? "",
                UseCurrentCredentials = stored.UseCurrentCredentials
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new AppSettings();
        }
    }

    public void Save()
    {
        var stored = new StoredSettings
        {
            DellClientId              = DellClientId,
            DellClientSecretProtected = Protect(DellClientSecret),
            IpRange                   = IpRange,
            WmiUsername               = WmiUsername,
            WmiPasswordProtected      = Protect(WmiPassword),
            UseCurrentCredentials     = UseCurrentCredentials
        };

        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);

        // Write to a temp file first so a crash mid-write can't corrupt the settings.
        var tempPath = SettingsPath + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(stored, JsonOptions));
        File.Move(tempPath, SettingsPath, overwrite: true);
    }

    private static string? Protect(string value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        var encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(encrypted);
    }

    private static string? Unprotect(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        try
        {
            var decrypted = ProtectedData.Unprotect(Convert.FromBase64String(value), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(decrypted);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            // Encrypted by a different Windows user or corrupted; treat as not set.
            return "";
        }
    }
}
