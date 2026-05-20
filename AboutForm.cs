using System.Diagnostics;
using System.Reflection;

namespace DellWarrantyScanner;

public class AboutForm : Form
{
    private const string RepoUrl     = "https://github.com/kbaker827/DellWarrantyScanner";
    private const string ReleasesUrl = "https://github.com/kbaker827/DellWarrantyScanner/releases";

    public AboutForm(Icon? appIcon)
    {
        InitializeComponent(appIcon);
    }

    private void InitializeComponent(Icon? appIcon)
    {
        Text            = "About Dell Warranty Scanner";
        Size            = new Size(420, 320);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox     = false;
        MinimizeBox     = false;
        StartPosition   = FormStartPosition.CenterParent;
        BackColor       = SystemColors.Window;

        // App icon (48×48)
        if (appIcon != null)
        {
            var iconBox = new PictureBox
            {
                Image    = new Icon(appIcon, 48, 48).ToBitmap(),
                Size     = new Size(48, 48),
                Location = new Point(24, 24),
                SizeMode = PictureBoxSizeMode.Zoom
            };
            Controls.Add(iconBox);
        }

        int textX = appIcon != null ? 84 : 24;

        // App name
        Controls.Add(new Label
        {
            Text     = "Dell Warranty Scanner",
            Location = new Point(textX, 24),
            AutoSize = true,
            Font     = new Font("Segoe UI", 14f, FontStyle.Bold)
        });

        // Version
        var ver = Assembly.GetExecutingAssembly().GetName().Version;
        string versionStr = ver is null ? "v1.0.0" : $"v{ver.Major}.{ver.Minor}.{ver.Build}";
        Controls.Add(new Label
        {
            Text      = versionStr,
            Location  = new Point(textX, 52),
            AutoSize  = true,
            ForeColor = Color.DimGray,
            Font      = new Font("Segoe UI", 9f)
        });

        // Separator
        var sep = new Panel
        {
            Location  = new Point(24, 88),
            Size      = new Size(356, 1),
            BackColor = Color.FromArgb(220, 220, 220)
        };
        Controls.Add(sep);

        // Description
        Controls.Add(new Label
        {
            Text      = "Scans your network for Dell systems and retrieves warranty\n" +
                        "expiration dates via the Dell TechDirect API.",
            Location  = new Point(24, 100),
            Size      = new Size(356, 40),
            ForeColor = Color.FromArgb(60, 60, 60),
            Font      = new Font("Segoe UI", 9f)
        });

        // GitHub repo link
        Controls.Add(new Label
        {
            Text      = "Source & Releases:",
            Location  = new Point(24, 150),
            AutoSize  = true,
            ForeColor = Color.DimGray,
            Font      = new Font("Segoe UI", 9f)
        });

        var repoLink = new LinkLabel
        {
            Text      = RepoUrl,
            Location  = new Point(24, 168),
            AutoSize  = true,
            Font      = new Font("Segoe UI", 9f)
        };
        repoLink.LinkClicked += (_, _) => OpenUrl(RepoUrl);
        Controls.Add(repoLink);

        // Copyright
        var copyright = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright
            ?? "Copyright © 2026 kbaker827";
        Controls.Add(new Label
        {
            Text      = copyright,
            Location  = new Point(24, 196),
            AutoSize  = true,
            ForeColor = Color.DimGray,
            Font      = new Font("Segoe UI", 8.5f)
        });

        // Releases link button
        var btnReleases = new Button
        {
            Text     = "View Releases",
            Location = new Point(24, 228),
            Width    = 110,
            Height   = 30
        };
        btnReleases.Click += (_, _) => OpenUrl(ReleasesUrl);
        Controls.Add(btnReleases);

        // Close button
        var btnClose = new Button
        {
            Text         = "Close",
            Location     = new Point(290, 228),
            Width        = 90,
            Height       = 30,
            DialogResult = DialogResult.OK
        };
        Controls.Add(btnClose);
        AcceptButton = btnClose;
    }

    private static void OpenUrl(string url) =>
        Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
}
