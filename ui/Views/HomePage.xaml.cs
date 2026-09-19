using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Nyx.Services;

namespace Nyx.Views;

public partial class HomePage : UserControl
{
    public HomePage()
    {
        InitializeComponent();
        ProcessService.StatusChanged += (_, _) => Dispatcher.Invoke(UpdateState);
        Loaded += (_, _) =>
        {
            UpdateState();
            VersionText.Text = "Текущая версия: " + Services.UpdateService.CurrentVersionString;
        };
    }

    private void UpdateState()
    {
        var running = ProcessService.IsRunning;
        StateDot.Fill = (SolidColorBrush)FindResource(running ? "SuccessBrush" : "DangerBrush");
        StateText.Text = running ? "Запущен" : "Остановлен";
        // One button: "Запустить" when stopped, "Перезапустить" when running.
        BtnStart.Content = running ? "↻  Перезапустить" : "▶  Запустить";
        BtnStart.IsEnabled = true;
        BtnStop.IsEnabled = running;
    }

    private async void BtnStartOrRestart_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        try
        {
            if (ProcessService.IsRunning)
                await ProcessService.RestartAsync();
            else
                await ProcessService.StartAsync();
        }
        finally { SetBusy(false); UpdateState(); }
    }

    private async void BtnStop_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        try { await ProcessService.StopAsync(); }
        finally { SetBusy(false); UpdateState(); }
    }

    private void SetBusy(bool busy)
    {
        BtnStart.IsEnabled = !busy;
        BtnStop.IsEnabled = !busy && ProcessService.IsRunning;
    }

    private void BtnGenWarp_Click(object sender, RoutedEventArgs e)
    {
        var dlg = GenerateConfigDialog.ForWarp();
        dlg.Owner = Window.GetWindow(this);
        dlg.ShowDialog();
    }

    private void BtnGenGeo_Click(object sender, RoutedEventArgs e)
    {
        var dlg = GenerateConfigDialog.ForGeo();
        dlg.Owner = Window.GetWindow(this);
        dlg.ShowDialog();
    }

    private async void BtnCheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        BtnCheckUpdate.IsEnabled = false;
        UpdateStatus.Text = "Проверяем…";
        try
        {
            var info = await Services.UpdateService.CheckAsync();
            if (!string.IsNullOrEmpty(info.Error))
            {
                UpdateStatus.Text = "Ошибка проверки";
                MessageBox.Show(info.Error, "Обновление", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!info.Available)
            {
                UpdateStatus.Text = $"Установлена последняя версия ({info.Current})";
                return;
            }

            UpdateStatus.Text = $"Доступна версия {info.Latest}";

            var dlg = new UpdateDialog(info) { Owner = Window.GetWindow(this) };
            dlg.ShowDialog();

            if (dlg.InstallStarted)
                (Window.GetWindow(this) as MainWindow)?.ShutdownForUpdate();
            else
                UpdateStatus.Text = $"Доступна версия {info.Latest} — обновление отложено";
        }
        catch (Exception ex)
        {
            UpdateStatus.Text = "Ошибка";
            MessageBox.Show(ex.Message, "Обновление", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            BtnCheckUpdate.IsEnabled = true;
        }
    }

    private void BtnSupport_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://www.donationalerts.com/r/vexorter")
            { UseShellExecute = true });
        }
        catch { }
    }

    private void OpenAppFolder_Click(object s, RoutedEventArgs e) => Open(Paths.AppRoot);
    private void OpenSettingsFile_Click(object s, RoutedEventArgs e) => Open(Paths.SettingsJson);
    private void OpenConfigFile_Click(object s, RoutedEventArgs e) => Open(Paths.ConfigJson);
    private void OpenRulesFile_Click(object s, RoutedEventArgs e) => Open(Paths.RulesJson);

    private void Open(string path)
    {
        try
        {
            if (File.Exists(path) || Directory.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch { }
    }
}
