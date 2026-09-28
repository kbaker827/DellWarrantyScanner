using DellWarrantyScanner.Models;
using System.Windows;
using System.Windows.Controls;

namespace DellWarrantyScanner.Converters;

public class WarrantyRowStyleSelector : StyleSelector
{
    public override Style? SelectStyle(object item, DependencyObject container)
    {
        if (item is not DeviceInfo device)
            return null;

        string key = device.WarrantyStatus switch
        {
            WarrantyStatus.Expired => "RowStyleExpired",
            WarrantyStatus.Active when device.IsExpiringSoon => "RowStyleExpiringSoon",
            WarrantyStatus.Active => "RowStyleActive",
            _ => "RowStyleDefault"
        };

        return Application.Current.TryFindResource(key) as Style;
    }
}
