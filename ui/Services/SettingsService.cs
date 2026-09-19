using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Nyx.Services;

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
        if (!File.Exists(Nyx.Services.Paths.SettingsJson))
            return new AppSettings();
        try
        {
            var json = File.ReadAllText(Nyx.Services.Paths.SettingsJson);
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
        File.WriteAllText(Nyx.Services.Paths.SettingsJson, json);
    }
}
