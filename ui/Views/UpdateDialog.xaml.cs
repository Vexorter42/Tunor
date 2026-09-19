using System;
using System.Windows;
using System.Windows.Media;
using Nyx.Services;

namespace Nyx.Views;

public partial class UpdateDialog : Window
{
    private readonly UpdateInfo _info;

    /// <summary>True once the installer has been launched — the caller must exit the app.</summary>
    public bool InstallStarted { get; private set; }

    public UpdateDialog(UpdateInfo info)
    {
        InitializeComponent();
        _info = info;

        CurrentVersionText.Text = "v" + info.Current;
        NewVersionText.Text = "v" + info.Latest;
        NotesText.Text = string.IsNullOrWhiteSpace(info.Notes)
            ? "Описание изменений не указано."
            : info.Notes;

        KindText.Text = info.KindLabel;
        var (fg, bg) = info.Kind switch
        {
            UpdateKind.Major => (Color.FromRgb(0xFF, 0xB4, 0x57), Color.FromArgb(0x2E, 0xFF, 0xB4, 0x57)),
            UpdateKind.Minor => (Color.FromRgb(0x3D, 0xDC, 0x5C), Color.FromArgb(0x2E, 0x3D, 0xDC, 0x5C)),
            _               => (Color.FromRgb(0x8B, 0x92, 0x9C), Color.FromArgb(0x2E, 0x8B, 0x92, 0x9C)),
        };
        KindText.Foreground = new SolidColorBrush(fg);
        KindBadge.Background = new SolidColorBrush(bg);
    }

    private void Later_Click(object sender, RoutedEventArgs e) => Close();

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        BtnUpdate.IsEnabled = false;
        BtnLater.IsEnabled = false;
        ProgressPanel.Visibility = Visibility.Visible;
        ProgressText.Text = "Скачиваем обновление…";
        Bar.Value = 0;

        var progress = new Progress<double>(p => Dispatcher.Invoke(() =>
        {
            Bar.Value = p * 100;
            ProgressPercent.Text = $"{p * 100:0}%";
        }));

        try
        {
            var (ok, message) = await UpdateService.DownloadAndApplyAsync(_info, progress);
            if (ok)
            {
                InstallStarted = true;
                ProgressText.Text = "Устанавливаем — приложение закроется…";
                ProgressPercent.Text = "";
                Bar.Value = 100;
                await System.Threading.Tasks.Task.Delay(900);
                Close();
            }
            else
            {
                ProgressText.Text = "Не удалось обновить";
                MessageBox.Show(message, "Обновление", MessageBoxButton.OK, MessageBoxImage.Warning);
                BtnUpdate.IsEnabled = true;
                BtnLater.IsEnabled = true;
            }
        }
        catch (Exception ex)
        {
            ProgressText.Text = "Ошибка";
            MessageBox.Show(ex.Message, "Обновление", MessageBoxButton.OK, MessageBoxImage.Error);
            BtnUpdate.IsEnabled = true;
            BtnLater.IsEnabled = true;
        }
    }
}
