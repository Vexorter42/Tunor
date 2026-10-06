using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Tunor.Services;

namespace Tunor.Desktop.Services;

/// <summary>
/// What the engine said.
///
/// Until this existed the app started a process with its output redirected and then never
/// read it, which is the worst of both: the engine's explanation of why it would not run
/// went nowhere, and the pipe filled up. Lines are kept here, shown on the Logs page and
/// used to explain a failed start.
/// </summary>
public static class EngineLog
{
    /// <summary>How many lines to keep. The engine is chatty at trace level; this is a
    /// few minutes of it, and the file on disk has the rest.</summary>
    private const int Keep = 2000;

    private static readonly object Lock = new();
    private static readonly Queue<string> Lines = new();
    private static StreamWriter? _file;

    public static event EventHandler<string>? Line;

    public static string LogPath => Path.Combine(Paths.DataDir, "engine.log");

    public static void Add(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var stamped = $"{DateTime.Now:HH:mm:ss}  {Strip(text)}";
        lock (Lock)
        {
            Lines.Enqueue(stamped);
            while (Lines.Count > Keep) Lines.Dequeue();
            try { _file?.WriteLine(stamped); } catch { /* a full disk must not stop the app */ }
        }
        Line?.Invoke(null, stamped);
    }

    /// <summary>Everything kept, oldest first.</summary>
    public static string[] Snapshot()
    {
        lock (Lock) return Lines.ToArray();
    }

    public static void Clear()
    {
        lock (Lock) Lines.Clear();
        Line?.Invoke(null, "");
    }

    /// <summary>Starts a fresh file for one run of the engine.</summary>
    public static void BeginRun()
    {
        lock (Lock)
        {
            try
            {
                _file?.Dispose();
                Directory.CreateDirectory(Paths.DataDir);
                _file = new StreamWriter(LogPath, append: false) { AutoFlush = true };
            }
            catch { _file = null; }
        }
    }

    public static void EndRun()
    {
        lock (Lock)
        {
            try { _file?.Dispose(); } catch { }
            _file = null;
        }
    }

    /// <summary>The engine writes for a terminal; the colour codes are noise in a list.</summary>
    private static string Strip(string s) =>
        Regex.Replace(s, @"\x1B\[[0-9;]*[A-Za-z]", "").TrimEnd();

    /// <summary>
    /// The line that explains a failed start. The engine says what is wrong in its last
    /// FATAL before quitting; showing that beats "process exited".
    /// </summary>
    public static string? Reason()
    {
        var all = Snapshot();
        var fatal = all.LastOrDefault(l => l.Contains("FATAL", StringComparison.OrdinalIgnoreCase));
        if (fatal != null) return Tidy(fatal);
        var error = all.LastOrDefault(l => l.Contains("ERROR", StringComparison.OrdinalIgnoreCase));
        return error != null ? Tidy(error) : all.LastOrDefault();
    }

    /// <summary>Drops the timestamp and level so the sentence can be read on its own.</summary>
    private static string Tidy(string line)
    {
        var t = Regex.Replace(line, @"^\d{2}:\d{2}:\d{2}\s+", "");
        t = Regex.Replace(t, @"^\+\d{4}\s+\S+\s+\S+\s+", "");           // the engine's own stamp
        t = Regex.Replace(t, @"^(FATAL|ERROR|WARN|INFO)\s*(\[[^\]]*\])?\s*", "",
            RegexOptions.IgnoreCase);
        return t.Trim();
    }
}
