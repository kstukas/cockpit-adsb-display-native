using Avalonia.Data.Converters;
using Avalonia.Media;
using System.Globalization;

namespace CockpitDisplay.Views;

public class BoolToOkConverter : IValueConverter
{
    public static readonly BoolToOkConverter Instance = new();

    public object Convert(object? value, System.Type targetType, object? parameter, CultureInfo culture)
        => value is true ? "OK" : "--";

    public object ConvertBack(object? value, System.Type targetType, object? parameter, CultureInfo culture)
        => throw new System.NotSupportedException();
}

public class BoolToStatusColorConverter : IValueConverter
{
    public static readonly BoolToStatusColorConverter Instance = new();

    private static readonly IBrush Green = new SolidColorBrush(Color.Parse("#00ff88"));
    private static readonly IBrush Red   = new SolidColorBrush(Color.Parse("#ff3333"));

    public object Convert(object? value, System.Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Green : Red;

    public object ConvertBack(object? value, System.Type targetType, object? parameter, CultureInfo culture)
        => throw new System.NotSupportedException();
}
