using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
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
public partial class AddVpnWindow : DialogPanel
{
    private readonly List<Tunnel> _tunnels;
    private readonly Tunnel? _editing;
    private ProxyLink.Parsed? _parsed;
    private List<SubNode> _nodes = new();
    private string _subTitle = "", _subTraffic = "";
    private bool _nameTouched;
    private Dictionary<SubNode, PingResult> _pings = new();   // what the last ping said, by server
    private List<SubNode> _view = new();                      // the servers in the order the list shows them
    private CancellationTokenSource? _pingCts;

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

        _pingCts?.Cancel();
        _pings = new Dictionary<SubNode, PingResult>();
        PingStatus.Text = "";
        BtnPing.IsEnabled = true;
        BtnFastest.IsEnabled = false;

        var pick = _nodes.FirstOrDefault(n => n.Name == chosen);
        RenderNodes(pick);
        RbPick.IsChecked = pick != null;
        RbAuto.IsChecked = pick == null;
        BtnSave.IsEnabled = true;
    }

    /// <summary>The list of servers, nearest first once they have been pinged; <paramref name="select"/> stays selected.</summary>
    private void RenderNodes(SubNode? select)
    {
        _view = _pings.Count > 0 ? NodePing.Sorted(_nodes, _pings) : _nodes.ToList();
        NodeBox.ItemsSource = _view.Select(n =>
            (_pings.TryGetValue(n, out var r) ? NodePing.Format(r) + "  ·  " : "") + $"{n.Name}  —  {n.Summary}").ToList();
        var at = select == null ? -1 : _view.IndexOf(select);
        NodeBox.SelectedIndex = at >= 0 ? at : 0;
    }

    private async void Ping_Click(object? sender, RoutedEventArgs e)
    {
        if (_nodes.Count == 0) return;
        _pingCts?.Cancel();
        var cts = _pingCts = new CancellationTokenSource();
        var selected = NodeBox.SelectedIndex is var s and >= 0 && s < _view.Count ? _view[s] : null;
        BtnPing.IsEnabled = BtnFastest.IsEnabled = false;
        PingStatus.Text = "проверяю…";
        try
        {
            var results = await NodePing.MeasureAllAsync(_nodes.ToList(),
                (done, total) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (!cts.IsCancellationRequested) PingStatus.Text = $"проверено {done} из {total}";
                }), cts.Token);
            if (cts.IsCancellationRequested) return;

            _pings = results;
            RenderNodes(selected);
            var best = NodePing.Sorted(_nodes, _pings).FirstOrDefault(n => _pings.TryGetValue(n, out var r) && r.Answered);
            var alive = _pings.Values.Count(r => r.Answered);
            PingStatus.Text = best == null
                ? "ни один сервер не ответил: либо они недоступны отсюда, либо работают только по UDP"
                : $"ответили {alive} из {_nodes.Count}. Быстрейший — {best.Name}, {NodePing.Format(_pings[best])}";
            BtnFastest.IsEnabled = best != null;
        }
        catch (Exception ex) { PingStatus.Text = "не получилось: " + ex.Message; }
        finally { if (!cts.IsCancellationRequested) BtnPing.IsEnabled = true; }
    }

    /// <summary>Picks the server that answered fastest, and switches the window to choosing a server by hand.</summary>
    private void Fastest_Click(object? sender, RoutedEventArgs e)
    {
        var best = _view.FindIndex(n => _pings.TryGetValue(n, out var p) && p.Answered);
        if (best < 0) return;
        RbPick.IsChecked = true;
        NodeBox.SelectedIndex = best;
    }

    public override void Dismiss()
    {
        _pingCts?.Cancel();
        base.Dismiss();
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
        var clip = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clip == null) return;
        try
        {
            var text = await clip.TryGetTextAsync();
            if (!string.IsNullOrWhiteSpace(text)) LinkBox.Text = text.Trim();
        }
        catch { /* another program may hold the clipboard; typing still works */ }
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        _pingCts?.Cancel();
        Close();
    }

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
                         && i < _view.Count
                    ? _view[i].Name
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
            _pingCts?.Cancel();
            Close();
        }
        catch (Exception ex)
        {
            ErrorCard.IsVisible = true;
            ErrorText.Text = "Не удалось сохранить: " + ex.Message;
        }
    }
}
