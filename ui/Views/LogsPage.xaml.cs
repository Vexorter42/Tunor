using System;
using System.Collections.Concurrent;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Nyx.Services;

namespace Nyx.Views;

public partial class LogsPage : UserControl
{
    // Trim with hysteresis: cutting on every flush would copy ~200 KB each time.
    private const int MaxChars = 200_000;
    private const int TrimToChars = 120_000;

    /// <summary>Lines allowed to queue between flushes; beyond this they are counted, not kept.</summary>
    private const int MaxPending = 4_000;

    private readonly ConcurrentQueue<string> _pending = new();

    // The on-screen log keeps only its tail, but the lines that explain a failure are
    // usually the first ones after a start. The session's opening lines are kept apart
    // so a saved log still has them after the tail has been trimmed.
    private const int HeadLines = 200;
    private readonly StringBuilder _head = new();
    private int _headCount;
    private bool _trimmed;
    private int _dropped;
    private readonly DispatcherTimer _flush;

    public LogsPage()
    {
        InitializeComponent();

        // The engine can emit hundreds of lines a second when the network is failing
        // (every blocked connection logs an error). Appending to the TextBox per line
        // from the dispatcher pegged the UI thread, so lines are batched instead.
        _flush = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        _flush.Tick += (_, _) => Flush();
        _flush.Start();

        ProcessService.LogReceived += OnLog;
    }

    private void OnLog(object? sender, LogEventArgs e)
    {
        if (_pending.Count >= MaxPending)
        {
            Interlocked.Increment(ref _dropped);
            return;
        }
        var tag = e.IsError ? "[err]" : "     ";
        _pending.Enqueue($"{DateTime.Now:HH:mm:ss} {tag} {e.Line}\r\n");
    }

    private void Flush()
    {
        if (_pending.IsEmpty && _dropped == 0) return;

        var sb = new StringBuilder();
        while (_pending.TryDequeue(out var line)) sb.Append(line);

        var dropped = Interlocked.Exchange(ref _dropped, 0);
        if (dropped > 0)
            sb.Append($"{DateTime.Now:HH:mm:ss} [err] [ui] поток логов слишком плотный — пропущено строк: {dropped}\r\n");

        var chunk = sb.ToString();
        if (_headCount < HeadLines)
        {
            foreach (var line in chunk.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries))
            {
                if (_headCount >= HeadLines) break;
                _head.Append(line).Append(Environment.NewLine);
                _headCount++;
            }
        }

        Log.AppendText(chunk);

        if (Log.Text.Length > MaxChars)
        {
            Log.Text = Log.Text.Substring(Log.Text.Length - TrimToChars);
            _trimmed = true;
        }

        if (AutoScroll.IsChecked == true)
        {
            Log.CaretIndex = Log.Text.Length;
            Log.ScrollToEnd();
        }
    }

    /// <summary>
    /// Writes the whole log to a file, so a problem report is the full log rather than a
    /// screenshot of its last screen — the first lines are usually the ones that matter.
    /// </summary>
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        Flush();   // include whatever is still queued

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"nyx-log-{DateTime.Now:yyyy-MM-dd_HH-mm}.txt",
            DefaultExt = ".txt",
            Filter = "Текст (*.txt)|*.txt",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;

        try
        {
            var header =
                $"Nyx {UpdateService.CurrentVersionString} · {DateTime.Now:yyyy-MM-dd HH:mm:ss} · " +
                $"{Environment.OSVersion.VersionString}\r\n" +
                (ProcessService.LastFailure is { } f ? $"Последняя ошибка: {f}\r\n" : "") +
                new string('-', 60) + "\r\n";
            var nl = Environment.NewLine;
            var body = _trimmed
                ? $"{_head}{nl}... середина лога пропущена ...{nl}{nl}{Log.Text}"
                : Log.Text;
            System.IO.File.WriteAllText(dlg.FileName, header + body, Encoding.UTF8);
            MessageBox.Show("Лог сохранён:\n" + dlg.FileName, "Nyx",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Не удалось сохранить: " + ex.Message, "Nyx",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        while (_pending.TryDequeue(out _)) { }
        Interlocked.Exchange(ref _dropped, 0);
        _head.Clear();
        _headCount = 0;
        _trimmed = false;
        Log.Clear();
    }
}
