using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Nyx.Services;

namespace Nyx.Views;

public partial class RulesPage : UserControl
{
    private ObservableCollection<RuleGroup> _groups = new();
    private RuleGroup? _current;
    private bool _suppress;

    public RulesPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Reload();
    }

    public void Reload()
    {
        _groups = new ObservableCollection<RuleGroup>(RulesService.Load());
        GroupsList.ItemsSource = _groups;
        if (_groups.Count > 0)
            GroupsList.SelectedIndex = 0;
        else
            ShowGroup(null);
        if (Query.Length > 0) ApplySearch();
    }

    private void GroupsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ShowGroup(GroupsList.SelectedItem as RuleGroup);
    }

    private void ShowGroup(RuleGroup? g)
    {
        _suppress = true;
        try
        {
            _current = g;
            if (g == null)
            {
                TagBox.IsEnabled = false;
                TypeBox.IsEnabled = false;
                InlinePanel.Visibility = Visibility.Collapsed;
                RemotePanel.Visibility = Visibility.Collapsed;
                EmptyHint.Visibility = Visibility.Visible;
                TagBox.Text = "";
                return;
            }

            EmptyHint.Visibility = Visibility.Collapsed;
            TagBox.IsEnabled = true;
            TypeBox.IsEnabled = true;
            TagBox.Text = g.Tag;
            TypeBox.SelectedIndex = g.IsRemote ? 1 : (g.IsLocal ? 2 : 0);

            InlinePanel.Visibility = Visibility.Collapsed;
            RemotePanel.Visibility = Visibility.Collapsed;
            LocalPanel.Visibility = Visibility.Collapsed;

            if (g.IsRemote)
            {
                RemotePanel.Visibility = Visibility.Visible;
                UrlBox.Text = g.Url;
                FormatBox.Text = g.Format;
                IntervalBox.Text = g.UpdateInterval;
            }
            else if (g.IsLocal)
            {
                LocalPanel.Visibility = Visibility.Visible;
                PathBox.Text = g.Path;
                LocalFormatBox.Text = g.Format;
                SourceUrlBox.Text = g.SourceUrl;
            }
            else
            {
                InlinePanel.Visibility = Visibility.Visible;
                KindDomain.IsChecked = g.ItemKind == RuleItemKind.Domain;
                KindProcess.IsChecked = g.ItemKind == RuleItemKind.ProcessName;
                ItemsList.ItemsSource = g.Items;
                UpdateItemPlaceholder();
                ApplyItemFilter();
            }
        }
        finally { _suppress = false; }
    }

    private void UpdateItemPlaceholder()
    {
        var isProc = _current?.ItemKind == RuleItemKind.ProcessName;
        NewItemBox.Tag = isProc ? "process_name (например, Discord.exe)" : "domain (например, example.com)";
        AddItemBtn.Content = isProc ? "+ Процесс" : "+ Домен";
    }

    private void TagBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppress || _current == null) return;
        _current.Tag = TagBox.Text;
        GroupsList.Items.Refresh();
    }

    private void TypeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || _current == null) return;
        var tag = (TypeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "inline";
        _current.Type = tag;
        ShowGroup(_current);
        GroupsList.Items.Refresh();
    }

    private void Kind_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppress || _current == null) return;
        _current.ItemKind = KindProcess.IsChecked == true
            ? RuleItemKind.ProcessName
            : RuleItemKind.Domain;
        UpdateItemPlaceholder();
        GroupsList.Items.Refresh();
    }

    private void RemoteField_Changed(object sender, TextChangedEventArgs e)
    {
        if (_suppress || _current == null) return;
        _current.Url = UrlBox.Text;
        _current.Format = FormatBox.Text;
        _current.UpdateInterval = IntervalBox.Text;
    }

    private void LocalField_Changed(object sender, TextChangedEventArgs e)
    {
        if (_suppress || _current == null) return;
        _current.Path = PathBox.Text;
        _current.Format = LocalFormatBox.Text;
        _current.SourceUrl = SourceUrlBox.Text;
    }

    private async void UpdateRulesets_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new UpdateRulesetsDialog(_groups.ToList())
        {
            Owner = Window.GetWindow(this)
        };
        dlg.ShowDialog();

        if (dlg.AnyDownloaded)
        {
            // The dialog mutated the groups in-place; reload UI and offer to save+apply
            GroupsList.Items.Refresh();
            if (_current != null) ShowGroup(_current);

            StatusText.Text = "Списки скачаны. Сохраняем и применяем…";
            try
            {
                RulesService.Save(_groups);
                var (ok, output) = await RulesService.ApplyAsync();
                StatusText.Text = ok
                    ? $"Готово · {DateTime.Now:HH:mm:ss}"
                    : "Ошибка генерации конфига";
                if (!ok)
                    MessageBox.Show(output, "Генерация config.json", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                StatusText.Text = "Ошибка: " + ex.Message;
            }
        }
    }

    private void AddItem_Click(object sender, RoutedEventArgs e) => AddItem();
    private void NewItemBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { AddItem(); e.Handled = true; }
    }

    private void AddItem()
    {
        if (_current == null || !_current.IsInline) return;
        var d = NewItemBox.Text.Trim();
        if (string.IsNullOrEmpty(d)) return;
        if (!_current.Items.Contains(d))
            _current.Items.Add(d);
        NewItemBox.Text = "";
        NewItemBox.Focus();
    }

    private void RemoveItem_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        if (sender is Button b && b.Tag is string item)
            _current.Items.Remove(item);
    }

    private void AddGroup_Click(object sender, RoutedEventArgs e)
    {
        var g = new RuleGroup { Tag = "new-group", Type = "inline", ItemKind = RuleItemKind.Domain };
        _groups.Add(g);
        GroupsList.SelectedItem = g;
    }

    private void DeleteGroup_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        var idx = _groups.IndexOf(_current);
        _groups.Remove(_current);
        if (_groups.Count > 0)
            GroupsList.SelectedIndex = Math.Min(idx, _groups.Count - 1);
        else
            ShowGroup(null);
    }

    private async void SaveApply_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Text = "Сохраняем…";
        try
        {
            RulesService.Save(_groups);
            StatusText.Text = "Пересобираем config.json…";
            var (ok, output) = await RulesService.ApplyAsync();
            StatusText.Text = ok
                ? $"Готово · {DateTime.Now:HH:mm:ss}"
                : "Ошибка генерации конфига";
            if (!ok)
                MessageBox.Show(output, "Генерация config.json", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            StatusText.Text = "Ошибка: " + ex.Message;
        }
    }

    // ---------------------------------------------------------------- search

    private string Query => SearchBox.Text.Trim();

    private static bool Hit(string? text, string q)
        => !string.IsNullOrEmpty(text) && text.Contains(q, StringComparison.OrdinalIgnoreCase);

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplySearch();

    /// <summary>
    /// Filters the group list by name and by content, and narrows the open group's items
    /// to the matching ones — finding which list holds a domain meant scrolling through
    /// every group by hand.
    /// </summary>
    private void ApplySearch()
    {
        var q = Query;
        var view = CollectionViewSource.GetDefaultView(_groups);
        if (view == null) return;

        view.Filter = q.Length == 0
            ? null
            : o => o is RuleGroup g && (Hit(g.Tag, q) || g.Items.Any(i => Hit(i, q)));

        var shown = view.Cast<object>().Count();
        SearchHint.Text = q.Length == 0 ? "Поиск по группам и их содержимому"
                        : shown == 0 ? "Ничего не найдено"
                        : $"Найдено групп: {shown}";

        // Keep a sensible selection: the current one if it still matches, else the first.
        if (GroupsList.SelectedItem == null || !view.Contains(GroupsList.SelectedItem))
            GroupsList.SelectedIndex = shown > 0 ? 0 : -1;

        ApplyItemFilter();
    }

    private void ApplyItemFilter()
    {
        if (_current == null || !_current.IsInline) return;
        var view = CollectionViewSource.GetDefaultView(_current.Items);
        if (view == null) return;

        var q = Query;
        // Filter the items only when the query is about them; a match on the group's
        // name alone should still show the whole group.
        view.Filter = q.Length == 0 || !_current.Items.Any(i => Hit(i, q))
            ? null
            : o => o is string s && Hit(s, q);
    }

    // ---------------------------------------------------------------- lookup

    private void LookupBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Lookup_Click(sender, e);
    }

    private async void Lookup_Click(object sender, RoutedEventArgs e)
    {
        var input = LookupBox.Text;
        if (string.IsNullOrWhiteSpace(input)) return;

        LookupBtn.IsEnabled = false;
        LookupResult.Visibility = Visibility.Visible;
        LookupResult.Text = "Проверяю…";
        LookupDetail.Visibility = Visibility.Collapsed;
        try
        {
            // The lookup reads rules.json: unsaved edits here would give a stale answer.
            RulesService.Save(_groups);
            var r = await RouteLookup.CheckAsync(input);

            LookupResult.Text = $"{r.Input}  →  {r.Route}";
            var detail = r.Why;
            if (r.Unchecked.Count > 0)
                detail += $"\nНе проверено (скачиваются движком сами): {string.Join(", ", r.Unchecked)}";
            if (r.Hits.Count == 0 && !r.Input.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                && r.Input.Count(c => c == '.') >= 2)
                detail += "\nДомены в группах совпадают только целиком: «example.com» не покрывает «www.example.com».";
            LookupDetail.Text = detail;
            LookupDetail.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            LookupResult.Text = "Не удалось проверить: " + ex.Message;
        }
        finally
        {
            LookupBtn.IsEnabled = true;
        }
    }
}
