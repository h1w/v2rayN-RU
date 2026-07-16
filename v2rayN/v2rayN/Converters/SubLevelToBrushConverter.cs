using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace v2rayN.Converters;

public class SubLevelToBrushConverter : IValueConverter
{
    // 0 = Normal (primary), 1 = Warn (amber), 2 = Crit (red)
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var level = value is int i ? i : 0;
        return level switch
        {
            2 => new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35)),
            1 => new SolidColorBrush(Color.FromRgb(0xF5, 0x7C, 0x00)),
            _ => System.Windows.Application.Current.TryFindResource("MaterialDesign.Brush.Primary") as Brush
                 ?? new SolidColorBrush(Color.FromRgb(0x21, 0x96, 0xF3)),
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
