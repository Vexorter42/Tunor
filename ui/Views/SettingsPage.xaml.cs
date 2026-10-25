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
            ChkAutoUpdate.IsChecked = _settings.AutoCheckUpdates;
            foreach (ComboBoxItem item in CmbLogLevel.Items)
                item.IsSelected = (string?)item.Tag == AppSettings.NormalizeLevel(_settings.LogLevel);
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
        _settings.AutoCheckUpdates = ChkAutoUpdate.IsChecked == true;
        Persist();
    }

    private void LogLevel_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || CmbLogLevel.SelectedItem is not ComboBoxItem { Tag: string level }) return;
        _settings.LogLevel = level;
        Persist();
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.IO.Directory.CreateDirectory(AppLog.Dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{AppLog.Dir}\"")
                { UseShellExecute = false });
        }
        catch (Exception ex) { Msg.Show(ex.Message, "Tunor", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    // ------------------------------------------------------------ backup

    private const string BackupFilter =
        "Резервная копия Tunor (*" + Backup.Extension + ")|*" + Backup.Extension + "|Все файлы|*.*";

    private void ExportBackup_Click(object sender, RoutedEventArgs e)
    {
        var ask = PasswordDialog.ForSaving();
        ask.ShowDialog();
        if (ask.Choice == PasswordChoice.Cancel) return;
        var password = ask.Choice == PasswordChoice.Password ? ask.Password : null;

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"tunor-{DateTime.Now:yyyy-MM-dd}{Backup.Extension}",
            DefaultExt = Backup.Extension,
            Filter = BackupFilter,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;

        try
        {
            System.IO.File.WriteAllBytes(dlg.FileName, Backup.Export(password));
            // What is on the disk is opened again, so a file that would not open is found now and not on the day it is needed.
            Backup.Read(System.IO.File.ReadAllBytes(dlg.FileName), password);
            BackupHint.Text = $"Сохранено: {dlg.FileName}" + (password == null ? " · без пароля, храните файл только у себя" : "");
        }
        catch (Exception ex)
        {
            BackupHint.Text = "";
            Msg.Show("Не удалось сохранить: " + ex.Message, "Tunor", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ImportBackup_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = BackupFilter,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;

        BackupContents? contents;
        try
        {
            if (new System.IO.FileInfo(dlg.FileName).Length > 70L * 1024 * 1024)
                throw new BackupException("Это не резервная копия Tunor: файл слишком большой.");
            contents = OpenBackup(System.IO.File.ReadAllBytes(dlg.FileName));
        }
        catch (Exception ex)
        {
            Msg.Show(ex.Message, "Tunor", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (contents == null) return;

        if (Msg.Show(Backup.Describe(contents), "Загрузить настройки?", MessageBoxButton.YesNo, MessageBoxImage.Question)
            != MessageBoxResult.Yes) return;

        string copies;
        try { copies = Backup.Apply(contents); }
        catch (Exception ex)
        {
            Reload();
            Msg.Show("Не загрузилось: " + ex.Message + "\n\nТо, что успели заменить, сохранено в папке data\\backups.",
                     "Tunor", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Reload();
        var note = "";
        try { ConfigGenerator.Generate(); }
        catch (Exception ex) { note += "\n\nКонфигурацию движка собрать не удалось: " + ex.Message; }
        if (Backup.ListsMissing())
        {
            _ = ListsUpdater.RunIfDueAsync(force: true);
            note += "\n\nСписков правил на этом компьютере ещё нет — они скачиваются в фоне.";
        }
        BackupHint.Text = $"Загружено · {DateTime.Now:HH:mm:ss}";
        Msg.Show("Настройки загружены." + (ProcessService.IsRunning ? " Чтобы они вступили в силу, перезапустите движок." : "")
                 + note + "\n\nПрежние файлы сохранены: " + copies,
                 "Tunor", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>Opens a backup, asking for the password as long as it is needed; null if the person gave up.</summary>
    private static BackupContents? OpenBackup(byte[] data)
    {
        string? password = null;
        while (true)
        {
            try { return Backup.Read(data, password); }
            catch (BackupException ex) when (ex.NeedsPassword || ex.WrongPassword)
            {
                var ask = PasswordDialog.ForOpening(ex.WrongPassword ? ex.Message : null);
                ask.ShowDialog();
                if (ask.Choice != PasswordChoice.Password) return null;
                password = ask.Password;
            }
        }
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
            Msg.Show(ex.Message, "Tunor", MessageBoxButton.OK, MessageBoxImage.Warning);
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
