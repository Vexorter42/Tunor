using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Tunor.Services;

namespace Tunor.Views;

/// <summary>
/// Adds or edits a tunnel built from a share link or a subscription. Both go in the same
/// box: a vless:// link is read as it is typed, an http(s) address is fetched on request,
/// and the user sees what was recognised before anything is saved.
/// </summary>
public partial class AddVpnDialog : Window
{
    private readonly List<Tunnel> _tunnels;
    private readonly Tunnel? _editing;
    private ProxyLink.Parsed? _parsed;        // set when the box holds a single link
    private List<SubNode> _nodes = new();     // set when it holds a subscription
    private string _subTitle = "", _subTraffic = "";
    private bool _nameTouched;

    /// <summary>A row of the server list.</summary>
    private sealed record NodeRow(SubNode Node, string Label);

    /// <summary>True when the tunnel list was written and config.json rebuilt.</summary>
    public bool Saved { get; private set; }

    public AddVpnDialog(Tunnel? editing = null)
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
                    ? $"список от {at.ToLocalTime():d MMMM, HH:mm}"
                    : "";
            }
        }
        Loaded += (_, _) => LinkBox.Focus();
    }

    // ------------------------------------------------------------ reading the box

    private void Link_Changed(object sender, RoutedEventArgs e)
    {
        var text = LinkBox.Text.Trim();
        _parsed = null;

        if (text.Length == 0)
        {
            ResultCard.Visibility = Visibility.Collapsed;
            ErrorCard.Visibility = Visibility.Collapsed;
            BtnFetch.Visibility = Visibility.Collapsed;
            BtnSave.IsEnabled = false;
            return;
        }

        // A subscription has to be downloaded before anything can be shown, so the box
        // only offers the button; a plain link is read right away.
        if (Subscription.LooksLikeSubscription(text))
        {
            BtnFetch.Visibility = Visibility.Visible;
            var sameAsLoaded = _editing != null && _editing.Sub == text && _nodes.Count > 0;
            if (!sameAsLoaded)
            {
                _nodes = new List<SubNode>();
                NodePanel.Visibility = Visibility.Collapsed;
                ResultCard.Visibility = Visibility.Collapsed;
                ErrorCard.Visibility = Visibility.Collapsed;
                FetchStatus.Text = "нажми «Загрузить список»";
                BtnSave.IsEnabled = false;
            }
            return;
        }

        BtnFetch.Visibility = Visibility.Collapsed;
        FetchStatus.Text = "";
        _nodes = new List<SubNode>();
        NodePanel.Visibility = Visibility.Collapsed;

        _parsed = ProxyLink.Parse(text);
        if (_parsed.Ok)
        {
            ErrorCard.Visibility = Visibility.Collapsed;
            ResultCard.Visibility = Visibility.Visible;
            KindBadgeText("VLESS");
            SummaryText.Text = _parsed.Summary;
            if (!_nameTouched) NameBox.Text = _parsed.Name;
            BtnSave.IsEnabled = true;
        }
        else
        {
            ResultCard.Visibility = Visibility.Collapsed;
            ErrorCard.Visibility = Visibility.Visible;
            ErrorText.Text = _parsed.Problem;
            BtnSave.IsEnabled = false;
        }
    }

    private void KindBadgeText(string text) => KindText.Text = text;

    private async void Fetch_Click(object sender, RoutedEventArgs e)
    {
        var url = LinkBox.Text.Trim();
        BtnFetch.IsEnabled = false;
        FetchStatus.Text = "загружаю…";
        try
        {
            var r = await Subscription.FetchAsync(url);
            if (!r.Ok)
            {
                _nodes = new List<SubNode>();
                NodePanel.Visibility = Visibility.Collapsed;
                ResultCard.Visibility = Visibility.Collapsed;
                ErrorCard.Visibility = Visibility.Visible;
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
        ErrorCard.Visibility = Visibility.Collapsed;
        ResultCard.Visibility = Visibility.Visible;
        NodePanel.Visibility = Visibility.Visible;
        KindBadgeText("ПОДПИСКА");

        var parts = new List<string> { $"{_nodes.Count} серверов" };
        if (_subTraffic.Length > 0) parts.Add(_subTraffic);
        SummaryText.Text = string.Join(" · ", parts);

        NodeBox.ItemsSource = _nodes
            .Select(n => new NodeRow(n, $"{n.Name}  —  {n.Summary}"))
            .ToList();

        var pick = _nodes.FirstOrDefault(n => n.Name == chosen);
        RbPick.IsChecked = pick != null;
        RbAuto.IsChecked = pick == null;
        NodeBox.SelectedIndex = pick != null ? _nodes.IndexOf(pick) : 0;
        BtnSave.IsEnabled = true;
    }

    private void NodeMode_Changed(object sender, RoutedEventArgs e)
    {
        if (NodeBox != null) NodeBox.IsEnabled = RbPick.IsChecked == true;
    }

    // ------------------------------------------------------------ the rest

    private void Name_Changed(object sender, RoutedEventArgs e)
    {
        if (NameBox.IsKeyboardFocusWithin) _nameTouched = true;
    }

    private void Paste_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Clipboard.ContainsText()) LinkBox.Text = Clipboard.GetText().Trim();
        }
        catch { /* another program may hold the clipboard open; typing still works */ }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var isSub = _nodes.Count > 0;
        if (!isSub && _parsed is not { Ok: true }) return;

        var title = NameBox.Text.Trim();
        if (title.Length == 0) title = isSub ? (_subTitle.Length > 0 ? _subTitle : "Подписка") : _parsed!.Name;

        try
        {
            var t = _editing;
            if (t == null)
            {
                t = new Tunnel
                {
                    // The id is fixed when the tunnel is created and never changes: rule
                    // groups are tagged with it, so renaming it later would orphan them.
                    Id = TunnelService.FreeId(title, _tunnels),
                    Kind = Tunnel.Kinds.Vless,
                };
                _tunnels.Add(t);
            }
            t.Title = title;
            t.Detour = ViaWarp.IsChecked == true ? TunnelService.Warp : "";

            if (isSub)
            {
                t.Sub = LinkBox.Text.Trim();
                t.Url = "";
                t.Nodes = _nodes;
                t.Fetched = DateTime.UtcNow;
                t.Node = RbPick.IsChecked == true && NodeBox.SelectedItem is NodeRow row
                    ? row.Node.Name
                    : "";                       // empty: the engine picks by latency
            }
            else
            {
                t.Sub = "";
                t.Nodes = new List<SubNode>();
                t.Node = "";
                t.Fetched = null;
                t.Url = LinkBox.Text.Trim();
            }

            TunnelService.Save(_tunnels);
            ConfigGenerator.Generate();
            Saved = true;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Не удалось сохранить:\n\n" + ex.Message,
                "Свой VPN", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
