using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ClaudeUsageTracker.App.Views;

public static class Converters
{
    /// <summary>Visible for a non-null, non-empty value; collapsed otherwise.</summary>
    public static readonly IValueConverter NotNullToVisible = new NotNullConverter();

    /// <summary>Visible for false; collapsed for true.</summary>
    public static readonly IValueConverter InverseVisible = new InverseConverter();

    /// <summary>The theme brush whose resource key is the value, for colours a view model names by key.</summary>
    public static readonly IValueConverter ResourceBrush = new ResourceBrushConverter();

    private sealed class ResourceBrushConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is string key ? Application.Current?.TryFindResource(key) : null;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    private sealed class InverseConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is true ? Visibility.Collapsed : Visibility.Visible;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    private sealed class NotNullConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is null || value is string { Length: 0 } ? Visibility.Collapsed : Visibility.Visible;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
