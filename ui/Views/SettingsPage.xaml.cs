using System;
using System.Windows;
using System.Windows.Controls;
using Nyx.Services;

namespace Nyx.Views;

public partial class SettingsPage : UserControl
{
    private AppSettings _settings = new();
    private bool _loading;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Reload();
    }

    private void Reload()
    {
        _loading = true;
        try
        {
            _settings = SettingsService.Load();
            ChkTun.IsChecked = _settings.Tun;
            ChkProxy.IsChecked = _settings.Proxy;
            ChkLogging.IsChecked = _settings.Logging;
            ChkAccept.IsChecked = _settings.Accept;
            RbDirect.IsChecked = _settings.Final == "direct";
            RbProxy.IsChecked = _settings.Final == "proxy";
        }
        finally { _loading = false; }
    }

    private void Toggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.Tun = ChkTun.IsChecked == true;
        _settings.Proxy = ChkProxy.IsChecked == true;
        _settings.Logging = ChkLogging.IsChecked == true;
        _settings.Accept = ChkAccept.IsChecked == true;
        Persist();
    }

    private void Final_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.Final = RbProxy.IsChecked == true ? "proxy" : "direct";
        Persist();
    }

    private void Persist()
    {
        try
        {
            SettingsService.Save(_settings);
            SaveHint.Text = $"Сохранено · {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            SaveHint.Text = "Ошибка сохранения: " + ex.Message;
        }
    }
}
