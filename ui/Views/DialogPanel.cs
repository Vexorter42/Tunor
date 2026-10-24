using System.Windows;
using System.Windows.Controls;

namespace Tunor.Views;

/// <summary>
/// A dialog that opens inside the main window instead of in a window of its own.
///
/// A dialog in a window of its own can end up behind the main window — one click on the
/// app, or one Alt+Tab, and it is gone, while the app behind it refuses every click
/// because the dialog is still waiting for an answer. Here the dialog is a card over a
/// dimmed copy of the app, in the same window, so it cannot be lost: wherever the main
/// window is, the dialog is on it.
///
/// It is used the way the old <c>Window</c> was — <c>ShowDialog()</c> blocks until
/// <c>Close()</c> or <c>DialogResult = …</c>, and returns the result — so the callers
/// did not have to change. <see cref="Owner"/> is accepted and ignored for the same reason.
///
/// A panel that sets no <c>Height</c> is as tall as its content, which is what
/// <c>SizeToContent="Height"</c> was; one that sets it keeps it, and scrolls if the
/// main window is smaller than that.
/// </summary>
public class DialogPanel : UserControl
{
    private bool? _result;

    /// <summary>Only shown if the dialog ever has to open in a window of its own.</summary>
    public string Title { get; set; } = "";

    /// <summary>Accepted for compatibility with <c>Window</c>; the dialog opens in the main window.</summary>
    public Window? Owner { get; set; }

    /// <summary>Setting it closes the dialog, as it does on a window.</summary>
    public bool? DialogResult
    {
        get => _result;
        set { _result = value; Close(); }
    }

    /// <summary>Shows the dialog and waits until it is closed. True / false / null, as on a window.</summary>
    public bool? ShowDialog() => DialogHost.Show(this);

    public void Close() => DialogHost.Close(this);

    /// <summary>
    /// What Esc and the cross in the corner do. Closing is the default; a dialog whose
    /// "no" is a different answer than "closed" says so here.
    /// </summary>
    public virtual void Dismiss() => Close();

    /// <summary>
    /// Whether Esc dismisses it. A wizard that gates the app turns this off: Esc
    /// should not be one stray key away from quitting.
    /// </summary>
    public virtual bool DismissOnEscape => true;

    /// <summary>What gets the keyboard when the dialog opens; null — the first control that can take it.</summary>
    public virtual UIElement? InitialFocus => null;
}
