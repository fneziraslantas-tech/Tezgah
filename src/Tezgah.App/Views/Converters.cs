using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Tezgah.Core.Model;

namespace Tezgah.App.Views;

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}

/// <summary>Null, empty string or empty collection → Collapsed. ConverterParameter "invert" flips it.</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var empty = value is null || (value is string s && string.IsNullOrWhiteSpace(s)) || (value is ICollection c && c.Count == 0);
        if (parameter as string == "invert")
        {
            empty = !empty;
        }

        return empty ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var any = value is int i && i > 0;
        if (parameter as string == "invert")
        {
            any = !any;
        }

        return any ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Two-way enum ↔ RadioButton.IsChecked; ConverterParameter is the enum member name.</summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is not null && parameter is string p && string.Equals(value.ToString(), p, StringComparison.Ordinal);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true && parameter is string p ? Enum.Parse(targetType, p) : Binding.DoNothing;
}

/// <summary>Maps an icon key (ecosystem, item kind, step kind, warning level...) to a Segoe Fluent Icons glyph.</summary>
public sealed class IconGlyphConverter : IValueConverter
{
    public static string Glyph(object? key) => key switch
    {
        WarningLevel.Info => "",
        WarningLevel.Warning => "",
        WarningLevel.Danger => "",
        WarningLevel.Blocker => "",
        UninstallStepKind.StopProcesses => "",
        UninstallStepKind.RunCommand => "",
        UninstallStepKind.RemoveDirectory => "",
        UninstallStepKind.RemoveShortcut => "",
        UninstallStepKind.RemoveCatalogEntry => "",
        RunProfileKind.Run => "",
        RunProfileKind.Setup => "",
        RunProfileKind.Build => "",
        RunProfileKind.Test => "",
        string s => s switch
        {
            "node" => "",
            "python" => "",
            "dotnet" => "",
            "rust" or "go" or "java" => "",
            "docker" => "",
            "static" => "",
            "git" => "",
            "folder" => "",
            "app" => "",
            "tool" => "",
            _ => "",
        },
        _ => "",
    };

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => Glyph(value);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>
/// Item tile: monogram for ecosystems (JS, Py, .N...), Fluent glyph for apps/tools/folders.
/// ConverterParameter: "text" (default), "font" (FontFamily) or "brush" (tile accent color).
/// </summary>
public sealed class TileConverter : IValueConverter
{
    private static readonly System.Windows.Media.FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    private static readonly System.Windows.Media.FontFamily TextFont = new("Segoe UI Variable Display, Segoe UI");
    private static readonly Dictionary<string, (string Text, string Color)> Monograms = new(StringComparer.OrdinalIgnoreCase)
    {
        ["node"] = ("JS", "#3C873A"),
        ["python"] = ("Py", "#3776AB"),
        ["dotnet"] = (".N", "#512BD4"),
        ["rust"] = ("Rs", "#B7410E"),
        ["go"] = ("Go", "#00ADD8"),
        ["java"] = ("Jv", "#E76F00"),
        ["docker"] = ("Dk", "#1D63ED"),
        ["static"] = ("</>", "#E34C26"),
        ["pipx"] = ("Py", "#3776AB"),
        ["npm"] = ("JS", "#CB3837"),
        ["dotnet-tool"] = (".N", "#512BD4"),
        ["cargo"] = ("Rs", "#B7410E"),
    };

    private static readonly Dictionary<string, System.Windows.Media.SolidColorBrush> Brushes = new(StringComparer.OrdinalIgnoreCase);

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value as string ?? "folder";
        var mode = parameter as string ?? "text";
        var mono = Monograms.TryGetValue(key, out var m);
        return mode switch
        {
            "font" => mono ? TextFont : IconFont,
            "brush" => BrushFor(mono ? m.Color : key switch { "app" => "#0F6CBD", "tool" => "#5C5C5C", "git" => "#F05133", _ => "#8A8A8A" }),
            _ => mono ? m.Text : IconGlyphConverter.Glyph(key),
        };
    }

    private static System.Windows.Media.SolidColorBrush BrushFor(string hex)
    {
        lock (Brushes)
        {
            if (!Brushes.TryGetValue(hex, out var b))
            {
                b = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
                b.Freeze();
                Brushes[hex] = b;
            }

            return b;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
