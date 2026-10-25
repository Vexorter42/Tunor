using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Tunor.Services;

namespace Tunor.Views;

/// <summary>
/// Which address WARP is reached at. See <see cref="WarpEndpoint"/>: the engine is restarted on the
/// chosen one, and what comes out — data centre, speed, whether the site opens — is shown; the old
/// address can be put back.
/// </summary>
public partial class WarpEndpointDialog : DialogPanel
{
    private readonly string? _original;
    private bool _busy;

    public WarpEndpointDialog()
    {
        InitializeComponent();
        _original = WarpEndpoint.Current();
        CurrentText.Text = _original ?? "— (WARP не настроен)";

        var options = WarpEndpoint.Options(_original);
        foreach (var o in options) Choice.Items.Add(o);
        Choice.SelectedIndex = Math.Min(1, options.Count - 1);

        if (_original == null)
            Block("В warp.conf нет адреса: сначала настройте WARP.");
        else if (!EngineState.IsRunning)
            Block("Движок не запущен. Запустите его на главной: адрес проверяется через работающий туннель.");
    }

    public override void Dismiss()
    {
        if (!_busy) Close();
    }

    private void Block(string why)
    {
        Say(why, false);
        BtnTry.IsEnabled = BtnNext.IsEnabled = Choice.IsEnabled = false;
    }

    private void Say(string text, bool problem)
    {
        ResultText.Text = text;
        ResultText.Foreground = (Brush)FindResource(problem ? "DangerBrush" : "TextBrush");
        ResultText.Visibility = Visibility.Visible;
    }

    private void Busy(bool on)
    {
        _busy = on;
        BtnTry.IsEnabled = BtnNext.IsEnabled = Choice.IsEnabled = !on;
        BtnRevert.IsEnabled = !on && Differs();
    }

    private bool Differs() => _original != null
        && !string.Equals(WarpEndpoint.Current(), _original, StringComparison.OrdinalIgnoreCase);

    private async void Try_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || Choice.SelectedItem is not string endpoint) return;
        Busy(true);
        Say("Меняю адрес и перезапускаю движок…", false);
        try
        {
            var t = await WarpEndpoint.TryAsync(endpoint, SiteBox.Text);
            Say(t.Report(), !t.Started || t.Warp is not { Status: "ok" } || t.Site is { Status: "fail" });
        }
        catch (Exception ex) { Say("Не вышло: " + ex.Message, true); }
        finally
        {
            CurrentText.Text = WarpEndpoint.Current() ?? "—";
            Busy(false);
        }
    }

    /// <summary>Takes the next address in the list and tries it: the way to go through them one by one.</summary>
    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        Choice.SelectedIndex = (Choice.SelectedIndex + 1) % Choice.Items.Count;
        Try_Click(sender, e);
    }

    private async void Revert_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _original == null) return;
        Busy(true);
        Say("Возвращаю прежний адрес и перезапускаю движок…", false);
        try
        {
            var problem = await WarpEndpoint.SwitchAsync(_original);
            Say(problem ?? $"Адрес возвращён: {_original}.", problem != null);
        }
        catch (Exception ex) { Say("Не вышло: " + ex.Message, true); }
        finally
        {
            CurrentText.Text = WarpEndpoint.Current() ?? "—";
            Busy(false);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (!_busy) Close();
    }
}
