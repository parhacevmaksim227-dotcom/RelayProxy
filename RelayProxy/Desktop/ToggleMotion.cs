using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace RelayProxy.Desktop;

// Owns each switch's clocks: showing a page synchronizes, changing a value animates.
internal sealed class ToggleMotion
{
    private static readonly Color On = Color.FromRgb(127, 127, 127), Off = Color.FromRgb(71, 71, 71);
    private const double Swell = 6;
    private readonly CheckBox check;
    private double restingWidth;
    public ToggleMotion(CheckBox check)
    {
        this.check = check;
        check.Checked += Changed; check.Unchecked += Changed; check.Indeterminate += Changed;
        check.Loaded += Synchronize; check.Unloaded += Synchronize;
        check.IsVisibleChanged += VisibilityChanged; check.SizeChanged += SizeChanged;
        check.PreviewMouseLeftButtonDown += Press; check.PreviewMouseLeftButtonUp += Release; check.LostMouseCapture += Release;
    }
    public void Detach()
    {
        Update(false);
        check.Checked -= Changed; check.Unchecked -= Changed; check.Indeterminate -= Changed;
        check.Loaded -= Synchronize; check.Unloaded -= Synchronize;
        check.IsVisibleChanged -= VisibilityChanged; check.SizeChanged -= SizeChanged;
        check.PreviewMouseLeftButtonDown -= Press; check.PreviewMouseLeftButtonUp -= Release; check.LostMouseCapture -= Release;
    }
    private void Changed(object sender, RoutedEventArgs e) => Update(check.IsLoaded && check.IsVisible && GlassMotion.CanAnimate);
    private void Synchronize(object sender, RoutedEventArgs e) => Update(false);
    private void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) => Update(false);
    private void SizeChanged(object sender, SizeChangedEventArgs e) => Update(false);
    private void Press(object sender, RoutedEventArgs e) => Stretch(true);
    private void Release(object sender, RoutedEventArgs e) => Stretch(false);
    // Like an iOS switch, the knob stretches toward the travel direction while pressed.
    private void Stretch(bool pressed)
    {
        if (check.Template?.FindName("Knob", check) is not Ellipse knob) return;
        if (restingWidth <= 0) restingWidth = knob.Width;
        var width = restingWidth + (pressed ? Swell : 0);
        var margin = new Thickness(check.IsChecked == true ? restingWidth - width : 0, 0, 0, 0);
        if (!GlassMotion.CanAnimate)
        {
            knob.BeginAnimation(FrameworkElement.WidthProperty, null); knob.BeginAnimation(FrameworkElement.MarginProperty, null);
            knob.Width = restingWidth; knob.Margin = new Thickness(0); return;
        }
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var time = TimeSpan.FromMilliseconds(pressed ? 140 : 360);
        knob.BeginAnimation(FrameworkElement.WidthProperty, new DoubleAnimation(width, time) { EasingFunction = easing }, HandoffBehavior.SnapshotAndReplace);
        knob.BeginAnimation(FrameworkElement.MarginProperty, new ThicknessAnimation(margin, time) { EasingFunction = easing }, HandoffBehavior.SnapshotAndReplace);
    }
    private void Update(bool animate)
    {
        if (check.Template?.FindName("Knob", check) is not Ellipse knob || check.Template.FindName("Track", check) is not Border track) return;
        if (restingWidth <= 0) restingWidth = knob.Width;
        var travel = Math.Max(0, track.ActualWidth - track.BorderThickness.Left - track.BorderThickness.Right - track.Padding.Left - track.Padding.Right - restingWidth);
        var destination = check.IsChecked == true ? travel : 0;
        var move = knob.RenderTransform as TranslateTransform ?? new TranslateTransform();
        knob.RenderTransform = move;
        double current = Math.Clamp(move.X, 0, travel);
        move.BeginAnimation(TranslateTransform.XProperty, null); move.X = destination;
        var color = check.IsChecked == true ? On : Off;
        var from = (track.Background as SolidColorBrush)?.Color ?? color;
        var brush = new SolidColorBrush(color); track.Background = brush;
        if (!animate || travel <= 0) return;
        // Monotonic easing cannot overshoot the track, including rapid reversals.
        move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(current, destination, TimeSpan.FromMilliseconds(380))
        { EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop });
        brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(from, color, TimeSpan.FromMilliseconds(300))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop });
    }
}
