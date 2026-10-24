using System.Threading.Tasks;
using Avalonia.Controls;

namespace Tunor.Desktop.Views;

/// <summary>
/// A dialog that opens inside the main window instead of in a window of its own.
///
/// A dialog in a window of its own can end up behind the main window — one click on the
/// app and it is gone, while the app behind it refuses every click because the dialog is
/// still waiting for an answer. Here the dialog is a card over a dimmed copy of the app,
/// in the same window, so it cannot be lost.
///
/// It is used the way the old <c>Window</c> was — <c>await dialog.ShowDialog(owner)</c>,
/// <c>Close()</c> from inside — so the callers did not have to change. The owner is
/// accepted and ignored for the same reason.
///
/// A panel that sets no <c>Height</c> is as tall as its content; one that sets it keeps
/// it, and scrolls if the main window is smaller than that.
/// </summary>
public class DialogPanel : UserControl
{
    /// <summary>Only shown if the dialog ever has to open in a window of its own.</summary>
    public string Title { get; set; } = "";

    /// <summary>Opens the dialog and completes when it is closed.</summary>
    public Task ShowDialog(Control? owner = null) => DialogHost.ShowAsync(this, owner);

    public void Close() => DialogHost.Close(this);

    /// <summary>What Esc and the cross in the corner do. Closing is the default.</summary>
    public virtual void Dismiss() => Close();

    /// <summary>Whether Esc dismisses it. A dialog that must not be one stray key from closing turns this off.</summary>
    public virtual bool DismissOnEscape => true;
}
