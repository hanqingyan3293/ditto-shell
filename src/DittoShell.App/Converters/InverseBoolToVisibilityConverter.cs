using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DittoShell.App.Converters;

/// <summary>true -> Collapsed，false -> Visible。</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var b = value is bool v && v;
        return b ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Visibility vis && vis != Visibility.Visible;
}
