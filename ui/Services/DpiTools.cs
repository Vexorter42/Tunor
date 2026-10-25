using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Tunor.Services;

/// <summary>A program that changes traffic to get past a block, found running.</summary>
public sealed record DpiTool(string Name, bool PacketLevel, bool IsZapret);

/// <summary>
/// Programs that work against blocks by rewriting packets — zapret, GoodbyeDPI — and what they do to Tunor.
///
/// They hook the network with WinDivert, which sees the packets of every adapter, the virtual one that
/// Tunor's TUN mode makes included. Their tricks (a fake packet first, a split hello) are meant for the
/// provider's equipment, and when they are done to the traffic on its way into the tunnel, Tunor takes them for
/// real data and the connection breaks. It looks like "YouTube and Discord stopped opening while Tunor is on",
/// and the person cannot tell why, so the program says so.
///
/// Only the tools that work at the level of packets are a cause; one that is a SOCKS proxy (ByeDPI) touches nothing here.
/// </summary>
public static class DpiTools
{
    // process name (no .exe, any case) → what it is, whether it hooks packets, whether it is zapret
    private static readonly (string Process, string Name, bool PacketLevel, bool Zapret)[] Known =
    {
        ("winws", "zapret (winws.exe)", true, true),
        ("goodbyedpi", "GoodbyeDPI", true, false),
        ("nfqws", "zapret (nfqws)", false, true),
        ("tpws", "zapret (tpws)", false, true),
        ("ciadpi", "ByeDPI", false, false),
        ("byedpi", "ByeDPI", false, false),
        ("spoofdpi", "SpoofDPI", false, false),
    };

    /// <summary>The names of the processes that are running. A test puts its own.</summary>
    internal static Func<IEnumerable<string>> ProcessNames { get; set; } = () =>
    {
        var names = new List<string>();
        foreach (var p in Process.GetProcesses())
        {
            try { names.Add(p.ProcessName); } catch { }
            finally { p.Dispose(); }
        }
        return names;
    };

    private static List<DpiTool> _cache = new();
    private static DateTime _at = DateTime.MinValue;
    private static readonly TimeSpan Fresh = TimeSpan.FromSeconds(10);

    internal static void Forget() => _at = DateTime.MinValue;

    /// <summary>What is running now. The list of processes is not asked for more often than every ten seconds: the Home page asks on every tick.</summary>
    public static IReadOnlyList<DpiTool> Running()
    {
        lock (Known)
        {
            if (DateTime.UtcNow - _at < Fresh) return _cache;
            try
            {
                var names = new HashSet<string>(ProcessNames(), StringComparer.OrdinalIgnoreCase);
                _cache = Known.Where(k => names.Contains(k.Process))
                              .GroupBy(k => k.Name)
                              .Select(g => new DpiTool(g.Key, g.First().PacketLevel, g.First().Zapret))
                              .ToList();
            }
            catch { _cache = new List<DpiTool>(); }
            _at = DateTime.UtcNow;
            return _cache;
        }
    }

    /// <summary>
    /// What to tell a person who has TUN on and one of these running; null when there is nothing to tell. Pure.
    /// <paramref name="windows"/>: the advice is about WinDivert, which is Windows only.
    /// </summary>
    internal static string? Hint(bool tunOn, bool hidden, IReadOnlyList<DpiTool> running, bool windows)
    {
        if (!tunOn || hidden || !windows) return null;
        var causes = running.Where(t => t.PacketLevel).ToList();
        if (causes.Count == 0) return null;

        var names = string.Join(" и ", causes.Select(c => c.Name));
        var text = $"Запущен {names}, а Tunor работает в режиме TUN. Такие программы правят пакеты на всех сетевых адаптерах, "
                 + "в том числе на виртуальном адаптере Tunor (172.18.0.1), и поэтому часть сайтов — чаще всего YouTube и Discord — "
                 + "может перестать открываться, пока включены обе.";
        text += causes.Any(c => c.IsZapret)
            ? " Что сделать: в .bat-файле запуска zapret добавьте ключ --wf-iface=<номер адаптера>, где номер — вашего Wi-Fi или Ethernet "
              + "(его покажет команда netsh interface ipv4 show interfaces): тогда zapret не будет трогать адаптер Tunor."
            : " Что сделать: остановите эту программу на время работы Tunor.";
        text += " Другой путь — выключить TUN в «Настройках»: тогда программы пойдут через прокси, а zapret будет править только выход из Tunor. "
              + "Чтобы понять, доходит ли трафик до Tunor, снимите галочку «Только через туннель» на странице «Соединения».";
        return text;
    }

    /// <summary>The hint for this machine and these settings.</summary>
    public static string? Hint()
    {
        try
        {
            var s = SettingsService.Load();
            return Hint(s.Tun, s.HideDpiHint, Running(), OperatingSystem.IsWindows());
        }
        catch { return null; }
    }
}
