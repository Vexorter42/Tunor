using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Tunor.Services;

/// <summary>
/// Fetches something that lives on GitHub: from GitHub itself first, and through a mirror
/// if GitHub does not answer.
///
/// The program used to go through one mirror and nothing else. When that mirror — a free
/// proxy somebody else runs — stopped answering, nothing could be fetched at all: not an
/// update, not the rule lists, and every try hung for half a minute before failing.
/// GitHub itself was reachable the whole time. A mirror is a way round a block, not the
/// way to the files, so it comes second.
///
/// Going to GitHub first is also the safer order. The checksum in version.json is only as
/// good as the place version.json came from, and a mirror that serves both the manifest and
/// the file vouches for itself.
///
/// Nothing waits long for a dead address: twelve seconds for an answer to begin, twenty
/// without a byte once it has. What worked last is tried first next time in this run, so
/// where GitHub is blocked it costs one wait, not one per file.
///
/// An empty mirror setting means "no go-between": GitHub only, and no other mirror either.
/// </summary>
public static class GitHubFetch
{
    /// <summary>
    /// Nothing worked. <see cref="Unreachable"/> is true when no place even answered — the
    /// network is down, or GitHub and every mirror are — as opposed to answering "no such
    /// file", which says nothing about the next file.
    /// </summary>
    public sealed class FetchFailedException : HttpRequestException
    {
        public FetchFailedException(string message, bool unreachable) : base(message) => Unreachable = unreachable;
        public bool Unreachable { get; }
    }

    /// <summary>A place answered, and the answer was no.</summary>
    private sealed class HttpStatusException : HttpRequestException
    {
        public HttpStatusException(int code) : base($"HTTP {code}") { }
    }

    /// <summary>
    /// Tried after the configured mirror, because a mirror that was the default once is the
    /// one most likely to be dead by the time somebody needs it — and the setting lives in a
    /// file that is kept across upgrades, so it cannot be changed for people who already
    /// have it.
    /// </summary>
    internal static string[] BackupMirrors = { "https://gh-proxy.com/", "https://ghfast.top/" };

    internal static TimeSpan HeaderTimeout = TimeSpan.FromSeconds(12);   // until an answer begins
    internal static TimeSpan StallTimeout = TimeSpan.FromSeconds(20);    // without a byte, once it has

    private const string Direct = "GitHub";

    private static readonly HttpClient Client = new() { Timeout = Timeout.InfiniteTimeSpan };
    private static volatile string _lastGood = Direct;

    internal sealed record Way(string Name, string Url);

    /// <summary>Where to try, in order: what worked last, then GitHub, then the mirrors.</summary>
    internal static List<Way> Ways(string url, string? mirror)
    {
        var ways = new List<Way> { new(Direct, url) };
        if (string.IsNullOrWhiteSpace(mirror)) return ways;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in new[] { mirror }.Concat(BackupMirrors))
        {
            if (string.IsNullOrWhiteSpace(m)) continue;
            var prefix = m.Trim().TrimEnd('/') + "/";
            if (!seen.Add(prefix)) continue;
            ways.Add(new Way("зеркало " + HostOf(prefix), prefix + url));
        }

        var last = ways.FindIndex(w => w.Name == _lastGood);
        if (last > 0)
        {
            var w = ways[last];
            ways.RemoveAt(last);
            ways.Insert(0, w);
        }
        return ways;
    }

    /// <summary>Forgets what worked last. For the checks; a run of the program has no use for it.</summary>
    internal static void Forget() => _lastGood = Direct;

    /// <summary>A small file, whole, as text.</summary>
    public static async Task<string> GetStringAsync(string url, string? mirror, CancellationToken ct = default)
    {
        var bytes = await GetBytesAsync(url, mirror, ct).ConfigureAwait(false);
        return Encoding.UTF8.GetString(bytes).TrimStart('﻿');
    }

    /// <summary>A small file, whole.</summary>
    public static Task<byte[]> GetBytesAsync(string url, string? mirror, CancellationToken ct = default)
        => TryWays(url, mirror, async (u, token) =>
        {
            using var resp = await OpenAsync(u, token).ConfigureAwait(false);
            await using var body = await resp.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var ms = new MemoryStream();
            var buffer = new byte[16384];
            int n;
            while ((n = await ReadAsync(body, buffer, token).ConfigureAwait(false)) > 0)
                ms.Write(buffer, 0, n);
            return ms.ToArray();
        }, ct);

    /// <summary>
    /// A large file, to <paramref name="path"/>. A try that fails part-way leaves nothing
    /// behind and starts over from the next place, with the progress back at zero.
    /// </summary>
    public static Task DownloadAsync(string url, string? mirror, string path,
                                     IProgress<double>? progress = null, CancellationToken ct = default)
        => TryWays<bool>(url, mirror, async (u, token) =>
        {
            try
            {
                using var resp = await OpenAsync(u, token).ConfigureAwait(false);
                var total = resp.Content.Headers.ContentLength ?? -1;
                await using var src = await resp.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                await using var dst = File.Create(path);
                var buffer = new byte[81920];
                long read = 0;
                int n;
                progress?.Report(0);
                while ((n = await ReadAsync(src, buffer, token).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, n), token).ConfigureAwait(false);
                    read += n;
                    if (total > 0) progress?.Report((double)read / total);
                }
                if (total > 0 && read != total) throw new IOException($"получено {read} из {total} байт");
                return true;
            }
            catch
            {
                try { File.Delete(path); } catch { }
                throw;
            }
        }, ct);

    // ------------------------------------------------------------------

    private static async Task<T> TryWays<T>(string url, string? mirror,
        Func<string, CancellationToken, Task<T>> one, CancellationToken ct)
    {
        var why = new List<string>();
        var answered = false;
        foreach (var way in Ways(url, mirror))
        {
            try
            {
                var result = await one(way.Url, ct).ConfigureAwait(false);
                _lastGood = way.Name;
                return result;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                if (ex is HttpStatusException) answered = true;
                why.Add($"{way.Name}: {Reason(ex)}");
            }
        }
        throw new FetchFailedException(string.Join("; ", why), unreachable: !answered);
    }

    /// <summary>The answer's headers, or an exception that says what was wrong.</summary>
    private static async Task<HttpResponseMessage> OpenAsync(string url, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(HeaderTimeout);
        try
        {
            var resp = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, limit.Token)
                .ConfigureAwait(false);
            if (resp.IsSuccessStatusCode) return resp;
            var code = (int)resp.StatusCode;
            resp.Dispose();
            throw new HttpStatusException(code);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"нет ответа за {HeaderTimeout.TotalSeconds:0} с");
        }
    }

    /// <summary>One read, which gives up if it waits longer than <see cref="StallTimeout"/> for a byte.</summary>
    private static async Task<int> ReadAsync(Stream s, byte[] buffer, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(StallTimeout);
        try
        {
            return await s.ReadAsync(buffer.AsMemory(), limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"передача встала на {StallTimeout.TotalSeconds:0} с");
        }
    }

    private static string Reason(Exception ex) => ex switch
    {
        TimeoutException t => t.Message,
        HttpRequestException { InnerException: { } inner } => inner.Message,
        _ => ex.Message,
    };

    private static string HostOf(string prefix)
    {
        try { return new Uri(prefix).Host; }
        catch { return prefix; }
    }
}
