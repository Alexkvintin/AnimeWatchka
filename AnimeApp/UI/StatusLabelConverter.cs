using System.Globalization;
using Avalonia.Data.Converters;
using AnimeApp.Models;

namespace AnimeApp.UI;
public sealed class StatusLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => StatusLabels.Get(value as string);
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
