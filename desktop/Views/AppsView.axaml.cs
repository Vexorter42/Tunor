using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
using Tunor.Services;

namespace Tunor.Desktop.Views;

/// <summary>
/// Which tunnel each program's traffic takes.
///
/// The list fills itself: the engine tags every connection with the process that made it
/// (find_process, which this engine supports on macOS too), and TrafficRecorder keeps
/// what it saw. Choosing a tunnel writes an app rule through RouteEditor — the same one
/// the Windows app uses — and that rebuilds the config.
/// </summary>
public partial class AppsView : UserControl
{
    public sealed record AppRow(string Exe, string Badge, IBrush? BadgeBrush, string Summary);
    public sealed record HostRow(string Host, string Down);

    private string? _chosen;

    public AppsView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => Refresh();
    }

    private void Refresh()
    {
        List<AppStat> stats;
        List<RuleGroup> groups;
        List<Tunnel> tunnels;
        try
        {
            stats = TrafficRecorder.Snapshot();
            groups = RulesService.Load();
            tunnels = TunnelService.Load();
        }
        catch { return; }

        // Programs seen in traffic, plus any that already carry a rule but have been
        // quiet — otherwise a rule someone set yesterday would vanish from the page.
        var names = new HashSet<string>(stats.Select(s => s.Exe), StringComparer.OrdinalIgnoreCase);
        foreach (var g in groups.Where(g => g.IsInline && g.ItemKind == RuleItemKind.ProcessName))
            foreach (var item in g.Items)
                if (!string.IsNullOrWhiteSpace(item)) names.Add(item.Trim());

        var rows = new List<AppRow>();
        foreach (var exe in names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            var route = RouteEditor.ProcessRoute(exe, groups, tunnels);
            var at = route == null ? -1
                : tunnels.FindIndex(t => string.Equals(t.Id, route, StringComparison.OrdinalIgnoreCase));
            var stat = stats.FirstOrDefault(s => string.Equals(s.Exe, exe, StringComparison.OrdinalIgnoreCase));

            rows.Add(new AppRow(
                exe,
                at < 0 ? "правила" : tunnels[at].Title,
                at < 0 ? Palette.Brush("TextDimBrush") : Palette.TunnelBrush(tunnels[at].Id, at),
                stat == null
                    ? (route != null ? "правило есть, трафика пока нет" : "трафика пока нет")
                    : $"{stat.Hosts.Count} адресов · {Size(stat.Down)}"));
        }
        List.ItemsSource = rows;

        if (_chosen != null) Show(_chosen, stats, groups, tunnels);
    }

    private void Pick_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string exe }) return;
        _chosen = exe;
        Refresh();
    }

    private void Show(string exe, List<AppStat> stats, List<RuleGroup> groups, List<Tunnel> tunnels)
    {
        EmptyText.IsVisible = false;
        Detail.IsVisible = true;

        var stat = stats.FirstOrDefault(s => string.Equals(s.Exe, exe, StringComparison.OrdinalIgnoreCase));
        AppName.Text = exe;
        AppPath.Text = string.IsNullOrEmpty(stat?.Path)
            ? "путь неизвестен — программа ещё не выходила в сеть"
            : stat.Path;

        var route = RouteEditor.ProcessRoute(exe, groups, tunnels);
        BuildRouteButtons(tunnels, route);
        RouteHint.Text = RouteHintFor(route, tunnels);

        var hosts = stat?.Hosts.OrderByDescending(h => h.Down).Take(40).ToList() ?? new List<HostStat>();
        Hosts.ItemsSource = hosts.Select(h => new HostRow(h.Host, Size(h.Down))).ToList();
        HostsTitle.Text = hosts.Count == 0
            ? "Куда ходит за этот сеанс — пока ничего"
            : $"Куда ходит за этот сеанс — {hosts.Count} адресов";
    }

    /// <summary>One button per tunnel, then "по общим правилам".</summary>
    private void BuildRouteButtons(List<Tunnel> tunnels, string? route)
    {
        RouteButtons.Children.Clear();
        foreach (var t in tunnels)
            Add("через " + t.Title, t.Id, string.Equals(route, t.Id, StringComparison.OrdinalIgnoreCase));
        Add("по общим правилам", null, route == null);

        void Add(string text, string? id, bool active)
        {
            var b = new Button
            {
                Content = text,
                Padding = new Avalonia.Thickness(14, 7),
                Margin = new Avalonia.Thickness(0, 0, 8, 6),
                Tag = id,
            };
            if (active) b.Classes.Add("primary");
            b.Click += (_, _) => SetRoute((string?)b.Tag);
            RouteButtons.Children.Add(b);
        }
    }

    private static string RouteHintFor(string? route, List<Tunnel> tunnels)
    {
        if (route == null)
            return "Отдельного правила нет: каждый адрес решают списки доменов и маршрут по умолчанию.";

        var t = tunnels.FirstOrDefault(x => string.Equals(x.Id, route, StringComparison.OrdinalIgnoreCase));
        if (t == null) return $"Трафик отправлен в туннель «{route}», но такого туннеля больше нет.";
        if (ConfigGenerator.LiveTunnels(tunnels).Contains(t.Id))
            return $"Весь трафик программы идёт через {t.Title} — какие бы адреса она ни открывала.";

        var via = tunnels.FirstOrDefault(x => string.Equals(x.Id, t.Detour, StringComparison.OrdinalIgnoreCase));
        return via != null
            ? $"Отправлено в {t.Title}, но он не настроен — пока трафик идёт через {via.Title}."
            : $"Отправлено в {t.Title}, но он не настроен — пока трафик идёт напрямую.";
    }

    private void SetRoute(string? route)
    {
        if (_chosen == null) return;
        try
        {
            RouteEditor.SetProcessRoute(_chosen, route);
            StatusText.Text = (route == null
                ? $"{_chosen}: правило снято"
                : $"{_chosen} → весь трафик через {TitleOf(route)}")
                + ". Перезапусти движок, чтобы применить.";
            Refresh();
        }
        catch (Exception ex) { StatusText.Text = "Не удалось изменить правило: " + ex.Message; }
    }

    private static string TitleOf(string id) =>
        TunnelService.Load().FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase))?.Title ?? id;

    /// <summary>Sends one address into a tunnel, chosen from a menu of them.</summary>
    private void HostRoute_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string host } btn) return;
        // Avalonia opens a ContextMenu by attaching it to the control, not by setting
        // IsOpen, which is read-only here.
        var menu = new ContextMenu { Placement = PlacementMode.Bottom };
        foreach (var t in TunnelService.Load())
        {
            var item = new MenuItem { Header = $"в список {t.Title}" };
            var id = t.Id;
            item.Click += (_, _) =>
            {
                try
                {
                    RouteEditor.AddDomain(host, id);
                    StatusText.Text = $"{host} добавлен в список {TitleOf(id)}. "
                                      + "Перезапусти движок, чтобы применить.";
                    Refresh();
                }
                catch (Exception ex) { StatusText.Text = "Не удалось добавить: " + ex.Message; }
            };
            menu.Items.Add(item);
        }
        btn.ContextMenu = menu;
        menu.Open(btn);
    }

    private static string Size(long b) => b switch
    {
        >= 1L << 30 => $"{b / (double)(1L << 30):0.#} ГБ",
        >= 1L << 20 => $"{b / (double)(1L << 20):0.#} МБ",
        >= 1024 => $"{b / 1024.0:0.#} КБ",
        _ => $"{b} Б",
    };
}
