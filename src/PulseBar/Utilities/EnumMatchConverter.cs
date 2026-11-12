using System.Globalization;
using System.Windows.Data;

namespace PulseBar.Utilities;

/// <summary>
/// Enum মান আর ConverterParameter মিললে true। Filter chip (RadioButton) এর IsChecked এর জন্য;
/// check হলে উল্টো দিকে সেই মান বসায়।
/// </summary>
public sealed class EnumMatchConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not null && parameter is not null &&
        string.Equals(value.ToString(), parameter.ToString(), StringComparison.Ordinal);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && parameter is string name && Enum.TryParse(targetType, name, out object? result)
            ? result!
            : Binding.DoNothing;
}
