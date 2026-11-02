using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace RelayProxy.Desktop;

// A small line icon that redraws itself for every connection state: a power symbol when off,
// a spinning arc while connecting, a ring and tick when live, a ring and cross on error.
public sealed class StatusGlyph : FrameworkElement
{
    private static readonly DependencyProperty PhaseProperty = DependencyProperty.Register("Phase", typeof(double), typeof(StatusGlyph), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly DependencyProperty RevealProperty = DependencyProperty.Register("Reveal", typeof(double), typeof(StatusGlyph), new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly DependencyProperty RippleProperty = DependencyProperty.Register("Ripple", typeof(double), typeof(StatusGlyph), new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly Pen Ink = MakePen(new SolidColorBrush(Color.FromRgb(240, 240, 240)), 2.3);
    private static readonly Pen Faint = MakePen(new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)), 2.3);
    private string state = "idle";
    private readonly ScaleTransform scale = new(1, 1);

    public StatusGlyph()
    {
        RenderTransformOrigin = new Point(.5, .5); RenderTransform = scale;
        IsVisibleChanged += (_, _) => Restart(false);
        Loaded += (_, _) => Restart(false);
    }
    public string State => state;

    public void SetState(string next)
    {
        if (next == state) return;
        state = next; Restart(true);
    }

    private void Restart(bool animate)
    {
        BeginAnimation(PhaseProperty, null); BeginAnimation(RevealProperty, null); BeginAnimation(RippleProperty, null);
        SetValue(RevealProperty, 1d); SetValue(RippleProperty, 1d);
        bool motion = GlassMotion.CanAnimate && IsLoaded && IsVisible;
        if (state == "busy" && motion)
            BeginAnimation(PhaseProperty, new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(1100)) { RepeatBehavior = RepeatBehavior.Forever });
        if (!animate || !motion) return;
        if (state != "busy")
            BeginAnimation(RevealProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(state == "error" ? 520 : 680)) { EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut } });
        BeginAnimation(RippleProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(900)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        // A short spring: the icon swells a little as it changes meaning.
        var pulse = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(620) };
        pulse.KeyFrames.Add(new EasingDoubleKeyFrame(state == "error" ? .8 : 1.22, KeyTime.FromPercent(.28), new CubicEase { EasingMode = EasingMode.EaseOut }));
        pulse.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(1), new ElasticEase { EasingMode = EasingMode.EaseOut, Oscillations = 1, Springiness = 5 }));
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, pulse); scale.BeginAnimation(ScaleTransform.ScaleYProperty, pulse);
        if (state == "error")
        {
            var move = new TranslateTransform(); RenderTransform = new TransformGroup { Children = { scale, move } };
            var shake = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(520) };
            double[] steps = { 0, -4, 4, -3, 2, -1, 0 };
            for (int i = 0; i < steps.Length; i++) shake.KeyFrames.Add(new EasingDoubleKeyFrame(steps[i], KeyTime.FromPercent(i / (double)(steps.Length - 1)), new SineEase { EasingMode = EasingMode.EaseInOut }));
            shake.Completed += (_, _) => RenderTransform = scale;
            move.BeginAnimation(TranslateTransform.XProperty, shake);
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size < 1) return;
        var k = size / 28;
        dc.PushTransform(new TranslateTransform((ActualWidth - size) / 2, (ActualHeight - size) / 2));
        dc.PushTransform(new ScaleTransform(k, k));
        var reveal = (double)GetValue(RevealProperty);
        var ripple = (double)GetValue(RippleProperty);
        var centre = new Point(14, 14);
        if (Scene.Sponge) { DrawSponge(dc, centre, reveal, ripple); dc.Pop(); dc.Pop(); return; }
        if (state != "busy" && ripple < 1)
            dc.DrawEllipse(null, MakePen(new SolidColorBrush(Color.FromArgb((byte)(120 * (1 - ripple)), 255, 255, 255)), 1.4), centre, 11 + 7 * ripple, 11 + 7 * ripple);
        switch (state)
        {
            case "busy":
            {
                dc.DrawEllipse(null, Faint, centre, 10, 10);
                dc.PushTransform(new RotateTransform((double)GetValue(PhaseProperty), 14, 14));
                dc.DrawGeometry(null, Ink, Arc(centre, 10, -90, 105 + 40 * Math.Sin((double)GetValue(PhaseProperty) * Math.PI / 180)));
                dc.Pop();
                break;
            }
            case "live":
            {
                dc.DrawGeometry(null, Ink, Arc(centre, 10, -90, 359.9 * Math.Min(1, reveal / .6)));
                var t = Math.Clamp((reveal - .45) / .55, 0, 1);
                Polyline(dc, Ink, t, new Point(8.6, 14.4), new Point(12.5, 18.3), new Point(19.6, 10));
                break;
            }
            case "error":
            {
                dc.DrawGeometry(null, Ink, Arc(centre, 10, -90, 359.9 * Math.Min(1, reveal / .6)));
                var t = Math.Clamp((reveal - .4) / .6, 0, 1);
                Polyline(dc, Ink, Math.Min(1, t * 2), new Point(9.8, 9.8), new Point(18.2, 18.2));
                Polyline(dc, Ink, Math.Clamp(t * 2 - 1, 0, 1), new Point(18.2, 9.8), new Point(9.8, 18.2));
                break;
            }
            default:
            {
                // Power symbol: ring open at the top, stem dropping into the gap.
                dc.DrawGeometry(null, Ink, Arc(centre, 10, -50, 280 * reveal));
                Polyline(dc, Ink, reveal, new Point(14, 4), new Point(14, 13));
                break;
            }
        }
        dc.Pop(); dc.Pop();
    }

    // GuSHin look: a soap bubble that wobbles while connecting, grins sun-yellow when live, pops into
    // droplets when switched off, and frowns coral-red with crossed eyes on error.
    private void DrawSponge(DrawingContext dc, Point centre, double reveal, double ripple)
    {
        var tone = state switch { "live" => Color.FromRgb(255, 222, 64), "error" => Color.FromRgb(255, 112, 120), _ => Color.FromRgb(205, 238, 255) };
        var ink = MakePen(new SolidColorBrush(tone), 2.2);
        var thin = MakePen(new SolidColorBrush(Color.FromArgb(200, tone.R, tone.G, tone.B)), 1.5);
        var spot = new SolidColorBrush(tone);
        double phase = (double)GetValue(PhaseProperty) * Math.PI / 180;
        if (state == "busy")
        {
            double wobble = .9 * Math.Sin(phase * 2);
            dc.DrawEllipse(null, ink, centre, 9.6 + wobble, 9.6 - wobble);
            dc.DrawGeometry(null, thin, Arc(centre, 6.4, 200, 60));
            for (int k = 0; k < 3; k++)
            {
                double t = ((phase / (2 * Math.PI)) + k / 3.0) % 1;
                double x = 14 + (k - 1) * 4.6 + 1.4 * Math.Sin(t * 9 + k), y = 21 - 14 * t, r = 1.9 - 1.1 * t;
                dc.DrawEllipse(null, MakePen(new SolidColorBrush(Color.FromArgb((byte)(230 * (1 - t * .8)), 255, 255, 255)), 1), new Point(x, y), r, r);
            }
            return;
        }
        double grow = state == "idle" ? .55 + .45 * reveal : 1;
        dc.DrawEllipse(null, ink, centre, 10 * grow, 10 * grow);
        if (state == "idle") dc.DrawGeometry(null, thin, Arc(centre, 6.6, 200, 60 * reveal));
        else if (state == "live")
        {
            var t = Math.Clamp((reveal - .3) / .7, 0, 1);
            dc.DrawGeometry(null, ink, Arc(new Point(14, 14.2), 5.6, 28, 124 * t));
            dc.DrawEllipse(spot, null, new Point(10.4, 10.6), 1.5 * t, 1.7 * t);
            dc.DrawEllipse(spot, null, new Point(17.6, 10.6), 1.5 * t, 1.7 * t);
        }
        else if (state == "error")
        {
            var t = Math.Clamp((reveal - .3) / .7, 0, 1);
            dc.DrawGeometry(null, ink, Arc(new Point(14, 21), 5.4, 205, 130 * t));
            foreach (var ex in new[] { 10.4, 17.6 })
            {
                Polyline(dc, thin, t, new Point(ex - 1.6, 9.2), new Point(ex + 1.6, 12.4));
                Polyline(dc, thin, t, new Point(ex + 1.6, 9.2), new Point(ex - 1.6, 12.4));
            }
        }
        // Droplets fly outward when the state changes; for "idle" this is the bubble popping.
        if (ripple < 1)
            for (int i = 0; i < 8; i++)
            {
                double a = (i * 45 + 12) * Math.PI / 180, d = 11 + 9 * ripple, r = 1.9 * (1 - ripple) + .3;
                dc.DrawEllipse(new SolidColorBrush(Color.FromArgb((byte)(210 * (1 - ripple)), tone.R, tone.G, tone.B)), null, new Point(14 + d * Math.Cos(a), 14 + d * Math.Sin(a)), r, r);
            }
    }

    private static void Polyline(DrawingContext dc, Pen pen, double progress, params Point[] points)
    {
        if (progress <= 0) return;
        double total = 0; for (int i = 1; i < points.Length; i++) total += (points[i] - points[i - 1]).Length;
        double left = total * progress; var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(points[0], false, false);
            for (int i = 1; i < points.Length && left > 0; i++)
            {
                var segment = points[i] - points[i - 1]; var length = segment.Length;
                var end = left >= length ? points[i] : points[i - 1] + segment * (left / length);
                ctx.LineTo(end, true, true); left -= length;
            }
        }
        dc.DrawGeometry(null, pen, geometry);
    }
    private static Geometry Arc(Point centre, double radius, double startDegrees, double sweepDegrees)
    {
        sweepDegrees = Math.Clamp(sweepDegrees, .1, 359.9);
        Point At(double deg) => new(centre.X + radius * Math.Cos(deg * Math.PI / 180), centre.Y + radius * Math.Sin(deg * Math.PI / 180));
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(At(startDegrees), false, false);
            ctx.ArcTo(At(startDegrees + sweepDegrees), new Size(radius, radius), 0, sweepDegrees > 180, SweepDirection.Clockwise, true, true);
        }
        return geometry;
    }
    private static Pen MakePen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        return pen;
    }
}
