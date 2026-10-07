using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Tunor.Services;

namespace Tunor.Desktop;

/// <summary>
/// Theme colours for code-behind. Named Palette, not Theme: inside a control, "Theme"
/// resolves to Avalonia's own ControlTheme property and the call does not compile.
///
/// FindResource walks up the visual tree, and a control that has not been attached yet
/// has no tree to walk: it answers UnsetValue, and casting that to IBrush throws. A page
/// that filled its rows in its constructor therefore died before it could be shown. Every
/// lookup goes through here instead, where a miss is a colour rather than an exception.
/// </summary>
public static class Palette
{
    public static IBrush Brush(string key)
    {
        if (Application.Current?.Resources.TryGetResource(key, null, out var found) == true
            && found is IBrush brush)
            return brush;
        return new SolidColorBrush(Fallback(key));
    }

    /// <summary>The colour of a tunnel, from the list shared with the Windows app.</summary>
    public static IBrush TunnelBrush(string id, int index)
    {
        if (id == TunnelService.Warp) return Brush("AccentBrush");
        var c = TunnelColors.For(id, index);
        return c == null ? Brush("TextDimBrush")
                         : new SolidColorBrush(Color.FromRgb(c.Value.R, c.Value.G, c.Value.B));
    }

    /// <summary>The Storm palette, used only when the dictionary cannot be reached.</summary>
    private static Color Fallback(string key) => key switch
    {
        "AccentBrush" or "SuccessBrush" => Color.FromRgb(0x3D, 0xDC, 0x5C),
        "DangerBrush" => Color.FromRgb(0xFF, 0x6B, 0x81),
        "TextBrush" => Color.FromRgb(0xF4, 0xF6, 0xF7),
        _ => Color.FromRgb(0x95, 0x9D, 0xA9),          // TextDimBrush
    };
}
