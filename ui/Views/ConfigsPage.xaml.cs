using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Nyx.Services;

namespace Nyx.Views;

public partial class ConfigsPage : UserControl
{
    private string _currentTab = "config";
    private bool _dirty;
    private bool _loading;

    public ConfigsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Load();
    }

    private string CurrentPath => _currentTab switch
    {
        "warp" => Paths.WarpConf,
        "geo"  => Paths.GeoConf,
        _      => Paths.ConfigJson,
    };

    /// <summary>warp / geo tabs hold a tunnel; config.json is the generated result.</summary>
    private bool IsTunnelTab => _currentTab is "warp" or "geo";

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb || Editor == null) return;
        if (_dirty && !ConfirmDiscard()) { ResetTabSelection(); return; }
        _currentTab = (rb.Tag as string) ?? "config";
        Load();
    }

    private void ResetTabSelection()
    {
        _loading = true;
        try
        {
            TabConfig.IsChecked = _currentTab == "config";
            TabWarp.IsChecked = _currentTab == "warp";
            TabGeo.IsChecked = _currentTab == "geo";
        }
        finally { _loading = false; }
    }

    private bool ConfirmDiscard()
    {
        var r = MessageBox.Show("Есть несохранённые изменения. Отбросить?",
            "Несохранённые изменения", MessageBoxButton.YesNo, MessageBoxImage.Question);
        return r == MessageBoxResult.Yes;
    }

    private void Load()
    {
        _loading = true;
        try
        {
            Editor.Text = File.Exists(CurrentPath) ? File.ReadAllText(CurrentPath) : "";
            _dirty = false;
            StatusText.Text = $"Загружено: {Path.GetFileName(CurrentPath)}";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Ошибка чтения: " + ex.Message;
        }
        finally { _loading = false; }
        RefreshProfiles();
    }

    private void Editor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        _dirty = true;
        StatusText.Text = "Изменено (не сохранено)";
    }

    private void Save_Click(object sender, RoutedEventArgs e) => SaveEditor();

    private bool SaveEditor()
    {
        try
        {
            File.WriteAllText(CurrentPath, Editor.Text);
            _dirty = false;

            // A tunnel file only matters once config.json is rebuilt from it. Saving here
            // used to stop at the file, so a WARP config pasted exactly as the tutorial
            // says never reached the engine. config.json itself is not rebuilt: that
            // would throw away the hand edit just saved.
            if (IsTunnelTab) ConfigGenerator.Generate();

            StatusText.Text = IsTunnelTab && ProcessService.IsRunning
                ? $"Сохранено · {DateTime.Now:HH:mm:ss} · перезапусти туннель, чтобы применить"
                : $"Сохранено · {DateTime.Now:HH:mm:ss}";
            RefreshProfiles();
            return true;
        }
        catch (Exception ex)
        {
            StatusText.Text = "Ошибка сохранения: " + ex.Message;
            MessageBox.Show(ex.Message, "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private void Reload_Click(object sender, RoutedEventArgs e)
    {
        if (_dirty && !ConfirmDiscard()) return;
        Load();
    }

    // -------------------------------------------------------------- profiles

    private void RefreshProfiles()
    {
        ProfileBar.Visibility = IsTunnelTab ? Visibility.Visible : Visibility.Collapsed;
        if (!IsTunnelTab) return;

        _loading = true;
        try
        {
            var names = ProfileService.List(_currentTab);
            ProfileBox.ItemsSource = names;
            ProfileBox.SelectedItem = ProfileService.ActiveName(_currentTab);
            ProfileBox.IsEnabled = names.Count > 0;
            DeleteProfileBtn.IsEnabled = ProfileBox.SelectedItem != null;
        }
        catch { /* a missing profiles folder just means no profiles yet */ }
        finally { _loading = false; }
    }

    private async void ProfileBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ProfileBox.SelectedItem is not string name) return;
        DeleteProfileBtn.IsEnabled = true;
        if (name == ProfileService.ActiveName(_currentTab)) return;
        if (_dirty && !ConfirmDiscard()) { RefreshProfiles(); return; }

        try
        {
            ProfileService.Activate(_currentTab, name);
            Load();
            if (ProcessService.IsRunning)
            {
                StatusText.Text = $"Профиль «{name}» включён — перезапускаю туннель…";
                await ProcessService.RestartAsync();
            }
            StatusText.Text = $"Профиль «{name}» включён";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Не удалось включить профиль: " + ex.Message;
            RefreshProfiles();
        }
    }

    private void SaveProfile_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NewProfileBox.Text))
        {
            StatusText.Text = "Впиши имя профиля слева от кнопки";
            NewProfileBox.Focus();
            return;
        }
        // Store what is on screen, not a stale file.
        if (_dirty && !SaveEditor()) return;

        try
        {
            var saved = ProfileService.SaveCurrentAs(_currentTab, NewProfileBox.Text);
            NewProfileBox.Text = "";
            RefreshProfiles();
            StatusText.Text = $"Сохранено как профиль «{saved}»";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Не удалось сохранить профиль: " + ex.Message;
        }
    }

    private void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileBox.SelectedItem is not string name) return;
        var ok = MessageBox.Show(
            $"Удалить профиль «{name}»?\nАктивный {_currentTab}.conf останется как есть.",
            "Удаление профиля", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (ok != MessageBoxResult.Yes) return;

        try
        {
            ProfileService.Delete(_currentTab, name);
            RefreshProfiles();
            StatusText.Text = $"Профиль «{name}» удалён";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Не удалось удалить: " + ex.Message;
        }
    }
}
