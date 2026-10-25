using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Tunor.Services;

namespace Tunor.Views;

/// <summary>
/// The catalog of ready rule lists (<see cref="Catalog"/>): tick, add, and they are in the rules and downloaded.
/// The window opens with what is on the disk at once and then asks the repository whether there is a newer one.
/// </summary>
public partial class CatalogDialog : DialogPanel
{
    private readonly Dictionary<CheckBox, CatalogEntry> _boxes = new();
    private bool _busy;

    /// <summary>Something was added: the page behind has to read the rules again.</summary>
    public bool Changed { get; private set; }

    public CatalogDialog()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            await Populate(askRepository: false);
            await Populate(askRepository: true);
        };
    }

    public override void Dismiss()
    {
        if (!_busy) Close();
    }

    private async System.Threading.Tasks.Task Populate(bool askRepository)
    {
        _busy = true;
        BtnRefresh.IsEnabled = BtnAdd.IsEnabled = false;
        try
        {
            if (askRepository) Say("Проверяю, нет ли каталога посвежее…", false);
            var (entries, from) = await Catalog.LoadAsync(askRepository);
            Fill(entries);
            if (askRepository)
                Say(from switch
                {
                    Catalog.From.Repository => "Каталог обновлён из репозитория.",
                    Catalog.From.Saved => "Репозиторий не ответил — показан каталог, сохранённый в прошлый раз.",
                    _ => "Репозиторий не ответил — показан каталог, который идёт с программой.",
                }, false);
        }
        catch (Exception ex) { Say("Каталог не открылся: " + ex.Message, true); }
        finally
        {
            _busy = false;
            BtnRefresh.IsEnabled = BtnAdd.IsEnabled = true;
        }
    }

    /// <summary>Draws the list, keeping what was already ticked.</summary>
    private void Fill(List<CatalogEntry> entries)
    {
        var ticked = _boxes.Where(kv => kv.Key.IsChecked == true && kv.Key.IsEnabled).Select(kv => kv.Value.Id).ToHashSet();
        var have = RulesService.Load();
        ListPanel.Children.Clear();
        _boxes.Clear();

        foreach (var group in entries.GroupBy(e => e.Category))
        {
            ListPanel.Children.Add(new TextBlock
            {
                Text = group.Key, FontWeight = FontWeights.SemiBold, FontSize = 14,
                Margin = new Thickness(0, ListPanel.Children.Count == 0 ? 0 : 16, 0, 4),
            });
            foreach (var e in group)
            {
                var added = Catalog.IsAdded(e, have);
                var text = new StackPanel();
                text.Children.Add(new TextBlock { Text = e.Title + (added ? "  — уже в правилах" : ""), TextWrapping = TextWrapping.Wrap });
                if (e.About.Length > 0)
                    text.Children.Add(new TextBlock
                    {
                        Text = e.About + (e.Route == "geo" ? " · по умолчанию через geo" : ""),
                        Style = (Style)FindResource("Dim"), TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 1, 0, 0),
                    });
                var box = new CheckBox
                {
                    Content = text, Margin = new Thickness(0, 6, 0, 2),
                    IsChecked = added || ticked.Contains(e.Id), IsEnabled = !added,
                };
                _boxes[box] = e;
                ListPanel.Children.Add(box);
            }
        }
    }

    private void Say(string text, bool problem)
    {
        StatusText.Text = text;
        StatusText.Foreground = (System.Windows.Media.Brush)FindResource(problem ? "DangerBrush" : "TextDimBrush");
    }

    private string? Route => (RouteBox.SelectedItem as ComboBoxItem)?.Tag as string is { Length: > 0 } r ? r : null;

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (!_busy) await Populate(askRepository: true);
    }

    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var picks = _boxes.Where(kv => kv.Key.IsChecked == true && kv.Key.IsEnabled).Select(kv => kv.Value).ToList();
        if (picks.Count == 0) { Say("Ничего не отмечено.", false); return; }

        _busy = true;
        BtnRefresh.IsEnabled = BtnAdd.IsEnabled = false;
        Say($"Добавляю и скачиваю ({picks.Count})…", false);
        try
        {
            var done = await Catalog.AddAsync(picks, Route);
            Changed |= done.Count > 0;
            var ok = done.Count(d => d.Downloaded);
            Say($"Добавлено: {done.Count}, скачано: {ok}."
                + (ok < done.Count ? " Остальные подтянет кнопка «Обновить» на странице правил." : "")
                + " Перезапустите движок, чтобы правила вступили в силу.", ok < done.Count);
            Fill(_boxes.Values.ToList());
        }
        catch (Exception ex) { Say("Не добавилось: " + ex.Message, true); }
        finally
        {
            _busy = false;
            BtnRefresh.IsEnabled = BtnAdd.IsEnabled = true;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (!_busy) Close();
    }
}
