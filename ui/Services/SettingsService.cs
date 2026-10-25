using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Tunor.Services;

public class AppSettings : INotifyPropertyChanged
{
    private bool _accept;
    private bool _tun = true;
    private bool _proxy = true;
    private string _final = "direct";
    private bool _logging = true;
    private PathSettings _paths = new();

    [JsonPropertyName("accept")]
    public bool Accept { get => _accept; set => Set(ref _accept, value); }

    [JsonPropertyName("tun")]
    public bool Tun { get => _tun; set => Set(ref _tun, value); }

    [JsonPropertyName("proxy")]
    public bool Proxy { get => _proxy; set => Set(ref _proxy, value); }

    [JsonPropertyName("final")]
    public string Final { get => _final; set => Set(ref _final, value); }

    [JsonPropertyName("logging")]
    public bool Logging { get => _logging; set => Set(ref _logging, value); }

    [JsonPropertyName("paths")]
    public PathSettings Paths { get => _paths; set => Set(ref _paths, value); }

    private bool _watchdog = true;
    private bool _autoUpdateLists = true;
    private bool _autoCheckUpdates = true;
    private DateTime? _listsUpdatedAt;

    /// <summary>Restart the engine if it dies on its own. On by default.</summary>
    [JsonPropertyName("watchdog")]
    public bool Watchdog { get => _watchdog; set => Set(ref _watchdog, value); }

    /// <summary>Refresh downloaded rule lists in the background once they are a week old.</summary>
    [JsonPropertyName("autoUpdateLists")]
    public bool AutoUpdateLists { get => _autoUpdateLists; set => Set(ref _autoUpdateLists, value); }

    /// <summary>
    /// Look for a new version of the program by itself, shortly after it starts and then every
    /// few hours, and say so on the Home page. On by default.
    /// </summary>
    [JsonPropertyName("autoCheckUpdates")]
    public bool AutoCheckUpdates { get => _autoCheckUpdates; set => Set(ref _autoCheckUpdates, value); }

    private string _logLevel = "warn";

    /// <summary>
    /// How much the engine says: error, warn (the default), info or debug. A tunnel's handshakes —
    /// whether a drop is the network or the keys — are debug lines; the rest of debug is every
    /// connection there is, so it is raised to look into something and lowered after.
    /// </summary>
    [JsonPropertyName("logLevel")]
    public string LogLevel { get => _logLevel; set => Set(ref _logLevel, NormalizeLevel(value)); }

    public static string NormalizeLevel(string? level) => level?.Trim().ToLowerInvariant() switch
    {
        "error" => "error",
        "info" => "info",
        "debug" => "debug",
        _ => "warn",
    };

    private string _lastSeenVersion = "";

    /// <summary>The version whose "what's new" was last shown; empty on a first run.</summary>
    [JsonPropertyName("lastSeenVersion")]
    public string LastSeenVersion { get => _lastSeenVersion; set => Set(ref _lastSeenVersion, value ?? ""); }

    private bool _hideDpiHint;

    /// <summary>The person has read the note about zapret and TUN and does not want it on the Home page again.</summary>
    [JsonPropertyName("hideDpiHint")]
    public bool HideDpiHint { get => _hideDpiHint; set => Set(ref _hideDpiHint, value); }

    private bool _healthNotify = true;

    /// <summary>Say so when a tunnel stops answering, and when it answers again. On by default.</summary>
    [JsonPropertyName("healthNotify")]
    public bool HealthNotify { get => _healthNotify; set => Set(ref _healthNotify, value); }

    [JsonPropertyName("listsUpdatedAt")]
    public DateTime? ListsUpdatedAt { get => _listsUpdatedAt; set => Set(ref _listsUpdatedAt, value); }

    private int _controllerPort;
    private string _controllerSecret = "";

    /// <summary>
    /// Loopback port of the engine's stats API (the Connections page). Chosen once and
    /// kept; 0 means "pick a free one on the next config build".
    /// </summary>
    [JsonPropertyName("controllerPort")]
    public int ControllerPort { get => _controllerPort; set => Set(ref _controllerPort, value); }

    /// <summary>Bearer token for that API, so other local programs cannot read it.</summary>
    [JsonPropertyName("controllerSecret")]
    public string ControllerSecret { get => _controllerSecret; set => Set(ref _controllerSecret, value); }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (!Equals(field, value))
        {
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}

public class PathSettings
{
    [JsonPropertyName("config")] public string Config { get; set; } = "build/config.json";
    [JsonPropertyName("warp")]   public string Warp { get; set; } = "data/warp.conf";
    [JsonPropertyName("geo")]    public string Geo { get; set; } = "data/geo.conf";
    [JsonPropertyName("rules")]  public string Rules { get; set; } = "data/rules.json";
}

public static class SettingsService
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static AppSettings Load()
    {
        if (!File.Exists(Tunor.Services.Paths.SettingsJson))
            return new AppSettings();
        try
        {
            var json = File.ReadAllText(Tunor.Services.Paths.SettingsJson);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, Options);
        File.WriteAllText(Tunor.Services.Paths.SettingsJson, json);
    }
}
