using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using DellWarrantyScanner.Models;

namespace DellWarrantyScanner.Services;

public sealed class DellWarrantyService : IDisposable
{
    // The Dell API allows up to 100 service tags per request.
    public const int MaxTagsPerRequest = 100;

    private const string TokenUrl = "https://apigtwb2c.us.dell.com/auth/oauth/v2/token";
    private const string WarrantyUrl = "https://apigtwb2c.us.dell.com/PROD/sbil/eapi/v5/asset-entitlements";

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly string _clientId;
    private readonly string _clientSecret;
    private string _token = "";
    private DateTime _tokenExpiry = DateTime.MinValue;

    public DellWarrantyService(string clientId, string clientSecret)
    {
        _clientId = clientId;
        _clientSecret = clientSecret;
    }

    private async Task EnsureTokenAsync(CancellationToken ct)
    {
        if (_token.Length > 0 && DateTime.UtcNow < _tokenExpiry)
            return;

        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"]    = "client_credentials",
            ["client_id"]     = _clientId,
            ["client_secret"] = _clientSecret
        });

        using var response = await _http.PostAsync(TokenUrl, content, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Dell API authentication failed (HTTP {(int)response.StatusCode} {response.ReasonPhrase}). " +
                $"Check the Client ID and Secret in Settings.\n\n{body}");

        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;

        _token = GetString(root, "access_token") is { Length: > 0 } token
            ? token
            : throw new HttpRequestException("Dell API response did not contain an access token.");

        int expiresIn = root.TryGetProperty("expires_in", out var exp) && exp.ValueKind == JsonValueKind.Number
            ? exp.GetInt32()
            : 3600;
        _tokenExpiry = DateTime.UtcNow.AddSeconds(Math.Max(expiresIn - 60, 0));
    }

    // Looks up every device still marked Pending that has a service tag, updating it in place.
    // Per-batch API failures are recorded on the devices; authentication failures throw.
    public async Task LookupWarrantiesAsync(
        IReadOnlyList<DeviceInfo> devices, IProgress<string> progress, CancellationToken ct)
    {
        // A machine with several IPs shows up once per IP but has one service tag,
        // so each tag is looked up once and the result applied to all its rows.
        var devicesByTag = devices
            .Where(d => d.WarrantyStatus == WarrantyStatus.Pending && !string.IsNullOrWhiteSpace(d.ServiceTag))
            .GroupBy(d => d.ServiceTag.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var tags = devicesByTag.Keys.ToList();

        for (int i = 0; i < tags.Count; i += MaxTagsPerRequest)
        {
            ct.ThrowIfCancellationRequested();

            var batchTags = tags.Skip(i).Take(MaxTagsPerRequest).ToList();
            var batchDevices = batchTags.SelectMany(t => devicesByTag[t]).ToList();
            progress.Report($"Looking up warranties ({i + 1}-{i + batchTags.Count} of {tags.Count})...");

            // Inside the loop so a long run re-authenticates if the token expires.
            await EnsureTokenAsync(ct);

            try
            {
                var query = string.Join(",", batchTags.Select(Uri.EscapeDataString));
                using var request = new HttpRequestMessage(HttpMethod.Get, $"{WarrantyUrl}?servicetags={query}");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                using var response = await _http.SendAsync(request, ct);
                var body = await response.Content.ReadAsStringAsync(ct);

                if (!response.IsSuccessStatusCode)
                {
                    if (response.StatusCode == HttpStatusCode.Unauthorized)
                        _token = ""; // force a fresh token for the next batch

                    SetStatus(batchDevices, WarrantyStatus.ApiError,
                        $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
                    continue;
                }

                using var json = JsonDocument.Parse(body);
                if (json.RootElement.ValueKind != JsonValueKind.Array)
                    throw new JsonException("Unexpected response format from the Dell API.");

                foreach (var result in json.RootElement.EnumerateArray())
                {
                    var tag = GetString(result, "serviceTag");
                    if (tag == null || !devicesByTag.TryGetValue(tag.Trim(), out var matches)) continue;
                    foreach (var device in matches)
                        ApplyResult(device, result);
                }

                SetStatus(batchDevices.Where(d => d.WarrantyStatus == WarrantyStatus.Pending),
                    WarrantyStatus.NotFound, "Service tag not returned by the Dell API");
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // Includes HttpClient timeouts, which surface as TaskCanceledException.
                SetStatus(batchDevices, WarrantyStatus.Error, ex.Message);
            }
        }
    }

    private static void ApplyResult(DeviceInfo device, JsonElement result)
    {
        if (result.TryGetProperty("invalid", out var invalid) && invalid.ValueKind == JsonValueKind.True)
        {
            device.WarrantyStatus = WarrantyStatus.InvalidTag;
            device.Notes = "Dell does not recognize this service tag";
            return;
        }

        if (string.IsNullOrWhiteSpace(device.Model) &&
            GetString(result, "productLineDescription") is { Length: > 0 } productLine)
            device.Model = productLine;

        if (!result.TryGetProperty("entitlements", out var entitlements) ||
            entitlements.ValueKind != JsonValueKind.Array ||
            entitlements.GetArrayLength() == 0)
        {
            device.WarrantyStatus = WarrantyStatus.NoEntitlements;
            return;
        }

        // The warranty runs until the latest end date across all entitlements.
        DateTime? latestEnd = null;
        foreach (var ent in entitlements.EnumerateArray())
        {
            if (GetString(ent, "endDate") is { } endStr &&
                DateTimeOffset.TryParse(endStr, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var end))
            {
                // Dell reports end dates in UTC; show them in local time as Dell's own site does.
                var endDate = end.LocalDateTime.Date;
                if (latestEnd == null || endDate > latestEnd)
                    latestEnd = endDate;
            }
        }

        if (latestEnd is { } lastDay)
        {
            int days = (lastDay - DateTime.Today).Days;
            device.WarrantyEndDate = lastDay;
            device.DaysRemaining = days;
            device.WarrantyStatus = days >= 0 ? WarrantyStatus.Active : WarrantyStatus.Expired;
        }
        else
        {
            device.WarrantyStatus = WarrantyStatus.Unknown;
            device.Notes = "No end date in entitlement data";
        }
    }

    private static string? GetString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static void SetStatus(IEnumerable<DeviceInfo> devices, string status, string notes)
    {
        foreach (var d in devices)
        {
            d.WarrantyStatus = status;
            d.Notes = notes;
        }
    }

    public void Dispose() => _http.Dispose();
}
