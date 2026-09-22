using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Nyx.Services;

namespace Nyx.Views;

public abstract class Notifier : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public sealed class AppRow : Notifier
{
    public string Exe { get; init; } = "";
    private string _badge = "", _summary = "";
    private Brush _badgeBrush = Brushes.Gray;
    public string Badge { get => _badge; set => Set(ref _badge, value); }
    public Brush BadgeBrush { get => _badgeBrush; set => Set(ref _badgeBrush, value); }
    public string Summary { get => _summary; set => Set(ref _summary, value); }
}

public sealed class HostRow : Notifier
{
    public string Host { get; init; } = "";
    private string _via = "", _conns = "", _down = "";
    private Brush _viaBrush = Brushes.Gray;
    private Visibility _actions = Visibility.Visible;
    public string Via { get => _via; set => Set(ref _via, value); }
    public Brush ViaBrush { get => _viaBrush; set => Set(ref _viaBrush, value); }
    public string Conns { get => _conns; set => Set(ref _conns, value); }
    public string Down { get => _down; set => Set(ref _down, value); }
    public Visibility ActionsVisibility { get => _actions; set => Set(ref _actions, value); }
}

/// <summary>
/// Programs and the addresses they talk to, with one-click routing of a whole program
/// to WARP or geo. The per-app rule is the robust fix for anything that connects by
/// bare IP (Telegram, games, voice) — no domain list can catch those.
/// </summary>
public partial class AppsPage : UserControl
{
    private readonly ObservableCollection<AppRow> _apps = new();
    private readonly ObservableCollection<HostRow> _hosts = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _restart = new() { Interval = TimeSpan.FromSeconds(1.5) };
    /// <summary>Programs picked by hand this session, shown even before they send anything.</summary>
    private readonly HashSet<string> _picked = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _paths = new(StringComparer.OrdinalIgnoreCase);
    private string? _hostsFor;

    private static readonly Brush GeoBrush = new SolidColorBrush(Color.FromRgb(0x6E, 0xA8, 0xFE));

    public AppsPage()
    {
        InitializeComponent();
        AppsList.ItemsSource = _apps;
        HostsList.ItemsSource = _hosts;

        _timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) => { Refresh(); _timer.Start(); };
        Unloaded += (_, _) => _timer.Stop();

        // Several routing changes in a row cost one restart, not one each.
        _restart.Tick += async (_, _) =>
        {
            _restart.Stop();
            if (!ProcessService.IsRunning) return;
            StatusText.Text = "Перезапускаю туннель, чтобы применить правила…";
            await ProcessService.RestartAsync();
            StatusText.Text = $"Готово · {DateTime.Now:HH:mm:ss}";
        };
    }

    private string? SelectedExe => (AppsList.SelectedItem as AppRow)?.Exe;

    // ------------------------------------------------------------ refresh

    private bool _refreshing;

    private void Refresh()
    {
        if (_refreshing) return;
        _refreshing = true;
        try { RefreshCore(); }
        finally { _refreshing = false; }
    }

    private void RefreshCore()
    {
        List<AppStat> stats;
        List<RuleGroup> groups;
        try
        {
            stats = TrafficRecorder.Snapshot();
            groups = RulesService.Load();
        }
        catch { return; }

        foreach (var s in stats) if (!string.IsNullOrEmpty(s.Path)) _paths[s.Exe] = s.Path;

        // Programs: seen in traffic, picked by hand, or already carrying an app rule.
        var names = new List<string>();
        names.AddRange(stats.OrderByDescending(s => s.LastSeen).Select(s => s.Exe));
        names.AddRange(_picked.Where(p => !names.Contains(p, StringComparer.OrdinalIgnoreCase)));
        foreach (var g in groups.Where(g => g.IsInline && g.ItemKind == RuleItemKind.ProcessName))
            foreach (var item in g.Items)
                if (!names.Any(n => ConfigGenerator.ProcessMatches(item, n)))
                    names.Add(item.Trim());

        // Update in place; new programs go on top, as the most recently active ones.
        var wanted = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        for (var i = _apps.Count - 1; i >= 0; i--)
            if (!wanted.Contains(_apps[i].Exe)) _apps.RemoveAt(i);
        var have = new HashSet<string>(_apps.Select(a => a.Exe), StringComparer.OrdinalIgnoreCase);
        var insertAt = 0;
        foreach (var n in names)
        {
            if (have.Contains(n)) continue;
            var fresh = stats.Any(s => string.Equals(s.Exe, n, StringComparison.OrdinalIgnoreCase));
            var row = new AppRow { Exe = n };
            if (fresh) _apps.Insert(insertAt++, row); else _apps.Add(row);
        }

        foreach (var row in _apps)
        {
            var route = RouteEditor.ProcessRoute(row.Exe, groups);
            row.Badge = route == RouteEditor.Warp ? "WARP" : route == RouteEditor.Geo ? "geo" : "правила";
            row.BadgeBrush = route == RouteEditor.Warp ? Ui.Brush("AccentBrush")
                           : route == RouteEditor.Geo ? GeoBrush
                           : Ui.Brush("TextDimBrush");
            var stat = stats.FirstOrDefault(s => string.Equals(s.Exe, row.Exe, StringComparison.OrdinalIgnoreCase));
            row.Summary = stat == null
                ? (route != null ? "правило есть, трафика пока нет" : "трафика пока нет")
                : $"{Count(stat.Hosts.Count, "адрес", "адреса", "адресов")} · {Size(stat.Down)}";
        }

        AppsEmpty.Visibility = _apps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowDetail(stats, groups);
    }

    // Deferred: removing the selected program from the list changes the selection from
    // inside the collection's change notification, and editing the collection there
    // throws. Run once that has finished.
    private void AppsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => Dispatcher.BeginInvoke(new Action(Refresh), DispatcherPriority.Background);

    private void ShowDetail(List<AppStat> stats, List<RuleGroup> groups)
    {
        var exe = SelectedExe;
        if (exe == null)
        {
            DetailPanel.Visibility = Visibility.Collapsed;
            DetailEmpty.Visibility = Visibility.Visible;
            return;
        }
        DetailPanel.Visibility = Visibility.Visible;
        DetailEmpty.Visibility = Visibility.Collapsed;

        AppName.Text = exe;
        AppPath.Text = _paths.TryGetValue(exe, out var p) ? p : "путь неизвестен — программа ещё не выходила в сеть";

        var route = RouteEditor.ProcessRoute(exe, groups);
        StyleButton(BtnWarp, route == RouteEditor.Warp);
        StyleButton(BtnGeo, route == RouteEditor.Geo);
        StyleButton(BtnNone, route == null);
        RouteHint.Text = route switch
        {
            RouteEditor.Warp => "Весь трафик программы идёт через WARP — какие бы адреса она ни открывала.",
            RouteEditor.Geo => ConfigGenerator.GeoConfigured
                ? "Весь трафик программы идёт через geo — какие бы адреса она ни открывала."
                : "Отправлено в geo, но geo не настроен — пока трафик идёт через WARP.",
            _ => "Отдельного правила нет: каждый адрес решают списки доменов и маршрут по умолчанию.",
        };

        var stat = stats.FirstOrDefault(s => string.Equals(s.Exe, exe, StringComparison.OrdinalIgnoreCase));
        var hosts = stat?.Hosts.OrderByDescending(h => h.Down).ThenBy(h => h.Host).ToList() ?? new List<HostStat>();

        // A different program was selected: start the list over rather than diff it.
        if (_hostsFor != exe) { _hosts.Clear(); _hostsFor = exe; }

        var wanted = new HashSet<string>(hosts.Select(h => h.Host), StringComparer.OrdinalIgnoreCase);
        for (var i = _hosts.Count - 1; i >= 0; i--)
            if (!wanted.Contains(_hosts[i].Host)) _hosts.RemoveAt(i);
        var byHost = _hosts.ToDictionary(h => h.Host, StringComparer.OrdinalIgnoreCase);
        foreach (var h in hosts)
        {
            if (!byHost.TryGetValue(h.Host, out var row))
            {
                row = new HostRow { Host = h.Host };
                _hosts.Add(row);
            }
            row.Via = ConnectionsService.RouteLabel(h.Outbound);
            row.ViaBrush = h.Outbound switch
            {
                "warp-out" => Ui.Brush("AccentBrush"),
                "geo-out" => GeoBrush,
                _ => Ui.Brush("TextDimBrush"),
            };
            row.Conns = h.Connections.ToString();
            row.Down = Size(h.Down);
            // A bare IP cannot go into a domain list; the app rule is the way for those.
            row.ActionsVisibility = h.IsIp ? Visibility.Hidden : Visibility.Visible;
        }

        HostsTitle.Text = hosts.Count == 0
            ? "Куда ходит за этот сеанс"
            : $"Куда ходит за этот сеанс — {Count(hosts.Count, "адрес", "адреса", "адресов")}";
        HostsEmpty.Text = !ProcessService.IsRunning
            ? "Туннель не запущен — Nyx видит только тот трафик, который идёт через него."
            : "Пока ничего. Поработай в программе — адреса появятся здесь. Видно только то, что идёт через Nyx: весь трафик в режиме TUN, а без него — только то, что настроено на прокси.";
        HostsEmpty.Visibility = _hosts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void StyleButton(Button b, bool active)
        => b.Style = Ui.Style(active ? "PrimaryButton" : "SecondaryButton") ?? b.Style;

    // ------------------------------------------------------------ actions

    private void Pick_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new ProcessPickerDialog { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true || dlg.Picked == null) return;
        AddPicked(dlg.Picked.Exe, dlg.Picked.Path);
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Программа, для которой нужно правило",
            Filter = "Программы (*.exe)|*.exe",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        AddPicked(System.IO.Path.GetFileName(dlg.FileName), dlg.FileName);
    }

    private void AddPicked(string exe, string path)
    {
        _picked.Add(exe);
        if (!string.IsNullOrEmpty(path)) _paths[exe] = path;
        Refresh();
        AppsList.SelectedItem = _apps.FirstOrDefault(a => string.Equals(a.Exe, exe, StringComparison.OrdinalIgnoreCase));
        AppsList.ScrollIntoView(AppsList.SelectedItem);
        StatusText.Text = $"{exe}: выбери, куда отправлять её трафик";
    }

    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        TrafficRecorder.Clear();
        _hosts.Clear();
        Refresh();
        StatusText.Text = "История очищена. Правила не тронуты.";
    }

    private void RouteWarp_Click(object sender, RoutedEventArgs e) => SetRoute(RouteEditor.Warp);
    private void RouteGeo_Click(object sender, RoutedEventArgs e) => SetRoute(RouteEditor.Geo);
    private void RouteNone_Click(object sender, RoutedEventArgs e) => SetRoute(null);

    private void SetRoute(string? route)
    {
        var exe = SelectedExe;
        if (exe == null) return;
        try
        {
            RouteEditor.SetProcessRoute(exe, route);
            StatusText.Text = route switch
            {
                RouteEditor.Warp => $"{exe} → весь трафик через WARP",
                RouteEditor.Geo => $"{exe} → весь трафик через geo",
                _ => $"{exe}: правило снято, решают списки доменов",
            } + (ProcessService.IsRunning ? " · применится через секунду" : " · применится при запуске");
            ScheduleRestart();
            Refresh();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Не удалось изменить правило: " + ex.Message;
        }
    }

    private void HostWarp_Click(object sender, RoutedEventArgs e) => AddHost(sender, RouteEditor.Warp);
    private void HostGeo_Click(object sender, RoutedEventArgs e) => AddHost(sender, RouteEditor.Geo);

    private void AddHost(object sender, string route)
    {
        if (sender is not Button { Tag: string host }) return;
        try
        {
            RouteEditor.AddDomain(host, route);
            StatusText.Text = $"{host} добавлен в список {(route == RouteEditor.Geo ? "geo" : "WARP")}" +
                              (ProcessService.IsRunning ? " · применится через секунду" : "");
            ScheduleRestart();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Не удалось добавить: " + ex.Message;
        }
    }

    private void ScheduleRestart()
    {
        if (!ProcessService.IsRunning) return;
        _restart.Stop();
        _restart.Start();
    }

    // ------------------------------------------------------------ formatting

    private static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} Б",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} КБ",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.#} МБ",
        _ => $"{bytes / 1024.0 / 1024 / 1024:0.##} ГБ",
    };

    private static string Count(int n, string one, string few, string many)
    {
        var m10 = n % 10; var m100 = n % 100;
        var word = m10 == 1 && m100 != 11 ? one
                 : m10 is >= 2 and <= 4 && (m100 < 12 || m100 > 14) ? few
                 : many;
        return $"{n} {word}";
    }
}
