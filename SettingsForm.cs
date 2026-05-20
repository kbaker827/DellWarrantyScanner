using DellWarrantyScanner.Models;

namespace DellWarrantyScanner;

public class SettingsForm : Form
{
    private readonly AppSettings _settings;

    private TextBox _txtClientId = null!;
    private TextBox _txtClientSecret = null!;
    private RadioButton _rbCurrentUser = null!;
    private RadioButton _rbSpecifiedUser = null!;
    private TextBox _txtWmiUser = null!;
    private TextBox _txtWmiPass = null!;
    private Panel _wmiCredPanel = null!;

    public SettingsForm(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();
        LoadSettings();
    }

    private void InitializeComponent()
    {
        Text = "Settings";
        Size = new Size(520, 420);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;

        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
            RowCount = 2,
            ColumnCount = 1
        };
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));

        var form = new GroupBox { Text = "Configuration", Dock = DockStyle.Fill, Padding = new Padding(8) };
        var inner = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 9,
            ColumnCount = 2,
            AutoSize = true
        };
        inner.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
        inner.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        // Dell API section
        inner.Controls.Add(MakeLabel("— Dell TechDirect API —", bold: true), 0, 0);
        inner.SetColumnSpan(inner.Controls[inner.Controls.Count - 1], 2);

        inner.Controls.Add(MakeLabel("Client ID:"), 0, 1);
        _txtClientId = new TextBox { Dock = DockStyle.Fill };
        inner.Controls.Add(_txtClientId, 1, 1);

        inner.Controls.Add(MakeLabel("Client Secret:"), 0, 2);
        _txtClientSecret = new TextBox { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
        inner.Controls.Add(_txtClientSecret, 1, 2);

        var apiHint = new LinkLabel
        {
            Text = "Get credentials at Dell TechDirect → API Management",
            Dock = DockStyle.Fill,
            Font = new Font(Font.FontFamily, 8f)
        };
        apiHint.LinkClicked += (_, _) =>
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            { FileName = "https://tdm.dell.com", UseShellExecute = true });
        inner.Controls.Add(apiHint, 0, 3);
        inner.SetColumnSpan(apiHint, 2);

        // WMI section
        var sep = MakeLabel("— WMI Credentials —", bold: true);
        inner.Controls.Add(sep, 0, 4);
        inner.SetColumnSpan(sep, 2);

        _rbCurrentUser = new RadioButton { Text = "Use current Windows credentials", Dock = DockStyle.Fill, Checked = true };
        inner.Controls.Add(_rbCurrentUser, 0, 5);
        inner.SetColumnSpan(_rbCurrentUser, 2);

        _rbSpecifiedUser = new RadioButton { Text = "Use specified credentials", Dock = DockStyle.Fill };
        _rbSpecifiedUser.CheckedChanged += (_, _) =>
            _wmiCredPanel.Enabled = _rbSpecifiedUser.Checked;
        inner.Controls.Add(_rbSpecifiedUser, 0, 6);
        inner.SetColumnSpan(_rbSpecifiedUser, 2);

        _wmiCredPanel = new Panel { Dock = DockStyle.Fill, Enabled = false };
        var wmiLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 2,
            ColumnCount = 2
        };
        wmiLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
        wmiLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        wmiLayout.Controls.Add(MakeLabel("Username:"), 0, 0);
        _txtWmiUser = new TextBox { Dock = DockStyle.Fill, PlaceholderText = @"DOMAIN\username" };
        wmiLayout.Controls.Add(_txtWmiUser, 1, 0);
        wmiLayout.Controls.Add(MakeLabel("Password:"), 0, 1);
        _txtWmiPass = new TextBox { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
        wmiLayout.Controls.Add(_txtWmiPass, 1, 1);
        _wmiCredPanel.Controls.Add(wmiLayout);
        inner.Controls.Add(_wmiCredPanel, 0, 7);
        inner.SetColumnSpan(_wmiCredPanel, 2);

        form.Controls.Add(inner);
        panel.Controls.Add(form, 0, 0);

        // Buttons
        var btnPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft
        };
        var btnSave = new Button { Text = "Save", Width = 90, Height = 34 };
        btnSave.Click += (_, _) => SaveAndClose();
        var btnCancel = new Button { Text = "Cancel", Width = 90, Height = 34, DialogResult = DialogResult.Cancel };
        btnPanel.Controls.Add(btnSave);
        btnPanel.Controls.Add(btnCancel);
        panel.Controls.Add(btnPanel, 0, 1);

        Controls.Add(panel);
        AcceptButton = btnSave;
        CancelButton = btnCancel;
    }

    private void LoadSettings()
    {
        _txtClientId.Text = _settings.DellClientId;
        _txtClientSecret.Text = _settings.DellClientSecret;
        _rbCurrentUser.Checked = _settings.UseCurrentCredentials;
        _rbSpecifiedUser.Checked = !_settings.UseCurrentCredentials;
        _txtWmiUser.Text = _settings.WmiUsername;
        _txtWmiPass.Text = _settings.WmiPassword;
        _wmiCredPanel.Enabled = !_settings.UseCurrentCredentials;
    }

    private void SaveAndClose()
    {
        _settings.DellClientId = _txtClientId.Text.Trim();
        _settings.DellClientSecret = _txtClientSecret.Text.Trim();
        _settings.UseCurrentCredentials = _rbCurrentUser.Checked;
        _settings.WmiUsername = _txtWmiUser.Text.Trim();
        _settings.WmiPassword = _txtWmiPass.Text;
        _settings.Save();
        DialogResult = DialogResult.OK;
        Close();
    }

    private static Label MakeLabel(string text, bool bold = false) =>
        new Label
        {
            Text = text,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = bold ? new Font(SystemFonts.DefaultFont!, FontStyle.Bold) : SystemFonts.DefaultFont!
        };
}
