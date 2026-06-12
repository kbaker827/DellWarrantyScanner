using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using DellWarrantyScanner.Models;
using DellWarrantyScanner.Services;
using Microsoft.Win32;

namespace DellWarrantyScanner;

public partial class MainWindow : Window
{
    private AppSettings _settings;
    private readonly ObservableCollection<DeviceInfo> _scanResults = new();
    private readonly ObservableCollection<DeviceInfo> _tagResults = new();
    private CancellationTokenSource? _cts;
    private static readonly System.Net.Http.HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    public MainWindow()
    {
        InitializeComponent();

        _settings = AppSettings.Load();

        ThemeService.ThemeChanged += OnThemeChanged;

        _scanGrid.ItemsSource = _scanResults;
        _tagGrid.ItemsSource = _tagResults;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        AutoDetectAndFillSubnet();
        UpdateStatus("Ready. Configure an IP range and click Scan Network, or enter service tags on the Lookup tab.");
        _ = CheckForUpdatesAsync(userTriggered: false);
    }

    // -------------------------------------------------------------------------
    // Auto-detect subnet
    // -------------------------------------------------------------------------

    private void AutoDetectAndFillSubnet()
    {
        if (!string.IsNullOrWhiteSpace(_settings.IpRange))
        {
            _txtIpRange.Text = _settings.IpRange;
            return;
        }

        var subnets = NetworkScanner.DetectLocalSubnets();
        if (subnets.Count == 1)
        {
            _txtIpRange.Text = subnets[0].Cidr;
        }
        else if (subnets.Count > 1)
        {
            var best = subnets.FirstOrDefault(s =>
                !s.AdapterName.Contains("VPN", StringComparison.OrdinalIgnoreCase) &&
                !s.AdapterName.Contains("Loopback", StringComparison.OrdinalIgnoreCase) &&
                !s.AdapterName.Contains("Virtual", StringComparison.OrdinalIgnoreCase));
            _txtIpRange.Text = best.Cidr ?? subnets[0].Cidr;
        }
    }

    private void AutoDetect_Click(object sender, RoutedEventArgs e)
    {
        var subnets = NetworkScanner.DetectLocalSubnets();
        if (subnets.Count == 0)
        {
            MessageBox.Show("No active network adapters found.", "Auto-detect",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (subnets.Count == 1)
        {
            _txtIpRange.Text = subnets[0].Cidr;
            return;
        }

        var picker = new SubnetPickerWindow(subnets);
        picker.Owner = this;
        if (picker.ShowDialog() == true && picker.SelectedCidr != null)
            _txtIpRange.Text = picker.SelectedCidr;
    }

    // -------------------------------------------------------------------------
    // Network Scan
    // -------------------------------------------------------------------------

    private async void StartScan(object sender, RoutedEventArgs e)
    {
        var ipRange = _txtIpRange.Text.Trim();
        if (string.IsNullOrWhiteSpace(ipRange))
        {
            MessageBox.Show("Please enter an IP range.", "Missing Input",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        List<string> ips;
        try { ips = NetworkScanner.ParseIpRange(ipRange); }
        catch (Exception ex)
        {
            MessageBox.Show($"Invalid IP range: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        if (ips.Count == 0)
        {
            MessageBox.Show("No IP addresses found in that range.", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        if (ips.Count > 65536)
        {
            MessageBox.Show("Range too large (max 65,536 IPs). Please narrow it down.", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (string.IsNullOrWhiteSpace(_settings.DellClientId) ||
            string.IsNullOrWhiteSpace(_settings.DellClientSecret))
        {
            var choice = MessageBox.Show(
                "Dell API credentials are not configured.\n\nWithout them the scan will find Dell systems but cannot retrieve warranty dates.\n\nConfigure credentials now?",
                "No API Credentials", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (choice == MessageBoxResult.Yes) { OpenSettings(sender, e); return; }
        }

        _settings.IpRange = ipRange;
        _settings.Save();

        SetScanState(true);
        _scanResults.Clear();
        _tabs.SelectedIndex = 0;

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        var scanProgress = new Progress<(int completed, int total, string message)>(p =>
        {
            if (p.total > 0)
            {
                _progress.IsIndeterminate = false;
                _progress.Maximum = p.total;
                _progress.Value = Math.Min(p.completed, p.total);
            }
            UpdateStatus(p.message);
        });

        try
        {
            var scanner = new NetworkScanner(_settings);
            var results = await scanner.ScanAsync(ipRange, scanProgress, ct);
            foreach (var d in results) _scanResults.Add(d);

            UpdateStatus($"Found {_scanResults.Count} Dell system(s). Looking up warranties...");
            _progress.IsIndeterminate = true;

            if (!string.IsNullOrWhiteSpace(_settings.DellClientId) &&
                _scanResults.Any(d => !string.IsNullOrEmpty(d.ServiceTag)))
            {
                using var warranty = new DellWarrantyService(
                    _settings.DellClientId, _settings.DellClientSecret);
                await warranty.LookupWarrantiesAsync(_scanResults.ToList(),
                    new Progress<string>(msg => UpdateStatus(msg)));
                _scanGrid.Items.Refresh();
            }
            else
            {
                foreach (var d in _scanResults.Where(d => d.WarrantyStatus == "Pending"))
                    d.WarrantyStatus = "No API Key";
                _scanGrid.Items.Refresh();
            }

            RefreshStatCards(_scanResults);

            int expired = _scanResults.Count(d => d.WarrantyStatus == "Expired");
            int active  = _scanResults.Count(d => d.WarrantyStatus == "Active");
            UpdateStatus($"Done — {_scanResults.Count} Dell system(s) found  |  {active} active  |  {expired} expired.");
            _menuExport.IsEnabled = _scanResults.Count > 0;
        }
        catch (OperationCanceledException)
        {
            _scanGrid.Items.Refresh();
            RefreshStatCards(_scanResults);
            UpdateStatus($"Scan stopped. {_scanResults.Count} Dell system(s) found so far.");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error during scan:\n\n{ex.Message}", "Scan Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            UpdateStatus("Scan failed. Check settings and try again.");
        }
        finally
        {
            SetScanState(false);
            _progress.IsIndeterminate = false;
            _progress.Value = 0;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void StopScan_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    // -------------------------------------------------------------------------
    // Service Tag Lookup
    // -------------------------------------------------------------------------

    private async void StartTagLookup(object sender, RoutedEventArgs e)
    {
        var raw = _txtServiceTags.Text.Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            MessageBox.Show("Please enter at least one service tag.", "Missing Input",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(_settings.DellClientId) ||
            string.IsNullOrWhiteSpace(_settings.DellClientSecret))
        {
            var choice = MessageBox.Show(
                "Dell API credentials are not configured.\n\nService tag lookup requires API credentials.\n\nConfigure now?",
                "No API Credentials", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (choice == MessageBoxResult.Yes) { OpenSettings(sender, e); return; }
            return;
        }

        var tags = ParseServiceTags(raw);
        if (tags.Count == 0)
        {
            MessageBox.Show(
                "No valid service tags found. Tags should be 5–8 alphanumeric characters.",
                "Invalid Input", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (tags.Count > 100)
        {
            MessageBox.Show(
                $"Too many tags ({tags.Count}). Dell API allows up to 100 per lookup.",
                "Too Many Tags", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SetTagLookupState(true);
        _tagResults.Clear();
        foreach (var t in tags)
            _tagResults.Add(new DeviceInfo { ServiceTag = t, WarrantyStatus = "Pending" });

        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        try
        {
            _progress.IsIndeterminate = true;
            UpdateStatus($"Looking up {tags.Count} service tag(s)...");

            using var warranty = new DellWarrantyService(
                _settings.DellClientId, _settings.DellClientSecret);
            await warranty.LookupWarrantiesAsync(_tagResults.ToList(),
                new Progress<string>(msg => UpdateStatus(msg)));

            _tagGrid.Items.Refresh();
            RefreshStatCards(_tagResults);

            int expired = _tagResults.Count(d => d.WarrantyStatus == "Expired");
            int active  = _tagResults.Count(d => d.WarrantyStatus == "Active");
            UpdateStatus($"Done. {_tagResults.Count} tag(s) looked up  |  {active} active  |  {expired} expired.");
        }
        catch (OperationCanceledException)
        {
            _tagGrid.Items.Refresh();
            UpdateStatus("Lookup stopped.");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error during lookup:\n\n{ex.Message}", "Lookup Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            UpdateStatus("Lookup failed. Check API credentials in Settings.");
        }
        finally
        {
            SetTagLookupState(false);
            _progress.IsIndeterminate = false;
            _progress.Value = 0;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void StopLookup_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void ImportCsv_Click(object sender, RoutedEventArgs e) => ImportTagsFromCsv();

    private void ClearTags_Click(object sender, RoutedEventArgs e)
    {
        _txtServiceTags.Clear();
        _tagResults.Clear();
        RefreshStatCards(_tagResults);
        UpdateStatus("Cleared.");
    }

    private void ImportTagsFromCsv()
    {
        var dlg = new OpenFileDialog
        {
            Title  = "Import Service Tags from CSV",
            Filter = "CSV files (*.csv)|*.csv|Text files (*.txt)|*.txt|All files (*.*)|*.*"
        };
        if (dlg.ShowDialog(this) != true) return;

        string[] lines;
        try { lines = File.ReadAllLines(dlg.FileName); }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not read file:\n{ex.Message}", "Import Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (lines.Length == 0)
        {
            MessageBox.Show("The file is empty.", "Import",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var rows = lines.Where(l => !string.IsNullOrWhiteSpace(l)).Select(ParseCsvLine).Where(r => r.Length > 0).ToList();
        if (rows.Count == 0)
        {
            MessageBox.Show("No data found in the file.", "Import",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        int columnCount = rows.Max(r => r.Length);
        List<string> rawValues;

        if (columnCount == 1)
        {
            rawValues = rows
                .Select(r => r[0].Trim())
                .Where(v => v.Length > 0)
                .ToList();
        }
        else
        {
            int colIndex = PickCsvColumn(rows, columnCount);
            if (colIndex < 0) return;

            rawValues = rows
                .Skip(1)
                .Where(r => r.Length > colIndex)
                .Select(r => r[colIndex].Trim())
                .Where(v => v.Length > 0)
                .ToList();
        }

        var tags = rawValues
            .Select(v => v.ToUpperInvariant())
            .Where(v => v.Length is >= 4 and <= 10 && v.All(char.IsLetterOrDigit))
            .Distinct()
            .ToList();

        if (tags.Count == 0)
        {
            MessageBox.Show(
                "No valid service tags found in the selected column.\n\n" +
                "Service tags must be 4–10 alphanumeric characters.",
                "No Tags Found", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string existing = _txtServiceTags.Text.Trim();
        string toAdd    = string.Join(Environment.NewLine, tags);
        _txtServiceTags.Text = string.IsNullOrEmpty(existing)
            ? toAdd
            : existing + Environment.NewLine + toAdd;

        UpdateStatus($"Imported {tags.Count} service tag(s) from {Path.GetFileName(dlg.FileName)}.");
    }

    private int PickCsvColumn(List<string[]> rows, int columnCount)
    {
        var picker = new CsvColumnPickerWindow(rows, columnCount);
        picker.Owner = this;
        return picker.ShowDialog() == true ? picker.SelectedColumnIndex : -1;
    }

    private static string[] ParseCsvLine(string line)
    {
        var fields = new List<string>();
        int i = 0;
        while (i <= line.Length)
        {
            if (i == line.Length) { fields.Add(""); break; }

            if (line[i] == '"')
            {
                i++;
                var sb = new StringBuilder();
                while (i < line.Length)
                {
                    if (line[i] == '"' && i + 1 < line.Length && line[i + 1] == '"')
                    { sb.Append('"'); i += 2; }
                    else if (line[i] == '"')
                    { i++; break; }
                    else
                    { sb.Append(line[i++]); }
                }
                fields.Add(sb.ToString());
                if (i < line.Length && line[i] == ',') i++;
            }
            else
            {
                int start = i;
                while (i < line.Length && line[i] != ',') i++;
                fields.Add(line[start..i]);
                if (i < line.Length) i++;
            }
        }
        return fields.ToArray();
    }

    private static List<string> ParseServiceTags(string input) =>
        input
            .Split(new[] { '\n', '\r', ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim().ToUpperInvariant())
            .Where(t => t.Length is >= 4 and <= 10 && t.All(char.IsLetterOrDigit))
            .Distinct()
            .ToList();

    // -------------------------------------------------------------------------
    // Export
    // -------------------------------------------------------------------------

    private void ExportCsv(object sender, RoutedEventArgs e)
    {
        List<DeviceInfo> source = (_tabs.SelectedIndex == 0
            ? (IEnumerable<DeviceInfo>)_scanResults
            : _tagResults).ToList();

        if (source.Count == 0)
        {
            MessageBox.Show("No results to export on the active tab.", "Export",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new SaveFileDialog
        {
            Filter   = "CSV files (*.csv)|*.csv",
            FileName = $"DellWarranty_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
            Title    = "Export Results"
        };
        if (dlg.ShowDialog(this) != true) return;

        bool hasIpHost = _tabs.SelectedIndex == 0;
        var sb = new StringBuilder();
        sb.AppendLine(hasIpHost
            ? "IP Address,Hostname,Service Tag,Model,Warranty Status,Warranty End,Days Remaining,Notes"
            : "Service Tag,Model,Warranty Status,Warranty End,Days Remaining,Notes");

        foreach (var d in source)
        {
            if (hasIpHost)
                sb.AppendLine(
                    $"{Esc(d.IpAddress)},{Esc(d.Hostname)},{Esc(d.ServiceTag)},{Esc(d.Model)}," +
                    $"{Esc(d.WarrantyStatus)},{Esc(d.WarrantyEndDisplay)},{Esc(d.DaysRemainingDisplay)},{Esc(d.Notes)}");
            else
                sb.AppendLine(
                    $"{Esc(d.ServiceTag)},{Esc(d.Model)}," +
                    $"{Esc(d.WarrantyStatus)},{Esc(d.WarrantyEndDisplay)},{Esc(d.DaysRemainingDisplay)},{Esc(d.Notes)}");
        }

        File.WriteAllText(dlg.FileName, sb.ToString(), Encoding.UTF8);
        MessageBox.Show($"Exported {source.Count} record(s) to:\n{dlg.FileName}",
            "Export Complete", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static string Esc(string s) =>
        s.Contains(',') || s.Contains('"') || s.Contains('\n')
            ? $"\"{s.Replace("\"", "\"\"")}\""
            : s;

    // -------------------------------------------------------------------------
    // Stat cards
    // -------------------------------------------------------------------------

    private void RefreshStatCards(IEnumerable<DeviceInfo> items)
    {
        int active = 0, expiringSoon = 0, expired = 0;
        foreach (var d in items)
        {
            switch (d.WarrantyStatus)
            {
                case "Active" when d.DaysRemaining.HasValue && d.DaysRemaining.Value < 90:
                    expiringSoon++; break;
                case "Active":
                    active++; break;
                case "Expired":
                    expired++; break;
            }
        }
        _txtActiveCount.Text = active.ToString();
        _txtExpiringSoonCount.Text = expiringSoon.ToString();
        _txtExpiredCount.Text = expired.ToString();
    }

    // -------------------------------------------------------------------------
    // State management
    // -------------------------------------------------------------------------

    private void SetScanState(bool scanning)
    {
        _btnScan.IsEnabled       = !scanning;
        _btnStopScan.IsEnabled   =  scanning;
        _txtIpRange.IsEnabled    = !scanning;
        _btnLookupTags.IsEnabled = !scanning;
        _menuSettings.IsEnabled  = !scanning;
        _menuExport.IsEnabled    = !scanning && (_scanResults.Count > 0 || _tagResults.Count > 0);
    }

    private void SetTagLookupState(bool running)
    {
        _btnLookupTags.IsEnabled  = !running;
        _btnStopLookup.IsEnabled  =  running;
        _txtServiceTags.IsEnabled = !running;
        _btnScan.IsEnabled        = !running;
        _menuSettings.IsEnabled   = !running;
        _menuExport.IsEnabled     = !running && (_scanResults.Count > 0 || _tagResults.Count > 0);
    }

    // -------------------------------------------------------------------------
    // Menu handlers
    // -------------------------------------------------------------------------

    private void OpenSettings(object sender, RoutedEventArgs e)
    {
        var win = new SettingsWindow(_settings);
        win.Owner = this;
        win.ShowDialog();
        _settings = AppSettings.Load();
    }

    private void ShowAbout(object sender, RoutedEventArgs e)
    {
        var win = new AboutWindow();
        win.Owner = this;
        win.ShowDialog();
    }

    private void CheckUpdates(object sender, RoutedEventArgs e) =>
        _ = CheckForUpdatesAsync(userTriggered: true);

    private void ShowHelp(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            "To use Dell warranty lookup, you need free API credentials from Dell TechDirect:\n\n" +
            "1. Go to https://tdm.dell.com and sign in with your Dell account\n" +
            "2. Navigate to API Management → My Applications\n" +
            "3. Create a new application — select the 'Warranty' API\n" +
            "4. Copy your Client ID and Client Secret\n" +
            "5. Open Tools → Settings in this app and paste them in\n\n" +
            "The API is free. Registration takes a few minutes.",
            "Dell API Setup Instructions", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    // -------------------------------------------------------------------------
    // Update check
    // -------------------------------------------------------------------------

    private async Task CheckForUpdatesAsync(bool userTriggered)
    {
        const string apiUrl      = "https://api.github.com/repos/kbaker827/DellWarrantyScanner/releases/latest";
        const string releasesUrl = "https://github.com/kbaker827/DellWarrantyScanner/releases";

        var current = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);

        if (userTriggered)
            UpdateStatus("Checking for updates...");

        try
        {
            using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, apiUrl);
            request.Headers.UserAgent.ParseAdd("DellWarrantyScanner/" + current.ToString(3));
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();
            var obj  = Newtonsoft.Json.Linq.JObject.Parse(json);

            string tagName  = obj["tag_name"]?.ToString() ?? "";
            string cleanTag = tagName.TrimStart('v', 'V');

            if (!Version.TryParse(cleanTag, out var latest))
            {
                if (userTriggered)
                    MessageBox.Show("Could not read the version from GitHub.\n\nCheck manually at:\n" + releasesUrl,
                        "Update Check", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (latest > current)
            {
                var result = MessageBox.Show(
                    $"A new version is available!\n\n" +
                    $"  Current:   v{current.ToString(3)}\n" +
                    $"  Available: {tagName}\n\n" +
                    "Open the releases page to download?",
                    "Update Available", MessageBoxButton.YesNo, MessageBoxImage.Information);

                if (result == MessageBoxResult.Yes)
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        { FileName = releasesUrl, UseShellExecute = true });
            }
            else if (userTriggered)
            {
                MessageBox.Show($"You're up to date!  (v{current.ToString(3)})",
                    "Check for Updates", MessageBoxButton.OK, MessageBoxImage.Information);
            }

            UpdateStatus(latest > current ? $"Update available: {tagName}" : "Ready.");
        }
        catch (Exception ex) when (!userTriggered)
        {
            _ = ex;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not check for updates:\n\n{ex.Message}",
                "Update Check Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            UpdateStatus("Ready.");
        }
    }

    // -------------------------------------------------------------------------
    // UpdateStatus helper
    // -------------------------------------------------------------------------

    private void UpdateStatus(string message, int? progress = null)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => UpdateStatus(message, progress));
            return;
        }
        _statusLabel.Text = message;
        _lblStatus.Text   = message;
        if (progress.HasValue)
        {
            _progress.IsIndeterminate = false;
            _progress.Value = progress.Value;
        }
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        _scanGrid.Items.Refresh();
        _tagGrid.Items.Refresh();
    }

    protected override void OnClosed(EventArgs e)
    {
        ThemeService.ThemeChanged -= OnThemeChanged;
        _cts?.Dispose();
        base.OnClosed(e);
    }
}
