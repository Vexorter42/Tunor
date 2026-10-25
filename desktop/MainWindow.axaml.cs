using Tunor.Services;
using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Tunor.Desktop.Services;
using Tunor.Desktop.Views;

namespace Tunor.Desktop;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(2) };

    public MainWindow()
    {
        InitializeComponent();
        DialogHost.Attach(this, AppRoot, DialogLayers);
        PageHost.Content = new HomeView();
        VersionText.Text = "v" + UpdateService.CurrentVersionString;

        // The engine can be started or stopped from outside the app, so the badge follows
        // what is actually running rather than what this window last did.
        _tick.Tick += (_, _) => RefreshBadge();
        _tick.Start();
        RefreshBadge();

        Opened += (_, _) => TryShowWhatsNew();
    }

    private bool _whatsNewDone;

    /// <summary>The notes of the version that has just been installed, once, when the window is up.</summary>
    private async void TryShowWhatsNew()
    {
        if (_whatsNewDone || DialogHost.IsOpen) return;
        _whatsNewDone = true;
        try
        {
            var found = await WhatsNew.CheckAsync();
            if (found is not { } notes) return;
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
            {
                await new WhatsNewWindow(notes.Version, notes.Blocks).ShowDialog(this);
                WhatsNew.MarkSeen(notes.Version);
            });
        }
        catch { /* the notes are a courtesy; nothing here is worth a failure */ }
    }

    private void RefreshBadge()
    {
        var running = EngineService.IsRunning;
        StateBadge.Text = running ? "● Подключено" : "○ Остановлен";
        StateBadge.Foreground = running
            ? this.FindResource("AccentBrush") as Avalonia.Media.IBrush
            : this.FindResource("TextDimBrush") as Avalonia.Media.IBrush;
    }

    private void Nav_Checked(object? sender, RoutedEventArgs e)
    {
        // IsCheckedChanged fires for the entry being left as well as the one being
        // entered; acting on both let the old page overwrite the new one.
        if (PageHost == null || sender is not RadioButton { IsChecked: true, Tag: string page }) return;
        PageHost.Content = page switch
        {
            "tunnels" => new TunnelsView(),
            "apps" => new AppsView(),
            "rules" => new RulesView(),
            "configs" => new ConfigsView(),
            "conns" => new ConnectionsView(),
            "settings" => new SettingsView(),
            "logs" => new LogsView(),
            "autostart" => new AutostartView(),
            _ => new HomeView(),
        };
    }
}
