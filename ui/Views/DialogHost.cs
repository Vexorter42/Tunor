using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Tunor.Views;

/// <summary>
/// Draws <see cref="DialogPanel"/>s over the main window and waits for them.
///
/// The main window gives it one empty layer on top of everything else. A dialog becomes a
/// card on a dimmed backdrop in that layer; everything underneath stops taking the mouse and
/// Tab, so nothing reaches it; and <see cref="Show"/> waits the way
/// <c>Window.ShowDialog</c> does — it keeps the message loop going and returns when the
/// dialog is closed. A dialog may open another one (an error over a form): they stack,
/// the last one is the one that answers.
///
/// When there is no main window to draw on — not yet shown, or hidden in the tray — the
/// dialog opens in a window of its own, as before; it is better to show it than to wait
/// for a window nobody can see.
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
        public DispatcherFrame? Frame { get; set; }
    }

    private const double Gap = 16;                       // between the card and the edge of the window

    private static Window? _window;
    private static Panel? _root;
    private static Panel? _layers;
    private static readonly List<Entry> Stack = new();
    private static readonly Dictionary<DialogPanel, Window> Windows = new();
    private static readonly List<(UIElement Element, DependencyProperty Property, object? Was)> Switched = new();
    private static IInputElement? _focusBefore;

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
        // Bubbling, not tunnelling: a drop-down that has just closed itself on Esc has handled the key.
        layers.KeyDown += OnKey;
        window.Closed += (_, _) => ReleaseAll();
    }

    private static bool Available =>
        _window is { IsVisible: true } && _root != null && _layers != null && Application.Current != null;

    public static bool? Show(DialogPanel panel)
    {
        if (Available) InOverlay(new Entry(panel));
        else InWindow(panel);
        return panel.DialogResult;
    }

    public static void Close(DialogPanel panel)
    {
        if (Windows.TryGetValue(panel, out var w)) { w.Close(); return; }

        var e = Stack.Find(x => x.Panel == panel);
        if (e == null) return;                           // not shown, or already closed

        Stack.Remove(e);
        _layers?.Children.Remove(e.Layer);
        if (e.Scroll != null) e.Scroll.Content = null;   // the panel is still readable, it is just not on screen
        if (Stack.Count == 0) SwitchBackOn();
        if (e.Frame != null) e.Frame.Continue = false;
    }

    // ------------------------------------------------------------------ in the main window

    private static void InOverlay(Entry e)
    {
        if (Stack.Count == 0) SwitchOff();
        Build(e);
        Stack.Add(e);
        _layers!.Children.Add(e.Layer);
        Fit(e);

        e.Layer!.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)));
        e.Frame = new DispatcherFrame();
        e.Layer.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => FocusInto(e)));

        Dispatcher.PushFrame(e.Frame);                   // returns when Close() lets it go
    }

    private static void Build(Entry e)
    {
        var res = Application.Current.Resources;
        var panel = e.Panel;

        // The panel still carries the opaque background it had as a window. The card paints
        // it now, with round corners, so the panel itself must not cover them.
        var paint = panel.Background ?? (Brush)res["BgBrush"];
        panel.Background = Brushes.Transparent;

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
            Content = panel,
        };

        var cross = new Button
        {
            Style = (Style)res["GhostButton"],
            Content = "✕",
            Width = 32, Height = 32, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0),
            FontSize = 14,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 8, 8, 0),
            Focusable = false,                           // Tab goes through the dialog, not to its corner
            ToolTip = "Закрыть",
        };
        Panel.SetZIndex(cross, 1);
        cross.Click += (_, _) => panel.Dismiss();

        var inside = new Grid();
        inside.Children.Add(scroll);
        inside.Children.Add(cross);

        var card = new Border
        {
            Background = paint,
            BorderBrush = (Brush)res["BorderBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(Gap),
            SnapsToDevicePixels = true,
            Child = inside,
            Effect = new DropShadowEffect
            {
                BlurRadius = 40, ShadowDepth = 8, Direction = 270, Opacity = 0.55, Color = Colors.Black,
            },
        };

        var layer = new Grid { Background = new SolidColorBrush(Color.FromArgb(0xB8, 0x0B, 0x0D, 0x11)) };
        KeyboardNavigation.SetTabNavigation(layer, KeyboardNavigationMode.Cycle);
        KeyboardNavigation.SetControlTabNavigation(layer, KeyboardNavigationMode.Cycle);
        KeyboardNavigation.SetDirectionalNavigation(layer, KeyboardNavigationMode.Cycle);
        layer.Children.Add(card);

        e.Layer = layer;
        e.Card = card;
        e.Scroll = scroll;
    }

    /// <summary>A card never grows past the window; a tall dialog in a small window scrolls instead.</summary>
    private static void Fit(Entry e)
    {
        if (e.Card == null || _layers == null) return;
        if (_layers.ActualHeight > 0) e.Card.MaxHeight = Math.Max(120, _layers.ActualHeight - 2 * Gap);
        if (_layers.ActualWidth > 0) e.Card.MaxWidth = Math.Max(240, _layers.ActualWidth - 2 * Gap);
    }

    private static void FocusInto(Entry e)
    {
        if (e.Layer == null || !Stack.Contains(e)) return;
        if (e.Panel.InitialFocus is { } first && first.Focus()) return;
        if (e.Panel.MoveFocus(new TraversalRequest(FocusNavigationDirection.First))) return;

        // Nothing in it takes focus: let the layer, so that Esc still reaches us.
        e.Layer.Focusable = true;
        e.Layer.Focus();
    }

    private static void OnKey(object sender, KeyEventArgs k)
    {
        if (k.Handled || k.Key != Key.Escape || Stack.Count == 0) return;
        var top = Stack[^1].Panel;
        if (!top.DismissOnEscape) return;
        k.Handled = true;
        top.Dismiss();
    }

    // ------------------------------------------------------------------ the app behind it

    /// <summary>
    /// The app behind a dialog takes neither the mouse nor Tab — and still looks as it did.
    ///
    /// It used to be switched off with IsEnabled = false, and a disabled control draws itself
    /// in the system's disabled colours: a list and a text field in this app went white behind
    /// the dialog. Hit-testing and keyboard navigation are what has to stop, not the look.
    /// </summary>
    private static void SwitchOff()
    {
        _focusBefore = Keyboard.FocusedElement;
        foreach (UIElement child in _root!.Children)
        {
            if (ReferenceEquals(child, _layers)) continue;
            Set(child, UIElement.IsHitTestVisibleProperty, false);
            Set(child, KeyboardNavigation.TabNavigationProperty, KeyboardNavigationMode.None);
            Set(child, KeyboardNavigation.ControlTabNavigationProperty, KeyboardNavigationMode.None);
            Set(child, KeyboardNavigation.DirectionalNavigationProperty, KeyboardNavigationMode.None);
        }
    }

    /// <summary>Sets a value and remembers what was set locally before, to put it back exactly.</summary>
    private static void Set(UIElement element, DependencyProperty property, object value)
    {
        Switched.Add((element, property, element.ReadLocalValue(property)));
        element.SetValue(property, value);
    }

    private static void SwitchBackOn()
    {
        foreach (var (element, property, was) in Switched)
        {
            if (was == DependencyProperty.UnsetValue) element.ClearValue(property);
            else element.SetValue(property, was);
        }
        Switched.Clear();

        // Back to where the user was typing.
        var back = _focusBefore as UIElement;
        _focusBefore = null;
        back?.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (Stack.Count == 0 && back.IsVisible && back.IsEnabled) back.Focus();
        }));
    }

    private static void ReleaseAll()
    {
        foreach (var w in new List<Window>(Windows.Values)) w.Close();
        foreach (var e in Stack.ToArray())
        {
            if (e.Scroll != null) e.Scroll.Content = null;
            if (e.Frame != null) e.Frame.Continue = false;
        }
        Stack.Clear();
        _layers?.Children.Clear();
        SwitchBackOn();
    }

    // ------------------------------------------------------------------ no main window to draw on

    private static void InWindow(DialogPanel panel)
    {
        var owner = panel.Owner is { IsVisible: true } o ? o : null;
        var w = new Window
        {
            Title = panel.Title,
            Content = panel,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            Owner = owner,
            WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
        };
        if (Application.Current?.Resources["BgBrush"] is Brush bg) w.Background = bg;
        try { w.Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/app.ico")); } catch { /* no icon is not an error */ }

        Windows[panel] = w;
        try { w.ShowDialog(); }
        finally
        {
            Windows.Remove(panel);
            w.Content = null;
        }
    }
}
