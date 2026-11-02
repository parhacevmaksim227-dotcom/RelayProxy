using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace RelayProxy.Desktop;

public static class SmoothScroll
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached("Enabled", typeof(bool), typeof(SmoothScroll), new PropertyMetadata(false, Attach));
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached("State", typeof(Motion), typeof(SmoothScroll));
    public static void SetEnabled(DependencyObject target, bool value) => target.SetValue(EnabledProperty, value);
    public static bool GetEnabled(DependencyObject target) => (bool)target.GetValue(EnabledProperty);
    private static void Attach(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not ScrollViewer viewer) return;
        if ((bool)args.NewValue)
        {
            viewer.SetValue(StateProperty, new Motion(viewer));
            viewer.PreviewMouseWheel += Wheel;
            viewer.PreviewMouseDown += Cancel;
            viewer.PreviewKeyDown += Cancel;
            viewer.Unloaded += Cancel;
        }
        else
        {
            (viewer.GetValue(StateProperty) as Motion)?.Stop();
            viewer.PreviewMouseWheel -= Wheel; viewer.PreviewMouseDown -= Cancel;
            viewer.PreviewKeyDown -= Cancel; viewer.Unloaded -= Cancel;
            viewer.ClearValue(StateProperty);
        }
    }
    private static void Cancel(object sender, RoutedEventArgs args) => (((DependencyObject)sender).GetValue(StateProperty) as Motion)?.Stop();
    private static void Wheel(object sender, MouseWheelEventArgs args)
    {
        var viewer = (ScrollViewer)sender;
        // Only the nearest scrollable viewport consumes the wheel in nested panels.
        for (DependencyObject? node = args.OriginalSource as DependencyObject; node != null; node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
        {
            if (node is ScrollViewer candidate && candidate.ScrollableHeight > 0)
            {
                if (candidate != viewer) return;
                break;
            }
        }
        var lines = SystemParameters.WheelScrollLines;
        if (lines == 0) return;
        var amount = -args.Delta / 120d * (lines < 0 ? viewer.ViewportHeight : lines * 22);
        // Spinning the wheel quickly carries further, the way touchpads and phones do.
        if (viewer.GetValue(StateProperty) is Motion motion) amount *= motion.Boost(args.Timestamp);
        if (ScrollBy(viewer, amount)) args.Handled = true;
    }
    // Every ScrollViewer in the app glides, not only the ones that opted in: lists, drop-downs,
    // the log, the picker. Viewers that cannot scroll are left alone by the handler itself.
    public static void EnableEverywhere() =>
        EventManager.RegisterClassHandler(typeof(ScrollViewer), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) =>
        {
            if (sender is ScrollViewer viewer && !GetEnabled(viewer)) SetEnabled(viewer, true);
        }));
    public static bool ScrollBy(ScrollViewer viewer, double pixels) => viewer.GetValue(StateProperty) is Motion motion && motion.Scroll(pixels);
    // Frame-driven critically damped follow: every wheel notch only moves the target, and the view
    // glides toward it with continuous velocity. Restarting an easing curve per notch made scrolling jerky.
    private sealed class Motion
    {
        private const double Stiffness = 14;
        private readonly ScrollViewer viewer;
        private double target, current;
        private bool moving;
        private long last;
        private int lastWheel, streak;
        public Motion(ScrollViewer viewer) { this.viewer = viewer; }
        // 1 for a lone notch, up to 2.2 for a fast run of notches (timestamps are in milliseconds).
        public double Boost(int timestamp)
        {
            streak = unchecked(timestamp - lastWheel) is >= 0 and < 110 ? Math.Min(streak + 1, 5) : 0;
            lastWheel = timestamp;
            return 1 + streak * .24;
        }
        public void Stop()
        {
            if (moving) { CompositionTarget.Rendering -= Frame; moving = false; }
            current = target = viewer.VerticalOffset;
        }
        public bool Scroll(double pixels)
        {
            if (pixels == 0 || !viewer.IsEnabled || viewer.ScrollableHeight <= 0) return false;
            if (!moving) current = target = viewer.VerticalOffset;
            else if (Math.Sign(pixels) != Math.Sign(target - current) && Math.Abs(target - current) > .5) target = current;
            var before = target;
            target = Math.Clamp(target + pixels, 0, viewer.ScrollableHeight);
            if (Math.Abs(target - viewer.VerticalOffset) < .1) return false;
            if (!GlassMotion.CanAnimate) { current = target; viewer.ScrollToVerticalOffset(target); return true; }
            Scene.Hold(650);
            if (!moving) { moving = true; last = Stopwatch.GetTimestamp(); CompositionTarget.Rendering += Frame; }
            return true;
        }
        private void Frame(object? sender, EventArgs args)
        {
            var now = Stopwatch.GetTimestamp();
            var elapsed = (now - last) / (double)Stopwatch.Frequency;
            if (elapsed < .007) return;
            var dt = Math.Min(.05, elapsed); last = now;
            current += (target - current) * (1 - Math.Exp(-Stiffness * dt));
            if (Math.Abs(target - current) < .25) { current = target; CompositionTarget.Rendering -= Frame; moving = false; }
            viewer.ScrollToVerticalOffset(current);
        }
    }
}
