using System.Net.Http.Headers;
using System.Text;
using DellWarrantyScanner.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DellWarrantyScanner.Services;

public class DellWarrantyService : IDisposable
{
    private readonly HttpClient _http;
    private string _token = "";
    private DateTime _tokenExpiry = DateTime.MinValue;
    private readonly string _clientId;
    private readonly string _clientSecret;

    private const string TokenUrl = "https://apigtwb2c.us.dell.com/auth/oauth/v2/token";
    private const string WarrantyUrl = "https://apigtwb2c.us.dell.com/PROD/sbil/eapi/v5/asset-entitlements";

    public DellWarrantyService(string clientId, string clientSecret)
    {
        _clientId = clientId;
        _clientSecret = clientSecret;
        _http = new HttpClient();
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    private async Task EnsureTokenAsync()
    {
        if (!string.IsNullOrEmpty(_token) && DateTime.UtcNow < _tokenExpiry)
            return;

        var content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("grant_type", "client_credentials"),
            new KeyValuePair<string, string>("client_id", _clientId),
            new KeyValuePair<string, string>("client_secret", _clientSecret)
        });

        var response = await _http.PostAsync(TokenUrl, content);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
            throw new Exception($"Dell API auth failed ({response.StatusCode}): {body}");

        var json = JObject.Parse(body);
        _token = json["access_token"]?.ToString()
            ?? throw new Exception("No access_token in Dell API response");
        int expiresIn = json["expires_in"]?.Value<int>() ?? 3600;
        _tokenExpiry = DateTime.UtcNow.AddSeconds(expiresIn - 60);
    }

    // Lookup warranties for up to 100 service tags at once
    public async Task LookupWarrantiesAsync(List<DeviceInfo> devices, IProgress<string> progress)
    {
        var dellDevices = devices
            .Where(d => !string.IsNullOrWhiteSpace(d.ServiceTag) && d.WarrantyStatus == "Pending")
            .ToList();

        if (dellDevices.Count == 0)
            return;

        await EnsureTokenAsync();

        // Dell API allows up to 100 tags per request
        const int batchSize = 100;
        for (int i = 0; i < dellDevices.Count; i += batchSize)
        {
            var batch = dellDevices.Skip(i).Take(batchSize).ToList();
            var tags = string.Join(",", batch.Select(d => d.ServiceTag));
            progress.Report($"Looking up warranties ({i + 1}-{Math.Min(i + batchSize, dellDevices.Count)} of {dellDevices.Count})...");

            try
            {
                var request = new HttpRequestMessage(HttpMethod.Get, $"{WarrantyUrl}?servicetags={tags}");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
                request.Headers.Add("Accept", "application/json");

                var response = await _http.SendAsync(request);
                var body = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    foreach (var d in batch)
                    {
                        d.WarrantyStatus = "API Error";
                        d.Notes = $"HTTP {response.StatusCode}";
                    }
                    continue;
                }

                var results = JArray.Parse(body);
                foreach (var result in results)
                {
                    var tag = result["serviceTag"]?.ToString() ?? "";
                    var device = batch.FirstOrDefault(d =>
                        string.Equals(d.ServiceTag, tag, StringComparison.OrdinalIgnoreCase));
                    if (device == null) continue;

                    var entitlements = result["entitlements"] as JArray;
                    if (entitlements == null || entitlements.Count == 0)
                    {
                        device.WarrantyStatus = "No Entitlements";
                        continue;
                    }

                    // Find the latest end date across all entitlements
                    DateTime? latestEnd = null;
                    foreach (var ent in entitlements)
                    {
                        var endStr = ent["endDate"]?.ToString();
                        if (DateTime.TryParse(endStr, out var end))
                        {
                            if (latestEnd == null || end > latestEnd)
                                latestEnd = end;
                        }
                    }

                    if (latestEnd.HasValue)
                    {
                        device.WarrantyEndDate = latestEnd.Value.Date;
                        int days = (latestEnd.Value.Date - DateTime.Today).Days;
                        device.DaysRemaining = days;
                        device.WarrantyStatus = days >= 0 ? "Active" : "Expired";
                    }
                    else
                    {
                        device.WarrantyStatus = "Unknown";
                    }
                }
            }
            catch (Exception ex)
            {
                foreach (var d in batch)
                {
                    d.WarrantyStatus = "Error";
                    d.Notes = ex.Message;
                }
            }
        }
    }

    public void Dispose() => _http.Dispose();
}
