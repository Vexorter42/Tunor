using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Tunor.Desktop.Services;
using Tunor.Services;

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

    /// <summary>
    /// The whole state of the install in one text, for when something does not work and
    /// the log alone does not say why. Keys, personal lists and the user name are stripped
    /// by DiagnosticsReport itself: it is written to be handed to someone else.
    /// </summary>
    private async void Report_Click(object? sender, RoutedEventArgs e)
    {
        StatusText.Text = "собираю отчёт…";
        try
        {
            // The log tail goes in with it, and the engine is asked to check the config
            // — the same two things the Windows report carries.
            var tail = string.Join(Environment.NewLine, EngineLog.Snapshot().TakeLast(200));
            var text = await DiagnosticsReport.BuildAsync(tail, runCheck: true);
            if (await Write(text, "отчёт") is { } where) StatusText.Text = "Отчёт сохранён: " + where;
        }
        catch (Exception ex) { StatusText.Text = "Не удалось собрать отчёт: " + ex.Message; }
    }

    private async void Save_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var text = string.Join("\n", EngineLog.Snapshot());
            if (await Write(text, "лог") is { } where) StatusText.Text = "Сохранено: " + where;
        }
        catch (Exception ex) { StatusText.Text = "Не удалось сохранить: " + ex.Message; }
    }

    /// <summary>
    /// Asks where to put the text and writes it. A cancelled save puts it on the
    /// clipboard instead: it was worth producing, and losing it would waste the asking.
    /// </summary>
    private async Task<string?> Write(string text, string what)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return null;

        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = $"Сохранить {what}",
            SuggestedFileName = $"tunor-{what}-{DateTime.Now:yyyy-MM-dd-HHmm}.txt",
            DefaultExtension = "txt",
        });
        if (file != null)
        {
            await using var stream = await file.OpenWriteAsync();
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(text);
            return file.TryGetLocalPath() ?? file.Name;
        }

        var clip = top.Clipboard;
        if (clip != null)
        {
            await clip.SetTextAsync(text);
            StatusText.Text = $"Сохранение отменено — {what} скопирован в буфер.";
        }
        return null;
    }

    private void Clear_Click(object? sender, RoutedEventArgs e)
    {
        EngineLog.Clear();
        Render();
        StatusText.Text = "Очищено. Файл на диске не тронут.";
    }
}
