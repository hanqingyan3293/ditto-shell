using System.Globalization;
using System.Windows.Data;

namespace DittoShell.App.Converters;

/// <summary>true -> false，false -> true（用于"锁定时禁用勾选框"）。</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? !b : true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? !b : false;
}
