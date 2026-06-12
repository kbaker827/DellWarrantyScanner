using System.Windows;

namespace DellWarrantyScanner;

public partial class SubnetPickerWindow : Window
{
    public string? SelectedCidr { get; private set; }

    private readonly List<(string Cidr, string AdapterName)> _subnets;

    public SubnetPickerWindow(List<(string Cidr, string AdapterName)> subnets)
    {
        InitializeComponent();
        _subnets = subnets;
        foreach (var (cidr, name) in subnets)
            _combo.Items.Add($"{cidr}  ({name})");
        _combo.SelectedIndex = 0;
    }

    private void UseThis_Click(object sender, RoutedEventArgs e)
    {
        if (_combo.SelectedIndex >= 0)
            SelectedCidr = _subnets[_combo.SelectedIndex].Cidr;
        DialogResult = true;
    }
}
