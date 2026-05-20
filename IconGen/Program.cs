using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

// Generates a multi-size ICO file: monitor with a green checkmark on a blue background.
var outPath = args.Length > 0 ? args[0] : "app.ico";
var sizes = new[] { 256, 64, 48, 32, 16 };
var pngChunks = new List<byte[]>();

foreach (var sz in sizes)
{
    using var bmp = new Bitmap(sz, sz, PixelFormat.Format32bppArgb);
    using var g = Graphics.FromImage(bmp);
    g.SmoothingMode = SmoothingMode.AntiAlias;
    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
    g.Clear(Color.Transparent);

    // --- Rounded blue background ---
    int r = Math.Max(2, sz / 6);
    using (var path = RoundedRect(0, 0, sz, sz, r))
    using (var bg = new SolidBrush(Color.FromArgb(0, 120, 215)))
        g.FillPath(bg, path);

    // --- Monitor outline ---
    int px = Math.Max(1, (int)(sz * 0.04f));
    int mx = (int)(sz * 0.11f);
    int my = (int)(sz * 0.12f);
    int mw = sz - 2 * mx;
    int mh = (int)(sz * 0.55f);

    using var whitePen = new Pen(Color.White, Math.Max(1f, sz * 0.045f));
    using var whiteBrush = new SolidBrush(Color.White);
    g.DrawRectangle(whitePen, mx, my, mw, mh);

    // Screen (light blue fill)
    int si = (int)(sz * 0.05f);
    using (var screenFill = new SolidBrush(Color.FromArgb(160, 210, 255)))
        g.FillRectangle(screenFill, mx + si, my + si, mw - si * 2, mh - si * 2);

    // Stand + base
    int stx = mx + mw / 2 - Math.Max(1, mw / 8);
    int sty = my + mh;
    int stw = Math.Max(2, mw / 4);
    int sth = Math.Max(2, (int)(sz * 0.1f));
    int bsx = mx + mw / 5;
    int bsw = mw - mw * 2 / 5;
    int bsh = Math.Max(1, (int)(sz * 0.05f));
    g.FillRectangle(whiteBrush, stx, sty, stw, sth);
    g.FillRectangle(whiteBrush, bsx, sty + sth, bsw, bsh);

    // --- Green checkmark inside screen (only when big enough) ---
    if (sz >= 24)
    {
        int cx = mx + mw / 2;
        int cy = my + mh / 2;
        int ck = Math.Max(2, (int)(mw * 0.28f));
        float pw = Math.Max(1.5f, sz * 0.07f);
        using var checkPen = new Pen(Color.FromArgb(0, 200, 80), pw)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        g.DrawLines(checkPen, new[]
        {
            new Point(cx - ck,          cy),
            new Point(cx - ck / 4,      cy + (int)(ck * 0.8f)),
            new Point(cx + ck,          cy - (int)(ck * 0.65f))
        });
    }

    using var ms = new MemoryStream();
    bmp.Save(ms, ImageFormat.Png);
    pngChunks.Add(ms.ToArray());
}

// Write ICO (PNG-in-ICO, supported Vista+)
using var ico = new FileStream(outPath, FileMode.Create, FileAccess.Write);
using var w = new BinaryWriter(ico);

int n = pngChunks.Count;
int headerSize = 6 + 16 * n;

w.Write((ushort)0);   // Reserved
w.Write((ushort)1);   // Type = ICO
w.Write((ushort)n);   // Count

int offset = headerSize;
for (int i = 0; i < n; i++)
{
    int s = sizes[i];
    w.Write((byte)(s == 256 ? 0 : s));  // Width  (0 = 256)
    w.Write((byte)(s == 256 ? 0 : s));  // Height
    w.Write((byte)0);                    // Color count
    w.Write((byte)0);                    // Reserved
    w.Write((ushort)0);                  // Planes
    w.Write((ushort)32);                 // Bit depth
    w.Write((uint)pngChunks[i].Length);  // Data size
    w.Write((uint)offset);               // Data offset
    offset += pngChunks[i].Length;
}
foreach (var chunk in pngChunks)
    w.Write(chunk);

Console.WriteLine($"Icon written to {outPath} ({new FileInfo(outPath).Length:N0} bytes, {n} sizes)");

static GraphicsPath RoundedRect(int x, int y, int w, int h, int r)
{
    var path = new GraphicsPath();
    int d = r * 2;
    path.AddArc(x, y, d, d, 180, 90);
    path.AddArc(x + w - d, y, d, d, 270, 90);
    path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
    path.AddArc(x, y + h - d, d, d, 90, 90);
    path.CloseFigure();
    return path;
}
