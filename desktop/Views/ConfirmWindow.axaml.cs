using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Tunor.Desktop.Views;

/// <summary>A yes/no box in the app's own colours — Avalonia has no MessageBox.</summary>
public partial class ConfirmWindow : Window
{
    public bool Confirmed { get; private set; }

    public ConfirmWindow() : this("", "") { }

    public ConfirmWindow(string head, string body)
    {
        InitializeComponent();
        HeadText.Text = head;
        BodyText.Text = body;
    }

    private void Yes_Click(object? sender, RoutedEventArgs e) { Confirmed = true; Close(); }
    private void No_Click(object? sender, RoutedEventArgs e) => Close();
}
