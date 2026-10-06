namespace Tunor.Services;

/// <summary>
/// The colour that stands for a tunnel, as plain numbers.
///
/// Both front ends show the same tunnels and must agree on which one is which colour —
/// a badge on one page and a route column on another, in two different apps, should not
/// disagree. The brushes themselves cannot be shared, because WPF and Avalonia have
/// their own and neither exists on the other side, so what is shared is the list.
/// </summary>
public static class TunnelColors
{
    /// <summary>WARP keeps the accent green it has always had.</summary>
    public static readonly (byte R, byte G, byte B) Accent = (0x3D, 0xDC, 0x5C);

    /// <summary>In turn, for every tunnel that is not WARP. geo takes the first, the
    /// blue it started with; anything added later carries on down the list.</summary>
    public static readonly (byte R, byte G, byte B)[] Palette =
    {
        (0x6E, 0xA8, 0xFE),   // blue — geo's colour since the start
        (0xC9, 0x8B, 0xFF),   // violet
        (0xFF, 0xB4, 0x5C),   // amber
        (0x5C, 0xD6, 0xD6),   // teal
        (0xFF, 0x8F, 0xA3),   // rose
    };

    /// <summary>
    /// The colour for a tunnel, by its id and its place in the list. Null means "use the
    /// dim text colour" — the caller's theme owns that one.
    /// </summary>
    public static (byte R, byte G, byte B)? For(string id, int index) => id switch
    {
        TunnelService.Warp => Accent,
        TunnelService.Geo => Palette[0],
        _ => index < 0 ? null : Palette[(index + 1) % Palette.Length],
    };
}
