using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Nyx.Services;

namespace Nyx.Views;

public partial class AutostartPage : UserControl
{
    public AutostartPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    private void Refresh()
    {
        var installed = TaskService.IsInstalled();
        StateDot.Fill = (SolidColorBrush)FindResource(installed ? "SuccessBrush" : "DangerBrush");
        StateText.Text = installed ? "Установлено" : "Не установлено";
        BtnInstall.IsEnabled = !installed;
        BtnDelete.IsEnabled = installed;
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Text = "Создаём задачу…";
        var (ok, output) = await TaskService.InstallAsync();
        if (ok)
        {
            StatusText.Text = $"Готово · {DateTime.Now:HH:mm:ss}";
        }
        else
        {
            StatusText.Text = "Не удалось создать задачу";
            MessageBox.Show(string.IsNullOrWhiteSpace(output) ? "schtasks вернул ошибку" : output,
                "schtasks /create", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        Refresh();
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Text = "Удаляем задачу…";
        var (ok, output) = await TaskService.DeleteAsync();
        if (ok)
        {
            StatusText.Text = $"Готово · {DateTime.Now:HH:mm:ss}";
        }
        else
        {
            StatusText.Text = "Не удалось удалить задачу";
            MessageBox.Show(string.IsNullOrWhiteSpace(output) ? "schtasks вернул ошибку" : output,
                "schtasks /delete", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        Refresh();
    }
}
