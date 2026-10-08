using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

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

            // The tunnel outlives the window. Closing it puts Tunor in the menu bar
            // instead of ending it, which only works if the app is not wired to quit
            // when its last window goes — and quitting is then the tray's own command.
            Tray = Services.Tray.TryCreate();
            if (Tray != null)
            {
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                desktop.MainWindow.Closing += (sender, e) =>
                {
                    if (Tray.Quitting) return;
                    e.Cancel = true;
                    ((Window)sender!).Hide();
                };
                desktop.ShutdownRequested += (_, _) => Tray.Dispose();
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
}
