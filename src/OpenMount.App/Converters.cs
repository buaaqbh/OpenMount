using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace OpenMount.App;

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is bool b ? !b : value;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is bool b ? !b : value;
}

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>true (https) → index 0, false (http) → index 1.</summary>
public sealed class HttpsToIndexConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is bool b && b ? 0 : 1;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is int i && i == 0;
}

/// <summary>Visible when the value's string form equals the parameter (enum members, page indices, …).</summary>
public sealed class EqualsToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal) ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class NotNullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is null ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Storage preset id → Fluent symbol for the tiles in the new-drive dialog (same mapping as the Explorer drive icons).</summary>
public sealed class PresetSymbolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var preset = OpenMount.Core.Models.Presets.Find(value as string);
        var kind = preset is null ? "webdav" : OpenMount.Windows.ShellIntegration.DriveIconKind(preset.Base, preset.Id);
        return kind switch
        {
            "sftp" => Wpf.Ui.Controls.SymbolRegular.Key24,
            "cloud" => Wpf.Ui.Controls.SymbolRegular.Cloud24,
            "nas" => Wpf.Ui.Controls.SymbolRegular.Server24,
            _ => Wpf.Ui.Controls.SymbolRegular.Globe24,
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class StringNotEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
