using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Tunor.Services;

namespace Tunor.Desktop.Views;

/// <summary>What changed in the version that has just been installed, shown once. See <see cref="WhatsNew"/>.</summary>
public partial class WhatsNewWindow : DialogPanel
{
    private readonly string _version = "";

    public WhatsNewWindow() => InitializeComponent();

    public WhatsNewWindow(string version, IReadOnlyList<WhatsNew.Block> blocks) : this()
    {
        _version = version;
        TitleText.Text = "Tunor " + version;
        foreach (var b in blocks)
        {
            var text = new TextBlock { Text = b.Text, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
            if (b.Heading)
            {
                text.Classes.Add("h2");
                if (Body.Children.Count > 0) text.Margin = new Avalonia.Thickness(0, 10, 0, 0);
            }
            Body.Children.Add(text);
        }
    }

    private void Github_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var url = $"https://github.com/{UpdateConfig.Load().Repo}/releases/tag/v{_version}";
            var file = OperatingSystem.IsMacOS() ? "open" : OperatingSystem.IsWindows() ? "explorer.exe" : "xdg-open";
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(file, $"\"{url}\"") { UseShellExecute = false });
        }
        catch { }
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
