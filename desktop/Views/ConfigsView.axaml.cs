using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Tunor.Services;

namespace Tunor.Desktop.Views;

/// <summary>
/// The service files as text, for when the pages above are not enough: a config pasted
/// from a tutorial, a line the generator does not write, a look at what the engine is
/// actually being given.
///
/// The tabs come from the tunnel list rather than being the two that came with the app,
/// so a WireGuard tunnel added later is editable here too — and so are its profiles,
/// which are named copies of the same file.
/// </summary>
public partial class ConfigsView : UserControl
{
    /// <summary>A file the editor can open. Kind is a tunnel id, or empty for config.json.</summary>
    private sealed record Sheet(string Title, string Path, string Kind);

    private readonly List<Sheet> _sheets = new();
    private Sheet _current = new("config.json", "", "");
    private bool _dirty;
    private bool _loading;

    public ConfigsView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => Build();
    }

    private Window? Owner => TopLevel.GetTopLevel(this) as Window;

    private bool IsTunnel => _current.Kind.Length > 0;

    // ---------------------------------------------------------------- the tabs

    private void Build()
    {
        _sheets.Clear();
        _sheets.Add(new Sheet("config.json", Paths.ConfigJson, ""));
        foreach (var t in TunnelService.Load().Where(t => t.IsWireguard))
            _sheets.Add(new Sheet(Path.GetFileName(ConfigGenerator.ConfPath(t)),
                                  ConfigGenerator.ConfPath(t), t.Id));

        _current = _sheets.FirstOrDefault(s => s.Path == _current.Path) ?? _sheets[0];
        DrawTabs();
        Load();
    }

    private void DrawTabs()
    {
        Tabs.Children.Clear();
        foreach (var sheet in _sheets)
        {
            var b = new Button
            {
                Content = sheet.Title,
                Padding = new Avalonia.Thickness(14, 7),
                Margin = new Avalonia.Thickness(0, 0, 8, 6),
                Tag = sheet.Path,
            };
            if (sheet.Path == _current.Path) b.Classes.Add("primary");
            b.Click += async (_, _) => await Switch(sheet);
            Tabs.Children.Add(b);
        }
    }

    private async Task Switch(Sheet sheet)
    {
        if (sheet.Path == _current.Path) return;
        if (!await MayDiscard()) return;
        _current = sheet;
        DrawTabs();
        Load();
    }

    /// <summary>Asks before throwing away an edit; true when there is nothing to lose.</summary>
    private async Task<bool> MayDiscard()
    {
        if (!_dirty) return true;
        var owner = Owner;
        if (owner == null) return true;
        var ask = new ConfirmWindow("Есть несохранённые изменения",
            "Если уйти сейчас, правки в этом файле пропадут.", "Отбросить");
        await ask.ShowDialog(owner);
        return ask.Confirmed;
    }

    // ---------------------------------------------------------------- the text

    private void Load()
    {
        _loading = true;
        try
        {
            Editor.Text = File.Exists(_current.Path) ? File.ReadAllText(_current.Path) : "";
            _dirty = false;
            StatusText.Text = File.Exists(_current.Path)
                ? "Загружено: " + _current.Title
                : _current.Title + " ещё нет — он появится, когда ты сохранишь";
        }
        catch (Exception ex) { StatusText.Text = "Не прочитался: " + ex.Message; }
        finally { _loading = false; }

        PathText.Text = _current.Path;
        RefreshProfiles();
    }

    private void Editor_Changed(object? sender, TextChangedEventArgs e)
    {
        if (_loading || StatusText == null) return;
        _dirty = true;
        StatusText.Text = "Изменено — не сохранено";
    }

    private void Save_Click(object? sender, RoutedEventArgs e) => SaveEditor();

    private bool SaveEditor()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_current.Path) ?? Paths.DataDir);
            File.WriteAllText(_current.Path, Editor.Text ?? "");
            _dirty = false;

            // A tunnel file only matters once config.json is rebuilt from it. config.json
            // itself is not rebuilt: that would throw away the hand edit just saved.
            if (IsTunnel) ConfigGenerator.Generate();

            StatusText.Text = $"Сохранено · {DateTime.Now:HH:mm:ss}"
                              + (IsTunnel ? " · перезапусти движок, чтобы применить" : "");
            RefreshProfiles();
            return true;
        }
        catch (Exception ex)
        {
            StatusText.Text = "Не сохранилось: " + ex.Message;
            return false;
        }
    }

    private async void Reload_Click(object? sender, RoutedEventArgs e)
    {
        if (!await MayDiscard()) return;
        Build();
    }

    // ---------------------------------------------------------------- profiles

    private void RefreshProfiles()
    {
        ProfileBar.IsVisible = IsTunnel;
        if (!IsTunnel) return;

        List<string> names;
        try { names = ProfileService.List(_current.Kind); }
        catch { names = new List<string>(); }   // no folder just means no profiles yet

        // An empty dropdown says nothing; with no profiles, say how to make one instead.
        var any = names.Count > 0;
        ProfileBox.IsVisible = any;
        NoProfilesText.IsVisible = !any;
        BtnDeleteProfile.IsVisible = any;

        _loading = true;
        try
        {
            ProfileBox.ItemsSource = names;
            ProfileBox.SelectedItem = ProfileService.ActiveName(_current.Kind);
            BtnDeleteProfile.IsEnabled = ProfileBox.SelectedItem != null;
        }
        catch { }
        finally { _loading = false; }
    }

    private async void Profile_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading || ProfileBox.SelectedItem is not string name) return;
        BtnDeleteProfile.IsEnabled = true;
        if (name == ProfileService.ActiveName(_current.Kind)) return;
        if (!await MayDiscard()) { RefreshProfiles(); return; }

        try
        {
            ProfileService.Activate(_current.Kind, name);
            Load();
            // Not restarting the engine from a text editor: it may be the privileged one,
            // and the rest of the app asks for a restart in the same words.
            StatusText.Text = $"Профиль «{name}» включён. Перезапусти движок, чтобы применить.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Не удалось включить профиль: " + ex.Message;
            RefreshProfiles();
        }
    }

    private void SaveProfile_Click(object? sender, RoutedEventArgs e)
    {
        var name = (NewProfileBox.Text ?? "").Trim();
        if (name.Length == 0)
        {
            StatusText.Text = "Впиши имя профиля в поле «Новый».";
            NewProfileBox.Focus();
            return;
        }
        // Store what is on screen, not a stale file.
        if (_dirty && !SaveEditor()) return;

        try
        {
            var saved = ProfileService.SaveCurrentAs(_current.Kind, name);
            NewProfileBox.Text = "";
            RefreshProfiles();
            StatusText.Text = $"Сохранено как профиль «{saved}».";
        }
        catch (Exception ex) { StatusText.Text = "Не удалось сохранить профиль: " + ex.Message; }
    }

    private async void DeleteProfile_Click(object? sender, RoutedEventArgs e)
    {
        if (ProfileBox.SelectedItem is not string name) return;
        var owner = Owner;
        if (owner == null) return;

        var ask = new ConfirmWindow($"Удалить профиль «{name}»?",
            $"Активный {_current.Title} останется как есть — удалится только сохранённая копия.");
        await ask.ShowDialog(owner);
        if (!ask.Confirmed) return;

        try
        {
            ProfileService.Delete(_current.Kind, name);
            RefreshProfiles();
            StatusText.Text = $"Профиль «{name}» удалён.";
        }
        catch (Exception ex) { StatusText.Text = "Не удалось удалить: " + ex.Message; }
    }
}
