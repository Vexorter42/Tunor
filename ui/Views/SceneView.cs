using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Tunor.Services;

namespace Tunor.Views;

/// <summary>
/// Plays a <see cref="Scene"/>: the shapes of an animated picture, moved to where the scene says
/// they are at each moment. All the arithmetic is in the scene; this only owns one WPF element
/// per item and sets its position, size and opacity thirty times a second.
///
/// The picture is drawn on a canvas of the scene's own size inside a Viewbox, so it is sharp at any
/// size the dialog gives it. It stops when it leaves the screen — a timer that outlives the dialog
/// that started it is how a program ends up repainting something nobody can see.
/// </summary>
public sealed class SceneView : Border
{
    private static readonly TimeSpan Frame = TimeSpan.FromMilliseconds(33);

    /// <summary>The pause on the finished picture before it plays again, seconds.</summary>
    private const double Rest = 1.4;

    private readonly Canvas _canvas = new() { IsHitTestVisible = false, ClipToBounds = true };
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = new();
    private readonly Dictionary<uint, SolidColorBrush> _brushes = new();

    private Scene? _scene;
    private FrameworkElement[] _elements = Array.Empty<FrameworkElement>();
    private TextBlock?[] _labels = Array.Empty<TextBlock?>();
    private ItemState[]? _states;
    private bool _finished;

    /// <summary>Raised once, when the picture has played to its last frame for the first time.</summary>
    public event EventHandler? Finished;

    /// <summary>Raised on every frame of the first play with how far it has got, 0..1.</summary>
    public event Action<double>? Tick;

    public bool PlayedThrough => _finished;

    /// <summary>How far through the first play, 0..1; 1 once it has played through.</summary>
    public double Progress => _scene == null ? 0 : _finished ? 1 : Math.Min(1, _clock.Elapsed.TotalSeconds / _scene.Duration);

    public SceneView()
    {
        Background = Solid(SceneColors.Resolve("deep")!.Value);
        CornerRadius = new CornerRadius(12);
        ClipToBounds = true;
        Child = new Viewbox { Stretch = Stretch.Uniform, Child = _canvas };
        UseLayoutRounding = false;

        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = Frame };
        _timer.Tick += (_, _) => Step();
        Unloaded += (_, _) => Stop();
    }

    /// <summary>
    /// Starts the picture from its first frame. With animation switched off in Windows (or when
    /// <paramref name="still"/> is set) it shows the last frame and counts as played.
    /// </summary>
    public void Play(Scene scene, bool still = false)
    {
        Stop();
        Build(scene);
        _finished = false;

        if (still || !SystemParameters.ClientAreaAnimation)
        {
            Apply(scene.Duration);
            Complete();
            return;
        }

        _clock.Restart();
        Apply(0);
        _timer.Start();
    }

    public void Stop()
    {
        _timer.Stop();
        _clock.Stop();
    }

    private void Step()
    {
        if (_scene == null) return;
        var t = _clock.Elapsed.TotalSeconds;
        if (!_finished && t >= _scene.Duration) Complete();
        var cycle = _scene.Duration + Rest;
        Apply(Math.Min(t % cycle, _scene.Duration));
        if (!_finished) Tick?.Invoke(Progress);
    }

    private void Complete()
    {
        if (_finished) return;
        _finished = true;
        Finished?.Invoke(this, EventArgs.Empty);
    }

    // ------------------------------------------------------------------ building

    private SolidColorBrush Solid(uint argb)
    {
        if (_brushes.TryGetValue(argb, out var hit)) return hit;
        var b = new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
        b.Freeze();
        return _brushes[argb] = b;
    }

    private Brush? Paint(string? token) => SceneColors.Resolve(token) is { } c ? Solid(c) : null;

    private void Build(Scene scene)
    {
        _scene = scene;
        _canvas.Children.Clear();
        _canvas.Width = scene.Width;
        _canvas.Height = scene.Height;

        _elements = new FrameworkElement[scene.Items.Count];
        _labels = new TextBlock?[scene.Items.Count];
        _states = null;

        for (var i = 0; i < scene.Items.Count; i++)
        {
            var it = scene.Items[i];
            FrameworkElement el;
            switch (it.Kind)
            {
                case SceneKind.Rect:
                {
                    var b = new Border
                    {
                        Background = Paint(it.Fill),
                        BorderBrush = Paint(it.Stroke),
                        BorderThickness = new Thickness(it.Stroke == null ? 0 : it.StrokeWidth),
                    };
                    if (it.Text.Length > 0)
                    {
                        var tb = NewText(it, it.Bold);
                        tb.HorizontalAlignment = HorizontalAlignment.Center;
                        tb.VerticalAlignment = VerticalAlignment.Center;
                        tb.TextAlignment = TextAlignment.Center;
                        b.Child = tb;
                        _labels[i] = tb;
                    }
                    el = b;
                    break;
                }
                case SceneKind.Circle:
                    el = new Ellipse
                    {
                        Fill = Paint(it.Fill),
                        Stroke = Paint(it.Stroke),
                        StrokeThickness = it.Stroke == null ? 0 : it.StrokeWidth,
                    };
                    break;
                case SceneKind.Poly:
                    if (it.Closed)
                        el = new Polygon
                        {
                            Fill = Paint(it.Fill),
                            Stroke = Paint(it.Stroke),
                            StrokeThickness = it.Stroke == null ? 0 : it.StrokeWidth,
                            StrokeLineJoin = PenLineJoin.Round,
                        };
                    else
                        el = new Polyline
                        {
                            Stroke = Paint(it.Stroke),
                            StrokeThickness = it.StrokeWidth,
                            StrokeLineJoin = PenLineJoin.Round,
                            StrokeStartLineCap = PenLineCap.Round,
                            StrokeEndLineCap = PenLineCap.Round,
                        };
                    break;
                default:
                {
                    var tb = NewText(it, it.Bold);
                    // As wide as the picture twice over, so that alignment is a matter of where its left edge is.
                    tb.Width = scene.Width * 2;
                    tb.TextAlignment = it.Align switch { "l" => TextAlignment.Left, "r" => TextAlignment.Right, _ => TextAlignment.Center };
                    el = tb;
                    _labels[i] = tb;
                    break;
                }
            }
            el.IsHitTestVisible = false;
            el.Visibility = Visibility.Hidden;
            _canvas.Children.Add(el);
            _elements[i] = el;
        }
    }

    private TextBlock NewText(SceneItem it, bool bold) => new()
    {
        Foreground = Paint(it.TextColor),
        FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
        TextWrapping = TextWrapping.NoWrap,
        LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
    };

    // ------------------------------------------------------------------ one frame

    private void Apply(double t)
    {
        var scene = _scene;
        if (scene == null) return;
        _states = scene.At(t, _states);

        for (var i = 0; i < _states.Length; i++)
        {
            var s = _states[i];
            var el = _elements[i];
            if (s.Opacity <= 0.004)
            {
                if (el.Visibility != Visibility.Hidden) el.Visibility = Visibility.Hidden;
                continue;
            }
            el.Visibility = Visibility.Visible;
            el.Opacity = s.Opacity;

            switch (scene.Items[i].Kind)
            {
                case SceneKind.Rect:
                {
                    var b = (Border)el;
                    Canvas.SetLeft(b, s.X);
                    Canvas.SetTop(b, s.Y);
                    b.Width = Math.Max(0, s.W);
                    b.Height = Math.Max(0, s.H);
                    b.CornerRadius = new CornerRadius(Math.Max(0, s.R));
                    if (_labels[i] is { } label)
                    {
                        label.FontSize = s.Size;
                        label.Text = s.Text ?? "";
                    }
                    break;
                }
                case SceneKind.Circle:
                    el.Width = el.Height = Math.Max(0, 2 * s.R);
                    Canvas.SetLeft(el, s.X - s.R);
                    Canvas.SetTop(el, s.Y - s.R);
                    break;
                case SceneKind.Poly:
                {
                    var pts = new PointCollection(s.Points!.Select(p => new Point(p.X, p.Y)));
                    if (el is Polygon g) g.Points = pts; else ((Polyline)el).Points = pts;
                    break;
                }
                default:
                {
                    var tb = (TextBlock)el;
                    var item = scene.Items[i];
                    var lh = s.Size * 1.35;
                    var lines = (s.Text ?? "").Split('\n').Length;
                    tb.FontSize = s.Size;
                    tb.LineHeight = lh;
                    tb.Text = s.Text ?? "";
                    Canvas.SetLeft(tb, item.Align switch { "l" => s.X, "r" => s.X - tb.Width, _ => s.X - tb.Width / 2 });
                    Canvas.SetTop(tb, s.Y - lh * lines / 2);
                    break;
                }
            }
        }
    }
}
