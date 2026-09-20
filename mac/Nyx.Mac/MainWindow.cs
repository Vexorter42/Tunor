using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Nyx.Services;

namespace Nyx.Mac;

public class App : Application
{
    public override void Initialize()
    {
        // Fluent defaults to its light variant, which repainted secondary buttons white
        // over the dark palette.
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
        Styles.Add(new FluentTheme());
    }
}

/// <summary>
/// Same shape as the Windows window — sidebar plus content — so the two builds read as
/// one product.
/// </summary>
public class MainWindow : Window
{
    private readonly Ellipse _dot = new()
    {
        Width = 12, Height = 12, Fill = Skin.Danger, VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly TextBlock _state = new()
    {
        Text = "Остановлен", FontSize = 16, Foreground = Skin.Text,
        VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly TextBlock _pillText = new()
    {
        Text = "Отключено", Foreground = Skin.Danger, FontSize = 12,
    };
    private readonly Button _start = Skin.Primary("▶  Запустить");
    private readonly Button _stop = Skin.Secondary("■  Остановить");
    private readonly TextBlock _warn = Skin.Muted("");
    private readonly TextBox _log = new()
    {
        IsReadOnly = true,
        AcceptsReturn = true,
        Background = Skin.Input,
        Foreground = Skin.Text,
        BorderThickness = new Thickness(0),
        FontFamily = new FontFamily("Menlo, Monaco, monospace"),
        FontSize = 11.5,
        Height = 140,
        TextWrapping = TextWrapping.NoWrap,
    };

    public MainWindow()
    {
        Title = "Nyx";
        Width = 900;
        Height = 700;
        Background = Skin.Bg;

        var root = new Grid { ColumnDefinitions = new ColumnDefinitions("230,*") };
        root.Children.Add(Sidebar());

        var content = Home();
        Grid.SetColumn(content, 1);
        root.Children.Add(content);
        Content = root;

        _start.Click += async (_, _) =>
        {
            SetBusy(true);
            if (EngineService.IsRunning) await EngineService.RestartAsync();
            else await EngineService.StartAsync();
            SetBusy(false);
        };
        _stop.Click += async (_, _) =>
        {
            SetBusy(true);
            await EngineService.StopAsync();
            SetBusy(false);
        };

        EngineService.StatusChanged += (_, _) => Dispatcher.UIThread.Post(Refresh);
        EngineService.LogReceived += (_, line) => Dispatcher.UIThread.Post(() => Append(line));

        try { Paths.EnsureLayout(); } catch { /* first run on a locked-down home dir */ }
        Refresh();
    }

    private void SetBusy(bool busy)
    {
        _start.IsEnabled = !busy;
        _stop.IsEnabled = !busy && EngineService.IsRunning;
    }

    private void Refresh()
    {
        var running = EngineService.IsRunning;
        _dot.Fill = running ? Skin.Accent : Skin.Danger;
        _state.Text = running ? "Запущен" : "Остановлен";
        _pillText.Text = running ? "Подключено" : "Отключено";
        _pillText.Foreground = running ? Skin.Accent : Skin.Danger;
        _start.Content = running ? "↻  Перезапустить" : "▶  Запустить";
        _stop.IsEnabled = running;

        var warpOk = false;
        try { warpOk = ConfigGenerator.WarpConfigured; } catch { }
        _warn.Text = warpOk
            ? "Конфиг WARP на месте — трафик пойдёт через туннель по правилам."
            : "Конфиг WARP ещё не заполнен: весь трафик пойдёт напрямую, мимо туннеля. "
              + "Вставь конфиг в «Конфиги» или перетащи .conf на окно.";
        _warn.Foreground = warpOk ? Skin.Dim : Skin.Danger;
    }

    private void Append(LogLine line)
    {
        var tag = line.IsError ? "[err]" : "     ";
        _log.Text += $"{DateTime.Now:HH:mm:ss} {tag} {line.Text}\n";
        _log.CaretIndex = _log.Text?.Length ?? 0;
    }

    private Control Sidebar()
    {
        var panel = new StackPanel { Margin = new Thickness(22, 26, 22, 22) };

        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 11 };
        header.Children.Add(new Border
        {
            Width = 30, Height = 30, CornerRadius = new CornerRadius(8), Background = Skin.Accent,
        });
        header.Children.Add(Skin.H1("Nyx"));
        panel.Children.Add(header);

        panel.Children.Add(new Border
        {
            Background = Skin.AccentSoft,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(11, 4),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 16, 0, 0),
            Child = _pillText,
        });

        foreach (var (label, active) in new[]
                 {
                     ("Главная", true), ("Правила", false), ("Конфиги", false),
                     ("Настройки", false), ("Автозапуск", false), ("Логи", false),
                 })
        {
            panel.Children.Add(new Border
            {
                Background = active ? Skin.Input : Brushes.Transparent,
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14, 10),
                Margin = new Thickness(0, active ? 22 : 4, 0, 0),
                Child = new TextBlock
                {
                    Text = label,
                    Foreground = active ? Skin.Text : Skin.Dim,
                    FontSize = 13.5,
                    FontWeight = active ? FontWeight.SemiBold : FontWeight.Normal,
                },
            });
        }

        var wrap = new Grid();
        wrap.Children.Add(new Border { Background = Skin.Panel });
        wrap.Children.Add(panel);
        return wrap;
    }

    private Control Home()
    {
        var page = new StackPanel { Margin = new Thickness(32), Spacing = 16 };
        page.Children.Add(Skin.H1("Главная"));

        var sub = Skin.Muted("Управление прокси-сервисом");
        sub.Margin = new Thickness(0, -10, 0, 8);
        page.Children.Add(sub);

        var state = new StackPanel { Spacing = 12 };
        state.Children.Add(Skin.H2("Состояние"));
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        row.Children.Add(_dot);
        row.Children.Add(_state);
        state.Children.Add(row);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 8, 0, 0),
        };
        buttons.Children.Add(_start);
        buttons.Children.Add(_stop);
        state.Children.Add(buttons);
        page.Children.Add(Skin.Card(state));

        page.Children.Add(Skin.Card(_warn));

        // No TUN at this stage, so the user has to be told where to point things.
        var ports = new StackPanel { Spacing = 8 };
        ports.Children.Add(Skin.H2("Куда подключаться"));
        ports.Children.Add(Skin.Muted(
            "Туннель работает как локальный прокси: системные настройки не трогаются и "
            + "пароль не спрашивается. Укажи в браузере или приложении SOCKS5-прокси:"));
        foreach (var (name, port) in EngineService.ProxyPorts)
        {
            var line = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 2, 0, 0),
            };
            line.Children.Add(new TextBlock
            {
                Text = $"127.0.0.1:{port}", Foreground = Skin.Accent, FontSize = 12.5,
                FontFamily = new FontFamily("Menlo, Monaco, monospace"), Width = 130,
            });
            line.Children.Add(new TextBlock { Text = name, Foreground = Skin.Dim, FontSize = 12.5 });
            ports.Children.Add(line);
        }
        page.Children.Add(Skin.Card(ports));

        var logs = new StackPanel { Spacing = 10 };
        logs.Children.Add(Skin.H2("Логи"));
        logs.Children.Add(_log);
        page.Children.Add(Skin.Card(logs));

        return new ScrollViewer { Content = page };
    }
}
