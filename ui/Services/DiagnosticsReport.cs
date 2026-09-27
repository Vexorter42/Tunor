using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Nyx.Services;

/// <summary>
/// Everything needed to understand someone else's broken install, in one file they can
/// send. What it must never contain: tunnel keys, the stats API secret, and the contents
/// of personal rule lists — so rules are reported by shape and size, the .conf files are
/// described rather than quoted, and whatever slips through is caught by a last redaction
/// pass over the finished text.
/// </summary>
public static class DiagnosticsReport
{
    /// <summary>A WireGuard key as it appears in a config or in the generated JSON.</summary>
    private static readonly Regex KeyLike = new(@"(?<![A-Za-z0-9+/])[A-Za-z0-9+/_-]{42,44}=(?![A-Za-z0-9+/])", RegexOptions.Compiled);

    /// <summary>The clash API secret: 32 hex characters, on its own.</summary>
    private static readonly Regex SecretLike = new(@"(?<![0-9a-fA-F])[0-9a-fA-F]{32}(?![0-9a-fA-F])", RegexOptions.Compiled);

    public static string SuggestedFileName => $"nyx-отчёт-{DateTime.Now:yyyy-MM-dd_HH-mm}.txt";

    /// <param name="logTail">The log as the Logs page holds it; may be empty.</param>
    /// <param name="runCheck">Probe the three paths too. Takes up to half a minute.</param>
    public static async Task<string> BuildAsync(string logTail, bool runCheck)
    {
        var sb = new StringBuilder();

        Head(sb, "ОТЧЁТ NYX");
        sb.AppendLine("Этот файл можно отправлять как есть: ключи туннелей, секрет API статистики");
        sb.AppendLine("и содержимое списков правил в него не попадают — только их количество.");
        sb.AppendLine();

        App(sb);
        await EngineAsync(sb);
        Tunnels(sb);
        Settings(sb);
        Config(sb);
        Rules(sb);
        await AdapterAsync(sb);
        if (runCheck) await CheckAsync(sb);
        Crash(sb);
        LogSection(sb, logTail);

        return Redact(sb.ToString());
    }

    // ------------------------------------------------------------ sections

    private static void App(StringBuilder sb)
    {
        Head(sb, "ПРИЛОЖЕНИЕ");
        Line(sb, "Nyx", UpdateService.CurrentVersionString);
        Line(sb, "Файл", Paths.UiExe);
        Line(sb, "Папка", Paths.AppRoot);
        Line(sb, "Права администратора", IsElevated() ? "да" : "нет");
        Line(sb, "Windows", Environment.OSVersion.VersionString);
        Line(sb, ".NET", Environment.Version.ToString());
        Line(sb, "Время отчёта", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        try { Line(sb, "Зеркало обновлений", UpdateConfig.Load().Mirror); } catch { }
        sb.AppendLine();
    }

    private static async Task EngineAsync(StringBuilder sb)
    {
        Head(sb, "ДВИЖОК");
        var exe = Paths.SingBoxExe;
        if (File.Exists(exe))
        {
            var fi = new FileInfo(exe);
            Line(sb, "Файл", exe);
            Line(sb, "Размер", $"{fi.Length:N0} байт");
            Line(sb, "Изменён", fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"));
            Line(sb, "sha256", await Task.Run(() => Sha256(exe)));
        }
        else
        {
            Line(sb, "Файл", "НЕ НАЙДЕН: " + exe);
        }

        Line(sb, "Туннель запущен", ProcessService.IsRunning ? "да" : "нет");
        if (ProcessService.LastFailure is { } failure)
            Line(sb, "Последняя ошибка", failure);

        // Other apps ship sing-box too (Hiddify, v2rayN). Nyx only owns the one in its
        // own folder, and mixing them up has caused trouble before — so list both.
        foreach (var p in Process.GetProcessesByName("sing-box"))
        {
            string? path = null;
            var started = "?";
            try { path = p.MainModule?.FileName; } catch { /* another user's, or elevated */ }
            try { started = p.StartTime.ToString("yyyy-MM-dd HH:mm:ss"); } catch { }

            // Without the path there is no way to tell whose engine this is — say that
            // rather than guess, or a report turns a neighbour's tunnel into a culprit.
            var whose = path == null ? "чей — не видно (нет доступа к процессу)"
                      : string.Equals(path, exe, StringComparison.OrdinalIgnoreCase) ? "наш"
                      : "чужой (другое приложение)";
            Line(sb, $"Процесс pid {p.Id}", $"{whose} · запущен {started}{(path != null ? " · " + path : "")}");
            p.Dispose();
        }
        sb.AppendLine();
    }

    private static void Tunnels(StringBuilder sb)
    {
        Head(sb, "ТУННЕЛИ");
        Tunnel(sb, "warp.conf", Paths.WarpConf, ConfigGenerator.WarpState);
        Tunnel(sb, "geo.conf", Paths.GeoConf, ConfigGenerator.GeoState);
        sb.AppendLine();
    }

    private static void Tunnel(StringBuilder sb, string name, string path, ConfigGenerator.ConfState state)
    {
        var status = state.Problem != null ? "НЕ ГОДЕН: " + state.Problem
                   : state.Placeholder ? "не заполнен (заглушка из установщика)"
                   : state.Usable ? "в порядке"
                   : "не готов";
        Line(sb, name, status);

        if (!File.Exists(path)) { Line(sb, "  файл", "отсутствует"); return; }
        try
        {
            // Detect reads the file but reports only what is safe to show.
            var d = ConfImporter.Detect(path);
            Line(sb, "  определён как", $"{(d.Kind == ConfKind.Warp ? "WARP" : "geo")} — {d.Reason}");
            Line(sb, "  эндпоинт", string.IsNullOrEmpty(d.Endpoint) ? "нет" : d.Endpoint);
            Line(sb, "  адрес в туннеле", string.IsNullOrEmpty(d.Address) ? "нет" : d.Address);
            Line(sb, "  AmneziaWG", d.AwgVersions);
        }
        catch (Exception ex) { Line(sb, "  разбор", "не удался: " + ex.Message); }
    }

    private static void Settings(StringBuilder sb)
    {
        Head(sb, "НАСТРОЙКИ");
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(Paths.SettingsJson));
            if (node is JsonObject o)
            {
                if (o.ContainsKey("controllerSecret")) o["controllerSecret"] = "<скрыт>";
                // Default JSON escaping turns "<" and every Cyrillic letter into \uXXXX,
                // which is unreadable in a report a person is meant to read.
                sb.AppendLine(o.ToJsonString(new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                }));
            }
        }
        catch (Exception ex) { Line(sb, "settings.json", "не прочитан: " + ex.Message); }
        sb.AppendLine();
    }

    private static void Config(StringBuilder sb)
    {
        Head(sb, "CONFIG.JSON (структура, без личных списков)");
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(Paths.ConfigJson))?.AsObject();
            if (root == null) { Line(sb, "config.json", "пуст"); sb.AppendLine(); return; }

            Line(sb, "Размер", $"{new FileInfo(Paths.ConfigJson).Length:N0} байт");
            var stamp = Paths.ConfigJson + ".version";
            if (File.Exists(stamp)) Line(sb, "Собран версией", File.ReadAllText(stamp).Trim());

            var inbounds = root["inbounds"]?.AsArray() ?? new JsonArray();
            Line(sb, "Входы", string.Join(", ", inbounds.Select(i =>
                $"{i?["type"]}{(i?["listen_port"] != null ? ":" + i!["listen_port"] : "")}")));

            var outbounds = root["outbounds"]?.AsArray() ?? new JsonArray();
            Line(sb, "Выходы", string.Join(", ", outbounds.Select(o => $"{o?["tag"]} ({o?["type"]})")));

            var endpoints = root["endpoints"]?.AsArray() ?? new JsonArray();
            Line(sb, "Туннели в конфиге", endpoints.Count == 0
                ? "нет"
                : string.Join(", ", endpoints.Select(e => $"{e?["tag"]} → {e?["peers"]?[0]?["address"]}:{e?["peers"]?[0]?["port"]}")));

            var route = root["route"]?.AsObject();
            Line(sb, "Маршрут по умолчанию", route?["final"]?.ToString() ?? "нет");
            Line(sb, "find_process", route?["find_process"]?.ToString() ?? "нет");
            Line(sb, "Правил в маршруте", (route?["rules"]?.AsArray().Count ?? 0).ToString());

            var sets = route?["rule_set"]?.AsArray() ?? new JsonArray();
            Line(sb, "Наборов правил", sets.Count.ToString());
            foreach (var s in sets)
            {
                var type = s?["type"]?.ToString();
                var path = s?["path"]?.ToString();
                var where = type == "local"
                    ? $"{path} — файл {(File.Exists(Path.Combine(Paths.AppRoot, path ?? "")) ? "есть" : "ОТСУТСТВУЕТ")}"
                    : type == "remote" ? "по сети" : "встроенный";
                Line(sb, $"  {s?["tag"]}", $"{type} · {where}");
            }

            var clash = root["experimental"]?["clash_api"]?.AsObject();
            Line(sb, "API статистики", clash?["external_controller"]?.ToString() ?? "выключен");
        }
        catch (Exception ex) { Line(sb, "config.json", "не разобран: " + ex.Message); }
        sb.AppendLine();
    }

    private static void Rules(StringBuilder sb)
    {
        Head(sb, "ГРУППЫ ПРАВИЛ (только состав, без самих доменов)");
        try
        {
            foreach (var g in RulesService.Load())
            {
                var extra = g.IsInline
                    ? $"{Plural(g.Items.Count, "запись", "записи", "записей")} " +
                      $"({(g.ItemKind == RuleItemKind.ProcessName ? "процессы" : "домены")})"
                    : FileState(g.Path);
                Line(sb, g.Tag, $"{g.Type} · {extra}");
            }
        }
        catch (Exception ex) { Line(sb, "rules.json", "не прочитан: " + ex.Message); }
        sb.AppendLine();
    }

    private static string FileState(string? relative)
    {
        if (string.IsNullOrEmpty(relative)) return "путь не задан";
        var full = Path.Combine(Paths.AppRoot, relative);
        if (!File.Exists(full)) return $"{relative} — ФАЙЛ ОТСУТСТВУЕТ (нужно обновить списки)";
        var fi = new FileInfo(full);
        return $"{relative} — {fi.Length:N0} байт, обновлён {fi.LastWriteTime:yyyy-MM-dd}";
    }

    private static async Task AdapterAsync(StringBuilder sb)
    {
        Head(sb, "СЕТЕВОЙ АДАПТЕР ТУННЕЛЯ");
        try
        {
            var script =
                "Get-NetAdapter -IncludeHidden -ErrorAction SilentlyContinue | " +
                "Where-Object { $_.InterfaceDescription -like '*sing-tun*' -or $_.InterfaceDescription -like '*wintun*' } | " +
                "ForEach-Object { \"$($_.Name) | $($_.InterfaceDescription) | $($_.Status)\" }";

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-Command", script })
                psi.ArgumentList.Add(a);

            using var p = Process.Start(psi);
            if (p == null) { Line(sb, "Запрос", "не выполнен"); sb.AppendLine(); return; }
            var stdout = await p.StandardOutput.ReadToEndAsync();
            p.WaitForExit(10000);
            sb.AppendLine(string.IsNullOrWhiteSpace(stdout) ? "  адаптеров sing-tun / wintun нет" : stdout.TrimEnd());
        }
        catch (Exception ex) { Line(sb, "Запрос", "не удался: " + ex.Message); }
        sb.AppendLine();
    }

    private static async Task CheckAsync(StringBuilder sb)
    {
        Head(sb, "ПРОВЕРКА СОЕДИНЕНИЯ");
        var why = ConnectionCheck.Unavailable();
        if (why != null) { sb.AppendLine("  не выполнялась: " + why); sb.AppendLine(); return; }
        try
        {
            foreach (var r in await ConnectionCheck.RunAsync())
                Line(sb, r.Name, r.Status == "ok"
                    ? $"ок · {r.Ip} · {r.Country} · {r.Ms} мс{(r.ViaWarp ? " · Cloudflare видит WARP" : "")}"
                    : $"{r.Status} · {r.Note}");
        }
        catch (Exception ex) { sb.AppendLine("  не удалась: " + ex.Message); }
        sb.AppendLine();
    }

    private static void Crash(StringBuilder sb)
    {
        var crash = Path.Combine(Paths.AppRoot, "crash.log");
        if (!File.Exists(crash)) return;
        Head(sb, "CRASH.LOG (последние строки)");
        try
        {
            var lines = File.ReadAllLines(crash);
            foreach (var l in lines.Skip(Math.Max(0, lines.Length - 40))) sb.AppendLine(l);
        }
        catch (Exception ex) { sb.AppendLine("  не прочитан: " + ex.Message); }
        sb.AppendLine();
    }

    private static void LogSection(StringBuilder sb, string logTail)
    {
        Head(sb, "ЛОГ");
        sb.AppendLine(string.IsNullOrWhiteSpace(logTail)
            ? "  пусто — раздел «Логи» в этом запуске ничего не получил (движок не запускался?)"
            : logTail.TrimEnd());
        sb.AppendLine();
    }

    // ------------------------------------------------------------- helpers

    private static void Head(StringBuilder sb, string title)
    {
        sb.AppendLine(new string('=', 62));
        sb.AppendLine(title);
        sb.AppendLine(new string('=', 62));
    }

    private static void Line(StringBuilder sb, string name, string value)
        => sb.AppendLine($"{name,-24}{value}");

    private static string Plural(int n, string one, string few, string many)
    {
        var tail = n % 10;
        var hundred = n % 100;
        if (tail == 1 && hundred != 11) return $"{n} {one}";
        if (tail is >= 2 and <= 4 && hundred is < 12 or > 14) return $"{n} {few}";
        return $"{n} {many}";
    }

    private static bool IsElevated()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    private static string Sha256(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
        }
        catch (Exception ex) { return "не посчитан: " + ex.Message; }
    }

    /// <summary>
    /// Last line of defence. Whatever the sections above collect, nothing key-shaped and
    /// no home path leaves this method — the report is meant to be forwarded blindly.
    /// </summary>
    private static string Redact(string text)
    {
        text = KeyLike.Replace(text, "<ключ скрыт>");
        text = SecretLike.Replace(text, "<секрет скрыт>");

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
            text = text.Replace(home, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);

        var user = Environment.UserName;
        if (!string.IsNullOrEmpty(user) && user.Length > 2)
            text = Regex.Replace(text, Regex.Escape(user), "<пользователь>", RegexOptions.IgnoreCase);

        return text;
    }
}
