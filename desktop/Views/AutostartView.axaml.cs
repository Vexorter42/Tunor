using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Tunor.Desktop.Services;

namespace Tunor.Desktop.Views;

/// <summary>
/// Starting with the system. macOS does it with a LaunchAgent the user owns; the page
/// says where the file is, because that is also how to undo it without the app.
/// </summary>
public partial class AutostartView : UserControl
{
    public AutostartView()
    {
        InitializeComponent();
        Refresh();
    }

    private void Refresh()
    {
        var on = AutostartService.Enabled;
        var supported = AutostartService.Supported;

        StateDot.Fill = Palette.Brush(on ? "AccentBrush" : "TextDimBrush");
        StateText.Text = !supported ? "Недоступно" : on ? "Включён" : "Выключен";
        DetailText.Text = !supported
            ? "На этой системе автозапуском управляет само приложение по-другому."
            : on
                ? "Tunor запустится сам при следующем входе в систему."
                : "Tunor придётся запускать вручную после входа.";

        BtnOn.IsEnabled = supported && !on;
        BtnOff.IsEnabled = supported && on;

        // Named out loud, because both are ordinary files the user owns: deleting one
        // turns autostart off even with the app nowhere in sight.
        HowText.Text = !supported ? "—"
            : OperatingSystem.IsLinux()
                ? "Через systemd — обычный файл в ~/.config/systemd/user, он принадлежит "
                  + "тебе и не требует пароля. Удалить файл значит выключить автозапуск, "
                  + "даже если приложения под рукой нет."
                : "Через LaunchAgent — обычный файл в ~/Library/LaunchAgents, он принадлежит "
                  + "тебе и не требует пароля. Удалить файл значит выключить автозапуск, даже "
                  + "если приложения под рукой нет.";
    }

    private void On_Click(object? sender, RoutedEventArgs e) => Apply(true);
    private void Off_Click(object? sender, RoutedEventArgs e) => Apply(false);

    private void Apply(bool want)
    {
        var (ok, msg) = want ? AutostartService.Enable() : AutostartService.Disable();
        StatusText.Text = ok ? "Автозапуск " + msg : "Не удалось: " + msg;
        Refresh();
    }
}
