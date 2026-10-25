using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Tunor.Services;

/// <summary>A tunnel stopped answering, or started again. One of these covers every tunnel that changed in the same round.</summary>
public sealed record HealthEvent(DateTime At, IReadOnlyList<string> Routes, bool Down, TimeSpan? Downtime, string Text);

/// <summary>What the history says about one tunnel.</summary>
public sealed record RouteHealth(string Route, bool Known, bool Up, int LastMs, double UptimePercent, int Samples);

/// <summary>
/// Decides, from a stream of answers, when a tunnel has really stopped and when it is back. One missed
/// answer is nothing — a handshake that is late, a network that blinked — so it takes two in a row; and
/// what comes back is announced once, with how long it was out. No clocks and no network in here.
/// </summary>
internal sealed class HealthTracker
{
    private sealed class State { public int Fails; public DateTime FirstFail; public bool Down; public DateTime DownSince; }

    private readonly Dictionary<string, State> _s = new(StringComparer.OrdinalIgnoreCase);

    public int FailuresToDown { get; init; } = 2;

    /// <summary>What came of this answer: "down", "up", or nothing.</summary>
    public (string Route, bool Down, TimeSpan? Downtime)? Feed(string route, bool ok, DateTime now)
    {
        if (!_s.TryGetValue(route, out var st)) _s[route] = st = new State();

        if (ok)
        {
            var was = st.Down;
            var since = st.DownSince;
            st.Fails = 0;
            st.Down = false;
            return was ? (route, false, now - since) : null;
        }

        if (st.Fails == 0) st.FirstFail = now;
        st.Fails++;
        if (st.Down || st.Fails < FailuresToDown) return null;
        st.Down = true;
        st.DownSince = st.FirstFail;
        return (route, true, null);
    }

    /// <summary>Forgets everything: the engine was stopped or restarted, and what happens next starts from nothing.</summary>
    public void Reset() => _s.Clear();

    public bool IsDown(string route) => _s.TryGetValue(route, out var st) && st.Down;
}

/// <summary>
/// Watches the tunnels while the engine runs: every few minutes it asks each one the same question the
/// "Проверить" button does, keeps the answers for a week, and says so when one stops answering or is back —
/// the thing a person otherwise notices only when a page does not load, and by then the cause is gone.
/// The direct way is not watched: it fails whenever the provider throttles the test site, and that is not
/// the program's news.
/// </summary>
public static class HealthMonitor
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(3);
    /// <summary>After a miss the next look comes sooner, to tell a blink from an outage.</summary>
    public static readonly TimeSpan Confirm = TimeSpan.FromSeconds(25);
    public static readonly TimeSpan Keep = TimeSpan.FromDays(7);

    /// <summary>Raised on a thread of the pool; the front ends bring it to their own.</summary>
    public static event Action<HealthEvent>? Changed;

    /// <summary>Raised after every round, whatever it found, so a page showing the history can refresh.</summary>
    public static event Action? Updated;

    internal static Func<Task<List<PathResult>>> Probe { get; set; } = () => ConnectionCheck.RunAsync(log: false);
    internal static Func<string> FilePath { get; set; } = () => Path.Combine(Paths.DataDir, "health.jsonl");
    internal static Func<DateTime> Now { get; set; } = () => DateTime.UtcNow;

    private static readonly HealthTracker Tracker = new();
    private static readonly object Gate = new();
    private static CancellationTokenSource? _cts;

    /// <summary>Forgets what is known about who is down. For the tests, and for a restart of the engine.</summary>
    internal static void Reset() => Tracker.Reset();

    public static void Start()
    {
        lock (Gate)
        {
            if (_cts != null) return;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _ = Task.Run(() => Loop(token));
        }
    }

    public static void Stop()
    {
        lock (Gate) { _cts?.Cancel(); _cts = null; }
    }

    private static async Task Loop(CancellationToken ct)
    {
        try
        {
            Prune();
            // The engine has just started along with the program, and its tunnels need a moment.
            await Task.Delay(TimeSpan.FromSeconds(60), ct);
            while (!ct.IsCancellationRequested)
            {
                var wait = Interval;
                try
                {
                    if (EngineState.IsRunning && ConnectionCheck.Unavailable() == null && SettingsService.Load().HealthNotify)
                        wait = await CycleAsync();
                    else
                        Tracker.Reset();
                }
                catch (Exception ex) { AppLog.Write("мониторинг: " + ex.Message, problem: true); }
                await Task.Delay(wait, ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>One look at every tunnel; returns how long to wait before the next.</summary>
    internal static async Task<TimeSpan> CycleAsync()
    {
        var results = (await Probe()).Where(r => r.Status != "skip" && r.Name != ConnectionCheck.DirectName).ToList();
        var now = Now();
        var anyMiss = false;
        var changes = new List<(string Route, bool Down, TimeSpan? Downtime)>();

        foreach (var r in results)
        {
            var ok = r.Status == "ok";
            anyMiss |= !ok;
            Record(now, r.Name, ok, r.Ms);
            if (Tracker.Feed(r.Name, ok, now) is { } change) changes.Add(change);
        }

        foreach (var group in changes.GroupBy(c => c.Down))
        {
            var routes = group.Select(c => c.Route).ToList();
            var names = string.Join(", ", routes.Select(n => "«" + n + "»"));
            HealthEvent ev;
            if (group.Key)
                ev = new HealthEvent(now, routes, true, null,
                    routes.Count == 1 ? $"{names} перестал отвечать." : $"{names} перестали отвечать.");
            else
            {
                var gone = group.Max(c => c.Downtime ?? TimeSpan.Zero);
                ev = new HealthEvent(now, routes, false, gone,
                    (routes.Count == 1 ? $"{names} снова отвечает" : $"{names} снова отвечают") + $", перерыв — {Span(gone)}.");
            }
            Log(ev);
            Changed?.Invoke(ev);
        }

        Updated?.Invoke();
        return anyMiss ? Confirm : Interval;
    }

    internal static string Span(TimeSpan t) =>
        t.TotalSeconds < 90 ? $"{Math.Max(1, (int)t.TotalSeconds)} с"
        : t.TotalMinutes < 90 ? $"{(int)Math.Round(t.TotalMinutes)} мин"
        : $"{(int)t.TotalHours} ч {t.Minutes} мин";

    // ------------------------------------------------------------------ the history on disk

    /// <summary>A line of the history: an answer (<c>ok</c>) or a change (<c>e</c>, with its words).</summary>
    private sealed class Row
    {
        public DateTime t { get; set; }
        public string? r { get; set; }
        public bool? ok { get; set; }
        public int ms { get; set; }
        public string? e { get; set; }
        public string? text { get; set; }
    }

    private static void Record(DateTime at, string route, bool ok, int ms)
        => Append(new Row { t = at, r = route, ok = ok, ms = ms });

    private static void Log(HealthEvent ev)
    {
        Append(new Row { t = ev.At, e = ev.Down ? "down" : "up", r = string.Join(",", ev.Routes), text = ev.Text });
        AppLog.Write(ev.Text, problem: ev.Down);
    }

    private static void Append(Row row)
    {
        try
        {
            lock (Gate)
            {
                var path = FilePath();
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.AppendAllText(path, JsonSerializer.Serialize(row) + "\n", Encoding.UTF8);
            }
        }
        catch { /* the history is a courtesy */ }
    }

    private static List<Row> Read(TimeSpan span)
    {
        string[] lines;
        try
        {
            lock (Gate)
            {
                var path = FilePath();
                if (!File.Exists(path)) return new List<Row>();
                lines = File.ReadAllLines(path);
            }
        }
        catch { return new List<Row>(); }

        var cut = Now() - span;
        var rows = new List<Row>();
        foreach (var line in lines)
        {
            try
            {
                var row = JsonSerializer.Deserialize<Row>(line);
                if (row != null && row.t.ToUniversalTime() >= cut) rows.Add(row);
            }
            catch { /* a torn line from a crash is not worth stopping for */ }
        }
        return rows;
    }

    /// <summary>Drops what is older than a week; once at the start is enough for a file that grows by a few kilobytes an hour.</summary>
    internal static void Prune()
    {
        try
        {
            lock (Gate)
            {
                var path = FilePath();
                if (!File.Exists(path)) return;
                var kept = Read(Keep).Select(r => JsonSerializer.Serialize(r)).ToList();
                File.WriteAllLines(path, kept, Encoding.UTF8);
            }
        }
        catch { }
    }

    /// <summary>Per tunnel: is it answering now, and for what part of the last <paramref name="span"/>.</summary>
    public static List<RouteHealth> Summary(TimeSpan span)
        => Read(span).Where(r => r.ok != null && r.r != null)
            .GroupBy(r => r.r!)
            .Select(g =>
            {
                var last = g.Last();
                return new RouteHealth(g.Key, true, last.ok == true, last.ms,
                    Math.Round(100.0 * g.Count(x => x.ok == true) / g.Count(), 1), g.Count());
            })
            .OrderBy(r => r.Route, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    /// <summary>The latest outages and returns, newest first.</summary>
    public static List<(DateTime At, bool Down, string Text)> Events(TimeSpan span, int count)
        => Read(span).Where(r => r.e != null)
            .Select(r => (r.t.ToLocalTime(), r.e == "down", r.text ?? ""))
            .Reverse().Take(count).ToList();
}
