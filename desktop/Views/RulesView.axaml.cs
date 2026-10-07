using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Tunor.Services;

namespace Tunor.Desktop.Views;

/// <summary>
/// The domains and programs each tunnel takes.
///
/// The lists themselves are shared with the Windows app — RouteEditor writes them, and
/// writing one rebuilds config.json — so this page is only the showing and the asking.
/// Entries inside a downloaded rule-set are not edited here: they are other people's
/// files, refreshed from their source, and a change would be lost on the next update.
/// The group that holds them is editable, because that part is the user's: what it is
/// called, which tunnel it feeds and where it is fetched from.
/// </summary>
public partial class RulesView : UserControl
{
    /// <summary>One entry in a custom list. Key carries where it lives, for removal.</summary>
    public sealed record ItemRow(string Text, string Key);

    public sealed record GroupRow(string Tag, string Title, string TunnelName, IBrush? TunnelBrush,
                                  string Count, List<ItemRow> Items, string Empty)
    {
        public bool IsEmpty => Items.Count == 0;
    }

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

    private Window? Owner => TopLevel.GetTopLevel(this) as Window;

    private string Query => (SearchBox.Text ?? "").Trim();

    private static bool Hit(string? text, string q)
        => !string.IsNullOrEmpty(text) && text.Contains(q, StringComparison.OrdinalIgnoreCase);

    private void Refresh()
    {
        var tunnels = TunnelService.Load();
        var groups = RulesService.Load();

        if (TunnelBox.ItemsSource == null)
        {
            TunnelBox.ItemsSource = tunnels.Select(t => new TunnelChoice(t.Id, t.Title)).ToList();
            TunnelBox.SelectedIndex = 0;
            KindBox.ItemsSource = new[] { "домен", "программа" };
            KindBox.SelectedIndex = 0;
            LookupBox.PlaceholderText = OperatingSystem.IsWindows()
                ? "Сайт, ссылка, IP или программа вроде Discord.exe"
                : "Сайт, ссылка, IP или программа вроде Discord";
            Hint();
        }

        IBrush? BrushFor(string id)
        {
            var i = tunnels.FindIndex(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
            return Palette.TunnelBrush(i < 0 ? "" : tunnels[i].Id, i);
        }

        string NameOf(string id) =>
            tunnels.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase))?.Title ?? id;

        var q = Query;
        var custom = new List<GroupRow>();
        var sets = new List<SetRow>();

        foreach (var g in groups)
        {
            if (string.IsNullOrWhiteSpace(g.Tag)) continue;
            var slot = TunnelService.SlotOf(g.Tag, tunnels) ?? TunnelService.Warp;

            if (g.IsInline)
            {
                var isProcess = g.ItemKind == RuleItemKind.ProcessName;
                var all = g.Items.Where(i => !string.IsNullOrWhiteSpace(i)).ToList();
                // A search is about the entries as much as the groups: narrow the group
                // to what matched, but only when something in it did — a group whose
                // name alone matched still shows everything it holds.
                var inside = q.Length > 0 && all.Any(i => Hit(i, q));
                if (q.Length > 0 && !inside && !Hit(g.Tag, q)) continue;

                var items = (inside ? all.Where(i => Hit(i, q)) : all)
                    .Select(i => new ItemRow(i.Trim(), g.Tag + "\u0000" + i)).ToList();
                custom.Add(new GroupRow(
                    g.Tag,
                    isProcess ? "Программы" : "Домены",
                    NameOf(slot), BrushFor(slot),
                    Count(all.Count, isProcess), items,
                    q.Length > 0 ? "ничего не нашлось в этой группе" : "пока пусто"));
            }
            else
            {
                if (q.Length > 0 && !Hit(g.Tag, q)) continue;
                var where = g.IsLocal
                    ? (ConfigGenerator.LocalRuleSetExists(g.Path) ? "скачан" : "ещё не скачан")
                    : "обновляется по сети";
                sets.Add(new SetRow(g.Tag, NameOf(slot), BrushFor(slot), where));
            }
        }

        // Custom lists in tunnel order, domains before programs, so the page reads the
        // same way the routing works.
        Groups.ItemsSource = custom
            .OrderBy(r => tunnels.FindIndex(t => t.Title == r.TunnelName))
            .ThenBy(r => r.Title)
            .ThenBy(r => r.Tag)
            .ToList();
        Sets.ItemsSource = sets;

        var found = custom.Count + sets.Count;
        SearchHint.Text = found == 0 ? "Ничего не найдено" : $"Найдено групп: {found}";
        SearchHint.IsVisible = q.Length > 0;
    }

    private void Search_Changed(object? sender, TextChangedEventArgs e)
    {
        if (Groups != null) Refresh();
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

    /// <summary>
    /// What to type, in the terms of the system this is running on. A program is named
    /// differently on each: Discord.exe on Windows, plain Discord on macOS.
    /// </summary>
    private void Hint()
    {
        var process = KindBox.SelectedIndex == 1;
        var example = OperatingSystem.IsWindows() ? "Discord.exe" : "Discord";
        EntryBox.PlaceholderText = process ? $"например {example}" : "например youtube.com";
        AddHint.Text = process
            ? $"Имя программы, как её видит система: {example}. "
              + (OperatingSystem.IsWindows() ? "" : "Без расширения — на macOS его нет. ")
              + "Регистр не важен."
            : "Домен ловится вместе со всеми поддоменами: youtube.com поймает и m.youtube.com.";
    }

    private void Kind_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (AddHint != null) Hint();
    }

    // ---------------------------------------------------------------- where would it go

    private void Lookup_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { Lookup_Click(sender, e); e.Handled = true; }
    }

    private async void Lookup_Click(object? sender, RoutedEventArgs e)
    {
        var input = (LookupBox.Text ?? "").Trim();
        if (input.Length == 0) return;

        BtnLookup.IsEnabled = false;
        LookupResult.IsVisible = true;
        LookupResult.Text = "Проверяю…";
        LookupDetail.IsVisible = false;
        try
        {
            var r = await RouteLookup.CheckAsync(input);
            LookupResult.Text = $"{r.Input}  →  {r.Route}";
            var detail = r.Why;
            if (r.Unchecked.Count > 0)
                detail += "\nНе проверено (движок скачивает эти списки сам): "
                          + string.Join(", ", r.Unchecked);
            LookupDetail.Text = detail;
            LookupDetail.IsVisible = true;
        }
        catch (Exception ex) { LookupResult.Text = "Не удалось проверить: " + ex.Message; }
        finally { BtnLookup.IsEnabled = true; }
    }

    // ---------------------------------------------------------------- downloading

    private async void Update_Click(object? sender, RoutedEventArgs e)
    {
        BtnUpdate.IsEnabled = false;
        StatusText.Text = "скачиваю списки…";
        try
        {
            var groups = RulesService.Load();
            // Through the same mirror the updater uses: the original source is often
            // unreachable from here, which is rather the point of the whole program.
            var results = await RulesetDownloader.DownloadAllAsync(groups, "https://ghproxy.net/");
            RulesService.Save(groups);
            ConfigGenerator.Generate();
            ListsUpdater.MarkUpdated();
            Refresh();
            StatusText.Text = RulesetDownloader.FormatSummary(results)
                              + " Перезапусти движок, чтобы применить.";
        }
        catch (Exception ex) { StatusText.Text = "Не удалось обновить: " + ex.Message; }
        finally { BtnUpdate.IsEnabled = true; }
    }

    // ---------------------------------------------------------------- groups

    private async void AddGroup_Click(object? sender, RoutedEventArgs e) => await OpenGroup(null);

    private async void EditGroup_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag }) return;
        await OpenGroup(tag);
    }

    /// <summary>
    /// Opens the editor on a fresh copy of the list: the window saves the whole of it,
    /// so it has to be the list as it is on disk right now, not as this page last drew it.
    /// </summary>
    private async Task OpenGroup(string? tag)
    {
        var owner = Owner;
        if (owner == null) return;

        var all = RulesService.Load();
        var editing = tag == null ? null : all.FirstOrDefault(
            g => string.Equals(g.Tag, tag, StringComparison.OrdinalIgnoreCase));
        if (tag != null && editing == null) { Refresh(); return; }

        var dlg = new GroupWindow(editing, all);
        await dlg.ShowDialog(owner);
        if (!dlg.Saved) return;
        Refresh();
        StatusText.Text = dlg.Message;
    }

    // ---------------------------------------------------------------- editing

    private void Entry_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { Add_Click(sender, e); e.Handled = true; }
    }

    private void Add_Click(object? sender, RoutedEventArgs e)
    {
        var text = (EntryBox.Text ?? "").Trim();
        if (text.Length == 0) { StatusText.Text = "Введи домен или имя программы."; return; }
        if (TunnelBox.SelectedItem is not TunnelChoice pick) return;

        try
        {
            // Guessing by extension was wrong the moment this left Windows: a macOS
            // program is "Discord", not "Discord.exe", and that is indistinguishable
            // from a domain. The user says which.
            var isProcess = KindBox.SelectedIndex == 1;

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
