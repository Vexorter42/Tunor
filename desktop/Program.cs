using Avalonia;

namespace Tunor.Desktop;

internal static class Program
{
    // Avalonia has to be configured before anything touches its types, so Main stays bare.
    [System.STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
}
