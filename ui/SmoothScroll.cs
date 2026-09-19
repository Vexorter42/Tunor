using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace Nyx;

/// <summary>
/// Animated mouse-wheel scrolling. WPF scrolls in discrete jumps by default;
/// this eases the offset instead. Enabled app-wide through an implicit
/// ScrollViewer style in Styles/Theme.xaml.
/// </summary>
public static class SmoothScroll
{
    private const double PixelsPerNotch = 0.62;   // wheel delta (120) -> ~75 px
    private static readonly Duration Ease = new(TimeSpan.FromMilliseconds(300));

    public static readonly DependencyProperty EnabledProperty =
        DependencyProperty.RegisterAttached(
            "Enabled", typeof(bool), typeof(SmoothScroll),
            new PropertyMetadata(false, OnEnabledChanged));

    public static void SetEnabled(DependencyObject o, bool v) => o.SetValue(EnabledProperty, v);
    public static bool GetEnabled(DependencyObject o) => (bool)o.GetValue(EnabledProperty);

    /// <summary>Animated proxy — writing it scrolls the viewer.</summary>
    private static readonly DependencyProperty OffsetProperty =
        DependencyProperty.RegisterAttached(
            "Offset", typeof(double), typeof(SmoothScroll),
            new PropertyMetadata(0.0, OnOffsetChanged));

    /// <summary>Where the current animation is heading (NaN = idle).</summary>
    private static readonly DependencyProperty TargetProperty =
        DependencyProperty.RegisterAttached(
            "Target", typeof(double), typeof(SmoothScroll),
            new PropertyMetadata(double.NaN));

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer sv) return;
        sv.PreviewMouseWheel -= OnWheel;
        if (e.NewValue is true) sv.PreviewMouseWheel += OnWheel;
    }

    private static void OnOffsetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ScrollViewer sv) sv.ScrollToVerticalOffset((double)e.NewValue);
    }

    private static void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer sv) return;

        // Nothing to scroll here — let the event bubble to a parent scroller.
        if (sv.ScrollableHeight <= 0 || e.Delta == 0) return;

        var target = (double)sv.GetValue(TargetProperty);
        if (double.IsNaN(target)) target = sv.VerticalOffset;

        target = Math.Clamp(target - e.Delta * PixelsPerNotch, 0, sv.ScrollableHeight);

        // Already at the edge and pushing further — let the parent take it.
        if (Math.Abs(target - sv.VerticalOffset) < 0.5) return;

        e.Handled = true;
        sv.SetValue(TargetProperty, target);

        var anim = new DoubleAnimation
        {
            From = sv.VerticalOffset,
            To = target,
            Duration = Ease,
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd,
        };
        anim.Completed += (_, _) =>
        {
            // Mark idle only if no newer scroll superseded this one.
            var pending = (double)sv.GetValue(TargetProperty);
            if (!double.IsNaN(pending) && Math.Abs(pending - target) < 0.5)
                sv.SetValue(TargetProperty, double.NaN);
        };

        sv.BeginAnimation(OffsetProperty, anim);
    }
}
