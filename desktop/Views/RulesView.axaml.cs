using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Tunor.Services;

namespace Tunor.Desktop.Views;

/// <summary>
/// The domains and programs each tunnel takes.
///
/// The lists themselves are shared with the Windows app — RouteEditor writes them, and
/// writing one rebuilds config.json — so this page is only the showing and the asking.
/// The downloaded rule-sets are listed but not edited: they are other people's files,
/// refreshed from their source, and a change here would be lost on the next update.
/// </summary>
public partial class RulesView : UserControl
{
    /// <summary>One entry in a custom list. Key carries where it lives, for removal.</summary>
    public sealed record ItemRow(string Text, string Key);

    public sealed record GroupRow(string Title, string TunnelName, IBrush? TunnelBrush,
                                  string Count, List<ItemRow> Items);

    public sealed record SetRow(string Tag, string TunnelName, IBrush? TunnelBrush, string State);

    private sealed record TunnelChoice(string Id, string Title)
    {
        public override string ToString() => Title;
    }

    public RulesView()
    {
        InitializeComponent();
        Refresh();
    }

    private void Refresh()
    {
        var tunnels = TunnelService.Load();
        var groups = RulesService.Load();

        if (TunnelBox.ItemsSource == null)
        {
            TunnelBox.ItemsSource = tunnels.Select(t => new TunnelChoice(t.Id, t.Title)).ToList();
            TunnelBox.SelectedIndex = 0;
        }

        IBrush? BrushFor(string id)
        {
            var i = tunnels.FindIndex(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
            return Palette.TunnelBrush(i < 0 ? "" : tunnels[i].Id, i);
        }

        string NameOf(string id) =>
            tunnels.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase))?.Title ?? id;

        var custom = new List<GroupRow>();
        var sets = new List<SetRow>();

        foreach (var g in groups)
        {
            if (string.IsNullOrWhiteSpace(g.Tag)) continue;
            var slot = TunnelService.SlotOf(g.Tag, tunnels) ?? TunnelService.Warp;

            if (g.IsInline)
            {
                var isProcess = g.ItemKind == RuleItemKind.ProcessName;
                var items = g.Items.Where(i => !string.IsNullOrWhiteSpace(i))
                    .Select(i => new ItemRow(i.Trim(), g.Tag + "\u0000" + i)).ToList();
                custom.Add(new GroupRow(
                    isProcess ? "Программы" : "Домены",
                    NameOf(slot), BrushFor(slot),
                    Count(items.Count, isProcess), items));
            }
            else
            {
                var where = g.IsLocal
                    ? (LocalExists(g.Path) ? "скачан" : "ещё не скачан")
                    : "обновляется по сети";
                sets.Add(new SetRow(g.Tag, NameOf(slot), BrushFor(slot), where));
            }
        }

        // Custom lists in tunnel order, domains before programs, so the page reads the
        // same way the routing works.
        Groups.ItemsSource = custom
            .OrderBy(r => tunnels.FindIndex(t => t.Title == r.TunnelName))
            .ThenBy(r => r.Title)
            .ToList();
        Sets.ItemsSource = sets;
    }

    private static bool LocalExists(string path)
    {
        try { return path.Length > 0 && File.Exists(Path.Combine(Paths.AppRoot, path)); }
        catch { return false; }
    }

    private static string Count(int n, bool process)
    {
        var last = n % 10;
        var tens = n % 100;
        var word = process
            ? (tens is >= 11 and <= 14 ? "программ" : last == 1 ? "программа" : last is >= 2 and <= 4 ? "программы" : "программ")
            : (tens is >= 11 and <= 14 ? "доменов" : last == 1 ? "домен" : last is >= 2 and <= 4 ? "домена" : "доменов");
        return $"{n} {word}";
    }

    // ---------------------------------------------------------------- editing

    private void Add_Click(object? sender, RoutedEventArgs e)
    {
        var text = (EntryBox.Text ?? "").Trim();
        if (text.Length == 0) { StatusText.Text = "Введи домен или имя программы."; return; }
        if (TunnelBox.SelectedItem is not TunnelChoice pick) return;

        try
        {
            // A name with an extension is a program; anything else is a domain. That is
            // the same guess the Windows app makes, and the user can see the result.
            var isProcess = text.Contains('.') &&
                            Path.GetExtension(text).Length is > 1 and <= 5 &&
                            !text.Contains('/') && text.Count(c => c == '.') == 1 &&
                            Path.GetExtension(text).ToLowerInvariant() is ".exe" or ".app";

            if (isProcess) RouteEditor.SetProcessRoute(text, pick.Id);
            else RouteEditor.AddDomain(text, pick.Id);

            EntryBox.Text = "";
            Refresh();
            StatusText.Text = $"{text} → {pick.Title}. Перезапусти движок, чтобы применить.";
        }
        catch (Exception ex) { StatusText.Text = "Не удалось добавить: " + ex.Message; }
    }

    private void Remove_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string key }) return;
        var parts = key.Split('\u0000', 2);
        if (parts.Length != 2) return;

        try
        {
            var groups = RulesService.Load();
            var g = groups.FirstOrDefault(x => x.Tag == parts[0]);
            var item = g?.Items.FirstOrDefault(i => i == parts[1]);
            if (g == null || item == null) return;

            g.Items.Remove(item);
            RulesService.Save(groups);
            ConfigGenerator.Generate();
            Refresh();
            StatusText.Text = $"{parts[1]} удалён. Перезапусти движок, чтобы применить.";
        }
        catch (Exception ex) { StatusText.Text = "Не удалось удалить: " + ex.Message; }
    }
}
