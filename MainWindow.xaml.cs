using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using DellWarrantyScanner.Models;
using DellWarrantyScanner.Services;
using Microsoft.Win32;

namespace DellWarrantyScanner;

public partial class MainWindow : Window
{
    private const string RepoApiUrl  = "https://api.github.com/repos/kbaker827/DellWarrantyScanner/releases/latest";
    private const string ReleasesUrl = "https://github.com/kbaker827/DellWarrantyScanner/releases";

    private AppSettings _settings;
    private readonly ObservableCollection<DeviceInfo> _scanResults = new();
    private readonly ObservableCollection<DeviceInfo> _tagResults = new();
    private CancellationTokenSource? _cts;
    private static readonly HttpClient _httpClient = new()
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

    private bool IsScanTabActive => _tabs.SelectedIndex == 0;

    private IEnumerable<DeviceInfo> ActiveResults => IsScanTabActive ? _scanResults : _tagResults;

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
        if (subnets.Count == 0) return;

        // Prefer a physical adapter over VPN / virtual ones.
        var best = subnets.FirstOrDefault(s =>
            !s.AdapterName.Contains("VPN", StringComparison.OrdinalIgnoreCase) &&
            !s.AdapterName.Contains("Virtual", StringComparison.OrdinalIgnoreCase));
        _txtIpRange.Text = best.Cidr ?? subnets[0].Cidr;
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

        var picker = new SubnetPickerWindow(subnets) { Owner = this };
        if (picker.ShowDialog() == true && picker.SelectedCidr != null)
            _txtIpRange.Text = picker.SelectedCidr;
    }

    // -------------------------------------------------------------------------
    // Network Scan
    // -------------------------------------------------------------------------

    private async void StartScan(object sender, RoutedEventArgs e)
    {
        var ipRange = _txtIpRange.Text.Trim();
        if (ipRange.Length == 0)
        {
            MessageBox.Show("Please enter an IP range.", "Missing Input",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try { NetworkScanner.ParseIpRange(ipRange); }
        catch (ArgumentException ex)
        {
            MessageBox.Show(ex.Message, "Invalid IP Range",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (!_settings.HasApiCredentials)
        {
            var choice = MessageBox.Show(
                "Dell API credentials are not configured.\n\nWithout them the scan will find Dell systems but cannot retrieve warranty dates.\n\nConfigure credentials now?",
                "No API Credentials", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (choice == MessageBoxResult.Yes) { OpenSettings(sender, e); return; }
        }

        _settings.IpRange = ipRange;
        TrySaveSettings();

        _tabs.SelectedIndex = 0;
        _scanResults.Clear();
        RefreshStatCards();
        SetBusy(true, isScan: true);

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
        var deviceFound = new Progress<DeviceInfo>(d => _scanResults.Add(d));

        try
        {
            var scanner = new NetworkScanner(_settings);
            var results = await scanner.ScanAsync(ipRange, scanProgress, deviceFound, ct);

            // Replace the live, unordered rows with the IP-sorted list.
            _scanResults.Clear();
            foreach (var d in results) _scanResults.Add(d);

            if (ct.IsCancellationRequested)
            {
                MarkPending(_scanResults, WarrantyStatus.NotChecked, "Scan stopped before warranty lookup");
                UpdateStatus($"Scan stopped. {_scanResults.Count} Dell system(s) found so far.");
                return;
            }

            if (_settings.HasApiCredentials && _scanResults.Any(d => d.WarrantyStatus == WarrantyStatus.Pending))
            {
                UpdateStatus($"Found {_scanResults.Count} Dell system(s). Looking up warranties...");
                _progress.IsIndeterminate = true;

                using var warranty = new DellWarrantyService(_settings.DellClientId, _settings.DellClientSecret);
                await warranty.LookupWarrantiesAsync(_scanResults.ToList(), new Progress<string>(UpdateStatus), ct);
            }
            else
            {
                MarkPending(_scanResults, WarrantyStatus.NoApiKey, "Configure Dell API credentials in Tools → Settings");
            }

            UpdateStatus($"Done — {_scanResults.Count} Dell system(s) found  |  {SummarizeResults(_scanResults)}.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            MarkPending(_scanResults, WarrantyStatus.NotChecked, "Stopped before warranty lookup finished");
            UpdateStatus($"Stopped. {_scanResults.Count} Dell system(s) found; warranty lookup incomplete.");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error during scan:\n\n{ex.Message}", "Scan Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            UpdateStatus("Scan failed. Check settings and try again.");
        }
        finally
        {
            FinishOperation(_scanGrid);
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        UpdateStatus("Stopping...");
    }

    // -------------------------------------------------------------------------
    // Service Tag Lookup
    // -------------------------------------------------------------------------

    private async void StartTagLookup(object sender, RoutedEventArgs e)
    {
        var raw = _txtServiceTags.Text.Trim();
        if (raw.Length == 0)
        {
            MessageBox.Show("Please enter at least one service tag.", "Missing Input",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!_settings.HasApiCredentials)
        {
            var choice = MessageBox.Show(
                "Dell API credentials are not configured.\n\nService tag lookup requires API credentials.\n\nConfigure now?",
                "No API Credentials", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (choice == MessageBoxResult.Yes) OpenSettings(sender, e);
            return;
        }

        var tags = ServiceTags.Parse(raw);
        if (tags.Count == 0)
        {
            MessageBox.Show(
                $"No valid service tags found. Service tags are {ServiceTags.FormatDescription}.",
                "Invalid Input", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (tags.Count > DellWarrantyService.MaxTagsPerRequest)
        {
            MessageBox.Show(
                $"Too many tags ({tags.Count}). Look up at most {DellWarrantyService.MaxTagsPerRequest} at a time.",
                "Too Many Tags", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _tagResults.Clear();
        foreach (var t in tags)
            _tagResults.Add(new DeviceInfo { ServiceTag = t });
        RefreshStatCards();
        SetBusy(true, isScan: false);

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        try
        {
            _progress.IsIndeterminate = true;
            UpdateStatus($"Looking up {tags.Count} service tag(s)...");

            using var warranty = new DellWarrantyService(_settings.DellClientId, _settings.DellClientSecret);
            await warranty.LookupWarrantiesAsync(_tagResults.ToList(), new Progress<string>(UpdateStatus), ct);

            UpdateStatus($"Done — {_tagResults.Count} tag(s) looked up  |  {SummarizeResults(_tagResults)}.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            MarkPending(_tagResults, WarrantyStatus.NotChecked, "Lookup stopped");
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
            FinishOperation(_tagGrid);
        }
    }

    private void ImportCsv_Click(object sender, RoutedEventArgs e) => ImportTagsFromCsv();

    private void ClearTags_Click(object sender, RoutedEventArgs e)
    {
        _txtServiceTags.Clear();
        _tagResults.Clear();
        RefreshStatCards();
        UpdateExportEnabled();
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show($"Could not read file:\n{ex.Message}", "Import Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var nonEmpty = lines.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        if (nonEmpty.Count == 0)
        {
            MessageBox.Show("The file is empty.", "Import",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        char delimiter = CsvHelper.DetectDelimiter(nonEmpty[0]);
        var rows = nonEmpty.Select(l => CsvHelper.ParseLine(l, delimiter)).ToList();
        int columnCount = rows.Max(r => r.Length);

        IEnumerable<string> rawValues;
        if (columnCount == 1)
        {
            // Drop a header row such as "Service Tag" or "Serial".
            rawValues = rows
                .Skip(CsvHelper.LooksLikeTagHeader(rows[0][0]) ? 1 : 0)
                .Select(r => r[0]);
        }
        else
        {
            var picker = new CsvColumnPickerWindow(rows, columnCount) { Owner = this };
            if (picker.ShowDialog() != true || picker.SelectedColumnIndex < 0) return;
            int colIndex = picker.SelectedColumnIndex;

            // Multi-column files are assumed to have a header row.
            rawValues = rows
                .Skip(1)
                .Where(r => r.Length > colIndex)
                .Select(r => r[colIndex]);
        }

        var tags = ServiceTags.FilterValid(rawValues);
        if (tags.Count == 0)
        {
            MessageBox.Show(
                "No valid service tags found in the selected column.\n\n" +
                $"Service tags are {ServiceTags.FormatDescription}.",
                "No Tags Found", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string existing = _txtServiceTags.Text.Trim();
        string toAdd    = string.Join(Environment.NewLine, tags);
        _txtServiceTags.Text = existing.Length == 0
            ? toAdd
            : existing + Environment.NewLine + toAdd;

        UpdateStatus($"Imported {tags.Count} service tag(s) from {Path.GetFileName(dlg.FileName)}.");
    }

    // -------------------------------------------------------------------------
    // Export
    // -------------------------------------------------------------------------

    private void ExportCsv(object sender, RoutedEventArgs e)
    {
        var source = ActiveResults.ToList();
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

        bool includeNetwork = IsScanTabActive;
        var sb = new StringBuilder();
        sb.AppendLine(includeNetwork
            ? "IP Address,Hostname,Service Tag,Model,Warranty Status,Warranty End,Days Remaining,Notes"
            : "Service Tag,Model,Warranty Status,Warranty End,Days Remaining,Notes");

        foreach (var d in source)
        {
            var fields = new List<string>();
            if (includeNetwork)
            {
                fields.Add(CsvHelper.Escape(d.IpAddress));
                fields.Add(CsvHelper.Escape(d.Hostname));
            }
            fields.Add(CsvHelper.Escape(d.ServiceTag));
            fields.Add(CsvHelper.Escape(d.Model));
            fields.Add(CsvHelper.Escape(d.WarrantyStatus));
            fields.Add(CsvHelper.Escape(d.WarrantyEndDisplay, isText: false));
            fields.Add(CsvHelper.Escape(d.DaysRemainingDisplay, isText: false));
            fields.Add(CsvHelper.Escape(d.Notes));
            sb.AppendLine(string.Join(",", fields));
        }

        try
        {
            // UTF-8 with BOM so Excel detects the encoding.
            File.WriteAllText(dlg.FileName, sb.ToString(), Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show($"Could not save the file (is it open in Excel?):\n\n{ex.Message}",
                "Export Failed", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        MessageBox.Show($"Exported {source.Count} record(s) to:\n{dlg.FileName}",
            "Export Complete", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // -------------------------------------------------------------------------
    // Stat cards
    // -------------------------------------------------------------------------

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // SelectionChanged bubbles up from the DataGrids too; only react to tab changes.
        if (e.OriginalSource == _tabs && IsLoaded)
            RefreshStatCards();
    }

    private void RefreshStatCards()
    {
        int active = 0, expiringSoon = 0, expired = 0;
        foreach (var d in ActiveResults)
        {
            if (d.IsExpiringSoon) expiringSoon++;
            else if (d.WarrantyStatus == WarrantyStatus.Active) active++;
            else if (d.WarrantyStatus == WarrantyStatus.Expired) expired++;
        }
        _txtActiveCount.Text       = active.ToString();
        _txtExpiringSoonCount.Text = expiringSoon.ToString();
        _txtExpiredCount.Text      = expired.ToString();
    }

    private static string SummarizeResults(IEnumerable<DeviceInfo> results)
    {
        var list = results.ToList();
        int active  = list.Count(d => d.WarrantyStatus == WarrantyStatus.Active);
        int expired = list.Count(d => d.WarrantyStatus == WarrantyStatus.Expired);
        return $"{active} active  |  {expired} expired";
    }

    private static void MarkPending(IEnumerable<DeviceInfo> devices, string status, string notes)
    {
        foreach (var d in devices.Where(d => d.WarrantyStatus == WarrantyStatus.Pending))
        {
            d.WarrantyStatus = status;
            d.Notes = notes;
        }
    }

    // -------------------------------------------------------------------------
    // State management
    // -------------------------------------------------------------------------

    // Only one scan or lookup runs at a time since they share _cts and the progress bar.
    private void SetBusy(bool busy, bool isScan)
    {
        _btnScan.IsEnabled        = !busy;
        _btnStopScan.IsEnabled    = busy && isScan;
        _btnAutoDetect.IsEnabled  = !busy;
        _txtIpRange.IsEnabled     = !busy;

        _btnLookupTags.IsEnabled  = !busy;
        _btnStopLookup.IsEnabled  = busy && !isScan;
        _btnImportCsv.IsEnabled   = !busy;
        _btnClearTags.IsEnabled   = !busy;
        _txtServiceTags.IsEnabled = !busy;

        _menuSettings.IsEnabled   = !busy;
        UpdateExportEnabled(busy);
    }

    private void UpdateExportEnabled(bool busy = false)
    {
        bool canExport = !busy && (_scanResults.Count > 0 || _tagResults.Count > 0);
        _menuExport.IsEnabled = canExport;
        _btnExport.IsEnabled  = canExport;
    }

    private void FinishOperation(DataGrid grid)
    {
        // DeviceInfo doesn't raise change notifications, so re-render rows (and their colours).
        grid.Items.Refresh();
        RefreshStatCards();

        _progress.IsIndeterminate = false;
        _progress.Value = 0;
        _cts?.Dispose();
        _cts = null;
        SetBusy(false, isScan: false);
    }

    private void TrySaveSettings()
    {
        try { _settings.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            UpdateStatus($"Warning: could not save settings ({ex.Message}).");
        }
    }

    // -------------------------------------------------------------------------
    // Menu handlers
    // -------------------------------------------------------------------------

    private void OpenSettings(object sender, RoutedEventArgs e)
    {
        var win = new SettingsWindow(_settings) { Owner = this };
        win.ShowDialog();
        _settings = AppSettings.Load();
    }

    private void ShowAbout(object sender, RoutedEventArgs e)
    {
        new AboutWindow { Owner = this }.ShowDialog();
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
        var current = ToThreePart(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0));

        if (userTriggered)
            UpdateStatus("Checking for updates...");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, RepoApiUrl);
            request.Headers.UserAgent.ParseAdd("DellWarrantyScanner/" + current);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            string tagName = json.RootElement.TryGetProperty("tag_name", out var tag) ? tag.GetString() ?? "" : "";

            if (!Version.TryParse(tagName.TrimStart('v', 'V'), out var parsed))
            {
                if (userTriggered)
                {
                    MessageBox.Show("Could not read the version from GitHub.\n\nCheck manually at:\n" + ReleasesUrl,
                        "Update Check", MessageBoxButton.OK, MessageBoxImage.Warning);
                    UpdateStatus("Ready.");
                }
                return;
            }

            var latest = ToThreePart(parsed);
            if (latest > current)
            {
                UpdateStatus($"Update available: {tagName}");
                var result = MessageBox.Show(
                    "A new version is available!\n\n" +
                    $"  Current:   v{current}\n" +
                    $"  Available: {tagName}\n\n" +
                    "Open the releases page to download?",
                    "Update Available", MessageBoxButton.YesNo, MessageBoxImage.Information);

                if (result == MessageBoxResult.Yes)
                    UrlLauncher.Open(ReleasesUrl);
            }
            else if (userTriggered)
            {
                UpdateStatus("Ready.");
                MessageBox.Show($"You're up to date!  (v{current})",
                    "Check for Updates", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex) when (userTriggered)
        {
            MessageBox.Show($"Could not check for updates:\n\n{ex.Message}",
                "Update Check Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            UpdateStatus("Ready.");
        }
        catch
        {
            // The startup check is best-effort; stay quiet when offline.
        }
    }

    // Compare as major.minor.build so "1.2.0" (tag) equals 1.2.0.0 (assembly version).
    private static Version ToThreePart(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private void UpdateStatus(string message)
    {
        _statusLabel.Text = message;
        _lblStatus.Text   = message;
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        _scanGrid.Items.Refresh();
        _tagGrid.Items.Refresh();
    }

    protected override void OnClosed(EventArgs e)
    {
        ThemeService.ThemeChanged -= OnThemeChanged;
        _cts?.Cancel();
        base.OnClosed(e);
    }
}
