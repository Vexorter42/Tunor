using System.Windows;
using System.Windows.Media;

namespace Tunor;

/// <summary>
/// Theme resources for code-behind. Every lookup goes straight to the application's
/// dictionary and can never throw.
///
/// FrameworkElement.FindResource walks up the element tree before reaching the
/// application, and from a page in some states it does not get there: users saw
/// "Resource 'TextDimBrush' not found" dialogs from pages that were building rows,
/// although the brush is defined. A cosmetic colour must not be able to raise an error
/// dialog, so each key also carries its palette value as a fallback.
/// </summary>
public static class Ui
{
    private static ResourceDictionary? _theme;

    private static object? Resource(string key)
    {
        try
        {
            _theme ??= Application.Current?.Resources;
            return _theme?[key];   // the indexer searches merged dictionaries too
        }
        catch { return null; }
    }

    public static Brush Brush(string key) => Resource(key) as Brush ?? new SolidColorBrush(Fallback(key));

    public static SolidColorBrush Solid(string key)
        => Resource(key) as SolidColorBrush ?? new SolidColorBrush(Fallback(key));

    public static Style? Style(string key) => Resource(key) as Style;

    /// <summary>
    /// The colour that stands for a tunnel everywhere it is named: the badge in the app
    /// list, the route column on the Connections page. WARP keeps the accent green and
    /// geo the blue it has always had; tunnels the user adds take the rest in turn, so
    /// two of them never look alike until there are more than this list is long.
    /// </summary>
    public static Brush TunnelBrush(string id, int index) => id switch
    {
        "warp" => Brush("AccentBrush"),
        "geo" => Palette[0],
        _ => Palette[(index + 1) % Palette.Length],
    };

    private static readonly Brush[] Palette =
    {
        new SolidColorBrush(Rgb(0x6E, 0xA8, 0xFE)),   // blue — geo's colour since the start
        new SolidColorBrush(Rgb(0xC9, 0x8B, 0xFF)),   // violet
        new SolidColorBrush(Rgb(0xFF, 0xB4, 0x5C)),   // amber
        new SolidColorBrush(Rgb(0x5C, 0xD6, 0xD6)),   // teal
        new SolidColorBrush(Rgb(0xFF, 0x8F, 0xA3)),   // rose
    };

    private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);

    /// <summary>The Storm palette from Styles/Theme.xaml, used only if a lookup fails.</summary>
    private static Color Fallback(string key) => key switch
    {
        "BgBrush" => Rgb(0x1A1E25),
        "BgPanelBrush" => Rgb(0x242932),
        "BgInputBrush" => Rgb(0x1F242B),
        "BgHoverBrush" => Rgb(0x2E343E),
        "AccentBrush" or "SuccessBrush" => Rgb(0x3DDC5C),
        "AccentHoverBrush" => Rgb(0x5BE877),
        "AccentSoftBrush" => Color.FromArgb(0x38, 0x3D, 0xDC, 0x5C),
        "DangerBrush" => Rgb(0xFF6B6B),
        "TextDimBrush" => Rgb(0x959DA9),
        "BorderBrush" => Rgb(0x3A404A),
        _ => Rgb(0xF4F6F7),   // TextBrush, and anything unknown: readable on the dark theme
    };

    private static Color Rgb(int rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
}
