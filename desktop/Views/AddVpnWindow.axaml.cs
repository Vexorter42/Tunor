using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Tunor.Services;

namespace Tunor.Desktop.Views;

/// <summary>
/// Adds or edits a tunnel built from a share link or a subscription — the same window the
/// Windows app has, in Avalonia. One box takes both: a vless:// link is read as it is
/// typed, an http(s) address is fetched on request.
/// </summary>
public partial class AddVpnWindow : Window
{
    private readonly List<Tunnel> _tunnels;
    private readonly Tunnel? _editing;
    private ProxyLink.Parsed? _parsed;
    private List<SubNode> _nodes = new();
    private string _subTitle = "", _subTraffic = "";
    private bool _nameTouched;

    /// <summary>True when the tunnel list was written and config.json rebuilt.</summary>
    public bool Saved { get; private set; }

    public AddVpnWindow() : this(null) { }

    public AddVpnWindow(Tunnel? editing)
    {
        InitializeComponent();
        _tunnels = TunnelService.Load();
        _editing = editing;

        if (editing != null)
        {
            TitleText.Text = "Изменить VPN";
            BtnSave.Content = "Сохранить";
            LinkBox.Text = editing.IsSubscription ? editing.Sub : editing.Url;
            NameBox.Text = editing.Title;
            _nameTouched = true;
            ViaWarp.IsChecked = editing.Detour.Length > 0;
            if (editing.IsSubscription && editing.Nodes.Count > 0)
            {
                _nodes = editing.Nodes;
                ShowNodes(editing.Node);
                FetchStatus.Text = editing.Fetched is { } at
                    ? $"список от {at.ToLocalTime():d MMMM, HH:mm}" : "";
            }
        }
    }

    private void Link_Changed(object? sender, TextChangedEventArgs e)
    {
        if (BtnSave == null) return;                  // fires once while the XAML is loading
        var text = (LinkBox.Text ?? "").Trim();
        _parsed = null;

        if (text.Length == 0)
        {
            ResultCard.IsVisible = ErrorCard.IsVisible = BtnFetch.IsVisible = false;
            BtnSave.IsEnabled = false;
            return;
        }

        if (Subscription.LooksLikeSubscription(text))
        {
            BtnFetch.IsVisible = true;
            // Keep what was already fetched when the address has not changed.
            if (_editing == null || _editing.Sub != text || _nodes.Count == 0)
            {
                _nodes = new List<SubNode>();
                NodePanel.IsVisible = ResultCard.IsVisible = ErrorCard.IsVisible = false;
                FetchStatus.Text = "нажми «Загрузить список»";
                BtnSave.IsEnabled = false;
            }
            return;
        }

        BtnFetch.IsVisible = false;
        FetchStatus.Text = "";
        _nodes = new List<SubNode>();
        NodePanel.IsVisible = false;

        _parsed = ProxyLink.Parse(text);
        if (_parsed.Ok)
        {
            ErrorCard.IsVisible = false;
            ResultCard.IsVisible = true;
            KindText.Text = "VLESS";
            SummaryText.Text = _parsed.Summary;
            if (!_nameTouched) NameBox.Text = _parsed.Name;
            BtnSave.IsEnabled = true;
        }
        else
        {
            ResultCard.IsVisible = false;
            ErrorCard.IsVisible = true;
            ErrorText.Text = _parsed.Problem;
            BtnSave.IsEnabled = false;
        }
    }

    private async void Fetch_Click(object? sender, RoutedEventArgs e)
    {
        BtnFetch.IsEnabled = false;
        FetchStatus.Text = "загружаю…";
        try
        {
            var r = await Subscription.FetchAsync((LinkBox.Text ?? "").Trim());
            if (!r.Ok)
            {
                _nodes = new List<SubNode>();
                NodePanel.IsVisible = ResultCard.IsVisible = false;
                ErrorCard.IsVisible = true;
                ErrorText.Text = r.Problem;
                FetchStatus.Text = "";
                BtnSave.IsEnabled = false;
                return;
            }
            _nodes = r.Nodes;
            _subTitle = r.Title;
            _subTraffic = r.Traffic;
            if (!_nameTouched && r.Title.Length > 0) NameBox.Text = r.Title;
            FetchStatus.Text = $"серверов: {r.Nodes.Count}";
            ShowNodes(_editing?.Node ?? "");
        }
        finally { BtnFetch.IsEnabled = true; }
    }

    private void ShowNodes(string chosen)
    {
        ErrorCard.IsVisible = false;
        ResultCard.IsVisible = NodePanel.IsVisible = true;
        KindText.Text = "ПОДПИСКА";

        var parts = new List<string> { $"{_nodes.Count} серверов" };
        if (_subTraffic.Length > 0) parts.Add(_subTraffic);
        SummaryText.Text = string.Join(" · ", parts);

        NodeBox.ItemsSource = _nodes.Select(n => $"{n.Name}  —  {n.Summary}").ToList();
        var at = _nodes.FindIndex(n => n.Name == chosen);
        RbPick.IsChecked = at >= 0;
        RbAuto.IsChecked = at < 0;
        NodeBox.SelectedIndex = at >= 0 ? at : 0;
        BtnSave.IsEnabled = true;
    }

    private void NodeMode_Changed(object? sender, RoutedEventArgs e)
    {
        if (NodeBox != null) NodeBox.IsEnabled = RbPick.IsChecked == true;
    }

    private void Name_Changed(object? sender, TextChangedEventArgs e)
    {
        if (NameBox is { IsFocused: true }) _nameTouched = true;
    }

    private async void Paste_Click(object? sender, RoutedEventArgs e)
    {
        var clip = GetTopLevel(this)?.Clipboard;
        if (clip == null) return;
        try
        {
            var text = await clip.TryGetTextAsync();
            if (!string.IsNullOrWhiteSpace(text)) LinkBox.Text = text.Trim();
        }
        catch { /* another program may hold the clipboard; typing still works */ }
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close();

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        var isSub = _nodes.Count > 0;
        if (!isSub && _parsed is not { Ok: true }) return;

        var title = (NameBox.Text ?? "").Trim();
        if (title.Length == 0)
            title = isSub ? (_subTitle.Length > 0 ? _subTitle : "Подписка") : _parsed!.Name;

        try
        {
            var t = _editing;
            if (t == null)
            {
                // The id is fixed at creation and never changes: rule groups are tagged
                // with it, so renaming later would orphan them.
                t = new Tunnel { Id = TunnelService.FreeId(title, _tunnels), Kind = Tunnel.Kinds.Vless };
                _tunnels.Add(t);
            }
            t.Title = title;
            t.Detour = ViaWarp.IsChecked == true ? TunnelService.Warp : "";

            if (isSub)
            {
                t.Sub = (LinkBox.Text ?? "").Trim();
                t.Url = "";
                t.Nodes = _nodes;
                t.Fetched = DateTime.UtcNow;
                t.Node = RbPick.IsChecked == true && NodeBox.SelectedIndex is var i and >= 0
                         && i < _nodes.Count
                    ? _nodes[i].Name
                    : "";                            // empty: the engine picks by latency
            }
            else
            {
                t.Sub = "";
                t.Nodes = new List<SubNode>();
                t.Node = "";
                t.Fetched = null;
                t.Url = (LinkBox.Text ?? "").Trim();
            }

            TunnelService.Save(_tunnels);
            ConfigGenerator.Generate();
            Saved = true;
            Close();
        }
        catch (Exception ex)
        {
            ErrorCard.IsVisible = true;
            ErrorText.Text = "Не удалось сохранить: " + ex.Message;
        }
    }
}
