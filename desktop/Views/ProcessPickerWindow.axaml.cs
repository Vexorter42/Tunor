using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Tunor.Desktop.Services;

namespace Tunor.Desktop.Views;

/// <summary>
/// Picks a running program, so the rule carries the name the engine will actually match
/// rather than one typed from memory.
/// </summary>
public partial class ProcessPickerWindow : Window
{
    /// <summary>A row, flattened for binding.</summary>
    public sealed record Row(string Title, string Path, string Extra, ProcessEntry Entry);

    private List<ProcessEntry> _all = new();

    /// <summary>The chosen program, or null if cancelled.</summary>
    public ProcessEntry? Picked { get; private set; }

    public ProcessPickerWindow()
    {
        InitializeComponent();
        Opened += async (_, _) =>
        {
            _all = await ProcessList.CollectAsync();
            LoadingText.IsVisible = false;
            Apply();
            FilterBox.Focus();
        };
    }

    private void Apply()
    {
        var q = (FilterBox.Text ?? "").Trim();
        var showAll = ShowAll.IsChecked == true;
        var rows = _all
            .Where(e => showAll || e.IsApp)
            .Where(e => q.Length == 0
                        || e.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
                        || e.Names.Any(n => n.Contains(q, StringComparison.OrdinalIgnoreCase)))
            .Select(e => new Row(e.Title, e.Path, e.Extra, e))
            .ToList();
        List.ItemsSource = rows;

        // With the system processes hidden there may be genuinely nothing to show, and an
        // empty box without a word looks broken.
        LoadingText.IsVisible = rows.Count == 0;
        LoadingText.Text = q.Length > 0
            ? "Ничего не нашлось"
            : showAll ? "Ничего не запущено" : "Снаружи системы ничего не запущено — поставь галочку ниже";
    }

    private void Filter_Changed(object? sender, TextChangedEventArgs e)
    {
        if (List != null) Apply();
    }

    private void ShowAll_Changed(object? sender, RoutedEventArgs e)
    {
        if (List != null) Apply();
    }

    private void List_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        => BtnPick.IsEnabled = List.SelectedItem != null;

    private void List_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (List.SelectedItem != null) Pick_Click(sender, e);
    }

    private void Pick_Click(object? sender, RoutedEventArgs e)
    {
        Picked = (List.SelectedItem as Row)?.Entry;
        Close();
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close();
}
