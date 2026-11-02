using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace RelayProxy.Desktop;

public static class GlassMotion
{
    public static bool Enabled { get; set; } = true;
    public static bool CanAnimate => Enabled && SystemParameters.ClientAreaAnimation;
    public static readonly DependencyProperty InteractiveProperty = DependencyProperty.RegisterAttached("Interactive", typeof(bool), typeof(GlassMotion), new PropertyMetadata(false, Attach));
    public static void SetInteractive(DependencyObject target, bool value) => target.SetValue(InteractiveProperty, value);
    public static bool GetInteractive(DependencyObject target) => (bool)target.GetValue(InteractiveProperty);
    public static readonly DependencyProperty StageProperty = DependencyProperty.RegisterAttached("Stage", typeof(bool), typeof(GlassMotion), new PropertyMetadata(false));
    public static void SetStage(DependencyObject target, bool value) => target.SetValue(StageProperty, value);
    public static bool GetStage(DependencyObject target) => (bool)target.GetValue(StageProperty);
    public static readonly DependencyProperty SmoothToggleProperty = DependencyProperty.RegisterAttached("SmoothToggle", typeof(bool), typeof(GlassMotion), new PropertyMetadata(false, ToggleAttach));
    public static void SetSmoothToggle(DependencyObject target, bool value) => target.SetValue(SmoothToggleProperty, value);
    public static bool GetSmoothToggle(DependencyObject target) => (bool)target.GetValue(SmoothToggleProperty);
    private static readonly DependencyProperty ToggleStateProperty = DependencyProperty.RegisterAttached("ToggleState", typeof(ToggleMotion), typeof(GlassMotion));
    private static void ToggleAttach(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not CheckBox check) return;
        (check.GetValue(ToggleStateProperty) as ToggleMotion)?.Detach();
        check.ClearValue(ToggleStateProperty);
        if ((bool)args.NewValue) check.SetValue(ToggleStateProperty, new ToggleMotion(check));
    }

    private static readonly IEasingFunction Decelerate = Frozen(new QuinticEase { EasingMode = EasingMode.EaseOut });
    private static readonly IEasingFunction Smooth = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });
    private static readonly IEasingFunction Settle = Frozen(new SpringEase(.62, 15, .6));
    private static readonly IEasingFunction Soft = Frozen(new SpringEase(.84, 14, .5));
    // The water theme: pages and indicators move like drops of liquid, with springy overshoot.
    public static bool Liquid { get; set; }
    private static readonly IEasingFunction Wobble = Frozen(new SpringEase(.34, 15, 1));
    // Soft landing with only a hair of overshoot, so nothing swings past the edge of its container.
    private static readonly IEasingFunction Drop = Frozen(new SpringEase(.86, 11, 1));
    private static T Frozen<T>(T easing) where T : Freezable { easing.Freeze(); return easing; }

    private static void Attach(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not FrameworkElement element) return;
        if ((bool)args.NewValue)
        {
            element.RenderTransformOrigin = new Point(.5, .5);
            element.RenderTransform = new ScaleTransform(1, 1);
            element.MouseEnter += Enter; element.MouseLeave += Leave;
            element.PreviewMouseLeftButtonDown += Down; element.PreviewMouseLeftButtonUp += Up;
            element.LostMouseCapture += Release;
            element.IsEnabledChanged += Reset;
        }
        else
        {
            element.MouseEnter -= Enter; element.MouseLeave -= Leave;
            element.PreviewMouseLeftButtonDown -= Down; element.PreviewMouseLeftButtonUp -= Up;
            element.LostMouseCapture -= Release;
            element.IsEnabledChanged -= Reset;
        }
    }
    private static void Enter(object sender, System.Windows.Input.MouseEventArgs e) { var el = (FrameworkElement)sender; Scale(el, 1.02, Soft, 420); Shine(el, 1); }
    private static void Leave(object sender, System.Windows.Input.MouseEventArgs e) { var el = (FrameworkElement)sender; Scale(el, 1, Soft, 420); Shine(el, 0); }
    private static void Down(object sender, System.Windows.Input.MouseButtonEventArgs e) { var el = (FrameworkElement)sender; Scale(el, .955, Smooth, 120); Shine(el, 1.6); }
    private static void Up(object sender, System.Windows.Input.MouseButtonEventArgs e) => Release(sender, e);
    private static void Release(object sender, System.Windows.Input.MouseEventArgs e)
    {
        var el = (FrameworkElement)sender;
        Scale(el, el.IsMouseOver ? 1.02 : 1, Liquid ? Wobble : Settle, Liquid ? 900 : 560); Shine(el, el.IsMouseOver ? 1 : 0);
    }
    private static void Reset(object sender, DependencyPropertyChangedEventArgs e) { var el = (FrameworkElement)sender; Scale(el, 1, Soft, 300); Shine(el, 0); }
    private static void Scale(FrameworkElement element, double target, IEasingFunction easing, int milliseconds)
    {
        if (element.RenderTransform is not ScaleTransform scale) return;
        foreach (var property in new[] { ScaleTransform.ScaleXProperty, ScaleTransform.ScaleYProperty })
        {
            if (!CanAnimate) { scale.BeginAnimation(property, null); scale.SetValue(property, 1d); continue; }
            scale.BeginAnimation(property, new DoubleAnimation(target, TimeSpan.FromMilliseconds(milliseconds)) { EasingFunction = easing }, HandoffBehavior.SnapshotAndReplace);
        }
    }
    // Templates expose an optional "Shine" layer; its opacity follows hover and press.
    private static void Shine(FrameworkElement element, double level)
    {
        if (element is not Control { Template: { } template } control || template.FindName("Shine", control) is not UIElement shine) return;
        if (!CanAnimate) { shine.BeginAnimation(UIElement.OpacityProperty, null); shine.Opacity = 0; return; }
        shine.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(Math.Min(1, level), TimeSpan.FromMilliseconds(level > 1 ? 90 : 260)) { EasingFunction = Smooth }, HandoffBehavior.SnapshotAndReplace);
    }

    // Shows a page: the container fades in while its cards rise one after another.
    public static void Present(FrameworkElement page)
    {
        Fade(page, 0, 260);
        var stage = Descendants(page).Where(GetStage).Take(10).ToList();
        if (stage.Count == 0 && page is Panel panel) stage = panel.Children.OfType<FrameworkElement>().Where(c => c.Visibility == Visibility.Visible).Take(8).ToList();
        int index = 0;
        foreach (var card in stage) Rise(card, Math.Min(index++ * (Liquid ? 80 : 55), Liquid ? 400 : 280), Liquid ? 40 : 18);
    }
    public static void Dismiss(FrameworkElement page, Action completed)
    {
        if (!CanAnimate) { completed(); return; }
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(130)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }, FillBehavior = FillBehavior.Stop };
        fade.Completed += (_, _) => completed();
        page.Opacity = 0;
        page.BeginAnimation(UIElement.OpacityProperty, fade);
    }
    // Text that was just replaced glides in from slightly below while it fades up.
    public static void Nudge(FrameworkElement element)
    {
        if (!CanAnimate || !element.IsLoaded || !element.IsVisible) return;
        if (element.RenderTransform is not TranslateTransform { IsFrozen: false } move) { move = new TranslateTransform(); element.RenderTransform = move; }
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(340)) { EasingFunction = Smooth, FillBehavior = FillBehavior.Stop }, HandoffBehavior.SnapshotAndReplace);
        move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(560)) { EasingFunction = Decelerate, FillBehavior = FillBehavior.Stop }, HandoffBehavior.SnapshotAndReplace);
    }
    public static void FadeColor(Border border, Color color)
    {
        if (border.Background is SolidColorBrush { IsFrozen: false } current && CanAnimate && border.IsLoaded)
            current.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(color, TimeSpan.FromMilliseconds(320)) { EasingFunction = Smooth });
        else border.Background = new SolidColorBrush(color);
    }
    private static IEnumerable<FrameworkElement> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement element) yield return element;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
    private static void Fade(UIElement element, int delay, int milliseconds)
    {
        element.BeginAnimation(UIElement.OpacityProperty, null); element.Opacity = 1;
        if (!CanAnimate) return;
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(milliseconds)) { BeginTime = TimeSpan.FromMilliseconds(delay), EasingFunction = Smooth, FillBehavior = FillBehavior.Stop });
    }
    private static void Rise(FrameworkElement element, int delay, double offset)
    {
        // The starting pose is the base value and every animation holds its end, so a card waiting for its
        // turn stays invisible instead of flashing on screen first. Cleanup happens when the slide ends.
        element.BeginAnimation(UIElement.OpacityProperty, null);
        // Liquid mode (the water theme): cards rise like drops, squashing wide and stretching tall as a spring settles them.
        bool liquid = Liquid;
        // Cards live inside scroll viewers that clip them square, so a card never grows past its final
        // size (and barely overshoots its position): it starts small and settles from below.
        var scale = new ScaleTransform(liquid ? .93 : .975, liquid ? .96 : .975); var move = new TranslateTransform(0, offset);
        element.RenderTransformOrigin = new Point(.5, liquid ? .8 : .5);
        if (!CanAnimate) { element.Opacity = 1; element.RenderTransform = Transform.Identity; return; }
        element.Opacity = 0;
        element.RenderTransform = new TransformGroup { Children = { scale, move } };
        var begin = TimeSpan.FromMilliseconds(delay);
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(380)) { BeginTime = begin, EasingFunction = Smooth, FillBehavior = FillBehavior.HoldEnd };
        fade.Completed += (_, _) => { element.BeginAnimation(UIElement.OpacityProperty, null); element.Opacity = 1; };
        element.BeginAnimation(UIElement.OpacityProperty, fade);
        var rise = new DoubleAnimation(offset, 0, TimeSpan.FromMilliseconds(liquid ? 1000 : 680)) { BeginTime = begin, EasingFunction = liquid ? Drop : Decelerate, FillBehavior = FillBehavior.HoldEnd };
        rise.Completed += (_, _) => { if (element.RenderTransform is TransformGroup group && group.Children.Contains(move)) element.RenderTransform = Transform.Identity; };
        move.BeginAnimation(TranslateTransform.YProperty, rise);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(liquid ? .93 : .975, 1, TimeSpan.FromMilliseconds(liquid ? 900 : 680)) { BeginTime = begin, EasingFunction = Decelerate, FillBehavior = FillBehavior.HoldEnd });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(liquid ? .96 : .975, 1, TimeSpan.FromMilliseconds(liquid ? 1000 : 680)) { BeginTime = begin, EasingFunction = liquid ? Drop : Decelerate, FillBehavior = FillBehavior.HoldEnd });
    }

    // The navigation lens glides to its slot and stretches slightly while travelling.
    public static void MoveIndicator(FrameworkElement indicator, double offset)
    {
        var group = indicator.RenderTransform as TransformGroup;
        if (group is not { Children.Count: 2 })
        {
            group = new TransformGroup { Children = { new ScaleTransform(1, 1), new TranslateTransform() } };
            indicator.RenderTransformOrigin = new Point(.5, .5); indicator.RenderTransform = group;
        }
        var scale = (ScaleTransform)group.Children[0]; var move = (TranslateTransform)group.Children[1];
        if (!CanAnimate)
        {
            move.BeginAnimation(TranslateTransform.YProperty, null); move.Y = offset;
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null); scale.ScaleY = 1;
            return;
        }
        var distance = Math.Abs(offset - move.Y);
        move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(offset, TimeSpan.FromMilliseconds(620)) { EasingFunction = Settle }, HandoffBehavior.SnapshotAndReplace);
        if (distance < 1) return;
        var stretch = new DoubleAnimationUsingKeyFrames();
        stretch.KeyFrames.Add(new EasingDoubleKeyFrame(1 + Math.Min(.2, distance / 700), KeyTime.FromPercent(.3), new CubicEase { EasingMode = EasingMode.EaseOut }));
        stretch.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(1), new CubicEase { EasingMode = EasingMode.EaseInOut }));
        stretch.Duration = TimeSpan.FromMilliseconds(520);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, stretch, HandoffBehavior.SnapshotAndReplace);
    }

    // Damped spring evaluated by WPF at display refresh rate: x(t) = 1 - e^(-zeta*w*t) * (cos(wd*t) + zeta*w/wd * sin(wd*t)).
    private sealed class SpringEase(double damping, double frequency, double seconds) : Freezable, IEasingFunction
    {
        public double Ease(double normalizedTime)
        {
            if (normalizedTime >= 1) return 1;
            var t = normalizedTime * seconds;
            var decay = Math.Exp(-damping * frequency * t);
            var wd = frequency * Math.Sqrt(1 - damping * damping);
            return 1 - decay * (Math.Cos(wd * t) + damping * frequency / wd * Math.Sin(wd * t));
        }
        protected override Freezable CreateInstanceCore() => new SpringEase(damping, frequency, seconds);
    }
}
