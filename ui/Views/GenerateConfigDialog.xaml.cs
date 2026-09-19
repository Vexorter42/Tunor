using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Nyx.Views;

public partial class GenerateConfigDialog : Window
{
    private string _url = "";

    private GenerateConfigDialog()
    {
        InitializeComponent();
    }

    public static GenerateConfigDialog ForWarp() => Build(
        title: "Генерация WARP-конфига",
        intro: "WARP работает через AmneziaWG-обфускацию. Сгенерируй свежий конфиг в Telegram-боте с точно такими настройками:",
        settings: new (string, string)[]
        {
            ("Провайдер", "Cloudflare WARP"),
            ("Формат", "AmneziaWG"),
            ("Протокол", "AWG 1.0 + 2.0"),
            ("DNS", "Cloudflare"),
            ("Конечная точка", "По умолчанию"),
            ("Сайты", "Все сайты"),
        },
        steps: new[]
        {
            "Открой бота @warp_generator_bot (кнопка ниже).",
            "Выбери настройки как в таблице выше и сгенерируй конфиг.",
            "Скопируй выданный текст конфига (начинается с [Interface]).",
            "В приложении: раздел Конфиги → warp.conf → вставь конфиг → «Сохранить и применить».",
        },
        note: null,
        linkLabel: "Открыть @warp_generator_bot",
        url: "https://t.me/warp_generator_bot");

    public static GenerateConfigDialog ForGeo() => Build(
        title: "Генерация geo-конфига",
        intro: "Geo-туннель — это любой сервис, выдающий WireGuard-конфиг. Например ProtonVPN (бесплатный):",
        settings: null,
        steps: new[]
        {
            "Открой ProtonVPN → Downloads (кнопка ниже) или любой другой сервис с WireGuard.",
            "Сгенерируй/скачай WireGuard-конфиг под свою систему (Windows).",
            "Открой скачанный .conf файл в блокноте и скопируй его содержимое.",
            "В приложении: раздел Конфиги → geo.conf → вставь конфиг → «Сохранить и применить».",
        },
        note: "Подойдёт любой сервис, который выдаёт WireGuard-конфиги в том же формате, что и файл geo.conf — с секциями [Interface] и [Peer].",
        linkLabel: "Открыть ProtonVPN",
        url: "https://account.protonvpn.com/downloads");

    private static GenerateConfigDialog Build(
        string title, string intro,
        (string, string)[]? settings, string[] steps,
        string? note, string linkLabel, string url)
    {
        var d = new GenerateConfigDialog();
        d.TitleText.Text = title;
        d.IntroText.Text = intro;
        d._url = url;
        d.LinkButton.Content = linkLabel;

        if (settings != null)
        {
            d.SettingsCard.Visibility = Visibility.Visible;
            foreach (var (k, v) in settings)
                d.SettingsList.Children.Add(SettingRow(k, v));
        }

        int i = 1;
        foreach (var s in steps)
            d.StepsList.Children.Add(StepRow(i++, s));

        if (!string.IsNullOrEmpty(note))
        {
            d.NoteCard.Visibility = Visibility.Visible;
            d.NoteText.Text = "ℹ  " + note;
        }

        return d;
    }

    private static UIElement SettingRow(string key, string value)
    {
        var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var k = new TextBlock
        {
            Text = key,
            Foreground = (Brush)Application.Current.FindResource("TextDimBrush"),
            FontSize = 13,
        };
        var v = new TextBlock
        {
            Text = value,
            Foreground = (Brush)Application.Current.FindResource("TextBrush"),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };
        Grid.SetColumn(v, 1);
        grid.Children.Add(k);
        grid.Children.Add(v);
        return grid;
    }

    private static UIElement StepRow(int n, string text)
    {
        var grid = new Grid { Margin = new Thickness(0, 5, 0, 5) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var badge = new Border
        {
            Background = (Brush)Application.Current.FindResource("AccentBrush"),
            CornerRadius = new CornerRadius(10),
            Width = 22,
            Height = 22,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 1, 10, 0),
            Child = new TextBlock
            {
                Text = n.ToString(),
                Foreground = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x2E)),
                FontWeight = FontWeights.Bold,
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        var t = new TextBlock
        {
            Text = text,
            Foreground = (Brush)Application.Current.FindResource("TextBrush"),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(t, 1);
        grid.Children.Add(badge);
        grid.Children.Add(t);
        return grid;
    }

    private void LinkButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(_url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show("Не удалось открыть ссылку: " + ex.Message, "Ошибка",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
