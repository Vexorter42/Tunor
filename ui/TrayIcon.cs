using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Forms;
using Nyx.Services;
using Application = System.Windows.Application;

namespace Nyx;

public class TrayIcon : IDisposable
{
    // Palette mirrors Styles/Theme.xaml
    private static readonly Color Panel   = Color.FromArgb(0x24, 0x29, 0x32);
    private static readonly Color Hover   = Color.FromArgb(0x2E, 0x34, 0x3E);
    private static readonly Color BorderC = Color.FromArgb(0x3A, 0x40, 0x4A);
    private static readonly Color TextC   = Color.FromArgb(0xF4, 0xF6, 0xF7);
    private static readonly Color DimC    = Color.FromArgb(0x95, 0x9D, 0xA9);
    private static readonly Color Accent  = Color.FromArgb(0x3D, 0xDC, 0x5C);
    private static readonly Color Danger  = Color.FromArgb(0xFF, 0x6B, 0x6B);

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
            Text = "Nyx — отключено",
            Visible = false,
        };

        _miStatus  = new ToolStripMenuItem("Отключено") { Enabled = false, ForeColor = DimC };
        _miStart   = new ToolStripMenuItem("Запустить", null, async (_, _) => await ProcessService.StartAsync());
        _miStop    = new ToolStripMenuItem("Остановить", null, async (_, _) => await ProcessService.StopAsync());
        _miRestart = new ToolStripMenuItem("Перезапустить", null, async (_, _) => await ProcessService.RestartAsync());
        var miShow = new ToolStripMenuItem("Показать окно", null, (_, _) => ShowWindow());
        var miExit = new ToolStripMenuItem("Выход", null, (_, _) => ExitApp());

        var menu = new ContextMenuStrip
        {
            BackColor = Panel,
            ForeColor = TextC,
            Font = new Font("Segoe UI", 9.5f, System.Drawing.FontStyle.Regular, GraphicsUnit.Point),
            ShowImageMargin = false,
            Padding = new Padding(6),
            Renderer = new DarkRenderer(),
        };
        menu.Items.AddRange(new ToolStripItem[]
        {
            _miStatus,
            new ToolStripSeparator(),
            _miStart, _miStop, _miRestart,
            new ToolStripSeparator(),
            miShow,
            new ToolStripSeparator(),
            miExit,
        });

        foreach (ToolStripItem it in menu.Items)
        {
            it.Padding = new Padding(6, 5, 6, 5);
            if (it is ToolStripMenuItem mi && mi.Enabled) mi.ForeColor = TextC;
        }
        miExit.ForeColor = Danger;

        // Windows 11 rounded corners for the popup.
        menu.HandleCreated += (s, _) =>
        {
            try
            {
                var pref = DWMWCP_ROUND;
                DwmSetWindowAttribute(((Control)s!).Handle, DWMWA_WINDOW_CORNER_PREFERENCE,
                    ref pref, sizeof(int));
            }
            catch { }
        };

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
        var old = _icon.Icon;
        _icon.Icon = BuildIcon(running);
        old?.Dispose();

        _icon.Text = running ? "Nyx — подключено" : "Nyx — отключено";
        _miStatus.Text = running ? "Подключено" : "Отключено";
        _miStatus.ForeColor = running ? Accent : DimC;
        _miStart.Enabled = !running;
        _miStop.Enabled = running;
        _miStart.ForeColor = _miStart.Enabled ? TextC : DimC;
        _miStop.ForeColor = _miStop.Enabled ? TextC : DimC;
    }

    private void ShowWindow()
    {
        if (_window is MainWindow mw) { mw.RestoreWindow(); return; }
        _window.ShowInTaskbar = true;
        _window.Visibility = Visibility.Visible;
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Show();
        _window.Activate();
    }

    private void ExitApp()
    {
        IsExiting = true;
        _window.Close();
    }

    /// <summary>Tray mark: the Nyx crescent, tinted by connection state.</summary>
    private static Icon BuildIcon(bool running)
    {
        const int size = 32;
        var moonColor = running ? Accent : Color.FromArgb(0x6E, 0x76, 0x80);

        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;

            float cx = size * 0.5f, cy = size * 0.5f, r = size * 0.38f;
            using (var moon = new SolidBrush(moonColor))
                g.FillEllipse(moon, cx - r, cy - r, r * 2, r * 2);

            // Punch out an offset disc to carve the crescent (transparent bite).
            using (var path = new GraphicsPath())
            {
                float pr = r * 0.84f, px = cx + r * 0.44f, py = cy - r * 0.30f;
                path.AddEllipse(px - pr, py - pr, pr * 2, pr * 2);
                var prev = g.CompositingMode;
                g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                using (var clear = new SolidBrush(Color.Transparent))
                    g.FillPath(clear, path);
                g.CompositingMode = prev;
            }
        }

        var hIcon = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(hIcon).Clone(); }
        finally { DestroyIcon(hIcon); }
    }

    /// <summary>Flat dark renderer for the tray context menu.</summary>
    private sealed class DarkRenderer : ToolStripProfessionalRenderer
    {
        public DarkRenderer() : base(new DarkColors()) { RoundedEdges = false; }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            var r = new Rectangle(System.Drawing.Point.Empty, e.Item.Size);
            if (e.Item.Selected && e.Item.Enabled)
            {
                using var b = new SolidBrush(Hover);
                using var path = Rounded(new Rectangle(r.X + 2, r.Y, r.Width - 4, r.Height), 7);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                e.Graphics.FillPath(b, path);
            }
            else
            {
                using var b = new SolidBrush(Panel);
                e.Graphics.FillRectangle(b, r);
            }
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            using var p = new Pen(BorderC);
            var y = e.Item.Height / 2;
            e.Graphics.DrawLine(p, 10, y, e.Item.Width - 10, y);
        }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using var b = new SolidBrush(Panel);
            e.Graphics.FillRectangle(b, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using var p = new Pen(BorderC);
            var r = e.AffectedBounds;
            e.Graphics.DrawRectangle(p, 0, 0, r.Width - 1, r.Height - 1);
        }

        private static GraphicsPath Rounded(Rectangle r, int radius)
        {
            var d = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    private sealed class DarkColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Panel;
        public override Color MenuBorder => BorderC;
        public override Color MenuItemBorder => Hover;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color MenuItemPressedGradientBegin => Hover;
        public override Color MenuItemPressedGradientEnd => Hover;
        public override Color ImageMarginGradientBegin => Panel;
        public override Color ImageMarginGradientMiddle => Panel;
        public override Color ImageMarginGradientEnd => Panel;
        public override Color SeparatorDark => BorderC;
        public override Color SeparatorLight => BorderC;
    }

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
