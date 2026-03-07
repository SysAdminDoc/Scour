using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace Scour.App.Converters;

public class GroupColorConverter : IValueConverter
{
    private static readonly SolidColorBrush[] GroupBrushes =
    [
        new(Color.FromArgb(20, 203, 166, 247)), // Mauve
        new(Color.FromArgb(20, 137, 180, 250)), // Blue
        new(Color.FromArgb(20, 166, 227, 161)), // Green
        new(Color.FromArgb(20, 250, 179, 135)), // Peach
        new(Color.FromArgb(20, 245, 194, 231)), // Pink
        new(Color.FromArgb(20, 148, 226, 213)), // Teal
        new(Color.FromArgb(20, 249, 226, 175)), // Yellow
        new(Color.FromArgb(20, 180, 190, 254)), // Lavender
    ];

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string group && group.StartsWith("Group "))
        {
            if (int.TryParse(group.AsSpan(6), out var num))
                return GroupBrushes[(num - 1) % GroupBrushes.Length];
        }
        return Brushes.Transparent;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
