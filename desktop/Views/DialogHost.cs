using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Tunor.Desktop.Views;

/// <summary>
/// Draws <see cref="DialogPanel"/>s over the main window and tells when they are closed.
///
/// The main window gives it one empty layer on top of everything else. A dialog becomes a
/// card on a dimmed backdrop in that layer; everything underneath is switched off, so
/// neither a click nor Tab reaches it. A dialog may open another one (a "delete?" over a
/// form): they stack, the last one is the one that answers.
///
/// When there is no main window to draw on — it is hidden in the tray or the menu bar —
/// the dialog opens in a window of its own, as before; it is better to show it than to
/// wait for a window nobody can see.
/// </summary>
public static class DialogHost
{
    private sealed class Entry
    {
        public Entry(DialogPanel panel) => Panel = panel;
        public DialogPanel Panel { get; }
        public Grid? Layer { get; set; }
        public Border? Card { get; set; }
        public ScrollViewer? Scroll { get; set; }
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private const double Gap = 16;                       // between the card and the edge of the window

    private static Window? _window;
    private static Panel? _root;
    private static Panel? _layers;
    private static readonly List<Entry> Stack = new();
    private static readonly Dictionary<DialogPanel, (Window Window, TaskCompletionSource Done)> Windows = new();
    private static readonly List<(Control Element, bool HitTest, KeyboardNavigationMode Tab)> Switched = new();

    /// <summary>True while a dialog is waiting for an answer.</summary>
    public static bool IsOpen => Stack.Count > 0 || Windows.Count > 0;

    /// <param name="window">The main window.</param>
    /// <param name="root">The panel that holds the whole app; everything in it except <paramref name="layers"/> is switched off while a dialog is open.</param>
    /// <param name="layers">An empty panel over the rest of <paramref name="root"/>, as large as it.</param>
    public static void Attach(Window window, Panel root, Panel layers)
    {
        _window = window;
        _root = root;
        _layers = layers;
        layers.SizeChanged += (_, _) => { foreach (var e in Stack.ToArray()) Fit(e); };
        // Bubbling: a drop-down that has just closed itself on Esc has handled the key.
        layers.KeyDown += OnKey;
        window.Closed += (_, _) => ReleaseAll();
    }

    private static bool Available => _window is { IsVisible: true } && _root != null && _layers != null;

    public static Task ShowAsync(DialogPanel panel, Control? owner = null)
        => Available ? InOverlay(new Entry(panel)) : InWindow(panel, owner);

    public static void Close(DialogPanel panel)
    {
        if (Windows.TryGetValue(panel, out var w)) { w.Window.Close(); return; }

        var e = Stack.Find(x => x.Panel == panel);
        if (e == null) return;                           // not shown, or already closed

        Stack.Remove(e);
        _layers?.Children.Remove(e.Layer!);
        if (e.Scroll != null) e.Scroll.Content = null;   // the panel is still readable, it is just not on screen
        if (Stack.Count == 0) SwitchBackOn();
        e.Done.TrySetResult();
    }

    // ------------------------------------------------------------------ in the main window

    private static Task InOverlay(Entry e)
    {
        if (Stack.Count == 0) SwitchOff();
        Build(e);
        Stack.Add(e);
        _layers!.Children.Add(e.Layer!);
        Fit(e);
        Dispatcher.UIThread.Post(() => FocusInto(e), DispatcherPriority.Input);
        return e.Done.Task;
    }

    private static IBrush Brush(string key, IBrush fallback)
        => Application.Current is { } app
           && app.TryGetResource(key, app.ActualThemeVariant, out var v) && v is IBrush b ? b : fallback;

    private static void Build(Entry e)
    {
        var panel = e.Panel;

        // The panel carries no background of its own, but the one the window had is the
        // card's now, with round corners.
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = panel,
        };

        var cross = new Button
        {
            Content = "✕",
            Width = 32, Height = 32, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0),
            FontSize = 14,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 8, 8, 0),
            Focusable = false,                           // Tab goes through the dialog, not to its corner
            ZIndex = 1,
        };
        cross.Classes.Add("ghost");
        ToolTip.SetTip(cross, "Закрыть");
        cross.Click += (_, _) => panel.Dismiss();

        var inside = new Grid();
        inside.Children.Add(scroll);
        inside.Children.Add(cross);

        var card = new Border
        {
            Background = Brush("BgBrush", Brushes.Black),
            BorderBrush = Brush("BorderBrush", Brushes.Gray),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(Gap),
            ClipToBounds = true,
            Child = inside,
            BoxShadow = new BoxShadows(new BoxShadow
            {
                OffsetX = 0, OffsetY = 8, Blur = 40, Color = Color.FromArgb(0x8C, 0, 0, 0),
            }),
        };

        var layer = new Grid { Background = new SolidColorBrush(Color.FromArgb(0xB8, 0x0B, 0x0D, 0x11)) };
        KeyboardNavigation.SetTabNavigation(layer, KeyboardNavigationMode.Cycle);
        layer.Children.Add(card);

        e.Layer = layer;
        e.Card = card;
        e.Scroll = scroll;
    }

    /// <summary>A card never grows past the window; a tall dialog in a small window scrolls instead.</summary>
    private static void Fit(Entry e)
    {
        if (e.Card == null || _layers == null) return;
        var b = _layers.Bounds;
        if (b.Height > 0) e.Card.MaxHeight = Math.Max(120, b.Height - 2 * Gap);
        if (b.Width > 0) e.Card.MaxWidth = Math.Max(240, b.Width - 2 * Gap);
    }

    private static void FocusInto(Entry e)
    {
        if (e.Layer == null || !Stack.Contains(e)) return;
        // The first control of the dialog that can take the keyboard; the cross is not one.
        var first = e.Panel.GetVisualDescendants().OfType<InputElement>()
            .FirstOrDefault(c => c.Focusable && c.IsEffectivelyEnabled && c.IsEffectivelyVisible);
        if (first != null && first.Focus()) return;

        // Nothing in it takes focus: let the layer, so that Esc still reaches us.
        e.Layer.Focusable = true;
        e.Layer.Focus();
    }

    private static void OnKey(object? sender, KeyEventArgs k)
    {
        if (k.Handled || k.Key != Key.Escape || Stack.Count == 0) return;
        var top = Stack[^1].Panel;
        if (!top.DismissOnEscape) return;
        k.Handled = true;
        top.Dismiss();
    }

    // ------------------------------------------------------------------ the app behind it

    /// <summary>
    /// The app behind a dialog takes neither the mouse nor Tab — and still looks as it did. A
    /// disabled control draws itself in the theme's disabled colours (on Windows a list and a
    /// text field turned white behind a dialog that way); hit-testing and keyboard navigation are
    /// what has to stop, not the look.
    /// </summary>
    private static void SwitchOff()
    {
        foreach (var child in _root!.Children)
        {
            if (ReferenceEquals(child, _layers)) continue;
            Switched.Add((child, child.IsHitTestVisible, KeyboardNavigation.GetTabNavigation(child)));
            child.IsHitTestVisible = false;
            KeyboardNavigation.SetTabNavigation(child, KeyboardNavigationMode.None);
        }
    }

    private static void SwitchBackOn()
    {
        foreach (var (element, hit, tab) in Switched)
        {
            element.IsHitTestVisible = hit;
            KeyboardNavigation.SetTabNavigation(element, tab);
        }
        Switched.Clear();
    }

    private static void ReleaseAll()
    {
        foreach (var w in Windows.Values.ToArray()) w.Window.Close();
        foreach (var e in Stack.ToArray())
        {
            if (e.Scroll != null) e.Scroll.Content = null;
            e.Done.TrySetResult();
        }
        Stack.Clear();
        _layers?.Children.Clear();
        SwitchBackOn();
    }

    // ------------------------------------------------------------------ no main window to draw on

    private static Task InWindow(DialogPanel panel, Control? owner)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var parent = owner == null ? null : TopLevel.GetTopLevel(owner) as Window;
        if (parent is not { IsVisible: true }) parent = null;

        var w = new Window
        {
            Title = panel.Title,
            Content = panel,
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            WindowStartupLocation = parent != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            Background = Brush("BgBrush", Brushes.Black),
        };
        w.Closed += (_, _) =>
        {
            Windows.Remove(panel);
            w.Content = null;
            done.TrySetResult();
        };
        Windows[panel] = (w, done);

        if (parent != null) _ = w.ShowDialog(parent);
        else w.Show();
        return done.Task;
    }
}
