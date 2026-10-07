using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Tunor.Desktop.Views;

/// <summary>A yes/no box in the app's own colours — Avalonia has no MessageBox.</summary>
public partial class ConfirmWindow : Window
{
    public bool Confirmed { get; private set; }

    public ConfirmWindow() : this("", "") { }

    /// <summary>The label says what will happen, so "Удалить" is only the usual case.</summary>
    public ConfirmWindow(string head, string body, string yes = "Удалить")
    {
        InitializeComponent();
        HeadText.Text = head;
        BodyText.Text = body;
        BtnYes.Content = yes;
    }

    private void Yes_Click(object? sender, RoutedEventArgs e) { Confirmed = true; Close(); }
    private void No_Click(object? sender, RoutedEventArgs e) => Close();
}
