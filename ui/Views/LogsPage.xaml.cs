using System;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Nyx.Services;

namespace Nyx.Views;

public partial class LogsPage : UserControl
{
    private const int MaxChars = 200_000;

    public LogsPage()
    {
        InitializeComponent();
        ProcessService.LogReceived += OnLog;
    }

    private void OnLog(object? sender, LogEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            var tag = e.IsError ? "[err]" : "     ";
            var line = $"{DateTime.Now:HH:mm:ss} {tag} {e.Line}\r\n";

            Log.AppendText(line);
            if (Log.Text.Length > MaxChars)
            {
                Log.Text = Log.Text.Substring(Log.Text.Length - MaxChars);
            }
            if (AutoScroll.IsChecked == true)
            {
                Log.CaretIndex = Log.Text.Length;
                Log.ScrollToEnd();
            }
        }));
    }

    private void Clear_Click(object sender, RoutedEventArgs e) => Log.Clear();
}
