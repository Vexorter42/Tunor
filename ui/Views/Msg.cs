using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Tunor.Views;

/// <summary>
/// <c>MessageBox.Show</c>, drawn inside the main window: same arguments, same answer.
///
/// The system message box is a window of its own, and it has the same fault as any such
/// dialog — it can slip behind the app and leave the app unclickable. This one cannot;
/// see <see cref="DialogPanel"/>.
/// </summary>
public static class Msg
{
    public static MessageBoxResult Show(string text)
        => Show(text, "", MessageBoxButton.OK, MessageBoxImage.None, MessageBoxResult.None);

    public static MessageBoxResult Show(string text, string caption)
        => Show(text, caption, MessageBoxButton.OK, MessageBoxImage.None, MessageBoxResult.None);

    public static MessageBoxResult Show(string text, string caption, MessageBoxButton buttons)
        => Show(text, caption, buttons, MessageBoxImage.None, MessageBoxResult.None);

    public static MessageBoxResult Show(string text, string caption, MessageBoxButton buttons, MessageBoxImage icon)
        => Show(text, caption, buttons, icon, MessageBoxResult.None);

    public static MessageBoxResult Show(
        string text, string caption, MessageBoxButton buttons, MessageBoxImage icon, MessageBoxResult defaultResult)
    {
        // MessageBox could be called from any thread; a window can only be built on the UI one.
        // From another thread this waits for the answer, as the system box did.
        var ui = Application.Current?.Dispatcher;
        if (ui != null && !ui.CheckAccess())
            return ui.Invoke(() => Show(text, caption, buttons, icon, defaultResult));

        var panel = new MessagePanel(text, caption, buttons, icon, defaultResult);
        panel.ShowDialog();
        return panel.Answer;
    }
}

internal sealed class MessagePanel : DialogPanel
{
    private readonly MessageBoxResult _dismissAs;
    private readonly string _copy;
    private readonly Button _initial;

    public MessageBoxResult Answer { get; private set; }

    public MessagePanel(string text, string caption, MessageBoxButton buttons, MessageBoxImage icon,
                        MessageBoxResult defaultResult)
    {
        var res = Application.Current.Resources;
        Title = caption;
        Width = 440;                                     // no Height: as tall as the text
        Foreground = (Brush)res["TextBrush"];
        Background = (Brush)res["BgBrush"];

        // What the cross and Esc mean: what MessageBox gives for closing it.
        var set = buttons switch
        {
            MessageBoxButton.OKCancel => new[] { (MessageBoxResult.OK, "OK"), (MessageBoxResult.Cancel, "Отмена") },
            MessageBoxButton.YesNo => new[] { (MessageBoxResult.Yes, "Да"), (MessageBoxResult.No, "Нет") },
            MessageBoxButton.YesNoCancel => new[]
                { (MessageBoxResult.Yes, "Да"), (MessageBoxResult.No, "Нет"), (MessageBoxResult.Cancel, "Отмена") },
            _ => new[] { (MessageBoxResult.OK, "OK") },
        };
        _dismissAs = buttons switch
        {
            MessageBoxButton.OKCancel or MessageBoxButton.YesNoCancel => MessageBoxResult.Cancel,
            MessageBoxButton.YesNo => MessageBoxResult.No,
            _ => MessageBoxResult.OK,
        };
        Answer = _dismissAs;
        _copy = caption.Length > 0 ? caption + "\n\n" + text : text;

        var chosen = set.Any(s => s.Item1 == defaultResult) ? defaultResult : set[0].Item1;

        // Buttons, the chosen one in the accent colour.
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 20, 0, 0),
        };
        Button? initial = null;
        foreach (var (result, label) in set)
        {
            var b = new Button
            {
                Content = label,
                MinWidth = 92,
                Margin = new Thickness(8, 0, 0, 0),
                Style = (Style)res[result == chosen ? "PrimaryButton" : "SecondaryButton"],
            };
            var r = result;
            b.Click += (_, _) => { Answer = r; Close(); };
            row.Children.Add(b);
            if (result == chosen) initial = b;
        }
        _initial = initial ?? (Button)row.Children[0];

        // Text, with a mark for what kind of message it is.
        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var mark = Mark(icon, res);
        if (mark != null) { Grid.SetColumn(mark, 0); body.Children.Add(mark); }

        var column = new StackPanel { Margin = new Thickness(mark != null ? 16 : 0, 0, 0, 0) };
        if (caption.Length > 0)
            column.Children.Add(new TextBlock
            {
                Text = caption, Style = (Style)res["H2"], TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 28, 6),     // clear of the cross in the corner
            });
        column.Children.Add(new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = 320,
            Content = new TextBlock
            {
                Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 13,
                Foreground = (Brush)res["TextBrush"],
                Margin = new Thickness(0, 0, 4, 0),
            },
        });
        Grid.SetColumn(column, 1);
        body.Children.Add(column);

        var root = new Grid { Margin = new Thickness(24) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(row, 1);
        root.Children.Add(body);
        root.Children.Add(row);
        Content = root;

        // Ctrl+C copies the message, as it does on a system message box.
        KeyDown += (_, k) =>
        {
            if (k.Key != Key.C || Keyboard.Modifiers != ModifierKeys.Control) return;
            try { Clipboard.SetText(_copy); k.Handled = true; } catch { /* clipboard busy */ }
        };
    }

    public override UIElement? InitialFocus => _initial;

    public override void Dismiss()
    {
        Answer = _dismissAs;
        Close();
    }

    private static FrameworkElement? Mark(MessageBoxImage icon, ResourceDictionary res)
    {
        var (glyph, color) = icon switch
        {
            MessageBoxImage.Error => ("✕", (Color)res["DangerColor"]),
            MessageBoxImage.Warning => ("!", Color.FromRgb(0xF5, 0xB0, 0x42)),
            MessageBoxImage.Question => ("?", (Color)res["AccentColor"]),
            MessageBoxImage.Information => ("i", (Color)res["AccentColor"]),
            _ => ("", default(Color)),
        };
        if (glyph.Length == 0) return null;

        return new Border
        {
            Width = 40, Height = 40, CornerRadius = new CornerRadius(20),
            VerticalAlignment = VerticalAlignment.Top,
            Background = new SolidColorBrush(Color.FromArgb(0x33, color.R, color.G, color.B)),
            Child = new TextBlock
            {
                Text = glyph, FontSize = 20, FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(color),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
    }
}
