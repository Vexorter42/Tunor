using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Tunor.Services;

namespace Tunor.Desktop.Views;

/// <summary>
/// One rule group, whole: its name, where its list comes from, and what is in it.
///
/// The name is not decoration — its first part names the tunnel the group routes into,
/// so "geo-my-sites" is geo's. On Windows that is a plain text box and a convention to
/// remember; here the tunnel is a picker beside it and the consequence is spelled out
/// under it, because a name that matches no tunnel silently routes nowhere.
/// </summary>
public partial class GroupWindow : Window
{
    private readonly List<RuleGroup> _all;
    private readonly RuleGroup? _editing;
    private readonly List<Tunnel> _tunnels;
    private bool _suppress;

    public bool Saved { get; private set; }

    /// <summary>What happened, for the page to show once this closes.</summary>
    public string Message { get; private set; } = "";

    private sealed record TunnelChoice(string Id, string Title)
    {
        public override string ToString() => Title;
    }

    private static readonly (string Type, string Title, string Hint)[] Types =
    {
        ("inline", "Свой список", "Домены и программы, которые ты вписываешь сам."),
        ("remote", "По ссылке", "Движок скачивает список сам и обновляет его по расписанию."),
        ("local",  "Файл на диске", "Готовый список рядом с движком; кнопка «Обновить списки» его перекачивает."),
    };

    public GroupWindow() : this(null, new List<RuleGroup>()) { }

    public GroupWindow(RuleGroup? editing, List<RuleGroup> all)
    {
        InitializeComponent();
        _all = all;
        _editing = editing;
        _tunnels = TunnelService.Load();

        _suppress = true;
        HeadText.Text = editing == null ? "Новая группа" : "Группа «" + editing.Tag + "»";
        BtnDelete.IsVisible = editing != null;

        TunnelBox.ItemsSource = _tunnels.Select(t => new TunnelChoice(t.Id, t.Title)).ToList();
        TypeBox.ItemsSource = Types.Select(t => t.Title).ToList();
        KindBox.ItemsSource = new[] { "домены", "программы" };

        var g = editing;
        TagBox.Text = g?.Tag ?? "";
        TypeBox.SelectedIndex = Math.Max(0, Array.FindIndex(Types,
            t => string.Equals(t.Type, g?.Type ?? "inline", StringComparison.OrdinalIgnoreCase)));
        KindBox.SelectedIndex = g?.ItemKind == RuleItemKind.ProcessName ? 1 : 0;
        UrlBox.Text = g?.Url ?? "";
        FormatBox.Text = g?.Format ?? "binary";
        IntervalBox.Text = g?.UpdateInterval ?? "1d";
        PathBox.Text = g?.Path ?? "";
        LocalFormatBox.Text = g?.Format ?? "binary";
        SourceUrlBox.Text = g?.SourceUrl ?? "";
        _suppress = false;

        ShowType();
        SyncTunnel();
    }

    // ---------------------------------------------------------------- the name

    /// <summary>Which tunnel the name currently points at, and what that means.</summary>
    private void SyncTunnel()
    {
        var tag = (TagBox.Text ?? "").Trim();
        var slot = TunnelService.SlotOf(tag, _tunnels);

        _suppress = true;
        TunnelBox.SelectedIndex = slot == null ? -1
            : _tunnels.FindIndex(t => string.Equals(t.Id, slot, StringComparison.OrdinalIgnoreCase));
        _suppress = false;

        var known = slot == null ? null : _tunnels.FirstOrDefault(
            t => string.Equals(t.Id, slot, StringComparison.OrdinalIgnoreCase));
        TagHint.Text = known != null
            ? $"Пойдёт в «{known.Title}» — так решает начало имени."
            : "Имя не начинается с имени туннеля, поэтому группа ни в один не пойдёт. "
              + "Выбери туннель справа — начало подставится само.";
    }

    private void Tag_Changed(object? sender, TextChangedEventArgs e)
    {
        if (_suppress || TagHint == null) return;
        SyncTunnel();
    }

    /// <summary>
    /// Puts the chosen tunnel at the front of the name, replacing whichever was there.
    /// The rest of the name is the user's and is left alone.
    /// </summary>
    private void Tunnel_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppress || TagBox == null) return;
        if (TunnelBox.SelectedItem is not TunnelChoice pick) return;

        var tag = (TagBox.Text ?? "").Trim();
        var old = TunnelService.SlotOf(tag, _tunnels);
        var rest = old != null ? tag[(old.Length + 1)..] : tag.TrimStart('-');
        if (rest.Length == 0) rest = "custom-domains";

        _suppress = true;
        TagBox.Text = pick.Id + "-" + rest;
        _suppress = false;
        SyncTunnel();
    }

    // ---------------------------------------------------------------- the type

    private void Type_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppress || InlinePanel == null) return;
        ShowType();
    }

    private string ChosenType => Types[Math.Max(0, TypeBox.SelectedIndex)].Type;

    private void ShowType()
    {
        var type = ChosenType;
        InlinePanel.IsVisible = type == "inline";
        RemotePanel.IsVisible = type == "remote";
        LocalPanel.IsVisible = type == "local";
        TypeHint.Text = Types[Math.Max(0, TypeBox.SelectedIndex)].Hint;
    }

    // ---------------------------------------------------------------- saving

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        var tag = (TagBox.Text ?? "").Trim();
        var type = ChosenType;

        var problem = Problem(tag, type);
        if (problem != null)
        {
            ProblemText.Text = problem;
            ProblemText.IsVisible = true;
            return;
        }

        var g = _editing ?? new RuleGroup();
        g.Tag = tag;
        g.Type = type;
        if (type == "inline")
        {
            g.ItemKind = KindBox.SelectedIndex == 1 ? RuleItemKind.ProcessName : RuleItemKind.Domain;
        }
        else if (type == "remote")
        {
            g.Url = (UrlBox.Text ?? "").Trim();
            g.Format = Or(FormatBox.Text, "binary");
            g.UpdateInterval = Or(IntervalBox.Text, "1d");
        }
        else
        {
            g.Path = (PathBox.Text ?? "").Trim();
            g.Format = Or(LocalFormatBox.Text, "binary");
            g.SourceUrl = (SourceUrlBox.Text ?? "").Trim();
        }

        if (_editing == null) _all.Add(g);
        try
        {
            RulesService.Save(_all);
            ConfigGenerator.Generate();
        }
        catch (Exception ex)
        {
            if (_editing == null) _all.Remove(g);
            ProblemText.Text = "Не сохранилось: " + ex.Message;
            ProblemText.IsVisible = true;
            return;
        }

        Saved = true;
        Message = (_editing == null ? $"Группа «{tag}» создана. " : $"Группа «{tag}» изменена. ")
                  + "Перезапусти движок, чтобы применить.";
        Close();
    }

    /// <summary>Why this cannot be saved, in plain words, or null when it can.</summary>
    private string? Problem(string tag, string type)
    {
        if (tag.Length == 0) return "Впиши имя группы.";
        if (tag.Any(char.IsWhiteSpace)) return "В имени не должно быть пробелов.";
        if (_all.Any(x => x != _editing && string.Equals(x.Tag, tag, StringComparison.OrdinalIgnoreCase)))
            return $"Группа с именем «{tag}» уже есть.";

        if (type == "remote")
        {
            var url = (UrlBox.Text ?? "").Trim();
            if (url.Length == 0) return "Впиши адрес списка.";
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return "Адрес должен начинаться с http:// или https://.";
        }
        if (type == "local" && (PathBox.Text ?? "").Trim().Length == 0)
            return "Впиши имя файла со списком.";
        return null;
    }

    private static string Or(string? text, string fallback)
        => string.IsNullOrWhiteSpace(text) ? fallback : text.Trim();

    private async void Delete_Click(object? sender, RoutedEventArgs e)
    {
        if (_editing == null) return;
        var ask = new ConfirmWindow($"Удалить группу «{_editing.Tag}»?",
            _editing.Items.Count > 0
                ? $"Вместе с ней исчезнут {_editing.Items.Count} записей из неё."
                : "Трафик, который шёл по этим правилам, пойдёт туда, куда отправляют остальные.");
        await ask.ShowDialog(this);
        if (!ask.Confirmed) return;

        _all.Remove(_editing);
        try
        {
            RulesService.Save(_all);
            ConfigGenerator.Generate();
        }
        catch (Exception ex)
        {
            _all.Add(_editing);
            ProblemText.Text = "Не удалилось: " + ex.Message;
            ProblemText.IsVisible = true;
            return;
        }

        Saved = true;
        Message = $"Группа «{_editing.Tag}» удалена. Перезапусти движок, чтобы применить.";
        Close();
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close();
}
