using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Tunor.Services;

namespace Tunor.Desktop.Views;

public partial class SettingsView : UserControl
{
    private AppSettings _settings = null!;
    private bool _loading;

    public SettingsView()
    {
        InitializeComponent();
        Load();
    }

    private void Load()
    {
        _loading = true;
        try
        {
            _settings = SettingsService.Load();
            PathText.Text = Paths.SettingsJson;
            ChkTun.IsChecked = _settings.Tun;
            ChkProxy.IsChecked = _settings.Proxy;
            ChkLogging.IsChecked = _settings.Logging;
            BuildFinalChoices();
            Hints();
        }
        finally { _loading = false; }
    }

    /// <summary>"напрямую" and one choice per tunnel, same as the Windows app.</summary>
    private void BuildFinalChoices()
    {
        FinalChoices.Children.Clear();
        var slot = ConfigGenerator.FinalSlot(_settings.Final);

        Add("напрямую — никуда не заворачивать", null, slot.Length == 0);
        foreach (var t in TunnelService.Load())
            Add("через " + t.Title, t.Id, string.Equals(slot, t.Id, StringComparison.OrdinalIgnoreCase));

        void Add(string text, string? id, bool on)
        {
            var rb = new RadioButton { Content = text, GroupName = "Final", IsChecked = on, Tag = id };
            rb.IsCheckedChanged += Final_Changed;
            FinalChoices.Children.Add(rb);
        }
    }

    private void Final_Changed(object? sender, RoutedEventArgs e)
    {
        if (_loading || sender is not RadioButton { IsChecked: true } rb) return;
        _settings.Final = ConfigGenerator.FinalValue(rb.Tag as string);
        Persist();
    }

    private void Toggle_Changed(object? sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.Tun = ChkTun.IsChecked == true;
        _settings.Proxy = ChkProxy.IsChecked == true;
        _settings.Logging = ChkLogging.IsChecked == true;
        Persist();
    }

    /// <summary>Says what each mode needs, so neither is switched on into a dead end.</summary>
    private void Hints()
    {
        TunHint.Text = OperatingSystem.IsMacOS()
            ? "Нужна служба с правами — её кнопка на главной. Без неё движок не запустится."
            : "Перехватывает весь трафик системы.";
        var doors = new System.Collections.Generic.List<string> { "1080 — по правилам" };
        if (_settings.Proxy)
            foreach (var t in TunnelService.Load())
                doors.Add($"{ConfigGenerator.DoorPort(t.Id, TunnelService.Load())} — всегда через {t.Title}");
        ProxyHint.Text = _settings.Proxy
            ? "SOCKS и HTTP на 127.0.0.1, порты: " + string.Join(", ", doors)
            : "Выключено — локального прокси не будет.";
    }

    private void Persist()
    {
        try
        {
            SettingsService.Save(_settings);
            // Every option here ends up in config.json, so it is rebuilt at once — the
            // Windows app learned the same lesson: saving settings alone changed nothing.
            ConfigGenerator.Generate();
            Hints();
            StatusText.Text = "Сохранено. Перезапусти движок, чтобы применить.";
        }
        catch (Exception ex) { StatusText.Text = "Не удалось сохранить: " + ex.Message; }
    }
}
