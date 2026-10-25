using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Tunor.Services;

/// <summary>
/// The program's log, on disk: what the engine says and what the app does about it, one file a day.
///
/// Until this existed the log lived on the Logs page and nowhere else. Close the program, restart
/// the engine, wake the computer — and what had been said was gone, which is the moment someone
/// asks "why did it drop an hour ago". A drop of a tunnel every few minutes could not be looked
/// into at all, because by the time the report was made the log began after it.
///
/// A file per day, <c>logs/tunor-2026-10-25.log</c>; when one passes <see cref="MaxBytes"/> it is
/// moved to <c>.1.log</c> and a new one is begun, so a day takes at most twice that; files older
/// than <see cref="KeepDays"/> are removed. Writing is done on a timer, off the thread that reads
/// the engine's output: the engine can say hundreds of lines a second when a network fails, and
/// waiting for the disk on each would slow the pipe it is writing into.
/// </summary>
public static class AppLog
{
    internal static long MaxBytes = 5L * 1024 * 1024;
    internal static int KeepDays = 7;

    /// <summary>Where the files go. Replaceable, so that a check does not write beside the real ones.</summary>
    internal static Func<string> DirectoryOf = () => Path.Combine(Paths.AppRoot, "logs");

    public static string Dir => DirectoryOf();

    private static readonly ConcurrentQueue<string> Queue = new();
    private static readonly object Gate = new();
    private static Timer? _timer;
    private static bool _pruned;
    private static int _hooked;

    /// <summary>Adds a line, stamped with the time. Never throws and never waits for the disk.</summary>
    public static void Write(string text, bool problem = false)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        Queue.Enqueue($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {(problem ? "[err]" : "     ")} {text.TrimEnd()}");

        if (Interlocked.Exchange(ref _hooked, 1) == 0)
        {
            // Whatever is still waiting when the program ends is written then, not lost.
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Flush();
            _timer = new Timer(_ => Flush(), null, 500, 500);
        }
    }

    /// <summary>Writes what is waiting. The timer does it every half second; a caller that is about to read the file does it first.</summary>
    public static void Flush()
    {
        if (Queue.IsEmpty) return;
        lock (Gate)
        {
            try
            {
                var sb = new StringBuilder();
                while (Queue.TryDequeue(out var line)) sb.AppendLine(line);
                if (sb.Length == 0) return;

                var dir = Dir;
                Directory.CreateDirectory(dir);
                if (!_pruned) { _pruned = true; Prune(dir); }

                var path = TodayFile(dir);
                RollIfFull(path);
                File.AppendAllText(path, sb.ToString(), new UTF8Encoding(false));
            }
            catch { /* a full disk or a locked file must not take the program down with it */ }
        }
    }

    private static string TodayFile(string dir) => Path.Combine(dir, $"tunor-{DateTime.Now:yyyy-MM-dd}.log");

    private static void RollIfFull(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length < MaxBytes) return;
            File.Move(path, Path.ChangeExtension(path, ".1.log"), overwrite: true);
        }
        catch { }
    }

    internal static void Prune(string dir)
    {
        try
        {
            var limit = DateTime.Now.AddDays(-KeepDays);
            foreach (var f in Directory.EnumerateFiles(dir, "tunor-*.log"))
                if (File.GetLastWriteTime(f) < limit) { try { File.Delete(f); } catch { } }
        }
        catch { }
    }

    /// <summary>The newest files first: today's, the one rolled out of today's, then the days before.</summary>
    internal static IEnumerable<string> Files()
    {
        try
        {
            var dir = Dir;
            if (!Directory.Exists(dir)) return Array.Empty<string>();
            return Directory.EnumerateFiles(dir, "tunor-*.log")
                .OrderByDescending(f => Path.GetFileName(f).Replace(".1.log", ".0.log"), StringComparer.Ordinal)
                .ToList();
        }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>
    /// The last <paramref name="count"/> lines that <paramref name="keep"/> lets through (all if
    /// null), oldest first, reaching back through the days as far as it takes.
    /// </summary>
    public static string[] Tail(int count, Func<string, bool>? keep = null)
    {
        Flush();
        var found = new List<string>();
        foreach (var file in Files())
        {
            if (found.Count >= count) break;
            string[] lines;
            try { lines = File.ReadAllLines(file); } catch { continue; }
            for (var i = lines.Length - 1; i >= 0 && found.Count < count; i--)
                if (keep == null || keep(lines[i])) found.Add(lines[i]);
        }
        found.Reverse();
        return found.ToArray();
    }

    /// <summary>
    /// What may go into a report that is sent to someone: the program's own lines, the engine's
    /// warnings and errors, and what the tunnels say about their handshakes. Not the rest — at the
    /// "info" and "debug" levels the engine writes every site that is opened.
    /// </summary>
    public static bool IsSafeForReport(string line)
    {
        // "2026-10-25 12:00:00.000 [err] text" — the text begins after the stamp and the mark
        var body = line.Length > 30 ? line[30..].TrimStart() : line;
        return body.StartsWith("[ui]", StringComparison.Ordinal)
            || body.StartsWith("===", StringComparison.Ordinal)
            || body.Contains("ERROR", StringComparison.Ordinal)
            || body.Contains("WARN", StringComparison.Ordinal)
            || body.Contains("FATAL", StringComparison.Ordinal)
            || body.Contains("endpoint/wireguard", StringComparison.Ordinal)
            || body.Contains("health:", StringComparison.Ordinal);
    }
}
