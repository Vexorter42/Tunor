using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Tunor.Services;

namespace Tunor.Views;

/// <summary>What changed in the version that has just been installed, shown once. See <see cref="WhatsNew"/>.</summary>
public partial class WhatsNewDialog : DialogPanel
{
    private readonly string _version;

    public WhatsNewDialog(string version, IReadOnlyList<WhatsNew.Block> blocks)
    {
        InitializeComponent();
        _version = version;
        TitleText.Text = "Tunor " + version;

        var first = true;
        foreach (var b in blocks)
        {
            Body.Children.Add(new TextBlock
            {
                Text = b.Text,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)FindResource("TextBrush"),
                FontSize = b.Heading ? 14.5 : 13,
                FontWeight = b.Heading ? FontWeights.SemiBold : FontWeights.Normal,
                Margin = new Thickness(0, first ? 0 : b.Heading ? 18 : 8, 0, 0),
            });
            first = false;
        }
    }

    private void Github_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var repo = UpdateConfig.Load().Repo;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                $"https://github.com/{repo}/releases/tag/v{_version}") { UseShellExecute = true });
        }
        catch { }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
