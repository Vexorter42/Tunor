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

public sealed class ConnRow : INotifyPropertyChanged
{
    public string Id { get; init; } = "";
    public DateTime Start { get; init; }

    private string _host = "", _route = "", _process = "", _down = "", _up = "", _age = "", _detail = "";
    private Brush _routeBrush = Brushes.Gray;

    public string Host { get => _host; set => Set(ref _host, value); }
    public string Route { get => _route; set => Set(ref _route, value); }
    public Brush RouteBrush { get => _routeBrush; set => Set(ref _routeBrush, value); }
    public string Process { get => _process; set => Set(ref _process, value); }
    public string Down { get => _down; set => Set(ref _down, value); }
    public string Up { get => _up; set => Set(ref _up, value); }
    public string Age { get => _age; set => Set(ref _age, value); }
    public string Detail { get => _detail; set => Set(ref _detail, value); }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>
/// Live view of the engine's connections: where each one went (WARP, geo, direct) and
/// which rule sent it there. This is what answers "is this site actually using the
/// tunnel?" — previously only guessable from the rule lists.
/// </summary>
public partial class ConnectionsPage : UserControl
{
    private const int MaxRows = 400;

    private readonly ObservableCollection<ConnRow> _rows = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _busy;

    private long _lastUp, _lastDown;
    private DateTime _lastAt;

    private static readonly Brush GeoBrush = new SolidColorBrush(Color.FromRgb(0x6E, 0xA8, 0xFE));

    public ConnectionsPage()
    {
        InitializeComponent();
        List.ItemsSource = _rows;
        _timer.Tick += async (_, _) => await RefreshAsync();

        // Poll only while the page is on screen; nobody is looking otherwise.
        Loaded += async (_, _) => { _timer.Start(); await RefreshAsync(); };
        Unloaded += (_, _) => _timer.Stop();
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        _rows.Clear();   // rebuild from the next snapshot under the new filter
        _ = RefreshAsync();
    }

    private async System.Threading.Tasks.Task RefreshAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            if (!ProcessService.IsRunning)
            {
                ShowEmpty("Туннель не запущен.");
                ResetSpeed();
                return;
            }

            var snap = await ConnectionsService.FetchAsync();
            if (snap == null)
            {
                // Typically right after an update: the running engine was started with the
                // previous config, which had no stats API yet.
                ShowEmpty("Статистика недоступна. Перезапусти туннель на «Главной» — " +
                          "после этого соединения появятся здесь.");
                ResetSpeed();
                return;
            }

            UpdateSpeed(snap);
            UpdateRows(snap);
        }
        finally { _busy = false; }
    }

    private void UpdateSpeed(ConnSnapshot snap)
    {
        var now = DateTime.UtcNow;
        if (_lastAt != default)
        {
            var secs = Math.Max(0.2, (now - _lastAt).TotalSeconds);
            // Counters reset when the engine restarts; never show a negative speed.
            DownSpeed.Text = Size(Math.Max(0, snap.DownloadTotal - _lastDown) / secs) + "/с";
            UpSpeed.Text = Size(Math.Max(0, snap.UploadTotal - _lastUp) / secs) + "/с";
        }
        _lastDown = snap.DownloadTotal;
        _lastUp = snap.UploadTotal;
        _lastAt = now;

        Totals.Text = $"↓ {Size(snap.DownloadTotal)}   ↑ {Size(snap.UploadTotal)}";
        ActiveCount.Text = snap.Connections.Count.ToString();
    }

    private void ResetSpeed()
    {
        _lastAt = default;
        DownSpeed.Text = UpSpeed.Text = Totals.Text = ActiveCount.Text = "—";
        _rows.Clear();
    }

    private void UpdateRows(ConnSnapshot snap)
    {
        var q = FilterBox.Text.Trim();
        var tunnelOnly = TunnelOnly.IsChecked == true;

        var wanted = snap.Connections
            .Where(c => !tunnelOnly || c.Outbound is "warp-out" or "geo-out")
            .Where(c => q.Length == 0
                        || c.Host.Contains(q, StringComparison.OrdinalIgnoreCase)
                        || c.Process.Contains(q, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(c => c.Start)
            .Take(MaxRows)
            .ToList();

        // Update in place rather than rebuild: rebuilding every second would reset the
        // scroll position and make the list flicker.
        var ids = new HashSet<string>(wanted.Select(c => c.Id));
        for (var i = _rows.Count - 1; i >= 0; i--)
            if (!ids.Contains(_rows[i].Id)) _rows.RemoveAt(i);

        var byId = _rows.ToDictionary(r => r.Id);
        for (var i = 0; i < wanted.Count; i++)
        {
            var c = wanted[i];
            if (!byId.TryGetValue(c.Id, out var row))
            {
                row = new ConnRow { Id = c.Id, Start = c.Start };
                _rows.Insert(Math.Min(i, _rows.Count), row);   // newest go on top
            }
            Fill(row, c);
        }

        if (_rows.Count == 0)
            ShowEmpty(snap.Connections.Count == 0 ? "Активных соединений нет." : "Под фильтр ничего не попало.");
        else
            EmptyText.Visibility = Visibility.Collapsed;
    }

    private void Fill(ConnRow row, ConnInfo c)
    {
        row.Host = c.Host;
        row.Route = ConnectionsService.RouteLabel(c.Outbound);
        row.RouteBrush = c.Outbound switch
        {
            "warp-out" => (Brush)FindResource("AccentBrush"),
            "geo-out" => GeoBrush,
            _ => (Brush)FindResource("TextDimBrush"),
        };
        row.Process = c.Process;
        row.Down = Size(c.Download);
        row.Up = Size(c.Upload);
        row.Age = Age(DateTime.Now - c.Start);
        row.Detail = $"{c.Host} · {c.Network} · правило: {(string.IsNullOrEmpty(c.Rule) ? "—" : c.Rule)}";
    }

    private void ShowEmpty(string text)
    {
        EmptyText.Text = text;
        EmptyText.Visibility = Visibility.Visible;
    }

    private static string Size(double bytes) => bytes switch
    {
        < 1024 => $"{bytes:0} Б",
        < 1024 * 1024 => $"{bytes / 1024:0.#} КБ",
        < 1024L * 1024 * 1024 => $"{bytes / 1024 / 1024:0.#} МБ",
        _ => $"{bytes / 1024 / 1024 / 1024:0.##} ГБ",
    };

    private static string Age(TimeSpan t) =>
        t.TotalSeconds < 60 ? $"{Math.Max(0, (int)t.TotalSeconds)} с"
        : t.TotalMinutes < 60 ? $"{(int)t.TotalMinutes} мин"
        : $"{(int)t.TotalHours} ч";
}
