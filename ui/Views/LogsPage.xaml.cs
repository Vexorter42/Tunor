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

        Log.AppendText(sb.ToString());

        if (Log.Text.Length > MaxChars)
            Log.Text = Log.Text.Substring(Log.Text.Length - TrimToChars);

        if (AutoScroll.IsChecked == true)
        {
            Log.CaretIndex = Log.Text.Length;
            Log.ScrollToEnd();
        }
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        while (_pending.TryDequeue(out _)) { }
        Interlocked.Exchange(ref _dropped, 0);
        Log.Clear();
    }
}
