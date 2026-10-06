using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Tunor.Desktop.Services;

namespace Tunor.Desktop.Views;

/// <summary>
/// What the engine is saying, as it says it. The lines come from EngineLog, which the
/// engine process feeds; this only displays them.
/// </summary>
public partial class LogsView : UserControl
{
    public LogsView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => { EngineLog.Line += OnLine; Render(); };
        DetachedFromVisualTree += (_, _) => EngineLog.Line -= OnLine;
    }

    private void OnLine(object? sender, string line) => Dispatcher.UIThread.Post(Render);

    private void Render()
    {
        var lines = EngineLog.Snapshot();
        LogText.Text = lines.Length == 0
            ? "Пока пусто. Логи появятся, когда движок запустится."
            : string.Join("\n", lines);
        SubText.Text = lines.Length == 0
            ? "Вывод движка"
            : $"Вывод движка · строк {lines.Length} · файл {EngineLog.LogPath}";
        // Following the tail is what a log view is for, but not while something is being
        // read further up, so it is a switch rather than a rule.
        if (ChkFollow.IsChecked == true) Dispatcher.UIThread.Post(() => Scroll.ScrollToEnd());
    }

    private async void Copy_Click(object? sender, RoutedEventArgs e)
    {
        var clip = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clip == null) return;
        try
        {
            await clip.SetTextAsync(string.Join("\n", EngineLog.Snapshot()));
            StatusText.Text = "Скопировано.";
        }
        catch (Exception ex) { StatusText.Text = "Не удалось скопировать: " + ex.Message; }
    }

    private void Clear_Click(object? sender, RoutedEventArgs e)
    {
        EngineLog.Clear();
        Render();
        StatusText.Text = "Очищено. Файл на диске не тронут.";
    }
}
