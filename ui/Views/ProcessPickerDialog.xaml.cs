using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Nyx.Views;

public sealed class ProcessEntry
{
    public string Exe { get; init; } = "";
    public string Path { get; init; } = "";
    public string Title { get; init; } = "";
    public bool HasWindow { get; init; }
}

/// <summary>
/// Picks a running program. The rule is written for its exact file name as it is on
/// disk — the engine's process matching is case-sensitive, and typing the name by hand
/// is how rules like "telegram.exe" ended up never matching "Telegram.exe".
/// </summary>
public partial class ProcessPickerDialog : Window
{
    private List<ProcessEntry> _all = new();

    /// <summary>The chosen program, or null if cancelled.</summary>
    public ProcessEntry? Picked { get; private set; }

    public ProcessPickerDialog()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            // Reading every process's image path takes a moment; keep the window responsive.
            _all = await Task.Run(Collect);
            LoadingText.Visibility = Visibility.Collapsed;
            Apply();
            FilterBox.Focus();
        };
    }

    private static List<ProcessEntry> Collect()
    {
        var own = Process.GetCurrentProcess().Id;
        var byExe = new Dictionary<string, ProcessEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id == own || p.Id <= 4) continue;   // ourselves, Idle, System
                string path;
                try { path = p.MainModule?.FileName ?? ""; }
                catch { path = ""; }   // protected process: fall back to the name
                var exe = path.Length > 0 ? System.IO.Path.GetFileName(path) : p.ProcessName + ".exe";

                var hasWindow = p.MainWindowHandle != IntPtr.Zero && !string.IsNullOrWhiteSpace(p.MainWindowTitle);
                var entry = new ProcessEntry
                {
                    Exe = exe,
                    Path = path,
                    Title = hasWindow ? p.MainWindowTitle : (path.Length > 0 ? path : "фоновый процесс"),
                    HasWindow = hasWindow,
                };
                // One row per program; prefer the instance that has a window.
                if (!byExe.TryGetValue(exe, out var seen) || (!seen.HasWindow && hasWindow))
                    byExe[exe] = entry;
            }
            catch { /* the process exited while we looked at it */ }
            finally { p.Dispose(); }
        }

        return byExe.Values
            .OrderByDescending(e => e.HasWindow)
            .ThenBy(e => e.Exe, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private void Apply()
    {
        var q = FilterBox.Text.Trim();
        var showAll = ShowAll.IsChecked == true;
        List.ItemsSource = _all
            .Where(e => showAll || e.HasWindow)
            .Where(e => q.Length == 0
                        || e.Exe.Contains(q, StringComparison.OrdinalIgnoreCase)
                        || e.Title.Contains(q, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private void Filter_Changed(object sender, TextChangedEventArgs e) => Apply();
    private void ShowAll_Changed(object sender, RoutedEventArgs e) => Apply();

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => BtnPick.IsEnabled = List.SelectedItem != null;

    private void List_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (List.SelectedItem != null) Pick_Click(sender, e);
    }

    private void Pick_Click(object sender, RoutedEventArgs e)
    {
        Picked = List.SelectedItem as ProcessEntry;
        DialogResult = Picked != null;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
