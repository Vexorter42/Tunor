using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Tunor.Services;

namespace Tunor.Desktop.Views;

public partial class TunnelsView : UserControl
{
    /// <summary>A row: everything shown about one tunnel, and nothing secret.</summary>
    public sealed record Row(string Id, string Title, string Badge, string Detail,
                             string Where, bool ShowWhere, string Status, bool CanEdit);

    public TunnelsView()
    {
        InitializeComponent();
        DataPathText.Text = Paths.DataDir;
        Refresh();
    }

    private void Refresh()
    {
        var tunnels = TunnelService.Load();
        var live = ConfigGenerator.LiveTunnels(tunnels);
        var rows = new List<Row>();

        foreach (var t in tunnels)
        {
            string badge, detail, where = "";
            var showWhere = false;

            if (t.IsWireguard)
            {
                badge = "WIREGUARD";
                var path = ConfigGenerator.ConfPath(t);
                var state = ConfigGenerator.Inspect(path);

                // "не настроен" covered both "no file at all" and "a file that is a
                // placeholder", which is exactly the confusion to avoid: someone who has
                // put a config somewhere needs to know it was not the place this reads.
                if (state.Problem != null) detail = state.Problem;
                else if (!File.Exists(path)) { detail = "файла нет — положи его сюда:"; showWhere = true; }
                else if (state.Placeholder) { detail = "файл есть, но это заготовка без настоящих ключей"; showWhere = true; }
                else detail = "настроен";
                where = path;
            }
            else if (t.IsSubscription)
            {
                badge = "ПОДПИСКА";
                detail = $"серверов {t.Nodes.Count} · "
                         + (t.IsAuto ? "быстрейший автоматически" : t.Node);
            }
            else
            {
                badge = t.Kind.ToUpperInvariant();
                var p = ProxyLink.Parse(t.Url);
                detail = p.Ok ? p.Summary : "ссылка не читается";
            }
            if (t.Detour.Length > 0) detail += " · через " + t.Detour;

            rows.Add(new Row(t.Id, t.Title, badge, detail, where, showWhere,
                live.Contains(t.Id) ? "готов" : "не готов", !t.IsWireguard));
        }
        List.ItemsSource = rows;
    }

    private Window? Owner => TopLevel.GetTopLevel(this) as Window;

    // ---------------------------------------------------------------- .conf files

    private async void Import_Click(object? sender, RoutedEventArgs e)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Выбери файл конфига",
            AllowMultiple = true,
            // A .conf is plain text and people save it under every extension there is, so
            // the filter is a convenience, not a gate: the contents decide.
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Конфиги WireGuard") { Patterns = new[] { "*.conf", "*.txt" } },
                FilePickerFileTypes.All,
            },
        });
        if (files.Count == 0) return;

        var done = new List<string>();
        var failed = new List<string>();
        foreach (var f in files)
        {
            var path = f.TryGetLocalPath();
            if (path == null) continue;
            if (!ConfImporter.LooksLikeConf(path))
            {
                failed.Add($"{Path.GetFileName(path)}: не похоже на конфиг WireGuard");
                continue;
            }
            try
            {
                var d = ConfImporter.Detect(path);
                ConfImporter.Apply(path, d.Kind);
                done.Add($"{Path.GetFileName(path)} → {(d.Kind == ConfKind.Warp ? "WARP" : "geo")}"
                         + (d.Confident ? "" : " (определено предположительно)"));
            }
            catch (Exception ex) { failed.Add($"{Path.GetFileName(path)}: {ex.Message}"); }
        }

        if (done.Count > 0) ConfigGenerator.Generate();
        Refresh();
        StatusText.Text = string.Join("\n", done.Concat(failed))
                          + (done.Count > 0 ? "\nПерезапусти движок, чтобы применить." : "");
    }

    private void OpenFolder_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(Paths.DataDir);
            // Each system has its own way of showing a folder to a person.
            var (file, args) = OperatingSystem.IsMacOS() ? ("open", $"\"{Paths.DataDir}\"")
                             : OperatingSystem.IsWindows() ? ("explorer.exe", $"\"{Paths.DataDir}\"")
                             : ("xdg-open", $"\"{Paths.DataDir}\"");
            Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = false });
        }
        catch (Exception ex) { StatusText.Text = "Не удалось открыть папку: " + ex.Message; }
    }

    // ---------------------------------------------------------------- link tunnels

    private async void Add_Click(object? sender, RoutedEventArgs e) => await Open(null);

    private async void Edit_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }) return;
        await Open(TunnelService.Load().FirstOrDefault(t => t.Id == id));
    }

    private async Task Open(Tunnel? editing)
    {
        var owner = Owner;
        if (owner == null) return;
        var dlg = new AddVpnWindow(editing);
        await dlg.ShowDialog(owner);
        if (!dlg.Saved) return;
        Refresh();
        StatusText.Text = "Список туннелей изменён. Перезапусти движок, чтобы применить.";
    }

    private async void Delete_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }) return;
        var owner = Owner;
        var tunnels = TunnelService.Load();
        var t = tunnels.FirstOrDefault(x => x.Id == id);
        if (t == null || owner == null) return;

        var ask = new ConfirmWindow($"Удалить «{t.Title}»?",
            "Правила, которые отправляли трафик в этот туннель, останутся на месте, "
            + "но перестанут действовать — трафик пойдёт напрямую.");
        await ask.ShowDialog(owner);
        if (!ask.Confirmed) return;

        tunnels.Remove(t);
        TunnelService.Save(tunnels);
        ConfigGenerator.Generate();
        Refresh();
        StatusText.Text = $"«{t.Title}» удалён. Перезапусти движок, чтобы применить.";
    }
}
