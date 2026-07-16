using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace v2rayN.Desktop.Converters;

public class SubLevelToBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var level = value is int i ? i : 0;
        return level switch
        {
            2 => new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35)),
            1 => new SolidColorBrush(Color.FromRgb(0xF5, 0x7C, 0x00)),
            _ => new SolidColorBrush(Color.FromRgb(0x21, 0x96, 0xF3)),
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
