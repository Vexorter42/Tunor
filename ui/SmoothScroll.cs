using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Tunor
{
    /// <summary>
    /// Mouse-wheel scrolling that glides. WPF scrolls the wheel in discrete jumps by default;
    /// this lets the offset chase a target instead. Enabled app-wide through an implicit
    /// ScrollViewer style in Styles/Theme.xaml.
    ///
    /// One motion, not one animation per notch. The first version started a new eased
    /// animation on every wheel event, and an ease-out is at its fastest in its first frame —
    /// so a quick spin of the wheel, or any touchpad, restarted the speed over and over and
    /// the page stuttered. Here a wheel event only moves the target; the offset follows it
    /// like a damped spring. Speed is continuous across notches — it builds up as the wheel
    /// turns and dies away smoothly when it stops, instead of restarting or ending on a cut.
    /// </summary>
    public static class SmoothScroll
    {
        /// <summary>Wheel delta (120 per notch) to pixels, the same ~75 px a notch as before.</summary>
        private const double PixelsPerDelta = 0.62;

        /// <summary>
        /// How briskly the offset follows its target, in radians per second. The motion is a
        /// critically damped spring — the fastest way to arrive without overshooting — which
        /// settles in about a third of a second: quick enough to feel direct, slow enough to
        /// read as gliding.
        /// </summary>
        private const double Stiffness = 17;

        /// <summary>Under this far from the target and this slow, the motion is over.</summary>
        private const double SettledPixels = 0.4;
        private const double SettledSpeed = 8;     // pixels per second

        public static readonly DependencyProperty EnabledProperty =
            DependencyProperty.RegisterAttached(
                "Enabled", typeof(bool), typeof(SmoothScroll),
                new PropertyMetadata(false, OnEnabledChanged));

        public static void SetEnabled(DependencyObject o, bool v) => o.SetValue(EnabledProperty, v);
        public static bool GetEnabled(DependencyObject o) => (bool)o.GetValue(EnabledProperty);

        private sealed class Glide
        {
            public double Target;
            public double Current;
            public double Velocity;    // pixels per second; carried across wheel notches so speed never jumps
            public double Written;     // what this class last set, to notice anyone else scrolling
            public bool Active;
        }

        private static readonly DependencyProperty GlideProperty =
            DependencyProperty.RegisterAttached("Glide", typeof(Glide), typeof(SmoothScroll));

        private static readonly List<ScrollViewer> Running = new();
        private static TimeSpan _lastFrame = TimeSpan.MinValue;

        private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (!(d is ScrollViewer sv)) return;
            sv.PreviewMouseWheel -= OnWheel;
            sv.Loaded -= OnLoaded;
            if (e.NewValue is true)
            {
                sv.PreviewMouseWheel += OnWheel;
                sv.Loaded += OnLoaded;
                if (sv.IsLoaded) OnLoaded(sv, null);
            }
            else Stop(sv);
        }

        /// <summary>
        /// The panel that scrolls this viewer's content when CanContentScroll hands it over: the
        /// content itself, or — for a list — the panel its ItemsPresenter made. Null when it is
        /// not there yet.
        /// </summary>
        private static Panel? ContentHost(ScrollViewer sv)
        {
            if (sv.Content is ItemsPresenter presenter)
                return VisualTreeHelper.GetChildrenCount(presenter) > 0
                    ? VisualTreeHelper.GetChild(presenter, 0) as Panel
                    : null;
            return sv.Content as Panel;
        }

        /// <summary>
        /// With CanContentScroll on, the content scrolls itself and by its own unit — a row, a
        /// child — and every number here is pixels: asked to move 75 of them, a list moves 75
        /// rows. A virtualising list can be told to count in pixels, which keeps its
        /// virtualisation. A plain panel, such as the one a dropdown puts its items in, cannot:
        /// the viewer takes the scrolling back and moves it as one piece.
        /// Returns whether it changed anything, so the caller can lay out again before reading
        /// the new extent.
        /// </summary>
        private static bool Adapt(ScrollViewer sv)
        {
            if (!sv.CanContentScroll) return false;
            var host = ContentHost(sv);
            if (host == null) return false;                      // not built yet; asked again on the next wheel

            if (host is VirtualizingPanel)
            {
                if (sv.TemplatedParent is ItemsControl list && VirtualizingPanel.GetScrollUnit(list) == ScrollUnit.Item)
                {
                    VirtualizingPanel.SetScrollUnit(list, ScrollUnit.Pixel);
                    return true;
                }
                return false;
            }

            sv.CanContentScroll = false;
            return true;
        }

        private static void OnLoaded(object sender, RoutedEventArgs? e) => Adapt((ScrollViewer)sender);

        /// <summary>Whether offsets of this viewer are pixels. If not, WPF's own scrolling is left alone.</summary>
        private static bool ScrollsInPixels(ScrollViewer sv)
        {
            if (!sv.CanContentScroll) return true;

            // A list, or a panel: pixels only for a virtualising one that was told to count them.
            if (sv.Content is ItemsPresenter || sv.Content is Panel)
                return sv.TemplatedParent is ItemsControl list
                    && VirtualizingPanel.GetScrollUnit(list) == ScrollUnit.Pixel
                    && ContentHost(sv) is VirtualizingPanel;

            // Anything else that scrolls itself is a text view — a TextBox's — and counts in pixels.
            return true;
        }

        private static void OnWheel(object sender, MouseWheelEventArgs e)
        {
            var sv = (ScrollViewer)sender;

            if (e.Delta == 0) return;
            if (Adapt(sv)) sv.UpdateLayout();        // the extent changes unit; read it afterwards

            // Nothing to scroll here — let the event bubble to a parent scroller.
            if (sv.ScrollableHeight <= 0) return;
            if (!ScrollsInPixels(sv)) return;

            var g = (Glide?)sv.GetValue(GlideProperty);
            if (g == null) sv.SetValue(GlideProperty, g = new Glide());

            if (!g.Active)
            {
                g.Current = g.Target = g.Written = sv.VerticalOffset;
                g.Velocity = 0;
            }

            var target = Math.Max(0, Math.Min(sv.ScrollableHeight, g.Target - e.Delta * PixelsPerDelta));

            // Already at the edge and pushing further — let the parent take it.
            if (Math.Abs(target - g.Target) < 0.5 && Math.Abs(target - g.Current) < 0.5) return;

            e.Handled = true;
            g.Target = target;
            if (!g.Active)
            {
                g.Active = true;
                Running.Add(sv);
                if (Running.Count == 1)
                {
                    _lastFrame = TimeSpan.MinValue;
                    CompositionTarget.Rendering += OnFrame;
                }
            }
        }

        private static void OnFrame(object? sender, EventArgs e)
        {
            var now = ((RenderingEventArgs)e).RenderingTime;
            if (now == _lastFrame) return;                         // rendering can report a frame twice
            var dt = _lastFrame == TimeSpan.MinValue ? 16.7 : (now - _lastFrame).TotalMilliseconds;
            _lastFrame = now;
            dt = Math.Min(dt, 50);                                 // after a stall, do not leap

            for (var i = Running.Count - 1; i >= 0; i--)
            {
                var sv = Running[i];
                var g = (Glide)sv.GetValue(GlideProperty)!;

                // Someone else moved it — the scrollbar, the keyboard, a program: let go.
                if (Math.Abs(sv.VerticalOffset - g.Written) > 1.5) { Stop(sv); continue; }

                // The content can shrink under a glide in progress.
                g.Target = Math.Min(g.Target, sv.ScrollableHeight);

                // The exact solution of a critically damped spring over this frame's span, so the
                // result is the same whether the frame took 8 ms or 40.
                var t = dt / 1000.0;
                var d0 = g.Current - g.Target;
                var c = g.Velocity + Stiffness * d0;
                var decay = Math.Exp(-Stiffness * t);
                g.Current = g.Target + (d0 + c * t) * decay;
                g.Velocity = (g.Velocity - Stiffness * c * t) * decay;

                // Never past either end of the content, and no speed left to push against it.
                if (g.Current < 0) { g.Current = 0; g.Velocity = 0; }
                if (g.Current > sv.ScrollableHeight) { g.Current = sv.ScrollableHeight; g.Velocity = 0; }

                var done = Math.Abs(g.Target - g.Current) < SettledPixels && Math.Abs(g.Velocity) < SettledSpeed;
                if (done) { g.Current = g.Target; g.Velocity = 0; }

                // Whole device pixels: a fractional offset makes text shimmer as it moves.
                var scale = VisualTreeHelper.GetDpi(sv).DpiScaleY;
                var at = Math.Round(g.Current * scale) / scale;
                sv.ScrollToVerticalOffset(at);
                g.Written = at;

                if (done) Stop(sv);
            }
        }

        private static void Stop(ScrollViewer sv)
        {
            var g = (Glide?)sv.GetValue(GlideProperty);
            if (g != null) g.Active = false;
            Running.Remove(sv);
            if (Running.Count == 0) CompositionTarget.Rendering -= OnFrame;
        }
    }
}
