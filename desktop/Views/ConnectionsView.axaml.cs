using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Tunor.Services;

namespace Tunor.Desktop.Views;

/// <summary>
/// Live connections, read from the engine's stats API once a second. Nothing here holds
/// state: each tick replaces the list, which is simpler than diffing and fast enough for
/// the few hundred rows a busy machine produces.
/// </summary>
public partial class ConnectionsView : UserControl
{
    public sealed record Row(string Host, string Route, IBrush? RouteBrush,
                             string Process, string Down, string Age);

    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };

    public ConnectionsView()
    {
        InitializeComponent();
        _tick.Tick += async (_, _) => await Refresh();
        AttachedToVisualTree += async (_, _) => { await Refresh(); _tick.Start(); };
        DetachedFromVisualTree += (_, _) => _tick.Stop();
    }

    private async System.Threading.Tasks.Task Refresh()
    {
        var snap = await ConnectionsService.FetchAsync();
        if (snap == null)
        {
            List.ItemsSource = null;
            DownText.Text = UpText.Text = CountText.Text = "—";
            StatusText.Text = EngineState.IsRunning
                ? "Движок запущен, но статистика недоступна. Проверь, включён ли сбор статистики в конфиге."
                : "Движок не запущен.";
            return;
        }

        var tunnels = TunnelService.Load();
        var onlyTunnel = ChkTunnelOnly.IsChecked == true;
        var rows = snap.Connections
            .Where(c => !onlyTunnel || (c.Outbound.Length > 0 && c.Outbound != "direct-out"))
            .OrderByDescending(c => c.Download)
            .Take(300)
            .Select(c =>
            {
                var at = tunnels.FindIndex(t =>
                    string.Equals(t.OutboundTag, c.Outbound, StringComparison.OrdinalIgnoreCase));
                return new Row(
                    c.Host,
                    ConnectionsService.RouteLabel(c.Outbound),
                    Palette.TunnelBrush(at < 0 ? "" : tunnels[at].Id, at),
                    c.Process,
                    Size(c.Download),
                    Age(DateTime.Now - c.Start));
            })
            .ToList();

        List.ItemsSource = rows;
        DownText.Text = Size(snap.DownloadTotal);
        UpText.Text = Size(snap.UploadTotal);
        CountText.Text = rows.Count.ToString();
        StatusText.Text = "";
    }

    private static string Size(long b) => b switch
    {
        >= 1L << 30 => $"{b / (double)(1L << 30):0.#} ГБ",
        >= 1L << 20 => $"{b / (double)(1L << 20):0.#} МБ",
        >= 1024 => $"{b / 1024.0:0.#} КБ",
        _ => $"{b} Б",
    };

    private static string Age(TimeSpan t) => t.TotalSeconds < 60 ? $"{(int)t.TotalSeconds} с"
        : t.TotalMinutes < 60 ? $"{(int)t.TotalMinutes} мин"
        : $"{(int)t.TotalHours} ч";
}
