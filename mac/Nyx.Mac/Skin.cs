using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Nyx.Mac;

/// <summary>
/// The "Storm" palette, carried over from the Windows build's Styles/Skin.xaml so both
/// apps look like the same product. Colours are defined once here and reused.
/// </summary>
public static class Skin
{
    public static readonly IBrush Bg = B("#FF1A1E25");
    public static readonly IBrush Panel = B("#FF242932");
    public static readonly IBrush Input = B("#FF1F242B");
    public static readonly IBrush Accent = B("#FF3DDC5C");
    public static readonly IBrush AccentSoft = B("#383DDC5C");
    public static readonly IBrush Danger = B("#FFFF6B6B");
    public static readonly IBrush Text = B("#FFF4F6F7");
    public static readonly IBrush Dim = B("#FF959DA9");
    public static readonly IBrush Border = B("#FF3A404A");
    public static readonly IBrush Ink = B("#FF06120A");

    private static IBrush B(string hex) => new SolidColorBrush(Color.Parse(hex));

    public static TextBlock H1(string text) => new()
    {
        Text = text, FontSize = 22, FontWeight = FontWeight.SemiBold, Foreground = Text,
    };

    public static TextBlock H2(string text) => new()
    {
        Text = text, FontSize = 15, FontWeight = FontWeight.SemiBold, Foreground = Text,
    };

    public static TextBlock Muted(string text) => new()
    {
        Text = text, FontSize = 12.5, Foreground = Dim, TextWrapping = TextWrapping.Wrap,
    };

    public static Border Card(Control child) => new()
    {
        Background = Panel,
        CornerRadius = new CornerRadius(16),
        Padding = new Thickness(22),
        BorderBrush = Border,
        BorderThickness = new Thickness(1),
        Child = child,
    };

    public static Button Primary(string text) => new()
    {
        Content = text,
        Background = Accent,
        Foreground = Ink,
        FontWeight = FontWeight.SemiBold,
        Padding = new Thickness(22, 11),
        CornerRadius = new CornerRadius(10),
        BorderThickness = new Thickness(0),
        HorizontalAlignment = HorizontalAlignment.Left,
        MinWidth = 170,
        HorizontalContentAlignment = HorizontalAlignment.Center,
    };

    public static Button Secondary(string text)
    {
        var b = Primary(text);
        b.Background = Input;
        b.Foreground = Text;
        b.BorderBrush = Border;
        b.BorderThickness = new Thickness(1);
        b.MinWidth = 140;
        return b;
    }

    public static Border Pill(string text, IBrush colour) => new()
    {
        Background = AccentSoft,
        CornerRadius = new CornerRadius(10),
        Padding = new Thickness(11, 4),
        HorizontalAlignment = HorizontalAlignment.Left,
        Child = new TextBlock { Text = text, Foreground = colour, FontSize = 12 },
    };
}
