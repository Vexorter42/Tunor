using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Tunor.Services;

namespace Tunor.Views;

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
            BuildFinalChoices();
            ChkWatchdog.IsChecked = _settings.Watchdog;
            ChkAutoLists.IsChecked = _settings.AutoUpdateLists;
            ListsAgeText.Text = _settings.ListsUpdatedAt is { } at
                ? $"Последнее обновление: {at.ToLocalTime():d MMMM, HH:mm}"
                : "Скачиваются в фоне через зеркало";
        }
        finally { _loading = false; }
    }

    private void Toggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.Tun = ChkTun.IsChecked == true;
        _settings.Proxy = ChkProxy.IsChecked == true;
        _settings.Logging = ChkLogging.IsChecked == true;
        _settings.Watchdog = ChkWatchdog.IsChecked == true;
        _settings.AutoUpdateLists = ChkAutoLists.IsChecked == true;
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
            MessageBox.Show(ex.Message, "Tunor", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// "напрямую" and one choice per tunnel. Rebuilt whenever the page loads, because a
    /// tunnel may have been added since it was last open.
    /// </summary>
    private void BuildFinalChoices()
    {
        FinalChoices.Children.Clear();
        var slot = ConfigGenerator.FinalSlot(_settings.Final);
        var tunnels = TunnelService.Load();

        Add("напрямую — никуда не заворачивать", null, slot.Length == 0);
        foreach (var t in tunnels)
            Add($"через {t.Title}", t.Id, string.Equals(slot, t.Id, StringComparison.OrdinalIgnoreCase));

        var chosen = tunnels.FirstOrDefault(t => string.Equals(t.Id, slot, StringComparison.OrdinalIgnoreCase));
        FinalHint.Text = chosen == null
            ? "Весь трафик, не подходящий ни под одно правило, идёт мимо туннелей."
            : ConfigGenerator.LiveTunnels(tunnels).Contains(chosen.Id)
                ? $"Весь трафик, не подходящий ни под одно правило, идёт через {chosen.Title}."
                : $"{chosen.Title} не настроен — пока такой трафик идёт "
                  + (tunnels.Any(x => x.Id == chosen.Detour) ? "через запасной туннель." : "напрямую.");

        void Add(string text, string? id, bool on)
        {
            var rb = new RadioButton
            {
                Content = text,
                GroupName = "Final",
                Margin = new Thickness(0, FinalChoices.Children.Count == 0 ? 4 : 8, 0, 0),
                IsChecked = on,
                Tag = id,
            };
            rb.Checked += Final_Changed;
            FinalChoices.Children.Add(rb);
        }
    }

    private void Final_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (sender is not FrameworkElement fe) return;
        _settings.Final = ConfigGenerator.FinalValue(fe.Tag as string);
        Persist();
        _loading = true;
        try { BuildFinalChoices(); } finally { _loading = false; }
    }

    private void Persist()
    {
        try
        {
            SettingsService.Save(_settings);
            // Every option here ends up in config.json. Saving settings.json alone did
            // nothing until someone pressed "Save & apply" on the Rules page.
            ConfigGenerator.Generate();
            SaveHint.Text = ProcessService.IsRunning
                ? $"Сохранено · {DateTime.Now:HH:mm:ss} · применится после перезапуска"
                : $"Сохранено · {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            SaveHint.Text = "Ошибка сохранения: " + ex.Message;
        }
    }
}
