using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace Nyx.Services;

public class UpdateConfig
{
    public string Repo { get; set; } = "Vexorter42/Nyx";   // owner/repo on GitHub
    public string Mirror { get; set; } = "https://ghproxy.net/"; // prefix for blocked GitHub

    public static UpdateConfig Load()
    {
        try
        {
            if (File.Exists(Paths.UpdateJson))
            {
                var json = File.ReadAllText(Paths.UpdateJson);
                var cfg = JsonSerializer.Deserialize<UpdateConfig>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (cfg != null) return cfg;
            }
        }
        catch { }
        return new UpdateConfig();
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Paths.UpdateJson, json);
        }
        catch { }
    }
}

public enum UpdateKind { None, Patch, Minor, Major }

public class UpdateInfo
{
    public bool Available { get; init; }
    public UpdateKind Kind { get; init; }

    /// <summary>Short Russian label for the badge, e.g. "Крупное обновление".</summary>
    public string KindLabel => Kind switch
    {
        UpdateKind.Major => "MAJOR — крупное обновление",
        UpdateKind.Minor => "MINOR — новые возможности",
        UpdateKind.Patch => "PATCH — исправления",
        _ => "",
    };

    public string Current { get; init; } = "";
    public string Latest { get; init; } = "";
    public string Notes { get; init; } = "";
    public string Url { get; init; } = "";
    public string Sha256 { get; init; } = "";
    public string Error { get; init; } = "";
}

public static class UpdateService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public static Version CurrentVersion =>
        Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(1, 0, 0);

    public static string CurrentVersionString
    {
        get { var v = CurrentVersion; return $"{v.Major}.{v.Minor}.{v.Build}"; }
    }

    private static string Mirror(UpdateConfig c, string githubUrl)
        => string.IsNullOrWhiteSpace(c.Mirror) ? githubUrl : c.Mirror.TrimEnd('/') + "/" + githubUrl;

    /// <summary>Fetches version.json from the latest GitHub release (through the mirror).</summary>
    public static async Task<UpdateInfo> CheckAsync()
    {
        var cfg = UpdateConfig.Load();
        if (string.IsNullOrWhiteSpace(cfg.Repo) || cfg.Repo.Contains("CHANGEME"))
            return new UpdateInfo { Error = "Не задан GitHub-репозиторий в update.json" };

        try
        {
            var manifestUrl = Mirror(cfg,
                $"https://github.com/{cfg.Repo}/releases/latest/download/version.json");

            var text = await Http.GetStringAsync(manifestUrl);
            var json = JsonNode.Parse(text) as JsonObject;
            var latest = json?["version"]?.GetValue<string>() ?? "";
            var url = json?["url"]?.GetValue<string>() ?? "";
            var sha = json?["sha256"]?.GetValue<string>() ?? "";
            var notes = json?["notes"]?.GetValue<string>() ?? "";

            if (string.IsNullOrWhiteSpace(latest) || string.IsNullOrWhiteSpace(url))
                return new UpdateInfo { Error = "Некорректный version.json" };

            var latestV = TryParse(latest);
            var cur = CurrentVersion;
            var newer = latestV > cur;

            var kind = UpdateKind.None;
            if (newer)
            {
                if (latestV.Major > cur.Major) kind = UpdateKind.Major;
                else if (latestV.Minor > cur.Minor) kind = UpdateKind.Minor;
                else kind = UpdateKind.Patch;
            }

            return new UpdateInfo
            {
                Available = newer,
                Kind = kind,
                Current = CurrentVersionString,
                Latest = latest,
                Notes = notes,
                Url = url,
                Sha256 = sha,
            };
        }
        catch (Exception ex)
        {
            return new UpdateInfo { Error = ex.Message };
        }
    }

    /// <summary>Downloads the installer (through mirror), verifies SHA256, runs it silently.</summary>
    public static async Task<(bool ok, string message)> DownloadAndApplyAsync(
        UpdateInfo info, IProgress<double>? progress = null)
    {
        var cfg = UpdateConfig.Load();
        try
        {
            var dlUrl = Mirror(cfg, info.Url);
            var tmp = Path.Combine(Path.GetTempPath(), $"Nyx-Setup-{info.Latest}.exe");

            using (var resp = await Http.GetAsync(dlUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                resp.EnsureSuccessStatusCode();
                var total = resp.Content.Headers.ContentLength ?? -1;
                await using var src = await resp.Content.ReadAsStreamAsync();
                await using var dst = File.Create(tmp);
                var buffer = new byte[81920];
                long read = 0; int n;
                while ((n = await src.ReadAsync(buffer)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, n));
                    read += n;
                    if (total > 0) progress?.Report((double)read / total);
                }
            }

            // Verify SHA256
            if (!string.IsNullOrWhiteSpace(info.Sha256))
            {
                var actual = Sha256File(tmp);
                if (!actual.Equals(info.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(tmp); } catch { }
                    return (false, $"SHA256 не совпал!\nОжидался: {info.Sha256}\nПолучен:  {actual}\n\nЗагрузка отклонена (возможна подмена на зеркале).");
                }
            }

            // Run installer silently. It force-closes this app (taskkill in the .iss),
            // replaces files, and relaunches the app itself. The caller must exit this
            // process so file locks are released.
            var psi = new ProcessStartInfo
            {
                FileName = tmp,
                Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART",
                UseShellExecute = true,
            };
            Process.Start(psi);
            return (true, "Установщик запущен. Приложение сейчас закроется и обновится.");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static string Sha256File(string path)
    {
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(path);
        var hash = sha.ComputeHash(fs);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static Version TryParse(string s)
    {
        s = s.TrimStart('v', 'V').Trim();
        return Version.TryParse(s, out var v) ? v : new Version(0, 0, 0);
    }
}
