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
        Persist();
    }

    private void ShowWizard_Click(object sender, RoutedEventArgs e)
    {
        var w = new FirstRunWizard { Owner = Window.GetWindow(this) };
        w.ShowDialog();
    }

    private void Licenses_Click(object sender, RoutedEventArgs e)
    {
        // Open the notices file if it shipped, otherwise just reveal the folder.
        var target = System.IO.File.Exists(Paths.ThirdPartyNotices)
            ? Paths.ThirdPartyNotices
            : System.IO.Directory.Exists(Paths.LicensesDir) ? Paths.LicensesDir : Paths.AppRoot;
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Nyx", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
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
