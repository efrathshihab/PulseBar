using System.Globalization;
using System.Windows.Data;

namespace PulseBar.Utilities;

/// <summary>
/// True when the enum value matches the ConverterParameter. For filter chips' (RadioButton) IsChecked;
/// when checked, sets that value back the other way.
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
