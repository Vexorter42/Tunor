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
    }

    private void Editor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        _dirty = true;
        StatusText.Text = "Изменено (не сохранено)";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            File.WriteAllText(CurrentPath, Editor.Text);
            _dirty = false;
            StatusText.Text = $"Сохранено · {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Ошибка сохранения: " + ex.Message;
            MessageBox.Show(ex.Message, "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Reload_Click(object sender, RoutedEventArgs e)
    {
        if (_dirty && !ConfirmDiscard()) return;
        Load();
    }
}
