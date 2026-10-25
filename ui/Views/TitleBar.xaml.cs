using System;
using System.Windows;
using System.Windows.Controls;

namespace Tunor.Views;

/// <summary>
/// The title bar that replaces the system one: the app's colours, and a window's three buttons.
/// The window must have <see cref="FrameChrome.Create"/> applied, or it would have two bars.
/// </summary>
public partial class TitleBar : UserControl
{
    private Window? _window;

    public TitleBar()
    {
        InitializeComponent();
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
    }

    private void Attach()
    {
        _window = Window.GetWindow(this);
        if (_window == null) return;
        TitleText.Text = _window.Title;
        _window.StateChanged += OnState;
        _window.Activated += OnActive;
        _window.Deactivated += OnActive;
        OnState(null, EventArgs.Empty);
        OnActive(null, EventArgs.Empty);
    }

    private void Detach()
    {
        if (_window == null) return;
        _window.StateChanged -= OnState;
        _window.Activated -= OnActive;
        _window.Deactivated -= OnActive;
        _window = null;
    }

    /// <summary>The maximise button shows "restore" while the window is maximised.</summary>
    private void OnState(object? sender, EventArgs e)
    {
        var maximised = _window?.WindowState == WindowState.Maximized;
        MaxBtn.Content = maximised ? "" : "";
        MaxBtn.ToolTip = maximised ? "Свернуть в окно" : "Развернуть";
        System.Windows.Automation.AutomationProperties.SetName(MaxBtn, maximised ? "Свернуть в окно" : "Развернуть");
    }

    /// <summary>A window that is not in front has a quieter title, as a system one does.</summary>
    private void OnActive(object? sender, EventArgs e)
        => TitleText.Opacity = _window?.IsActive == false ? 0.55 : 1;

    private void Min_Click(object sender, RoutedEventArgs e)
    {
        if (_window != null) SystemCommands.MinimizeWindow(_window);
    }

    private void Max_Click(object sender, RoutedEventArgs e)
    {
        if (_window == null) return;
        if (_window.WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(_window);
        else SystemCommands.MaximizeWindow(_window);
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (_window != null) SystemCommands.CloseWindow(_window);
    }
}
