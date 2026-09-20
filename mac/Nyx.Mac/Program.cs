using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;

namespace Nyx.Mac;

public static class Program
{
    /// <summary>
    /// "shot &lt;file&gt;" renders the window to a PNG with no display attached — that is how
    /// the layout gets reviewed without a Mac. Otherwise it runs as a normal app.
    /// </summary>
    public static void Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "shot")
        {
            AppBuilder.Configure<App>()
                .UseSkia()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .SetupWithoutStarting();

            var w = new MainWindow();
            w.Show();
            Dispatcher.UIThread.RunJobs();
            var frame = w.CaptureRenderedFrame();
            if (frame == null) { System.Console.WriteLine("capture failed"); return; }
            frame.Save(args[1]);
            System.Console.WriteLine("wrote " + args[1]);
            return;
        }

        AppBuilder.Configure<App>().UsePlatformDetect().StartWithClassicDesktopLifetime(args);
    }
}
