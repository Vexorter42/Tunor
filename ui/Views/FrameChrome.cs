using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shell;

namespace Tunor.Views;

/// <summary>
/// Draws a window without the system's title bar, so that the app can have its own
/// (<see cref="TitleBar"/>) and the window looks like one thing rather than a system frame
/// around an app.
///
/// Only the look is replaced, not the behaviour: the bar is still the window's caption, so it
/// drags, double-clicks to maximise, opens the system menu on a right click and snaps to the
/// edges of the screen; the window still resizes from its edges and keeps the system's shadow.
/// </summary>
public static class FrameChrome
{
    /// <summary>The height of <see cref="TitleBar"/>: the part of the window that is its caption.</summary>
    public const double BarHeight = 36;

    public static WindowChrome Create() => new()
    {
        CaptionHeight = BarHeight,
        ResizeBorderThickness = new Thickness(6),
        GlassFrameThickness = new Thickness(0),
        CornerRadius = new CornerRadius(0),
        UseAeroCaptionButtons = false,           // the bar draws its own
    };

    public static void Apply(Window window)
    {
        WindowChrome.SetWindowChrome(window, Create());
        window.StateChanged += (_, _) => Fit(window);
    }

    /// <summary>
    /// A window with no system frame, when maximised, is larger than the screen by the width of
    /// its resize border on every side — the part that is normally the invisible frame. Without
    /// this the title bar's top and the close button's right edge are off the screen. The content
    /// is pulled in by that width while the window is maximised, and let out again after.
    /// </summary>
    private static void Fit(Window window)
    {
        if (window.Content is not FrameworkElement content) return;
        if (window.WindowState != WindowState.Maximized) { content.Margin = new Thickness(0); return; }

        // The frame and the padded border Windows adds to it, in this window's pixels. The
        // SystemParameters value leaves the padded border out, and a quarter of the overhang stays.
        var dpi = VisualTreeHelper.GetDpi(window);
        var px = GetSystemMetricsForDpi(SM_CXSIZEFRAME, (uint)dpi.PixelsPerInchX)
               + GetSystemMetricsForDpi(SM_CXPADDEDBORDER, (uint)dpi.PixelsPerInchX);
        content.Margin = new Thickness(px / dpi.DpiScaleX);
    }

    private const int SM_CXSIZEFRAME = 32;
    private const int SM_CXPADDEDBORDER = 92;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);
}
