namespace DellWarrantyScanner.Models;

public class DeviceInfo
{
    public string IpAddress { get; set; } = "";
    public string Hostname { get; set; } = "";
    public string ServiceTag { get; set; } = "";
    public string Model { get; set; } = "";
    public string WarrantyStatus { get; set; } = "Pending";
    public DateTime? WarrantyEndDate { get; set; }
    public int? DaysRemaining { get; set; }
    public string Notes { get; set; } = "";

    public string WarrantyEndDisplay =>
        WarrantyEndDate.HasValue ? WarrantyEndDate.Value.ToString("yyyy-MM-dd") : "";

    public string DaysRemainingDisplay =>
        DaysRemaining.HasValue ? DaysRemaining.Value.ToString() : "";
}
