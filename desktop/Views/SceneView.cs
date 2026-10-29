using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Tunor.Services;

namespace Tunor.Desktop.Views;

/// <summary>
/// Plays a <see cref="Scene"/>: one Avalonia element per item of an animated picture, moved to where
/// the scene says it is at each moment. The arithmetic is in the scene and shared with the Windows
/// front end; this only owns the elements and sets their position, size and opacity thirty times a second.
///
/// The picture is drawn on a canvas of the scene's own size inside a Viewbox, so it stays sharp at
/// any size, and it stops when it leaves the screen: a timer that outlives the dialog that started it
/// is how a program ends up repainting something nobody can see.
/// </summary>
public sealed class SceneView : Border
{
    private static readonly TimeSpan Frame = TimeSpan.FromMilliseconds(33);

    /// <summary>The pause on the finished picture before it plays again, seconds.</summary>
    private const double Rest = 1.4;

    private readonly Canvas _canvas = new() { IsHitTestVisible = false, ClipToBounds = true };
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = new();
    private readonly Dictionary<uint, ISolidColorBrush> _brushes = new();

    private Scene? _scene;
    private Control[] _elements = Array.Empty<Control>();
    private TextBlock?[] _labels = Array.Empty<TextBlock?>();
    private ItemState[]? _states;
    private bool _finished;

    /// <summary>Raised once, when the picture has played to its last frame for the first time.</summary>
    public event EventHandler? Finished;

    /// <summary>Raised on every frame of the first play with how far it has got, 0..1.</summary>
    public event Action<double>? Tick;

    public bool PlayedThrough => _finished;

    public double Progress => _scene == null ? 0 : _finished ? 1 : Math.Min(1, _clock.Elapsed.TotalSeconds / _scene.Duration);

    public SceneView()
    {
        Background = Solid(SceneColors.Resolve("deep")!.Value);
        CornerRadius = new CornerRadius(12);
        ClipToBounds = true;
        Child = new Viewbox { Stretch = Stretch.Uniform, Child = _canvas };

        _timer = new DispatcherTimer { Interval = Frame };
        _timer.Tick += (_, _) => Step();
        DetachedFromVisualTree += (_, _) => Stop();
    }

    /// <summary>Starts the picture from its first frame; with <paramref name="still"/> it shows the last frame and counts as played.</summary>
    public void Play(Scene scene, bool still = false)
    {
        Stop();
        Build(scene);
        _finished = false;

        if (still)
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

    private ISolidColorBrush Solid(uint argb)
    {
        if (_brushes.TryGetValue(argb, out var hit)) return hit;
        return _brushes[argb] = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.FromUInt32(argb));
    }

    private IBrush? Paint(string? token) => SceneColors.Resolve(token) is { } c ? Solid(c) : null;

    private void Build(Scene scene)
    {
        _scene = scene;
        _canvas.Children.Clear();
        _canvas.Width = scene.Width;
        _canvas.Height = scene.Height;

        _elements = new Control[scene.Items.Count];
        _labels = new TextBlock?[scene.Items.Count];
        _states = null;

        for (var i = 0; i < scene.Items.Count; i++)
        {
            var it = scene.Items[i];
            Control el;
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
                        var tb = NewText(it);
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
                            StrokeJoin = PenLineJoin.Round,
                        };
                    else
                        el = new Polyline
                        {
                            Stroke = Paint(it.Stroke),
                            StrokeThickness = it.StrokeWidth,
                            StrokeJoin = PenLineJoin.Round,
                            StrokeLineCap = PenLineCap.Round,
                        };
                    break;
                default:
                {
                    var tb = NewText(it);
                    // As wide as the picture twice over, so that alignment is a matter of where its left edge is.
                    tb.Width = scene.Width * 2;
                    tb.TextAlignment = it.Align switch { "l" => TextAlignment.Left, "r" => TextAlignment.Right, _ => TextAlignment.Center };
                    el = tb;
                    _labels[i] = tb;
                    break;
                }
            }
            el.IsHitTestVisible = false;
            el.IsVisible = false;
            _canvas.Children.Add(el);
            _elements[i] = el;
        }
    }

    private TextBlock NewText(SceneItem it) => new()
    {
        Foreground = Paint(it.TextColor),
        FontWeight = it.Bold ? FontWeight.Bold : FontWeight.Normal,
        TextWrapping = TextWrapping.NoWrap,
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
                el.IsVisible = false;
                continue;
            }
            el.IsVisible = true;
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
                    var pts = new Points(s.Points!.Select(p => new Point(p.X, p.Y)));
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
