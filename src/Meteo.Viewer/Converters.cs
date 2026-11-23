using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Meteo.Viewer;

/// <summary>"#RRGGBB" → SolidColorBrush.</summary>
public sealed class HexToBrushConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string s ? Map.NtsToWpf.Brush(s) : Brushes.Transparent;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
