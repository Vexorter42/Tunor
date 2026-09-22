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
        // Clicking a notification opens the window, where the full reason is shown.
        _icon.BalloonTipClicked += (_, _) => ShowWindow();
    }

    /// <summary>
    /// A Windows notification from the tray. Used when the tunnel stops for a reason a
    /// restart will not fix: with the window hidden, nobody would otherwise notice until
    /// sites stopped opening. Windows caps the text, so it is cut and points to the window.
    /// </summary>
    public void Notify(string title, string text)
    {
        const int max = 200;   // the balloon allows 255; leave room for the pointer below
        if (text.Length > max)
        {
            var cut = text.LastIndexOf(' ', max);
            text = text[..(cut > 120 ? cut : max)] + "…";
        }
        try { _icon.ShowBalloonTip(8000, title, text + "\nПодробности — в окне Nyx.", ToolTipIcon.Warning); }
        catch { /* notifications may be disabled; the home screen still shows it */ }
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

    /// <summary>
    /// Tray mark: the Nyx sign — traffic leaving a broken ring — tinted by connection
    /// state. Geometry mirrors Assets/make_icon.py; change both together. Drawn large
    /// and downsampled, since GDI+ anti-aliasing alone is coarse at 32px. No background
    /// plate here: the tray sits straight on the taskbar.
    /// </summary>
    private static Icon BuildIcon(bool running)
    {
        const int size = 32;
        const int draw = size * 4;
        var colour = running ? Accent : Color.FromArgb(0x6E, 0x76, 0x80);

        const double exitDeg = 315.0;   // the arrow leaves towards the upper right
        const float gapDeg = 86f;

        using var big = new Bitmap(draw, draw);
        using (var g = Graphics.FromImage(big))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;

            float cx = draw * 0.485f, cy = draw * 0.520f, r = draw * 0.300f;

            using (var pen = new Pen(colour, draw * 0.125f))
                g.DrawArc(pen, cx - r, cy - r, r * 2, r * 2,
                          (float)exitDeg + gapDeg / 2, 360f - gapDeg);

            // Shaft and head share one direction vector, so they meet without a seam.
            var ux = (float)Math.Cos(exitDeg * Math.PI / 180.0);
            var uy = (float)Math.Sin(exitDeg * Math.PI / 180.0);
            float px = -uy, py = ux;

            float tipX = cx + ux * r * 1.33f, tipY = cy + uy * r * 1.33f;
            var headHalf = r * 0.34f;
            var headLen = headHalf * 2f;
            float baseX = tipX - ux * headLen, baseY = tipY - uy * headLen;

            using (var pen = new Pen(colour, draw * 0.125f))
                g.DrawLine(pen,
                    cx - ux * r * 0.34f, cy - uy * r * 0.34f,
                    baseX + ux * headLen * 0.45f, baseY + uy * headLen * 0.45f);

            using (var brush = new SolidBrush(colour))
                g.FillPolygon(brush, new[]
                {
                    new PointF(tipX, tipY),
                    new PointF(baseX + px * headHalf, baseY + py * headHalf),
                    new PointF(baseX - px * headHalf, baseY - py * headHalf),
                });
        }

        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.DrawImage(big, 0, 0, size, size);
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
