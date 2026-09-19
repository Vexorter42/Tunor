using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using Nyx.Services;
using Application = System.Windows.Application;

namespace Nyx;

public class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly Window _window;
    private readonly ToolStripMenuItem _miStatus;
    private readonly ToolStripMenuItem _miStart;
    private readonly ToolStripMenuItem _miStop;
    private readonly ToolStripMenuItem _miRestart;

    public bool IsExiting { get; private set; }

    public TrayIcon(Window window)
    {
        _window = window;

        _icon = new NotifyIcon
        {
            Icon = BuildIcon(false),
            Text = "Nyx — Stopped",
            Visible = false,
        };

        _miStatus = new ToolStripMenuItem("Остановлен") { Enabled = false };
        _miStart = new ToolStripMenuItem("Запустить", null, async (_, _) => await ProcessService.StartAsync());
        _miStop = new ToolStripMenuItem("Остановить", null, async (_, _) => await ProcessService.StopAsync());
        _miRestart = new ToolStripMenuItem("Перезапустить", null, async (_, _) => await ProcessService.RestartAsync());

        var menu = new ContextMenuStrip();
        menu.Items.Add(_miStatus);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_miStart);
        menu.Items.Add(_miStop);
        menu.Items.Add(_miRestart);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Показать окно", null, (_, _) => ShowWindow());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => ExitApp());

        _icon.ContextMenuStrip = menu;
        _icon.DoubleClick += (_, _) => ShowWindow();
    }

    public void Show()
    {
        _icon.Visible = true;
        UpdateStatus(ProcessService.IsRunning);
    }

    public void UpdateStatus(bool running)
    {
        _icon.Icon?.Dispose();
        _icon.Icon = BuildIcon(running);
        _icon.Text = running ? "Nyx — Running" : "Nyx — Stopped";
        _miStatus.Text = running ? "Запущен" : "Остановлен";
        _miStart.Enabled = !running;
        _miStop.Enabled = running;
    }

    private void ShowWindow()
    {
        if (_window is MainWindow mw)
        {
            mw.RestoreWindow();
            return;
        }
        _window.ShowInTaskbar = true;
        _window.Visibility = Visibility.Visible;
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Show();
        _window.Activate();
        _window.Topmost = true;
        _window.Topmost = false;
        _window.Focus();
    }

    private void ExitApp()
    {
        IsExiting = true;
        _window.Close();
    }

    private static Icon BuildIcon(bool running)
    {
        const int size = 32;
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var bg = new SolidBrush(Color.FromArgb(30, 30, 46));
            g.FillEllipse(bg, 0, 0, size - 1, size - 1);
            using var fg = new SolidBrush(running
                ? Color.FromArgb(110, 255, 168)
                : Color.FromArgb(255, 110, 110));
            g.FillEllipse(fg, 8, 8, size - 17, size - 17);
        }
        var hIcon = bmp.GetHicon();
        var icon = (Icon)Icon.FromHandle(hIcon).Clone();
        DestroyIcon(hIcon);
        return icon;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
