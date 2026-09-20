using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using Nyx.Services;

namespace Nyx.Views;

public partial class ImportConfDialog : Window
{
    private readonly string _path;

    /// <summary>True when the config was written and config.json rebuilt.</summary>
    public bool Applied { get; private set; }
    public ConfKind AppliedKind { get; private set; }
    public bool ShouldRestart { get; private set; }

    public ImportConfDialog(string path, ConfDetection detection)
    {
        InitializeComponent();
        _path = path;

        FileNameText.Text = Path.GetFileName(path);

        var isWarp = detection.Kind == ConfKind.Warp;
        KindText.Text = isWarp ? "WARP" : "GEO";
        ConfidenceText.Text = detection.Confident ? "определено точно" : "определено предположительно";

        var (fg, bg) = isWarp
            ? (Color.FromRgb(0x3D, 0xDC, 0x5C), Color.FromArgb(0x38, 0x3D, 0xDC, 0x5C))
            : (Color.FromRgb(0x6F, 0xB4, 0xFF), Color.FromArgb(0x38, 0x6F, 0xB4, 0xFF));
        KindText.Foreground = new SolidColorBrush(fg);
        KindBadge.Background = new SolidColorBrush(bg);

        ReasonText.Text = "Определено по: " + detection.Reason;
        EndpointText.Text = string.IsNullOrWhiteSpace(detection.Endpoint) ? "—" : detection.Endpoint;
        AddressText.Text = string.IsNullOrWhiteSpace(detection.Address) ? "—" : detection.Address;
        AwgText.Text = detection.AwgVersions;

        RbWarp.IsChecked = isWarp;
        RbGeo.IsChecked = !isWarp;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        var kind = RbGeo.IsChecked == true ? ConfKind.Geo : ConfKind.Warp;
        try
        {
            ConfImporter.Apply(_path, kind);
            Applied = true;
            AppliedKind = kind;
            ShouldRestart = ChkRestart.IsChecked == true;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Не удалось применить конфиг:\n\n" + ex.Message,
                "Импорт", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
