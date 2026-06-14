using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Monocle.App.Converters;

/// <summary>
/// True when the bound value's string form equals the converter parameter. Used to light up the
/// active pill chip in the toolbar (e.g. <c>Classes.on="{Binding Rating, Converter=… Parameter=All}"</c>)
/// without a bespoke bool property per filter value.
/// </summary>
public sealed class EqualityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var a = value?.ToString();
        var b = parameter?.ToString();
        // A null/empty bound value matches a "None"/"Any"/empty parameter (the cleared facet state).
        if (string.IsNullOrEmpty(a))
            return string.IsNullOrEmpty(b) || b is "None" or "Any";
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
