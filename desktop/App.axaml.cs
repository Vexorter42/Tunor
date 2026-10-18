using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace Tunor.Desktop;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <summary>The menu-bar icon, or null where the desktop has no tray.</summary>
    public static Services.Tray? Tray { get; private set; }

    public override void OnFrameworkInitializationCompleted()
    {
        // A fresh install has no settings and no config; make them before any page
        // tries to read them.
        Services.Bootstrap.Run();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();

            // Looks for a new version in the background; the Home page shows what it finds.
            Tunor.Services.UpdateWatcher.Start();

            // The tunnel outlives the window. Closing it puts Tunor in the menu bar
            // instead of ending it, which only works if the app is not wired to quit
            // when its last window goes — and quitting is then the tray's own command.
            Tray = Services.Tray.TryCreate();
            if (Tray != null)
            {
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                desktop.MainWindow.Closing += (sender, e) =>
                {
                    // Only the close button means "out of the way". Quit from the Dock,
                    // Command-Q and the system shutting down all mean "end", and a window
                    // that hides itself in answer to those leaves a Tunor nobody can quit.
                    if (Tray.Quitting
                        || e.CloseReason is WindowCloseReason.ApplicationShutdown
                                         or WindowCloseReason.OSShutdown) return;
                    e.Cancel = true;
                    ((Window)sender!).Hide();
                };
                desktop.ShutdownRequested += (_, _) => Tray.Dispose();

                // The way back. A click on the Dock icon, or opening the app again from
                // Finder, is not a second launch: macOS tells the running copy it was
                // "reopened" and expects a window. Nothing answered, so a hidden Tunor
                // could not be opened by anything but the menu-bar icon.
                if (this.TryGetFeature<IActivatableLifetime>() is { } activatable)
                    activatable.Activated += (_, e) =>
                    {
                        if (e.Kind == ActivationKind.Reopen)
                            Dispatcher.UIThread.Post(Services.Tray.ShowWindow);
                    };
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
}
