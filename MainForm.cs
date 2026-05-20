using System.Reflection;
using System.Text;
using DellWarrantyScanner.Models;
using DellWarrantyScanner.Services;

namespace DellWarrantyScanner;

public class MainForm : Form
{
    private AppSettings _settings;
    private List<DeviceInfo> _scanResults = new();
    private List<DeviceInfo> _tagResults = new();
    private CancellationTokenSource? _cts;

    // Toolbar controls
    private TextBox _txtIpRange = null!;
    private Button _btnScan = null!;
    private Button _btnStopScan = null!;
    private Button _btnLookupTags = null!;
    private Button _btnStopLookup = null!;

    // Shared UI
    private ProgressBar _progress = null!;
    private Label _lblStatus = null!;
    private ToolStripStatusLabel _statusLabel = null!;
    private ToolStripMenuItem _menuSettings = null!;
    private ToolStripMenuItem _menuExport = null!;
    private TabControl _tabs = null!;

    // Tab 1 – Network Scan
    private DataGridView _scanGrid = null!;

    // Tab 2 – Service Tag Lookup
    private TextBox _txtServiceTags = null!;
    private DataGridView _tagGrid = null!;

    public MainForm()
    {
        _settings = AppSettings.Load();
        InitializeComponent();
        LoadEmbeddedIcon();
        AutoDetectAndFillSubnet();
        UpdateStatus("Ready. Configure an IP range and click Scan Network, or enter service tags on the Lookup tab.");
    }

    private void LoadEmbeddedIcon()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("DellWarrantyScanner.Resources.app.ico");
            if (stream != null) Icon = new Icon(stream);
        }
        catch { }
    }

    private void AutoDetectAndFillSubnet()
    {
        if (!string.IsNullOrWhiteSpace(_settings.IpRange))
            return;

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

    // -------------------------------------------------------------------------
    // Layout
    // -------------------------------------------------------------------------

    private void InitializeComponent()
    {
        Text = "Dell Warranty Scanner";
        Size = new Size(980, 680);
        MinimumSize = new Size(720, 500);
        StartPosition = FormStartPosition.CenterScreen;

        // WinForms docks DockStyle.Top controls so the LAST added is closest to the
        // top edge. Status bar and fill go first, then top-docked controls in reverse
        // visual order (status label → progress → toolbar → menu).
        BuildStatusBar();
        BuildTabs();
        BuildProgressArea();   // lblStatus added before progPanel → lblStatus ends up lower
        BuildTopToolbar();
        BuildMenu();           // added last → sits at the very top of the form
    }

    private void BuildMenu()
    {
        var menu = new MenuStrip();

        var fileMenu = new ToolStripMenuItem("File");
        _menuExport = new ToolStripMenuItem("Export to CSV...") { Enabled = false };
        _menuExport.Click += ExportCsv;
        var menuExit = new ToolStripMenuItem("Exit");
        menuExit.Click += (_, _) => Close();
        fileMenu.DropDownItems.AddRange(new ToolStripItem[] { _menuExport, new ToolStripSeparator(), menuExit });

        var toolsMenu = new ToolStripMenuItem("Tools");
        _menuSettings = new ToolStripMenuItem("Settings...");
        _menuSettings.Click += OpenSettings;
        toolsMenu.DropDownItems.Add(_menuSettings);

        var helpMenu = new ToolStripMenuItem("Help");

        var aboutItem = new ToolStripMenuItem("About Dell Warranty Scanner...");
        aboutItem.Click += (_, _) => { using var f = new AboutForm(Icon); f.ShowDialog(this); };

        var checkUpdatesItem = new ToolStripMenuItem("Check for Updates...");
        checkUpdatesItem.Click += async (_, _) => await CheckForUpdatesAsync(userTriggered: true);

        var apiSetupItem = new ToolStripMenuItem("Dell TechDirect API Setup...");
        apiSetupItem.Click += ShowHelp;

        helpMenu.DropDownItems.AddRange(new ToolStripItem[]
        {
            aboutItem,
            new ToolStripSeparator(),
            checkUpdatesItem,
            new ToolStripSeparator(),
            apiSetupItem
        });

        menu.Items.AddRange(new ToolStripItem[] { fileMenu, toolsMenu, helpMenu });
        MainMenuStrip = menu;
        Controls.Add(menu);
    }

    private void BuildTopToolbar()
    {
        var topPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 54,
            Padding = new Padding(8, 8, 8, 0)
        };

        var ipLabel = new Label
        {
            Text = "IP Range:",
            AutoSize = true,
            Location = new Point(8, 14),
            Font = new Font(SystemFonts.DefaultFont!, FontStyle.Bold)
        };

        _txtIpRange = new TextBox
        {
            Location = new Point(78, 10),
            Width = 230,
            Height = 26,
            PlaceholderText = "e.g. 192.168.1.0/24 or 10.0.0.1-254",
            Text = _settings.IpRange,
            Font = new Font("Consolas", 10f)
        };

        _btnScan = new Button
        {
            Text = "Scan Network",
            Location = new Point(318, 8),
            Width = 116,
            Height = 30,
            BackColor = Color.FromArgb(0, 120, 215),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        _btnScan.FlatAppearance.BorderSize = 0;
        _btnScan.Click += StartScan;

        _btnStopScan = new Button
        {
            Text = "Stop Scan",
            Location = new Point(444, 8),
            Width = 90,
            Height = 30,
            BackColor = Color.FromArgb(196, 43, 28),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Enabled = false
        };
        _btnStopScan.FlatAppearance.BorderSize = 0;
        _btnStopScan.Click += (_, _) => _cts?.Cancel();

        var btnDetect = new Button
        {
            Text = "Auto-detect",
            Location = new Point(544, 8),
            Width = 90,
            Height = 30
        };
        btnDetect.Click += (_, _) => ShowSubnetPicker();

        var btnExport = new Button
        {
            Text = "Export CSV",
            Location = new Point(644, 8),
            Width = 96,
            Height = 30
        };
        btnExport.Click += ExportCsv;

        var lblHint = new Label
        {
            Text = "Formats: 192.168.1.0/24  |  10.0.0.1-50  |  10.0.0.1-10.0.0.50",
            AutoSize = true,
            Location = new Point(78, 36),
            ForeColor = Color.Gray,
            Font = new Font(SystemFonts.DefaultFont!.FontFamily, 7.5f)
        };

        topPanel.Controls.AddRange(new Control[]
            { ipLabel, _txtIpRange, _btnScan, _btnStopScan, btnDetect, btnExport, lblHint });
        Controls.Add(topPanel);
    }

    private void BuildProgressArea()
    {
        var progPanel = new Panel { Dock = DockStyle.Top, Height = 28, Padding = new Padding(8, 4, 8, 0) };
        _progress = new ProgressBar { Dock = DockStyle.Fill, Style = ProgressBarStyle.Continuous };
        progPanel.Controls.Add(_progress);
        Controls.Add(progPanel);

        _lblStatus = new Label
        {
            Dock = DockStyle.Top,
            Height = 22,
            Padding = new Padding(8, 2, 0, 0),
            ForeColor = Color.DimGray,
            Font = new Font(SystemFonts.DefaultFont!.FontFamily, 8.5f)
        };
        Controls.Add(_lblStatus);
    }

    private void BuildTabs()
    {
        _tabs = new TabControl { Dock = DockStyle.Fill, Font = new Font("Segoe UI", 9f) };

        _tabs.TabPages.Add(BuildNetworkScanTab());
        _tabs.TabPages.Add(BuildServiceTagTab());

        Controls.Add(_tabs);
    }

    private TabPage BuildNetworkScanTab()
    {
        var tab = new TabPage("  Network Scan Results  ");

        _scanGrid = MakeGrid();
        AddGridColumns(_scanGrid, includeIpHost: true);
        _scanGrid.CellFormatting += (s, e) => FormatGridRow(e, _scanResults);
        tab.Controls.Add(_scanGrid);

        return tab;
    }

    private TabPage BuildServiceTagTab()
    {
        var tab = new TabPage("  Service Tag Lookup  ");

        // Input panel (top, fixed height)
        var inputPanel = new Panel { Dock = DockStyle.Top, Height = 114, Padding = new Padding(10, 8, 10, 6) };

        var lblTags = new Label
        {
            Text = "Service Tag(s):",
            AutoSize = true,
            Location = new Point(0, 10),
            Font = new Font(SystemFonts.DefaultFont!, FontStyle.Bold)
        };

        _txtServiceTags = new TextBox
        {
            Location = new Point(0, 30),
            Width = 400,
            Height = 68,
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            PlaceholderText = "Enter one service tag per line, or separate with commas\ne.g.\nABC1234\nXYZ5678, DEF9999",
            Font = new Font("Consolas", 10f)
        };

        _btnLookupTags = new Button
        {
            Text = "Look Up Warranty",
            Location = new Point(414, 30),
            Width = 148,
            Height = 30,
            BackColor = Color.FromArgb(0, 120, 215),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        _btnLookupTags.FlatAppearance.BorderSize = 0;
        _btnLookupTags.Click += StartTagLookup;

        var btnImportCsv = new Button
        {
            Text = "Import from CSV...",
            Location = new Point(414, 68),
            Width = 148,
            Height = 30
        };
        btnImportCsv.Click += (_, _) => ImportTagsFromCsv();

        _btnStopLookup = new Button
        {
            Text = "Stop",
            Location = new Point(572, 30),
            Width = 60,
            Height = 30,
            BackColor = Color.FromArgb(196, 43, 28),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Enabled = false
        };
        _btnStopLookup.FlatAppearance.BorderSize = 0;
        _btnStopLookup.Click += (_, _) => _cts?.Cancel();

        var btnClearTags = new Button
        {
            Text = "Clear",
            Location = new Point(572, 68),
            Width = 60,
            Height = 30
        };
        btnClearTags.Click += (_, _) =>
        {
            _txtServiceTags.Clear();
            _tagResults.Clear();
            _tagGrid.DataSource = null;
            UpdateStatus("Cleared.");
        };

        var lblHint = new Label
        {
            Text = "One tag per line, or comma/space/semicolon separated. Max 100 per lookup.",
            AutoSize = true,
            Location = new Point(0, 102),
            ForeColor = Color.Gray,
            Font = new Font(SystemFonts.DefaultFont!.FontFamily, 7.5f)
        };

        inputPanel.Controls.AddRange(new Control[]
            { lblTags, _txtServiceTags, _btnLookupTags, btnImportCsv, _btnStopLookup, btnClearTags, lblHint });
        tab.Controls.Add(inputPanel);

        // Results grid (fills remaining space)
        _tagGrid = MakeGrid();
        AddGridColumns(_tagGrid, includeIpHost: false);
        _tagGrid.CellFormatting += (s, e) => FormatGridRow(e, _tagResults);
        tab.Controls.Add(_tagGrid);

        return tab;
    }

    private void BuildStatusBar()
    {
        var statusBar = new StatusStrip();
        _statusLabel = new ToolStripStatusLabel("Ready") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        statusBar.Items.Add(_statusLabel);
        Controls.Add(statusBar);
    }

    // -------------------------------------------------------------------------
    // Grid helpers
    // -------------------------------------------------------------------------

    private static DataGridView MakeGrid()
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            AutoGenerateColumns = false,          // use only manually-defined columns
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = true,
            BackgroundColor = SystemColors.Window,
            BorderStyle = BorderStyle.None,
            RowHeadersVisible = false,
            ColumnHeadersVisible = true,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
            Font = new Font("Segoe UI", 9f)
        };
        grid.ColumnHeadersHeight = 28;
        grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(0, 120, 215);
        grid.DefaultCellStyle.SelectionForeColor = Color.White;
        // Header row: dark background, white bold text
        grid.ColumnHeadersDefaultCellStyle.BackColor  = Color.FromArgb(45, 45, 48);
        grid.ColumnHeadersDefaultCellStyle.ForeColor  = Color.White;
        grid.ColumnHeadersDefaultCellStyle.Font       = new Font("Segoe UI", 9f, FontStyle.Bold);
        grid.EnableHeadersVisualStyles = false;   // needed for custom header colors to apply
        return grid;
    }

    private static void AddGridColumns(DataGridView grid, bool includeIpHost)
    {
        if (includeIpHost)
        {
            grid.Columns.Add(Col("IpAddress",          "IP Address",    120));
            grid.Columns.Add(Col("Hostname",            "Hostname",      180));
        }
        grid.Columns.Add(Col("ServiceTag",          "Service Tag",   110));
        grid.Columns.Add(Col("Model",               "Model",         180));
        grid.Columns.Add(Col("WarrantyStatus",      "Status",         90));
        grid.Columns.Add(Col("WarrantyEndDisplay",  "Warranty End",  120));
        grid.Columns.Add(Col("DaysRemainingDisplay","Days Left",       80));
        grid.Columns.Add(Col("Notes",               "Notes",          160));
    }

    private static DataGridViewTextBoxColumn Col(string prop, string header, int minWidth) =>
        new DataGridViewTextBoxColumn
        {
            DataPropertyName = prop,
            HeaderText = header,
            MinimumWidth = minWidth,
            SortMode = DataGridViewColumnSortMode.Automatic
        };

    private static void FormatGridRow(DataGridViewCellFormattingEventArgs e, List<DeviceInfo> source)
    {
        if (e.RowIndex < 0 || e.RowIndex >= source.Count) return;
        var device = source[e.RowIndex];

        Color rowColor = device.WarrantyStatus switch
        {
            "Expired"                            => Color.FromArgb(255, 220, 220),
            "Active" when device.DaysRemaining < 90 => Color.FromArgb(255, 245, 200),
            "Active"                             => Color.FromArgb(220, 255, 220),
            "WMI Access Denied"                  => Color.FromArgb(240, 240, 255),
            _                                    => SystemColors.Window
        };

        e.CellStyle.BackColor = rowColor;
    }

    // -------------------------------------------------------------------------
    // Network Scan
    // -------------------------------------------------------------------------

    private async void StartScan(object? sender, EventArgs e)
    {
        var ipRange = _txtIpRange.Text.Trim();
        if (string.IsNullOrWhiteSpace(ipRange))
        {
            MessageBox.Show("Please enter an IP range.", "Missing Input", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        List<string> ips;
        try { ips = NetworkScanner.ParseIpRange(ipRange); }
        catch (Exception ex)
        {
            MessageBox.Show($"Invalid IP range: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        if (ips.Count == 0)
        {
            MessageBox.Show("No IP addresses found in that range.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        if (ips.Count > 65536)
        {
            MessageBox.Show("Range too large (max 65,536 IPs). Please narrow it down.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        if (string.IsNullOrWhiteSpace(_settings.DellClientId) || string.IsNullOrWhiteSpace(_settings.DellClientSecret))
        {
            var choice = MessageBox.Show(
                "Dell API credentials are not configured.\n\nWithout them the scan will find Dell systems but cannot retrieve warranty dates.\n\nConfigure credentials now?",
                "No API Credentials", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (choice == DialogResult.Yes) { OpenSettings(sender, e); return; }
        }

        _settings.IpRange = ipRange;
        _settings.Save();

        SetScanState(true);
        _scanResults.Clear();
        _scanGrid.DataSource = null;
        _tabs.SelectedIndex = 0;

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        var scanProgress = new Progress<(int completed, int total, string message)>(p =>
        {
            if (p.total > 0) { _progress.Maximum = p.total; _progress.Value = Math.Min(p.completed, p.total); }
            UpdateStatus(p.message);
        });

        try
        {
            var scanner = new NetworkScanner(_settings);
            _scanResults = await scanner.ScanAsync(ipRange, scanProgress, ct);

            UpdateStatus($"Found {_scanResults.Count} Dell system(s). Looking up warranties...");
            _progress.Style = ProgressBarStyle.Marquee;

            if (!string.IsNullOrWhiteSpace(_settings.DellClientId) && _scanResults.Any(d => !string.IsNullOrEmpty(d.ServiceTag)))
            {
                using var warranty = new DellWarrantyService(_settings.DellClientId, _settings.DellClientSecret);
                await warranty.LookupWarrantiesAsync(_scanResults, new Progress<string>(UpdateStatus));
            }
            else
            {
                foreach (var d in _scanResults.Where(d => d.WarrantyStatus == "Pending"))
                    d.WarrantyStatus = "No API Key";
            }

            BindGrid(_scanGrid, _scanResults);

            int expired = _scanResults.Count(d => d.WarrantyStatus == "Expired");
            int active  = _scanResults.Count(d => d.WarrantyStatus == "Active");
            UpdateStatus($"Done — {_scanResults.Count} Dell system(s) found  |  {active} active  |  {expired} expired.");
            _menuExport.Enabled = _scanResults.Count > 0;
        }
        catch (OperationCanceledException)
        {
            BindGrid(_scanGrid, _scanResults); // show partial results
            UpdateStatus($"Scan stopped. {_scanResults.Count} Dell system(s) found so far.");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error during scan:\n\n{ex.Message}", "Scan Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            UpdateStatus("Scan failed. Check settings and try again.");
        }
        finally
        {
            SetScanState(false);
            _progress.Style = ProgressBarStyle.Continuous;
            _progress.Value = 0;
        }
    }

    // -------------------------------------------------------------------------
    // Service Tag Lookup
    // -------------------------------------------------------------------------

    private async void StartTagLookup(object? sender, EventArgs e)
    {
        var raw = _txtServiceTags.Text.Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            MessageBox.Show("Please enter at least one service tag.", "Missing Input", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(_settings.DellClientId) || string.IsNullOrWhiteSpace(_settings.DellClientSecret))
        {
            var choice = MessageBox.Show(
                "Dell API credentials are not configured.\n\nService tag lookup requires API credentials.\n\nConfigure now?",
                "No API Credentials", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (choice == DialogResult.Yes) { OpenSettings(sender, e); return; }
            return;
        }

        var tags = ParseServiceTags(raw);
        if (tags.Count == 0)
        {
            MessageBox.Show("No valid service tags found. Tags should be 5–8 alphanumeric characters.", "Invalid Input", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (tags.Count > 100)
        {
            MessageBox.Show($"Too many tags ({tags.Count}). Dell API allows up to 100 per lookup.", "Too Many Tags", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        SetTagLookupState(true);
        _tagResults = tags.Select(t => new DeviceInfo { ServiceTag = t, WarrantyStatus = "Pending" }).ToList();
        _tagGrid.DataSource = null;

        _cts = new CancellationTokenSource();

        try
        {
            _progress.Style = ProgressBarStyle.Marquee;
            UpdateStatus($"Looking up {tags.Count} service tag(s)...");

            using var warranty = new DellWarrantyService(_settings.DellClientId, _settings.DellClientSecret);
            await warranty.LookupWarrantiesAsync(_tagResults, new Progress<string>(UpdateStatus));

            BindGrid(_tagGrid, _tagResults);

            int expired = _tagResults.Count(d => d.WarrantyStatus == "Expired");
            int active  = _tagResults.Count(d => d.WarrantyStatus == "Active");
            UpdateStatus($"Done. {_tagResults.Count} tag(s) looked up  |  {active} active  |  {expired} expired.");
        }
        catch (OperationCanceledException)
        {
            BindGrid(_tagGrid, _tagResults);
            UpdateStatus("Lookup stopped.");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error during lookup:\n\n{ex.Message}", "Lookup Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            UpdateStatus("Lookup failed. Check API credentials in Settings.");
        }
        finally
        {
            SetTagLookupState(false);
            _progress.Style = ProgressBarStyle.Continuous;
            _progress.Value = 0;
        }
    }

    private void ImportTagsFromCsv()
    {
        using var dlg = new OpenFileDialog
        {
            Title  = "Import Service Tags from CSV",
            Filter = "CSV files (*.csv)|*.csv|Text files (*.txt)|*.txt|All files (*.*)|*.*"
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        string[] lines;
        try { lines = File.ReadAllLines(dlg.FileName); }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not read file:\n{ex.Message}", "Import Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        if (lines.Length == 0)
        {
            MessageBox.Show("The file is empty.", "Import", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // Parse every line into cells
        var rows = lines.Select(ParseCsvLine).Where(r => r.Length > 0).ToList();
        if (rows.Count == 0)
        {
            MessageBox.Show("No data found in the file.", "Import", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        int columnCount = rows.Max(r => r.Length);
        List<string> rawValues;

        if (columnCount == 1)
        {
            // Single-column file — just grab everything (skip header if it doesn't look like a tag)
            rawValues = rows
                .Select(r => r[0].Trim())
                .Where(v => v.Length > 0)
                .ToList();
        }
        else
        {
            // Multi-column: let the user pick which column holds the service tags
            int colIndex = PickCsvColumn(rows, columnCount);
            if (colIndex < 0) return; // user cancelled

            rawValues = rows
                .Skip(1) // skip header row when there are multiple columns
                .Where(r => r.Length > colIndex)
                .Select(r => r[colIndex].Trim())
                .Where(v => v.Length > 0)
                .ToList();
        }

        // Validate and deduplicate using the same rules as manual entry
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
                "No Tags Found", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // Append to any tags already in the box
        string existing = _txtServiceTags.Text.Trim();
        string toAdd    = string.Join(Environment.NewLine, tags);
        _txtServiceTags.Text = string.IsNullOrEmpty(existing)
            ? toAdd
            : existing + Environment.NewLine + toAdd;

        UpdateStatus($"Imported {tags.Count} service tag(s) from {Path.GetFileName(dlg.FileName)}.");
    }

    // Show a column-picker dialog and return the selected column index (0-based), or -1 if cancelled.
    private int PickCsvColumn(List<string[]> rows, int columnCount)
    {
        // Build column display names from first row (treat as headers)
        string[] headers = rows[0];

        // Auto-detect: if a header contains "tag", "serial", or "asset", pre-select it
        int suggested = 0;
        for (int i = 0; i < headers.Length; i++)
        {
            string h = headers[i].ToLowerInvariant();
            if (h.Contains("tag") || h.Contains("serial") || h.Contains("asset"))
            {
                suggested = i;
                break;
            }
        }

        using var dlg = new Form
        {
            Text            = "Select Column",
            Size            = new Size(440, 210),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox     = false,
            MinimizeBox     = false,
            StartPosition   = FormStartPosition.CenterParent
        };

        var lbl = new Label
        {
            Text    = "Which column contains the service tags?",
            Dock    = DockStyle.Top,
            Height  = 32,
            Padding = new Padding(10, 10, 0, 0)
        };

        var combo = new ComboBox
        {
            Dock          = DockStyle.Top,
            DropDownStyle = ComboBoxStyle.DropDownList,
            Margin        = new Padding(10, 0, 10, 0),
            Height        = 28
        };
        for (int i = 0; i < columnCount; i++)
        {
            string header  = i < headers.Length ? headers[i].Trim() : $"Column {i + 1}";
            string preview = rows.Skip(1).Take(3)
                .Where(r => r.Length > i)
                .Select(r => r[i].Trim())
                .Where(v => v.Length > 0)
                .FirstOrDefault() ?? "";
            combo.Items.Add(preview.Length > 0 ? $"{header}  (e.g. {preview})" : header);
        }
        combo.SelectedIndex = Math.Min(suggested, combo.Items.Count - 1);

        var preview2 = new Label
        {
            Dock      = DockStyle.Top,
            Height    = 40,
            Padding   = new Padding(10, 4, 10, 0),
            ForeColor = Color.DimGray,
            Font      = new Font(SystemFonts.DefaultFont!.FontFamily, 8f)
        };

        void UpdatePreview()
        {
            int idx = combo.SelectedIndex;
            if (idx < 0) return;
            var sample = rows.Skip(1).Take(5)
                .Where(r => r.Length > idx)
                .Select(r => r[idx].Trim())
                .Where(v => v.Length > 0)
                .Take(3)
                .ToList();
            preview2.Text = sample.Count > 0
                ? "Preview: " + string.Join(", ", sample)
                : "No data in this column below the header row.";
        }
        combo.SelectedIndexChanged += (_, _) => UpdatePreview();
        UpdatePreview();

        var okBtn = new Button { Text = "Import",  DialogResult = DialogResult.OK,     Width = 90, Height = 30 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90, Height = 30 };
        var btnRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 44,
            Padding = new Padding(6, 6, 6, 0)
        };
        btnRow.Controls.AddRange(new Control[] { cancel, okBtn });

        dlg.AcceptButton = okBtn;
        dlg.CancelButton = cancel;
        dlg.Controls.AddRange(new Control[] { btnRow, preview2, combo, lbl });

        return dlg.ShowDialog(this) == DialogResult.OK ? combo.SelectedIndex : -1;
    }

    // Parses one CSV line into fields, respecting RFC 4180 quoted fields.
    private static string[] ParseCsvLine(string line)
    {
        var fields = new List<string>();
        int i = 0;
        while (i <= line.Length)
        {
            if (i == line.Length) { fields.Add(""); break; }

            if (line[i] == '"')
            {
                // Quoted field
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
                if (i < line.Length) i++; // skip comma
            }
        }
        return fields.ToArray();
    }

    private static List<string> ParseServiceTags(string input)
    {
        var tags = input
            .Split(new[] { '\n', '\r', ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim().ToUpperInvariant())
            .Where(t => t.Length is >= 4 and <= 10 && t.All(char.IsLetterOrDigit))
            .Distinct()
            .ToList();
        return tags;
    }

    // -------------------------------------------------------------------------
    // Grid binding
    // -------------------------------------------------------------------------

    private static void BindGrid(DataGridView grid, List<DeviceInfo> source)
    {
        grid.DataSource = null;
        grid.DataSource = source;
    }

    // -------------------------------------------------------------------------
    // Export
    // -------------------------------------------------------------------------

    private void ExportCsv(object? sender, EventArgs e)
    {
        // Export whichever tab is active
        var source = _tabs.SelectedIndex == 0 ? _scanResults : _tagResults;
        if (source.Count == 0)
        {
            MessageBox.Show("No results to export on the active tab.", "Export", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dlg = new SaveFileDialog
        {
            Filter = "CSV files (*.csv)|*.csv",
            FileName = $"DellWarranty_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
            Title = "Export Results"
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;

        bool hasIpHost = _tabs.SelectedIndex == 0;
        var sb = new StringBuilder();
        sb.AppendLine(hasIpHost
            ? "IP Address,Hostname,Service Tag,Model,Warranty Status,Warranty End,Days Remaining,Notes"
            : "Service Tag,Model,Warranty Status,Warranty End,Days Remaining,Notes");

        foreach (var d in source)
        {
            if (hasIpHost)
                sb.AppendLine($"{Esc(d.IpAddress)},{Esc(d.Hostname)},{Esc(d.ServiceTag)},{Esc(d.Model)}," +
                              $"{Esc(d.WarrantyStatus)},{Esc(d.WarrantyEndDisplay)},{Esc(d.DaysRemainingDisplay)},{Esc(d.Notes)}");
            else
                sb.AppendLine($"{Esc(d.ServiceTag)},{Esc(d.Model)}," +
                              $"{Esc(d.WarrantyStatus)},{Esc(d.WarrantyEndDisplay)},{Esc(d.DaysRemainingDisplay)},{Esc(d.Notes)}");
        }

        File.WriteAllText(dlg.FileName, sb.ToString(), Encoding.UTF8);
        MessageBox.Show($"Exported {source.Count} record(s) to:\n{dlg.FileName}", "Export Complete",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private static string Esc(string s) =>
        s.Contains(',') || s.Contains('"') || s.Contains('\n')
            ? $"\"{s.Replace("\"", "\"\"")}\""
            : s;

    // -------------------------------------------------------------------------
    // State management
    // -------------------------------------------------------------------------

    private void SetScanState(bool scanning)
    {
        _btnScan.Enabled     = !scanning;
        _btnStopScan.Enabled =  scanning;
        _txtIpRange.Enabled  = !scanning;
        _btnLookupTags.Enabled = !scanning;
        _menuSettings.Enabled  = !scanning;
        _menuExport.Enabled    = !scanning && (_scanResults.Count > 0 || _tagResults.Count > 0);
    }

    private void SetTagLookupState(bool running)
    {
        _btnLookupTags.Enabled = !running;
        _btnStopLookup.Enabled =  running;
        _txtServiceTags.Enabled = !running;
        _btnScan.Enabled       = !running;
        _menuSettings.Enabled  = !running;
        _menuExport.Enabled    = !running && (_scanResults.Count > 0 || _tagResults.Count > 0);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private void ShowSubnetPicker()
    {
        var subnets = NetworkScanner.DetectLocalSubnets();
        if (subnets.Count == 0)
        {
            MessageBox.Show("No active network adapters found.", "Auto-detect", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (subnets.Count == 1)
        {
            _txtIpRange.Text = subnets[0].Cidr;
            return;
        }

        using var picker = new Form
        {
            Text = "Select Network Adapter",
            Size = new Size(440, 180),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false, MinimizeBox = false,
            StartPosition = FormStartPosition.CenterParent
        };
        var lbl = new Label { Text = "Multiple adapters found. Choose one:", Dock = DockStyle.Top, Height = 30, Padding = new Padding(8, 8, 0, 0) };
        var combo = new ComboBox { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList };
        foreach (var (cidr, name) in subnets)
            combo.Items.Add($"{cidr}  ({name})");
        combo.SelectedIndex = 0;
        var okBtn = new Button { Text = "Use This", DialogResult = DialogResult.OK, Width = 100, Height = 30 };
        var btnPanel = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 42 };
        btnPanel.Controls.Add(okBtn);
        picker.Controls.AddRange(new Control[] { btnPanel, combo, lbl });
        picker.AcceptButton = okBtn;
        if (picker.ShowDialog(this) == DialogResult.OK && combo.SelectedIndex >= 0)
            _txtIpRange.Text = subnets[combo.SelectedIndex].Cidr;
    }

    private async Task CheckForUpdatesAsync(bool userTriggered)
    {
        const string apiUrl      = "https://api.github.com/repos/kbaker827/DellWarrantyScanner/releases/latest";
        const string releasesUrl = "https://github.com/kbaker827/DellWarrantyScanner/releases";

        var current = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version
                      ?? new Version(1, 0, 0);

        if (userTriggered)
            UpdateStatus("Checking for updates...");

        try
        {
            using var http = new System.Net.Http.HttpClient();
            http.Timeout = TimeSpan.FromSeconds(10);
            http.DefaultRequestHeaders.UserAgent
                .ParseAdd("DellWarrantyScanner/" + current.ToString(3));
            http.DefaultRequestHeaders.Accept
                .ParseAdd("application/vnd.github+json");

            var json = await http.GetStringAsync(apiUrl);
            var obj  = Newtonsoft.Json.Linq.JObject.Parse(json);

            string tagName = obj["tag_name"]?.ToString() ?? "";
            string cleanTag = tagName.TrimStart('v', 'V');

            if (!Version.TryParse(cleanTag, out var latest))
            {
                if (userTriggered)
                    MessageBox.Show("Could not read the version from GitHub.\n\nCheck manually at:\n" + releasesUrl,
                        "Update Check", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (latest > current)
            {
                var result = MessageBox.Show(
                    $"A new version is available!\n\n" +
                    $"  Current:   v{current.ToString(3)}\n" +
                    $"  Available: {tagName}\n\n" +
                    "Open the releases page to download?",
                    "Update Available",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Information);

                if (result == DialogResult.Yes)
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        { FileName = releasesUrl, UseShellExecute = true });
            }
            else if (userTriggered)
            {
                MessageBox.Show($"You're up to date!  (v{current.ToString(3)})",
                    "Check for Updates", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }

            UpdateStatus(latest > current
                ? $"Update available: {tagName}"
                : "Ready.");
        }
        catch (Exception ex) when (!userTriggered)
        {
            // Silent background check — swallow network errors
            _ = ex;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not check for updates:\n\n{ex.Message}",
                "Update Check Failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            UpdateStatus("Ready.");
        }
    }

    private void OpenSettings(object? sender, EventArgs e)
    {
        using var f = new SettingsForm(_settings);
        f.ShowDialog(this);
        _settings = AppSettings.Load();
    }

    private void ShowHelp(object? sender, EventArgs e)
    {
        MessageBox.Show(
            "To use Dell warranty lookup, you need free API credentials from Dell TechDirect:\n\n" +
            "1. Go to https://tdm.dell.com and sign in with your Dell account\n" +
            "2. Navigate to API Management → My Applications\n" +
            "3. Create a new application — select the 'Warranty' API\n" +
            "4. Copy your Client ID and Client Secret\n" +
            "5. Open Tools → Settings in this app and paste them in\n\n" +
            "The API is free. Registration takes a few minutes.",
            "Dell API Setup Instructions", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void UpdateStatus(string message)
    {
        if (InvokeRequired) { Invoke(() => UpdateStatus(message)); return; }
        _lblStatus.Text = message;
        _statusLabel.Text = message;
    }
}
