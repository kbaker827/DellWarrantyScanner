using DellWarrantyScanner.Services;
using System.Windows;
using System.Windows.Controls;

namespace DellWarrantyScanner;

public partial class CsvColumnPickerWindow : Window
{
    public int SelectedColumnIndex { get; private set; } = -1;

    private readonly List<string[]> _rows;

    public CsvColumnPickerWindow(List<string[]> rows, int columnCount)
    {
        InitializeComponent();
        _rows = rows;

        string[] headers = rows[0];

        int suggested = Math.Max(0, Array.FindIndex(headers, CsvHelper.LooksLikeTagHeader));

        for (int i = 0; i < columnCount; i++)
        {
            string header = i < headers.Length && headers[i].Trim().Length > 0
                ? headers[i].Trim()
                : $"Column {i + 1}";
            string preview = rows.Skip(1).Take(3)
                .Where(r => r.Length > i)
                .Select(r => r[i].Trim())
                .Where(v => v.Length > 0)
                .FirstOrDefault() ?? "";
            _combo.Items.Add(preview.Length > 0 ? $"{header}  (e.g. {preview})" : header);
        }
        _combo.SelectedIndex = Math.Min(suggested, _combo.Items.Count - 1);
    }

    private void Combo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        int idx = _combo.SelectedIndex;
        if (idx < 0 || _lblPreview == null) return;
        var sample = _rows.Skip(1).Take(5)
            .Where(r => r.Length > idx)
            .Select(r => r[idx].Trim())
            .Where(v => v.Length > 0)
            .Take(3)
            .ToList();
        _lblPreview.Text = sample.Count > 0
            ? "Preview: " + string.Join(", ", sample)
            : "No data in this column below the header row.";
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        SelectedColumnIndex = _combo.SelectedIndex;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
