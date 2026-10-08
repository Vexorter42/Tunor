using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Tunor.Desktop.Services;
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

    /// <summary>
    /// Programs the user named themselves. A program that has not been on the network
    /// yet is in no traffic record and carries no rule, so without this it would vanish
    /// from the list the moment it was picked — before there was anything to pick for it.
    /// </summary>
    private readonly HashSet<string> _picked = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _paths = new(StringComparer.OrdinalIgnoreCase);

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
        names.UnionWith(_picked);
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
        AppPath.Text = !string.IsNullOrEmpty(stat?.Path) ? stat.Path
            : _paths.TryGetValue(exe, out var known) ? known
            : "путь неизвестен — программа ещё не выходила в сеть";

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

    // ---------------------------------------------------------------- naming a program

    private async void PickRunning_Click(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        var dlg = new ProcessPickerWindow();
        await dlg.ShowDialog(owner);
        if (dlg.Picked == null) return;
        Took(dlg.Picked);
    }

    /// <summary>
    /// Points at a program on disk. On macOS that is a folder — an app is a bundle,
    /// and the name a rule needs belongs to the executable inside it, so the bundle is
    /// opened rather than its name taken. On Linux a program is the file itself.
    /// </summary>
    private async void Browse_Click(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not { } top) return;
        try
        {
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Программа, для которой нужно правило",
                AllowMultiple = false,
                FileTypeFilter = new[] { Programs() },
            });
            var path = files.FirstOrDefault()?.TryGetLocalPath();
            if (string.IsNullOrEmpty(path)) return;

            var entry = ProcessList.FromBundle(path);
            if (entry == null) { StatusText.Text = "Внутри не нашлось программы."; return; }
            Took(entry);
        }
        catch (Exception ex) { StatusText.Text = "Не удалось открыть: " + ex.Message; }
    }

    /// <summary>
    /// What the picker should let through. An app on macOS is a bundle and has to be
    /// named as one, or the panel greys it out; elsewhere a program is an ordinary
    /// file with no extension to filter on.
    /// </summary>
    private static FilePickerFileType Programs() => OperatingSystem.IsMacOS()
        ? new FilePickerFileType("Программы")
        {
            Patterns = new[] { "*.app", "*" },
            AppleUniformTypeIdentifiers = new[] { "com.apple.application-bundle" },
        }
        : new FilePickerFileType("Программы") { Patterns = new[] { "*" } };

    /// <summary>
    /// Takes a program into the list. Every executable of a bundle is taken, not just
    /// the one named like the app: the connections come from the helpers, so a rule for
    /// the app alone would match nothing it does.
    /// </summary>
    private void Took(ProcessEntry entry)
    {
        foreach (var name in entry.Names) _picked.Add(name);
        foreach (var name in entry.Names) _paths[name] = entry.Path;
        _chosen = entry.Names.FirstOrDefault();
        Refresh();

        StatusText.Text = entry.Names.Count > 1
            ? $"{entry.Title}: выбери туннель. Внутри {entry.Names.Count} программ — "
              + "правило нужно каждой, сеть обычно у вспомогательной."
            : $"{entry.Title}: выбери, куда отправлять её трафик.";
    }

    private void ClearHistory_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            TrafficRecorder.Clear();
            Refresh();
            StatusText.Text = "История очищена. Правила не тронуты.";
        }
        catch (Exception ex) { StatusText.Text = "Не удалось очистить: " + ex.Message; }
    }

    private static string Size(long b) => b switch
    {
        >= 1L << 30 => $"{b / (double)(1L << 30):0.#} ГБ",
        >= 1L << 20 => $"{b / (double)(1L << 20):0.#} МБ",
        >= 1024 => $"{b / 1024.0:0.#} КБ",
        _ => $"{b} Б",
    };
}
