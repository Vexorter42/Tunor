using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Tunor.Services;

/// <summary>How long a server took to answer, or why it is not known.</summary>
public sealed record PingResult(int? Ms, string Note)
{
    public bool Answered => Ms != null;
}

/// <summary>
/// "Which of the servers of this subscription is the near one?" The time it takes to open a TCP connection to the
/// server's own port — the same thing a client does first, so it is the number that matters, where an ICMP ping
/// would be refused by many servers that work fine. Each server is tried three times and the middle result is taken,
/// because the first connection is often slower than the rest and one lost packet should not make a good server look dead.
///
/// It measures the way to the server from this computer, straight, not what the server does with the traffic after
/// that; and a server that speaks only UDP (the QUIC transport) cannot be measured this way, which is said and not guessed.
/// </summary>
public static class NodePing
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);
    private const int Samples = 3;
    private const int InParallel = 8;

    /// <summary>One connection to <c>address:port</c>: how many milliseconds, or null if it did not open. A test puts its own.</summary>
    internal static Func<IPAddress, int, TimeSpan, CancellationToken, Task<double?>> Connect { get; set; } = TcpConnect;

    /// <summary>Host name to addresses. A test puts its own.</summary>
    internal static Func<string, Task<IPAddress[]>> Resolve { get; set; } = host => Dns.GetHostAddressesAsync(host);

    /// <summary>Where the server is: the host and port out of its link; null if the link cannot be read.</summary>
    public static (string Host, int Port)? Target(SubNode node)
    {
        var parsed = ProxyLink.Parse(node.Url);
        if (parsed.Outbound is not JsonObject o) return null;
        var host = o["server"]?.GetValue<string>();
        var port = o["server_port"]?.GetValue<int>() ?? 0;
        return string.IsNullOrEmpty(host) || port is <= 0 or > 65535 ? null : (host, port);
    }

    /// <summary>Why this server cannot be timed by opening a connection; null if it can.</summary>
    public static string? NotMeasurable(SubNode node)
    {
        var parsed = ProxyLink.Parse(node.Url);
        if (parsed.Outbound == null) return "ссылка не читается";
        var transport = (parsed.Outbound["transport"] as JsonObject)?["type"]?.GetValue<string>();
        return string.Equals(transport, "quic", StringComparison.OrdinalIgnoreCase)
            ? "работает по UDP, по TCP не измерить"
            : null;
    }

    private static async Task<double?> TcpConnect(IPAddress address, int port, TimeSpan timeout, CancellationToken ct)
    {
        using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        var watch = Stopwatch.StartNew();
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, port), limit.Token);
            watch.Stop();
            return watch.Elapsed.TotalMilliseconds;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException)
        {
            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
            return null;
        }
    }

    /// <summary>The middle of the answers that came; null when none did.</summary>
    internal static int? Middle(IReadOnlyList<double> answers)
    {
        if (answers.Count == 0) return null;
        var s = answers.OrderBy(x => x).ToList();
        var mid = s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2;
        return Math.Max(1, (int)Math.Round(mid));
    }

    /// <param name="resolved">Addresses already looked up for this host, so a provider's twenty servers on one host cost one lookup.</param>
    private static async Task<PingResult> MeasureAsync(SubNode node, Dictionary<string, Task<IPAddress[]>> resolved, CancellationToken ct)
    {
        if (NotMeasurable(node) is { } why) return new PingResult(null, why);
        if (Target(node) is not { } target) return new PingResult(null, "ссылка не читается");

        IPAddress[] addresses;
        try
        {
            Task<IPAddress[]> lookup;
            lock (resolved)
            {
                if (!resolved.TryGetValue(target.Host, out lookup!))
                    resolved[target.Host] = lookup = IPAddress.TryParse(target.Host, out var literal)
                        ? Task.FromResult(new[] { literal })
                        : Resolve(target.Host);
            }
            addresses = await lookup;
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            return new PingResult(null, "имя сервера не находится");
        }
        if (addresses.Length == 0) return new PingResult(null, "имя сервера не находится");

        // IPv4 first: a server that has both answers faster over the one the whole world still mostly uses
        var address = addresses.OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1).First();

        var answers = new List<double>();
        for (var i = 0; i < Samples; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (await Connect(address, target.Port, Timeout, ct) is { } ms) answers.Add(ms);
            if (i < Samples - 1) await Task.Delay(120, ct);
        }
        return Middle(answers) is { } mid ? new PingResult(mid, "") : new PingResult(null, "нет ответа");
    }

    /// <summary>
    /// Times every server, some at a time. <paramref name="done"/> is told after each one with how many are done so far.
    /// A cancelled run returns what it has.
    /// </summary>
    public static async Task<Dictionary<SubNode, PingResult>> MeasureAllAsync(
        IReadOnlyList<SubNode> nodes, Action<int, int>? done = null, CancellationToken ct = default)
    {
        var results = new Dictionary<SubNode, PingResult>();
        var resolved = new Dictionary<string, Task<IPAddress[]>>(StringComparer.OrdinalIgnoreCase);
        var gate = new SemaphoreSlim(InParallel);
        var finished = 0;

        async Task One(SubNode node)
        {
            await gate.WaitAsync(ct);
            try
            {
                var r = await MeasureAsync(node, resolved, ct);
                lock (results) results[node] = r;
            }
            finally { gate.Release(); }
            done?.Invoke(Interlocked.Increment(ref finished), nodes.Count);
        }

        try { await Task.WhenAll(nodes.Select(One)); }
        catch (OperationCanceledException) { /* what has come is kept */ }
        lock (results) return new Dictionary<SubNode, PingResult>(results);
    }

    public static string Format(PingResult r) => r.Ms is { } ms ? $"{ms} мс" : r.Note.Length > 0 ? r.Note : "нет ответа";

    /// <summary>The servers with the nearest first; those that did not answer last, in the order the provider gave them.</summary>
    public static List<SubNode> Sorted(IReadOnlyList<SubNode> nodes, IReadOnlyDictionary<SubNode, PingResult> results)
        => nodes.Select((n, i) => (n, i))
                .OrderBy(x => results.TryGetValue(x.n, out var r) && r.Ms is { } ms ? ms : int.MaxValue)
                .ThenBy(x => x.i)
                .Select(x => x.n)
                .ToList();
}
