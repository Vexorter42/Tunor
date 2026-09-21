using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace Nyx.Services;

public sealed class HostStat
{
    public string Host { get; set; } = "";
    public bool IsIp { get; set; }
    /// <summary>The outbound the last connection to this host went through.</summary>
    public string Outbound { get; set; } = "";
    public int Connections { get; set; }
    public long Down { get; set; }
    public long Up { get; set; }
    public DateTime LastSeen { get; set; }
}

public sealed class AppStat
{
    public string Exe { get; set; } = "";
    public string Path { get; set; } = "";
    public long Down { get; set; }
    public long Up { get; set; }
    public DateTime LastSeen { get; set; }
    public List<HostStat> Hosts { get; set; } = new();
}

/// <summary>
/// Remembers, per program, every address it talked to during this session and which
/// tunnel carried it. The live connection list forgets a connection the moment it
/// closes — and most last a second or two — so "what does this app connect to?" needs
/// a record, not a snapshot. Sampled from the engine's stats API every 1.5 s, which
/// catches anything that stays open that long.
/// </summary>
public static class TrafficRecorder
{
    private const int MaxApps = 300;
    private const int MaxHostsPerApp = 500;

    private sealed class AppData
    {
        public string Exe = "", Path = "";
        public long Down, Up;
        public DateTime LastSeen;
        public readonly Dictionary<string, HostStat> Hosts = new(StringComparer.OrdinalIgnoreCase);
    }

    private static readonly Dictionary<string, AppData> _apps = new(StringComparer.OrdinalIgnoreCase);
    // Bytes last seen per live connection, to turn cumulative counters into deltas.
    private static readonly Dictionary<string, (long down, long up)> _last = new();
    private static readonly object _lock = new();
    private static Timer? _timer;
    private static int _busy;

    public static void Start()
    {
        _timer ??= new Timer(_ => _ = SampleAsync(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1.5));
    }

    private static async System.Threading.Tasks.Task SampleAsync()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;   // never overlap samples
        try
        {
            var snap = await ConnectionsService.FetchAsync();
            if (snap == null) return;
            var now = DateTime.Now;

            lock (_lock)
            {
                var live = new HashSet<string>();
                foreach (var c in snap.Connections)
                {
                    live.Add(c.Id);
                    if (string.IsNullOrEmpty(c.ProcessPath) || string.IsNullOrEmpty(c.Target)) continue;

                    var exe = System.IO.Path.GetFileName(c.ProcessPath);
                    if (!_apps.TryGetValue(exe, out var app))
                    {
                        if (_apps.Count >= MaxApps) continue;
                        app = _apps[exe] = new AppData { Exe = exe, Path = c.ProcessPath };
                    }

                    if (!app.Hosts.TryGetValue(c.Target, out var h))
                    {
                        if (app.Hosts.Count >= MaxHostsPerApp) continue;
                        h = app.Hosts[c.Target] = new HostStat { Host = c.Target, IsIp = c.TargetIsIp };
                    }

                    // A connection id seen for the first time is a new connection.
                    var isNew = !_last.TryGetValue(c.Id, out var prev);
                    if (isNew) h.Connections++;
                    var dDown = isNew || c.Download < prev.down ? c.Download : c.Download - prev.down;
                    var dUp = isNew || c.Upload < prev.up ? c.Upload : c.Upload - prev.up;
                    _last[c.Id] = (c.Download, c.Upload);

                    h.Down += dDown; h.Up += dUp;
                    h.Outbound = c.Outbound;
                    h.LastSeen = now;
                    app.Down += dDown; app.Up += dUp;
                    app.LastSeen = now;
                }

                // Closed connections: forget their counters.
                foreach (var id in _last.Keys.Where(id => !live.Contains(id)).ToList())
                    _last.Remove(id);
            }
        }
        catch { /* the next sample will try again */ }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    /// <summary>A copy that the UI can read without holding the lock.</summary>
    public static List<AppStat> Snapshot()
    {
        lock (_lock)
        {
            return _apps.Values.Select(a => new AppStat
            {
                Exe = a.Exe,
                Path = a.Path,
                Down = a.Down,
                Up = a.Up,
                LastSeen = a.LastSeen,
                Hosts = a.Hosts.Values.Select(h => new HostStat
                {
                    Host = h.Host, IsIp = h.IsIp, Outbound = h.Outbound,
                    Connections = h.Connections, Down = h.Down, Up = h.Up, LastSeen = h.LastSeen,
                }).ToList(),
            }).ToList();
        }
    }

    public static void Clear()
    {
        lock (_lock) { _apps.Clear(); _last.Clear(); }
    }
}
