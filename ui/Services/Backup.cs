using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Tunor.Services;

public sealed class BackupException : Exception
{
    public BackupException(string message, bool needsPassword = false, bool wrongPassword = false) : base(message)
    {
        NeedsPassword = needsPassword;
        WrongPassword = wrongPassword;
    }

    /// <summary>The file is locked and no password was given.</summary>
    public bool NeedsPassword { get; }

    /// <summary>The password did not open it — or the file is damaged, which a lock cannot tell apart.</summary>
    public bool WrongPassword { get; }
}

/// <summary>What a backup file holds: the files, by their names inside the program's folder.</summary>
public sealed class BackupContents
{
    public string AppVersion { get; init; } = "";
    public DateTime Created { get; init; }
    public bool WasEncrypted { get; init; }
    public List<(string Name, byte[] Data)> Files { get; init; } = new();
}

/// <summary>
/// Everything that is set up — the settings, the rules, the tunnels with their keys and subscription
/// links, the profiles — in one file, to move to another computer or to go back to after a change
/// that went wrong.
///
/// The file holds private keys and the links of subscriptions, which are as good as passwords, so
/// it can be locked with a password (AES-256-GCM, the key from PBKDF2-SHA256); without one it is a
/// plain zip and the dialog says so. What is the machine's own is left out: the secret and port of the
/// stats API and the dates, and on loading these are kept as they are here. Loading does not trust the
/// file: only the known names are taken, nothing is written outside the program's folder, and every
/// file it replaces is first copied to <c>data/backups</c>.
///
/// The rule lists themselves (the .srs files) are not in it: they are downloaded again.
/// </summary>
public static class Backup
{
    public const string Extension = ".tunorbackup";

    private static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("TNRB1");
    private const int SaltSize = 16, NonceSize = 12, TagSize = 16, Iterations = 200_000;
    private const long MaxEntry = 8L * 1024 * 1024;
    private const long MaxTotal = 64L * 1024 * 1024;

    /// <summary>Keys of settings.json that belong to this machine and never travel.</summary>
    private static readonly string[] OwnSettings = { "accept", "paths", "controllerPort", "controllerSecret", "listsUpdatedAt", "lastSeenVersion" };

    // update.json is deliberately not here: it says where updates are fetched from, and a file that
    // someone else made must not be able to change that.
    private static readonly string[] Fixed =
    {
        "settings.json", "data/rules.json", "data/tunnels.json", "data/warp.conf", "data/geo.conf",
    };

    /// <summary>Where a downloaded rule list is kept, as the engine is told about it (it runs from the build folder).</summary>
    private static readonly Regex RulesetPath = new(@"^\.\./data/rulesets/[^/\\:*?""<>|]{1,120}$", RegexOptions.Compiled);

    private static readonly Regex Profile = new(@"^data/profiles/[A-Za-z0-9_-]{1,40}/[^/\\:*?""<>|]{1,120}\.conf$", RegexOptions.Compiled);

    /// <summary>Whether a name inside a backup is one that this program writes — and so one that may be loaded.</summary>
    internal static bool Allowed(string name) => Fixed.Contains(name, StringComparer.Ordinal) || Profile.IsMatch(name);

    /// <summary>The program's folder. A test points it somewhere of its own.</summary>
    internal static Func<string> Root { get; set; } = () => Paths.AppRoot;

    private static string PathOf(string name) => Path.Combine(Root(), name.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>The names, inside the program's folder, of what there is to save on this machine.</summary>
    public static IReadOnlyList<string> Present()
    {
        var names = new List<string>();
        foreach (var n in Fixed) if (File.Exists(PathOf(n))) names.Add(n);
        var profiles = Path.Combine(Root(), "data", "profiles");
        if (Directory.Exists(profiles))
            foreach (var f in Directory.EnumerateFiles(profiles, "*.conf", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
            {
                var rel = "data/" + Path.GetRelativePath(Path.Combine(Root(), "data"), f).Replace('\\', '/');
                if (Profile.IsMatch(rel)) names.Add(rel);
            }
        return names;
    }

    // ------------------------------------------------------------------ saving

    public static byte[] Export(string? password)
    {
        using var ms = new MemoryStream();
        var names = Present();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var manifest = new JsonObject
            {
                ["format"] = 1,
                ["app"] = UpdateService.CurrentVersionString,
                ["created"] = DateTime.UtcNow.ToString("o"),
                ["files"] = new JsonArray(names.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray()),
            };
            Add(zip, "backup.json", Encoding.UTF8.GetBytes(manifest.ToJsonString(Pretty)));

            foreach (var name in names)
            {
                var data = File.ReadAllBytes(PathOf(name));
                if (name == "settings.json") data = WithoutOwn(data);
                Add(zip, name, data);
            }
        }
        var plain = ms.ToArray();
        return string.IsNullOrEmpty(password) ? plain : Lock(plain, password);
    }

    private static void Add(ZipArchive zip, string name, byte[] data)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var s = entry.Open();
        s.Write(data, 0, data.Length);
    }

    /// <summary>settings.json without what is this machine's own.</summary>
    private static byte[] WithoutOwn(byte[] data)
    {
        try
        {
            if (JsonNode.Parse(data) is not JsonObject o) return data;
            foreach (var key in OwnSettings) o.Remove(key);
            return Encoding.UTF8.GetBytes(o.ToJsonString(Pretty));
        }
        catch { return data; }
    }

    private static byte[] Lock(byte[] plain, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, 32);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagSize];
        using (var aes = new AesGcm(key, TagSize)) aes.Encrypt(nonce, plain, cipher, tag, Magic);

        var all = new byte[Magic.Length + SaltSize + NonceSize + TagSize + cipher.Length];
        var at = 0;
        foreach (var part in new[] { Magic, salt, nonce, tag, cipher }) { Buffer.BlockCopy(part, 0, all, at, part.Length); at += part.Length; }
        return all;
    }

    // ------------------------------------------------------------------ loading

    /// <summary>Opens a backup and checks it; writes nothing. Throws <see cref="BackupException"/> with a sentence to show.</summary>
    public static BackupContents Read(byte[] data, string? password)
    {
        var encrypted = data.Length > Magic.Length && data.AsSpan(0, Magic.Length).SequenceEqual(Magic);
        if (encrypted)
        {
            if (string.IsNullOrEmpty(password)) throw new BackupException("Файл защищён паролем — введите его.", needsPassword: true);
            data = Unlock(data, password);
        }
        else if (!(data.Length > 4 && data[0] == 'P' && data[1] == 'K'))
            throw new BackupException("Это не резервная копия Tunor.");

        try
        {
            using var zip = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read);
            var manifest = zip.GetEntry("backup.json") ?? throw new BackupException("Это не резервная копия Tunor: в файле нет описания.");
            JsonObject? info;
            using (var s = manifest.Open()) info = JsonNode.Parse(s) as JsonObject;
            if (info?["format"]?.GetValue<int>() != 1)
                throw new BackupException("Копия сделана другой версией Tunor, которую эта не понимает. Обновите программу и попробуйте снова.");

            var files = new List<(string, byte[])>();
            long total = 0;
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName == "backup.json") continue;
                if (entry.Length == 0 && entry.FullName.EndsWith('/')) continue;   // a folder, as other zip programs write them
                if (!Allowed(entry.FullName)) throw new BackupException("В файле есть то, чего в копии быть не должно: «" + Shorten(entry.FullName) + "». Он не загружен.");
                if (entry.Length > MaxEntry || (total += entry.Length) > MaxTotal) throw new BackupException("Файл слишком большой для резервной копии настроек.");

                // The size in the header is the file's own word; what is read is counted.
                using var s = entry.Open();
                using var buf = new MemoryStream();
                var chunk = new byte[16 * 1024];
                int n;
                while ((n = s.Read(chunk, 0, chunk.Length)) > 0)
                {
                    buf.Write(chunk, 0, n);
                    if (buf.Length > MaxEntry) throw new BackupException("Файл слишком большой для резервной копии настроек.");
                }
                files.Add((entry.FullName, buf.ToArray()));
            }
            if (files.Count == 0) throw new BackupException("В копии нет ни одного файла настроек.");
            foreach (var (name, bytes) in files) Check(name, bytes);

            return new BackupContents
            {
                AppVersion = info["app"]?.GetValue<string>() ?? "",
                Created = DateTime.TryParse(info["created"]?.GetValue<string>(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var c) ? c.ToLocalTime() : default,
                WasEncrypted = encrypted,
                Files = files,
            };
        }
        catch (BackupException) { throw; }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or InvalidOperationException or FormatException)
        {
            throw new BackupException("Файл повреждён и не читается.");
        }
    }

    /// <summary>A file's content is looked at, not only its name: the tunnels may point at files, and only at the ones this program keeps.</summary>
    private static void Check(string name, byte[] data)
    {
        try
        {
            if (name == "data/tunnels.json")
            {
                if (JsonNode.Parse(data) is not JsonArray list) throw new BackupException("Список туннелей в копии не читается.");
                foreach (var item in list)
                {
                    var file = item?["file"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(file) && !Allowed(file.Replace('\\', '/')))
                        throw new BackupException("Туннель в копии ссылается на файл вне папки программы: «" + Shorten(file) + "». Копия не загружена.");
                }
            }
            else if (name == "data/rules.json")
            {
                if (JsonNode.Parse(data) is not JsonArray groups) throw new BackupException("Список правил в копии не читается.");
                foreach (var g in groups)
                {
                    var path = g?["path"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(path) && !RulesetPath.IsMatch(path))
                        throw new BackupException("Правило в копии ссылается на файл не из папки списков: «" + Shorten(path) + "». Копия не загружена.");
                    var url = g?["_source_url"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(url) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                        throw new BackupException("Адрес списка в копии не похож на адрес сайта: «" + Shorten(url) + "». Копия не загружена.");
                }
            }
            else if (name == "settings.json")
            {
                if (JsonNode.Parse(data) is not JsonObject) throw new BackupException("Настройки в копии не читаются.");
            }
        }
        catch (JsonException) { throw new BackupException("«" + name + "» в копии не читается."); }
        catch (InvalidOperationException) { throw new BackupException("«" + name + "» в копии не читается."); }
    }

    private static byte[] Unlock(byte[] data, string password)
    {
        var min = Magic.Length + SaltSize + NonceSize + TagSize;
        if (data.Length <= min) throw new BackupException("Файл повреждён и не читается.");
        var at = Magic.Length;
        var salt = data.AsSpan(at, SaltSize).ToArray(); at += SaltSize;
        var nonce = data.AsSpan(at, NonceSize).ToArray(); at += NonceSize;
        var tag = data.AsSpan(at, TagSize).ToArray(); at += TagSize;
        var cipher = data.AsSpan(at).ToArray();
        var key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, 32);
        var plain = new byte[cipher.Length];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, cipher, tag, plain, Magic);
        }
        catch (CryptographicException) { throw new BackupException("Пароль не подошёл, либо файл повреждён.", wrongPassword: true); }
        return plain;
    }

    private static string Shorten(string s) => s.Length > 60 ? s[..60] + "…" : s;

    // ------------------------------------------------------------------ putting it in place

    /// <summary>
    /// Writes the files of a backup over this machine's. Each file it replaces is first copied to
    /// <c>data/backups/before-import-&lt;time&gt;</c>. Returns where those copies went.
    /// </summary>
    public static string Apply(BackupContents contents)
    {
        var copies = Path.Combine(Root(), "data", "backups", "before-import-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));

        // The names are checked once more here: a BackupContents can be made by anyone.
        foreach (var (name, _) in contents.Files)
            if (!Allowed(name)) throw new BackupException("Недопустимое имя в копии: " + Shorten(name));

        foreach (var (name, data) in contents.Files)
        {
            var target = PathOf(name);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            if (File.Exists(target))
            {
                var copy = Path.Combine(copies, name.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                File.Copy(target, copy, overwrite: true);
            }

            File.WriteAllBytes(target, name == "settings.json" ? Merged(target, data) : data);
        }
        return copies;
    }

    /// <summary>The imported settings, with what is this machine's own kept as it is here.</summary>
    private static byte[] Merged(string target, byte[] imported)
    {
        var theirs = JsonNode.Parse(imported) as JsonObject ?? throw new BackupException("Настройки в копии не читаются.");
        JsonObject mine;
        try { mine = (File.Exists(target) ? JsonNode.Parse(File.ReadAllBytes(target)) as JsonObject : null) ?? new JsonObject(); }
        catch { mine = new JsonObject(); }

        foreach (var key in OwnSettings) theirs.Remove(key);
        foreach (var (key, value) in theirs) mine[key] = value?.DeepClone();
        return Encoding.UTF8.GetBytes(mine.ToJsonString(Pretty));
    }

    /// <summary>Which files a backup would replace here and which it would add.</summary>
    public static (List<string> Replaces, List<string> Adds) Plan(BackupContents contents)
    {
        var replaces = new List<string>();
        var adds = new List<string>();
        foreach (var (name, _) in contents.Files) (File.Exists(PathOf(name)) ? replaces : adds).Add(name);
        return (replaces, adds);
    }

    private static string Label(string name) => name switch
    {
        "settings.json" => "настройки",
        "data/rules.json" => "правила",
        "data/tunnels.json" => "список туннелей",
        "data/warp.conf" => "WARP",
        "data/geo.conf" => "geo",
        _ => "",
    };

    private static string Names(IEnumerable<string> files)
    {
        var list = files.Select(Label).Where(l => l.Length > 0).ToList();
        var profiles = files.Count(f => f.StartsWith("data/profiles/", StringComparison.Ordinal));
        if (profiles > 0) list.Add("профили (" + profiles + ")");
        return string.Join(", ", list);
    }

    /// <summary>What a person is told before a backup is loaded, in words rather than file names.</summary>
    public static string Describe(BackupContents contents)
    {
        var (replaces, adds) = Plan(contents);
        var sb = new StringBuilder();
        sb.Append("Копия от ").Append(contents.Created == default ? "неизвестной даты" : contents.Created.ToString("d MMMM yyyy, HH:mm"));
        if (contents.AppVersion.Length > 0) sb.Append(", версия ").Append(contents.AppVersion);
        sb.Append(".\n\n");
        if (replaces.Count > 0) sb.Append("Заменит: ").Append(Names(replaces)).Append(".\n");
        if (adds.Count > 0) sb.Append("Добавит: ").Append(Names(adds)).Append(".\n");
        sb.Append("\nНе меняются: адрес проверки обновлений, порт и секрет статистики, согласие с условиями. ");
        sb.Append("Прежние файлы будут сохранены в папке data/backups.\n\n");
        sb.Append("Загружайте только копии, которые сделали сами: в них адреса серверов, ключи и правила, по которым пойдёт весь трафик.");
        return sb.ToString();
    }

    /// <summary>Whether a rule list that the rules name is not on the disk — after a load on another computer, none is.</summary>
    public static bool ListsMissing()
    {
        try
        {
            return RulesService.Load().Any(g => g.IsLocal && !string.IsNullOrEmpty(g.Path) && !ConfigGenerator.LocalRuleSetExists(g.Path));
        }
        catch { return false; }
    }
}
