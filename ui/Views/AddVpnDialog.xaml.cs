using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Tunor.Services;

namespace Tunor.Views;

/// <summary>
/// Adds or edits a tunnel built from a share link. The link is parsed as it is typed, so
/// the user sees what was recognised — and what was wrong with it — before saving.
/// </summary>
public partial class AddVpnDialog : Window
{
    private readonly List<Tunnel> _tunnels;
    private readonly Tunnel? _editing;
    private ProxyLink.Parsed? _parsed;
    private bool _nameTouched;

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
            LinkBox.Text = editing.Url;
            NameBox.Text = editing.Title;
            _nameTouched = true;
            ViaWarp.IsChecked = editing.Detour.Length > 0;
        }
        Loaded += (_, _) => LinkBox.Focus();
    }

    private void Link_Changed(object sender, RoutedEventArgs e)
    {
        var text = LinkBox.Text.Trim();
        if (text.Length == 0)
        {
            ResultCard.Visibility = Visibility.Collapsed;
            ErrorCard.Visibility = Visibility.Collapsed;
            BtnSave.IsEnabled = false;
            _parsed = null;
            return;
        }

        _parsed = ProxyLink.Parse(text);
        if (_parsed.Ok)
        {
            ErrorCard.Visibility = Visibility.Collapsed;
            ResultCard.Visibility = Visibility.Visible;
            KindText.Text = "VLESS";
            SummaryText.Text = _parsed.Summary;
            // The name from the link is a suggestion until the user types one.
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
        if (_parsed is not { Ok: true }) return;
        var title = NameBox.Text.Trim();
        if (title.Length == 0) title = _parsed.Name;

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
            t.Url = LinkBox.Text.Trim();
            t.Detour = ViaWarp.IsChecked == true ? TunnelService.Warp : "";

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
