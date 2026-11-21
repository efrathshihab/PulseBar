using System.Globalization;
using System.Windows.Data;
using PulseBar.Models;
using Wpf.Ui.Controls;

namespace PulseBar.Utilities;

/// <summary>Fluent icon for the output device kind.</summary>
public sealed class AudioDeviceKindToSymbolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            AudioDeviceKind.Speakers => SymbolRegular.Speaker224,
            AudioDeviceKind.Headphones => SymbolRegular.Headphones24,
            AudioDeviceKind.Headset => SymbolRegular.Headset24,
            AudioDeviceKind.Display => SymbolRegular.Tv24,
            _ => SymbolRegular.DeviceEq24,
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
