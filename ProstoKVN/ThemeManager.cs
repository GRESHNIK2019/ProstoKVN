using System;
using System.Linq;
using System.Windows;

namespace ProstoKVN;

/// <summary>
/// Theme changes only WPF brushes. Network processes, TUN, subscriptions and
/// route configurations are deliberately untouched.
/// </summary>
public static class ThemeManager
{
    public const string Light = "light";
    public const string Dark = "dark";
    private const string PaletteName = "Palette";

    public static string Normalize(string? value) =>
        string.Equals(value, Dark, StringComparison.OrdinalIgnoreCase) ? Dark : Light;

    public static void Apply(ResourceDictionary resources, string? mode)
    {
        bool dark = Normalize(mode) == Dark;
        var merged = resources.MergedDictionaries;
        var old = merged.FirstOrDefault(x =>
            x.Source?.OriginalString.Contains(PaletteName, StringComparison.OrdinalIgnoreCase) == true);
        var uri = new Uri(
            "pack://application:,,,/ProstoKVN;component/Themes/" +
            (dark ? "PaletteDark.xaml" : "PaletteLight.xaml"), UriKind.Absolute);
        if (old?.Source == uri) return;
        var updated = new ResourceDictionary { Source = uri };
        if (old != null)
        {
            int index = merged.IndexOf(old);
            merged[index] = updated;
        }
        else
            merged.Insert(0, updated);
    }

    public static bool IsDark(ResourceDictionary resources) =>
        resources.MergedDictionaries.Any(x =>
            x.Source?.OriginalString.Contains("PaletteDark.xaml", StringComparison.OrdinalIgnoreCase) == true);
}
