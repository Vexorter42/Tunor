using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace Tunor.Services;

public class UpdateConfig
{
    public string Repo { get; set; } = "Vexorter42/Tunor";   // owner/repo on GitHub
    public string Mirror { get; set; } = "https://ghproxy.net/"; // prefix for blocked GitHub

    /// <summary>What the repository was called before the rename to Tunor.</summary>
    private const string FormerRepo = "Vexorter42/Nyx";

    public static UpdateConfig Load()
    {
        try
        {
            if (File.Exists(Paths.UpdateJson))
            {
                var json = File.ReadAllText(Paths.UpdateJson);
                var cfg = JsonSerializer.Deserialize<UpdateConfig>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (cfg != null)
                {
                    // update.json is kept on upgrade, so installs made before the rename
                    // still point at the old repository. GitHub redirects that name, but
                    // a redirect is not something to depend on for years — move it over
                    // once, quietly.
                    if (cfg.Repo.Equals(FormerRepo, StringComparison.OrdinalIgnoreCase))
                    {
                        cfg.Repo = new UpdateConfig().Repo;
                        cfg.Save();
                    }
                    return cfg;
                }
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
    /// <summary>For the manifest: a small file, and a slow answer means something is wrong.</summary>
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>
    /// For the build itself, which is sixty-odd megabytes through a mirror.
    ///
    /// HttpClient.Timeout covers the whole operation, reading the body included, even
    /// with ResponseHeadersRead — so the thirty seconds meant for a manifest were also
    /// the budget for the download, and the update simply stopped partway on any
    /// connection slower than two megabytes a second. Here the limit is on the whole
    /// transfer and generous enough to be about a stall rather than about speed.
    /// </summary>
    private static readonly HttpClient Fetch = new() { Timeout = TimeSpan.FromMinutes(20) };

    public static Version CurrentVersion =>
        Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(1, 0, 0);

    public static string CurrentVersionString
    {
        get
        {
            var v = CurrentVersion;
            // Three numbers, and the same three on every system. macOS builds once
            // carried a fourth that counted re-cuts of a preview while Windows stood
            // still, which left two builds of identical code wearing different
            // numbers and nobody able to say which one they were running.
            return $"{v.Major}.{v.Minor}.{v.Build}";
        }
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

            // One manifest for every system. Each gets its own file and checksum, and
            // may carry its own notes; the version is shared, because all of them are
            // built from one tree and saying otherwise only confused people.
            var section = OperatingSystem.IsMacOS() ? "mac"
                        : OperatingSystem.IsLinux() ? "linux"
                        : null;
            if (section != null)
            {
                if (json?[section] is not JsonObject part)
                    return new UpdateInfo
                    {
                        Error = $"Для {(section == "mac" ? "macOS" : "Linux")} сборки "
                                + "пока нет в этом релизе",
                    };

                latest = part["version"]?.GetValue<string>() ?? latest;
                url = part["url"]?.GetValue<string>() ?? "";
                sha = part["sha256"]?.GetValue<string>() ?? "";
                notes = part["notes"]?.GetValue<string>() ?? notes;
            }

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
            var name = OperatingSystem.IsMacOS() ? $"Tunor-mac-{info.Latest}.tar.gz"
                     : OperatingSystem.IsLinux() ? $"Tunor-{info.Latest}-x86_64.AppImage"
                     : $"Tunor-Setup-{info.Latest}.exe";
            var tmp = Path.Combine(Path.GetTempPath(), name);

            using (var resp = await Fetch.GetAsync(dlUrl, HttpCompletionOption.ResponseHeadersRead))
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

            // Spelled out so the compiler can see the guard: what follows each one is
            // that system's own API.
            if (OperatingSystem.IsMacOS()) return ApplyMac(tmp);
            if (OperatingSystem.IsLinux()) return ApplyLinux(tmp);

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

    // ---------------------------------------------------------------- Linux

    /// <summary>
    /// Puts the downloaded AppImage where the running one is.
    ///
    /// An AppImage is the whole program in one file, so an update is a copy over that
    /// file — but not while it is running: the kernel holds it open and writing into it
    /// gives a half-written program. It is written beside it and moved into place by a
    /// helper once this process is gone, the same dance as on macOS and for the same
    /// reason.
    ///
    /// A copy started from an unpacked folder has no such file, and $APPIMAGE is empty.
    /// That case is said out loud instead of guessed at.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private static (bool ok, string message) ApplyLinux(string downloaded)
    {
        var self = Environment.GetEnvironmentVariable("APPIMAGE");
        if (string.IsNullOrWhiteSpace(self) || !File.Exists(self))
            return (false, "это не AppImage, а распакованная папка — "
                         + "скачай новую сборку и замени её сам");

        try
        {
            File.SetUnixFileMode(downloaded,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

            var script = Path.Combine(Path.GetTempPath(), "tunor-swap.sh");
            File.WriteAllText(script, SwapFileScript(Environment.ProcessId, downloaded, self));
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite |
                                         UnixFileMode.UserExecute);

            Process.Start(new ProcessStartInfo("/bin/sh", $"\"{script}\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
            });
            return (true, "Обновление скачано. Приложение закроется и откроется заново.");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    private static string SwapFileScript(int pid, string fresh, string target) => string.Join("\n",
        "#!/bin/sh",
        "# Waits for Tunor to exit, then puts the new AppImage where the old one was.",
        $"i=0; while [ $i -lt 120 ] && kill -0 {pid} 2>/dev/null; do sleep 0.5; i=$((i+1)); done",
        $"OLD=\"{target}\"",
        $"NEW=\"{fresh}\"",
        "cp \"$OLD\" \"$OLD.old\" 2>/dev/null",
        "if cp \"$NEW\" \"$OLD\"; then",
        "  rm -f \"$OLD.old\" \"$NEW\"",
        "else",
        "  [ -f \"$OLD.old\" ] && mv \"$OLD.old\" \"$OLD\"",   // the old program back, rather than none
        "fi",
        "chmod +x \"$OLD\"",
        "\"$OLD\" &",
        "");

    // ---------------------------------------------------------------- macOS

    /// <summary>
    /// Puts a new app bundle where the running one is.
    ///
    /// There is no installer on macOS: an app is a folder, and updating it means
    /// replacing that folder. It cannot be replaced from inside itself — the executable
    /// being replaced is the one doing the work — so the swap is handed to a small script
    /// that waits for this process to end, moves the old bundle aside, puts the new one
    /// in its place and starts it. The old bundle goes back if the copy fails, so a
    /// broken download cannot leave the user with no app at all.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    private static (bool ok, string message) ApplyMac(string archive)
    {
        if (InstallProblem() is { } why) return (false, why);
        var bundle = CurrentBundle();
        if (bundle == null)
            return (false, "не нашёл, где лежит само приложение — замени Tunor.app вручную");

        var staging = Path.Combine(Path.GetTempPath(), "tunor-update");
        try
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            Directory.CreateDirectory(staging);

            var untar = Process.Start(new ProcessStartInfo("/usr/bin/tar",
                $"-xzf \"{archive}\" -C \"{staging}\"") { UseShellExecute = false });
            untar?.WaitForExit(120_000);
            if (untar is not { ExitCode: 0 }) return (false, "архив не распаковался");

            var fresh = Path.Combine(staging, "Tunor.app");
            if (!Directory.Exists(fresh)) return (false, "в архиве нет Tunor.app");

            var script = Path.Combine(staging, "swap.sh");
            File.WriteAllText(script, SwapScript(Environment.ProcessId, fresh, bundle));
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite |
                                         UnixFileMode.UserExecute);

            Process.Start(new ProcessStartInfo("/bin/bash", $"\"{script}\"")
            {
                UseShellExecute = false,
                // Detached from this app's streams, or it would die along with it.
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
            });
            return (true, "Обновление скачано. Приложение закроется и откроется заново.");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    private static string SwapScript(int pid, string fresh, string bundle) => string.Join("\n",
        "#!/bin/bash",
        "# Waits for Tunor to let go of its own folder, then puts the new one in its place.",
        $"for i in $(seq 1 120); do kill -0 {pid} 2>/dev/null || break; sleep 0.5; done",
        $"OLD=\"{bundle}\"",
        $"NEW=\"{fresh}\"",
        "rm -rf \"$OLD.old\"",
        "mv \"$OLD\" \"$OLD.old\" || exit 1",
        "if /usr/bin/ditto \"$NEW\" \"$OLD\"; then",
        "  rm -rf \"$OLD.old\"",
        "else",
        "  rm -rf \"$OLD\"; mv \"$OLD.old\" \"$OLD\"",   // the old app back, rather than none
        "fi",
        "xattr -dr com.apple.quarantine \"$OLD\" 2>/dev/null",
        "open \"$OLD\"",
        "");

    /// <summary>
    /// Why this copy cannot replace itself, in words that say what to do about it, or
    /// null when it can.
    ///
    /// Opening an app straight out of a mounted disk image is the easy mistake, and
    /// macOS makes it quietly: Gatekeeper runs an unsigned app from a randomised
    /// read-only copy under AppTranslocation, so the program works, is not where the
    /// user thinks it is, and nothing can be written over it. Said plainly here, because
    /// the alternative is an update that fails for reasons nobody could guess.
    /// </summary>
    public static string? InstallProblem()
    {
        if (!OperatingSystem.IsMacOS()) return null;
        var exe = Environment.ProcessPath ?? "";

        if (exe.Contains("/AppTranslocation/", StringComparison.Ordinal))
            return "Программа открыта прямо из образа, и macOS запустила её временную копию. "
                 + "Перетащи Tunor в «Программы» и запусти оттуда — тогда обновления будут ставиться.";
        if (exe.StartsWith("/Volumes/", StringComparison.Ordinal))
            return "Программа запущена с подключённого образа, туда ничего не записать. "
                 + "Перетащи Tunor в «Программы» и запусти оттуда.";

        var bundle = CurrentBundle();
        if (bundle == null) return null;      // a publish folder: replaced by hand anyway
        try
        {
            // The swap replaces the bundle inside its folder, so that folder is what has
            // to be writable — the bundle's own permissions say nothing about it.
            var holder = Path.GetDirectoryName(bundle.TrimEnd('/'));
            if (holder == null) return null;
            var probe = Path.Combine(holder, ".tunor-write-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
        }
        catch
        {
            return "Папку, где лежит Tunor, изменить нельзя — перенеси программу "
                 + "в «Программы» и запусти оттуда.";
        }
        return null;
    }

    /// <summary>
    /// The .app this process is running from, or null when it is not in a bundle — a
    /// build run straight from a publish folder, which is replaced by hand.
    /// </summary>
    private static string? CurrentBundle()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return null;
        var at = exe.IndexOf(".app/", StringComparison.OrdinalIgnoreCase);
        return at < 0 ? null : exe[..(at + 4)];
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
