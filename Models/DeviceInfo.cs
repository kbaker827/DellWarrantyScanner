namespace DellWarrantyScanner.Models;

public static class WarrantyStatus
{
    public const string Pending          = "Pending";
    public const string Active           = "Active";
    public const string Expired          = "Expired";
    public const string Unknown          = "Unknown";
    public const string NoEntitlements   = "No Entitlements";
    public const string InvalidTag       = "Invalid Tag";
    public const string NotFound         = "Not Found";
    public const string NoServiceTag     = "No Service Tag";
    public const string NoApiKey         = "No API Key";
    public const string NotChecked       = "Not Checked";
    public const string ApiError         = "API Error";
    public const string Error            = "Error";
    public const string WmiAccessDenied  = "WMI Access Denied";

    // Active warranties ending within this many days are flagged as "expiring soon".
    public const int ExpiringSoonDays = 90;
}

public class DeviceInfo
{
    public string IpAddress { get; set; } = "";
    public string Hostname { get; set; } = "";
    public string ServiceTag { get; set; } = "";
    public string Model { get; set; } = "";
    public string WarrantyStatus { get; set; } = Models.WarrantyStatus.Pending;
    public DateTime? WarrantyEndDate { get; set; }
    public int? DaysRemaining { get; set; }
    public string Notes { get; set; } = "";

    public bool IsExpiringSoon =>
        WarrantyStatus == Models.WarrantyStatus.Active &&
        DaysRemaining is < Models.WarrantyStatus.ExpiringSoonDays;

    public string WarrantyEndDisplay =>
        WarrantyEndDate.HasValue ? WarrantyEndDate.Value.ToString("yyyy-MM-dd") : "";

    public string DaysRemainingDisplay =>
        DaysRemaining.HasValue ? DaysRemaining.Value.ToString() : "";
}
