using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Tunor.Desktop.Services;
using Tunor.Services;

namespace Tunor.Desktop.Views;

public partial class SettingsView : UserControl
{
    private AppSettings _settings = null!;
    private bool _loading;

    public SettingsView()
    {
        InitializeComponent();
        Load();
    }

    private void Load()
    {
        _loading = true;
        try
        {
            _settings = SettingsService.Load();
            PathText.Text = Paths.SettingsJson;
            ChkTun.IsChecked = _settings.Tun;
            ChkProxy.IsChecked = _settings.Proxy;
            ChkLogging.IsChecked = _settings.Logging;
            ChkWatchdog.IsChecked = _settings.Watchdog;
            ChkAutoLists.IsChecked = _settings.AutoUpdateLists;
            ChkAutoUpdate.IsChecked = _settings.AutoCheckUpdates;
            foreach (var item in CmbLogLevel.Items.OfType<ComboBoxItem>())
                if ((string?)item.Tag == AppSettings.NormalizeLevel(_settings.LogLevel)) CmbLogLevel.SelectedItem = item;
            ListsAgeText.Text = _settings.ListsUpdatedAt is { } at
                ? $"Последнее обновление: {at.ToLocalTime():d MMMM, HH:mm}"
                : "Ещё не обновлялись — можно скачать на странице «Правила».";
            AboutText.Text = $"Tunor {GetType().Assembly.GetName().Version?.ToString(3)} · "
                             + "© 2026 Vexorter42 · MIT" + Environment.NewLine + Paths.AppRoot;
            BuildFinalChoices();
            Hints();
        }
        finally { _loading = false; }
    }

    // ------------------------------------------------------------ backup

    private static FilePickerFileType BackupKind { get; } =
        new("Резервная копия Tunor") { Patterns = new[] { "*" + Backup.Extension } };

    private async void ExportBackup_Click(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not { } top) return;
        var ask = PasswordWindow.ForSaving();
        await ask.ShowDialog(this);
        if (ask.Choice == PasswordChoice.Cancel) return;
        var password = ask.Choice == PasswordChoice.Password ? ask.Password : null;

        try
        {
            var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Сохранить настройки",
                SuggestedFileName = $"tunor-{DateTime.Now:yyyy-MM-dd}{Backup.Extension}",
                DefaultExtension = Backup.Extension.TrimStart('.'),
                FileTypeChoices = new[] { BackupKind },
            });
            if (file == null) return;

            await using (var stream = await file.OpenWriteAsync())
            {
                if (stream.CanSeek) stream.SetLength(0);   // a longer file that was there must not leave its tail
                var bytes = Backup.Export(password);
                await stream.WriteAsync(bytes);
            }
            // What is on the disk is opened again, so a file that would not open is found now and not on the day it is needed.
            Backup.Read(await ReadAll(file), password);
            BackupHint.Text = $"Сохранено: {file.TryGetLocalPath() ?? file.Name}"
                              + (password == null ? " · без пароля, храните файл только у себя" : "");
        }
        catch (Exception ex) { BackupHint.Text = "Не удалось сохранить: " + ex.Message; }
    }

    private async void ImportBackup_Click(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not { } top) return;
        BackupContents? contents;
        try
        {
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Резервная копия Tunor",
                AllowMultiple = false,
                FileTypeFilter = new[] { BackupKind, FilePickerFileTypes.All },
            });
            if (files.FirstOrDefault() is not { } file) return;
            contents = await OpenBackup(await ReadAll(file));
        }
        catch (Exception ex)
        {
            BackupHint.Text = ex.Message;
            return;
        }
        if (contents == null) return;

        var ask = new ConfirmWindow("Загрузить настройки?", Backup.Describe(contents), "Загрузить");
        await ask.ShowDialog(this);
        if (!ask.Confirmed) return;

        string copies;
        try { copies = Backup.Apply(contents); }
        catch (Exception ex)
        {
            Load();
            BackupHint.Text = "Не загрузилось: " + ex.Message + ". То, что успели заменить, сохранено в папке data/backups.";
            return;
        }

        Load();
        var note = "";
        try { ConfigGenerator.Generate(); }
        catch (Exception ex) { note += " Конфигурацию движка собрать не удалось: " + ex.Message + "."; }
        if (Backup.ListsMissing())
        {
            _ = ListsUpdater.RunIfDueAsync(force: true);
            note += " Списков правил на этом компьютере ещё нет — они скачиваются в фоне.";
        }
        BackupHint.Text = "Настройки загружены." + (EngineService.IsRunning ? " Чтобы они вступили в силу, перезапустите движок." : "")
                          + note + " Прежние файлы сохранены: " + copies;
    }

    /// <summary>The file a person picked, read whole — a backup is small, and anything big is not one.</summary>
    private static async Task<byte[]> ReadAll(IStorageFile file)
    {
        await using var stream = await file.OpenReadAsync();
        if (stream.CanSeek && stream.Length > 70L * 1024 * 1024)
            throw new BackupException("Это не резервная копия Tunor: файл слишком большой.");
        using var buf = new MemoryStream();
        await stream.CopyToAsync(buf);
        return buf.ToArray();
    }

    /// <summary>Opens a backup, asking for the password as long as it is needed; null if the person gave up.</summary>
    private async Task<BackupContents?> OpenBackup(byte[] data)
    {
        string? password = null;
        while (true)
        {
            try { return Backup.Read(data, password); }
            catch (BackupException ex) when (ex.NeedsPassword || ex.WrongPassword)
            {
                var ask = PasswordWindow.ForOpening(ex.WrongPassword ? ex.Message : null);
                await ask.ShowDialog(this);
                if (ask.Choice != PasswordChoice.Password) return null;
                password = ask.Password;
            }
        }
    }

    /// <summary>"напрямую" and one choice per tunnel, same as the Windows app.</summary>
    private void BuildFinalChoices()
    {
        FinalChoices.Children.Clear();
        var slot = ConfigGenerator.FinalSlot(_settings.Final);

        Add("напрямую — никуда не заворачивать", null, slot.Length == 0);
        foreach (var t in TunnelService.Load())
            Add("через " + t.Title, t.Id, string.Equals(slot, t.Id, StringComparison.OrdinalIgnoreCase));

        void Add(string text, string? id, bool on)
        {
            var rb = new RadioButton { Content = text, GroupName = "Final", IsChecked = on, Tag = id };
            rb.IsCheckedChanged += Final_Changed;
            FinalChoices.Children.Add(rb);
        }
    }

    private void Final_Changed(object? sender, RoutedEventArgs e)
    {
        if (_loading || sender is not RadioButton { IsChecked: true } rb) return;
        _settings.Final = ConfigGenerator.FinalValue(rb.Tag as string);
        Persist();
    }

    private void Toggle_Changed(object? sender, RoutedEventArgs e)
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

    /// <summary>Says what each mode needs, so neither is switched on into a dead end.</summary>
    private void Hints()
    {
        TunHint.Text = OperatingSystem.IsMacOS()
            ? "Нужна служба с правами — её кнопка на главной. Без неё движок не запустится."
            : "Перехватывает весь трафик системы.";
        var doors = new System.Collections.Generic.List<string> { "1080 — по правилам" };
        if (_settings.Proxy)
            foreach (var t in TunnelService.Load())
                doors.Add($"{ConfigGenerator.DoorPort(t.Id, TunnelService.Load())} — всегда через {t.Title}");
        ProxyHint.Text = _settings.Proxy
            ? "SOCKS и HTTP на 127.0.0.1, порты: " + string.Join(", ", doors)
            : "Выключено — локального прокси не будет.";
    }

    private void Licenses_Click(object? sender, RoutedEventArgs e) => Open(Paths.LicensesDir);
    private void OpenRoot_Click(object? sender, RoutedEventArgs e) => Open(Paths.AppRoot);

    private void OpenLogs_Click(object? sender, RoutedEventArgs e) => Open(AppLog.Dir);

    private void LogLevel_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading || CmbLogLevel.SelectedItem is not ComboBoxItem { Tag: string level }) return;
        _settings.LogLevel = level;
        Persist();
    }

    /// <summary>Shows a folder in whatever this system calls its file manager.</summary>
    private void Open(string path)
    {
        try
        {
            System.IO.Directory.CreateDirectory(path);
            var file = OperatingSystem.IsMacOS() ? "open"
                     : OperatingSystem.IsWindows() ? "explorer.exe" : "xdg-open";
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                file, $"\"{path}\"") { UseShellExecute = false });
        }
        catch (Exception ex) { StatusText.Text = "Не удалось открыть: " + ex.Message; }
    }

    private void Persist()
    {
        try
        {
            SettingsService.Save(_settings);
            // Every option here ends up in config.json, so it is rebuilt at once — the
            // Windows app learned the same lesson: saving settings alone changed nothing.
            ConfigGenerator.Generate();
            Hints();
            StatusText.Text = "Сохранено. Перезапусти движок, чтобы применить.";
        }
        catch (Exception ex) { StatusText.Text = "Не удалось сохранить: " + ex.Message; }
    }
}
