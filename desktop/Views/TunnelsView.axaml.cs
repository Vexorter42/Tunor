using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Tunor.Services;

namespace Tunor.Desktop.Views;

public partial class TunnelsView : UserControl
{
    /// <summary>A row: everything shown about one tunnel, and nothing secret.</summary>
    public sealed record Row(string Id, string Title, string Badge, string Detail,
                             string Status, bool CanEdit);

    public TunnelsView()
    {
        InitializeComponent();
        Refresh();
    }

    private void Refresh()
    {
        var tunnels = TunnelService.Load();
        var live = ConfigGenerator.LiveTunnels(tunnels);
        var rows = new List<Row>();

        foreach (var t in tunnels)
        {
            string badge, detail;
            if (t.IsWireguard)
            {
                badge = "WIREGUARD";
                var state = ConfigGenerator.Inspect(ConfigGenerator.ConfPath(t));
                detail = state.Problem ?? (state.Placeholder ? "не настроен" : "настроен");
            }
            else if (t.IsSubscription)
            {
                badge = "ПОДПИСКА";
                detail = $"серверов {t.Nodes.Count} · "
                         + (t.IsAuto ? "быстрейший автоматически" : t.Node);
            }
            else
            {
                badge = t.Kind.ToUpperInvariant();
                var p = ProxyLink.Parse(t.Url);
                detail = p.Ok ? p.Summary : "ссылка не читается";
            }
            if (t.Detour.Length > 0) detail += " · через " + t.Detour;

            // WARP and geo come from .conf files and are edited elsewhere; only the ones
            // added by link or subscription are managed here.
            rows.Add(new Row(t.Id, t.Title, badge, detail,
                live.Contains(t.Id) ? "готов" : "не готов", !t.IsWireguard));
        }
        List.ItemsSource = rows;
    }

    private Window? Owner => TopLevel.GetTopLevel(this) as Window;

    private async void Add_Click(object? sender, RoutedEventArgs e) => await Open(null);

    private async void Edit_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }) return;
        await Open(TunnelService.Load().FirstOrDefault(t => t.Id == id));
    }

    private async Task Open(Tunnel? editing)
    {
        var owner = Owner;
        if (owner == null) return;
        var dlg = new AddVpnWindow(editing);
        await dlg.ShowDialog(owner);
        if (!dlg.Saved) return;
        Refresh();
        StatusText.Text = "Список туннелей изменён. Перезапусти движок, чтобы применить.";
    }

    private async void Delete_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }) return;
        var owner = Owner;
        var tunnels = TunnelService.Load();
        var t = tunnels.FirstOrDefault(x => x.Id == id);
        if (t == null || owner == null) return;

        var ask = new ConfirmWindow($"Удалить «{t.Title}»?",
            "Правила, которые отправляли трафик в этот туннель, останутся на месте, "
            + "но перестанут действовать — трафик пойдёт напрямую.");
        await ask.ShowDialog(owner);
        if (!ask.Confirmed) return;

        tunnels.Remove(t);
        TunnelService.Save(tunnels);
        ConfigGenerator.Generate();
        Refresh();
        StatusText.Text = $"«{t.Title}» удалён. Перезапусти движок, чтобы применить.";
    }
}
