using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using RelayProxy.Core;

namespace RelayProxy.Desktop;

// The living wallpaper, designed on a fixed canvas and stretched to the window. Each glow and light
// ribbon is baked into a bitmap once; afterwards only their offsets and opacities animate, so the
// GPU just slides finished textures around and the CPU stays idle.
public static class Scene
{
    public static readonly Size Canvas = new(1600, 1000);
    private const int FrameRate = 24;
    // Fires every frame so glass can notice when it has moved over the wallpaper.
    public static event Action? Frame;
    private static readonly Brush Grain = MakeGrain();
    private static readonly List<AnimationClock> Clocks = [];
    private static DrawingGroup? drawing;
    private static bool running = true, ticking;

    private static bool requested = true, held;
    private static System.Windows.Threading.DispatcherTimer? holdTimer;

    // The wallpaper pauses while the interface itself is moving (page changes, scrolling, the
    // navigation drop), so those frames spend everything on the motion the eye follows and stay
    // smooth. It resumes by itself a moment after the last call.
    public static void Hold(int milliseconds)
    {
        if (!requested || !GlassMotion.CanAnimate) return;
        held = true; Apply();
        holdTimer ??= new System.Windows.Threading.DispatcherTimer();
        holdTimer.Stop(); holdTimer.Interval = TimeSpan.FromMilliseconds(milliseconds);
        holdTimer.Tick -= Release; holdTimer.Tick += Release; holdTimer.Start();
    }
    private static void Release(object? sender, EventArgs e) { holdTimer?.Stop(); held = false; Apply(); }
    private static void Apply()
    {
        running = requested && !held;
        foreach (var clock in Clocks) { if (running) clock.Controller?.Resume(); else clock.Controller?.Pause(); }
    }

    public static void Run(bool animate)
    {
        requested = animate; Apply();
        if (animate) suspended = false;
        if (!suspended && !ticking) { ticking = true; CompositionTarget.Rendering += Tick; }
    }
    private static bool suspended;

    // Rendering fires once per composed frame, which can be far above 60 Hz when nothing limits it; watching glass at that rate only burns CPU.
    private static long lastTick;
    private static void Tick(object? sender, EventArgs e)
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        if ((now - lastTick) * 1000.0 / System.Diagnostics.Stopwatch.Frequency < 14) return;
        lastTick = now; Frame?.Invoke();
    }
    // For a window living in the tray: stops every clock and the frame callback and drops the baked
    // wallpaper bitmaps. The scene is rebuilt on demand when something draws it again.
    public static void Suspend()
    {
        Run(false); suspended = true; holdTimer?.Stop(); held = false;
        if (ticking) { ticking = false; CompositionTarget.Rendering -= Tick; }
        foreach (var clock in Clocks) clock.Controller?.Remove();
        Clocks.Clear(); drawing = null;
    }

    public static Drawing Drawing => drawing ??= Sponge ? BuildSea() : Build();

    // The undersea wallpaper of the secret theme; the next render rebuilds the whole scene.
    public static bool Sponge { get; private set; }
    public static void UseSponge(bool on)
    {
        if (on == Sponge) return;
        Sponge = on;
        foreach (var clock in Clocks) clock.Controller?.Remove();
        Clocks.Clear(); drawing = null;
    }

    private static DrawingGroup BuildSea()
    {
        double w = Canvas.Width, h = Canvas.Height;
        var root = new DrawingGroup();
        var bounds = new Rect(-200, -200, w + 400, h + 400);
        var water = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute, StartPoint = new(w * .3, 0), EndPoint = new(w * .7, h) };
        water.GradientStops.Add(new(Color.FromRgb(4, 54, 108), 0));
        water.GradientStops.Add(new(Color.FromRgb(8, 104, 160), .55));
        water.GradientStops.Add(new(Color.FromRgb(22, 146, 170), 1));
        water.Freeze();
        root.Children.Add(new GeometryDrawing(water, null, new RectangleGeometry(bounds)));

        root.Children.Add(Glow(w, h, .78, .02, .60, Color.FromRgb(255, 240, 150), .42, 23, 31, 0));
        root.Children.Add(Glow(w, h, .50, 1.02, .75, Color.FromRgb(255, 206, 70), .55, 29, 21, .3));
        root.Children.Add(Glow(w, h, .06, .30, .45, Color.FromRgb(70, 230, 235), .32, 19, 27, .6));
        root.Children.Add(Glow(w, h, .10, .96, .40, Color.FromRgb(255, 128, 150), .22, 25, 17, .15));
        root.Children.Add(Glow(w, h, .56, .50, .32, Color.FromRgb(160, 255, 240), .12, 33, 23, .8));

        root.Children.Add(Ribbon(w, h, .78, .30, .62, .10, Color.FromRgb(255, 236, 130), .36, 17, 0));
        root.Children.Add(Ribbon(w, h, .95, .58, .40, .26, Color.FromRgb(140, 240, 255), .30, 23, .4));
        root.Children.Add(Ribbon(w, h, .60, .86, .20, .48, Color.FromRgb(255, 214, 90), .26, 29, .7));

        foreach (var (x, y, r, period, phase) in new[] { (.12, .20, 26d, 13d, 0d), (.22, .70, 40d, 17d, .3), (.38, .38, 16d, 11d, .6), (.55, .82, 30d, 15d, .1), (.68, .22, 22d, 12d, .45), (.80, .62, 46d, 19d, .8), (.92, .30, 18d, 10d, .2), (.46, .12, 12d, 9d, .7) })
            root.Children.Add(Bubble(w, h, x, y, r, period, phase));

        // Slow shafts of sunlight from the surface, and bubbles that keep rising from the sea floor.
        foreach (var (x, width, alpha, period, phase) in new[] { (.18, 190d, .20, 23d, 0d), (.46, 260d, .16, 29d, .35), (.72, 170d, .22, 19d, .6), (.92, 230d, .14, 31d, .15) })
            root.Children.Add(Ray(w, h, x, width, alpha, period, phase));
        // Rolling surface waves along the top, quick caustic threads of light and tiny glints.
        foreach (var (y, amp, length, alpha, seconds, phase) in new[] { (.035, 16d, 520d, .26, 11d, 0d), (.075, 22d, 760d, .17, 16d, .4), (.125, 14d, 380d, .11, 9d, .7) })
            root.Children.Add(Wave(w, h, y, amp, length, alpha, seconds, phase));
        root.Children.Add(Ribbon(w, h, .70, .52, .30, .62, Color.FromRgb(210, 255, 255), .22, 11, .2));
        root.Children.Add(Ribbon(w, h, .30, .12, .46, .70, Color.FromRgb(255, 250, 200), .18, 13, .9));
        var random = new Random(11);
        // Every layer is one more thing to blend on each wallpaper frame, so the counts stay modest.
        for (int i = 0; i < 12; i++)
            root.Children.Add(Glint(w, h, random.NextDouble(), random.NextDouble(), 2 + random.NextDouble() * 3, 2.5 + random.NextDouble() * 4, random.NextDouble()));
        for (int i = 0; i < 10; i++)
            root.Children.Add(Rise(w, h, .04 + .92 * i / 9 + random.NextDouble() * .03, 5 + random.NextDouble() * 12, 15 + random.NextDouble() * 16, random.NextDouble()));

        // Hand-drawn outline flowers in the colours of the Bikini Bottom sky.
        var flowers = new (double X, double Y, double R, Color Ink, double Turn, double Period)[]
        {
            (.07, .08, 38, Color.FromRgb(255, 255, 255), 10, 23), (.15, .27, 92, Color.FromRgb(46, 176, 60), 40, 29),
            (.43, .44, 132, Color.FromRgb(240, 224, 44), 0, 33), (.85, .20, 168, Color.FromRgb(210, 160, 230), 25, 37),
            (.06, .84, 190, Color.FromRgb(72, 206, 216), 55, 41), (.52, .92, 46, Color.FromRgb(218, 44, 142), 20, 21),
            (.79, .80, 92, Color.FromRgb(44, 74, 208), 62, 27), (.96, .66, 52, Color.FromRgb(46, 176, 60), 8, 25)
        };
        for (int i = 0; i < flowers.Length; i++)
            root.Children.Add(Flower(w, h, flowers[i].X, flowers[i].Y, flowers[i].R, flowers[i].Ink, flowers[i].Turn, flowers[i].Period, i * .17));
        foreach (var (x, y, r, period, phase) in new[] { (.46, .30, 46d, 21d, .1), (.90, .80, 56d, 25d, .55), (.05, .14, 38d, 19d, .8) })
            root.Children.Add(Jellyfish(w, h, x, y, r, period, phase));

        root.Children.Add(new GeometryDrawing(Grain, null, new RectangleGeometry(bounds)));
        return root;
    }

    // A travelling water-surface wave: the baked strip is exactly one wavelength wider on each side,
    // so sliding it by a wavelength on a loop looks endless.
    private static Drawing Wave(double w, double h, double y, double amplitude, double length, double alpha, double seconds, double phase)
    {
        double top = y * h, left = -length * 2;
        var area = new Rect(left, top - amplitude - 150, w + length * 4, amplitude * 2 + 150 + 8);
        var line = new StreamGeometry(); var body = new StreamGeometry();
        using (var a = line.Open()) using (var b = body.Open())
        {
            for (double x = left; x <= left + area.Width + 6; x += 6)
            {
                var p = new Point(x, top + amplitude * Math.Sin(x / length * 2 * Math.PI) + amplitude * .35 * Math.Sin(x / length * 4 * Math.PI + 1));
                if (x == left) { a.BeginFigure(p, false, false); b.BeginFigure(p, true, true); } else { a.LineTo(p, true, true); b.LineTo(p, true, true); }
            }
            b.LineTo(new Point(left + area.Width, area.Y), true, false); b.LineTo(new Point(left, area.Y), true, false);
        }
        var fill = new LinearGradientBrush(Color.FromArgb(0, 210, 250, 255), Color.FromArgb((byte)(255 * alpha * .8), 210, 250, 255), 90);
        fill.StartPoint = new(0, 0); fill.EndPoint = new(0, 1);
        var crest = new Pen(new SolidColorBrush(Color.FromArgb((byte)(255 * Math.Min(1, alpha * 3.2)), 235, 255, 255)), 2.6) { LineJoin = PenLineJoin.Round };
        var baked = Bake(area, .5, dc => { dc.DrawGeometry(fill, null, body); dc.DrawGeometry(null, crest, line); });
        var slide = new TranslateTransform();
        Loop(slide, TranslateTransform.XProperty, 0, length, seconds, phase);
        var group = new DrawingGroup { Transform = slide };
        group.Children.Add(baked);
        return group;
    }

    // A speck of light that twinkles in the water.
    private static Drawing Glint(double w, double h, double x, double y, double radius, double period, double phase)
    {
        var center = new Point(x * w, y * h); var size = radius * 8;
        var glow = new RadialGradientBrush(Color.FromArgb(230, 255, 255, 255), Color.FromArgb(0, 255, 255, 255));
        var baked = Bake(new Rect(center.X - size / 2, center.Y - size / 2, size, size), 1, dc => dc.DrawEllipse(glow, null, center, radius * 3.2, radius * 3.2));
        var group = new DrawingGroup { Transform = Drift(w * .004, h * .02, period * 1.6, period, phase) };
        group.Children.Add(baked);
        Animate(group, DrawingGroup.OpacityProperty, .05, 1, period, phase);
        return group;
    }

    // A tilted shaft of light that fades out with depth and slowly breathes and sways.
    private static Drawing Ray(double w, double h, double x, double width, double alpha, double period, double phase)
    {
        var area = new Rect(x * w - width / 2, -h * .25, width, h * 1.7);
        var band = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(1, 0) };
        band.GradientStops.Add(new(Color.FromArgb(0, 255, 246, 190), 0));
        band.GradientStops.Add(new(Color.FromArgb((byte)(255 * alpha), 255, 246, 190), .5));
        band.GradientStops.Add(new(Color.FromArgb(0, 255, 246, 190), 1));
        var fade = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(0, 1) };
        fade.GradientStops.Add(new(Color.FromArgb(255, 0, 0, 0), .22)); fade.GradientStops.Add(new(Color.FromArgb(0, 0, 0, 0), .8));
        var baked = Bake(area, .5, dc => { dc.PushOpacityMask(fade); dc.DrawRectangle(band, null, area); dc.Pop(); });
        var sway = new RotateTransform(-17, area.X + width / 2, area.Y);
        Animate(sway, RotateTransform.AngleProperty, -20, -14, period, phase);
        var group = new DrawingGroup { Transform = sway };
        group.Children.Add(baked);
        Animate(group, DrawingGroup.OpacityProperty, .45, 1, period * .6, phase + .3);
        return group;
    }

    // A small bubble that climbs the whole scene on a loop, wobbling from side to side.
    private static Drawing Rise(double w, double h, double x, double radius, double seconds, double phase)
    {
        var center = new Point(x * w, h + radius * 3); var size = radius * 2 + 6;
        var rim = new Pen(new SolidColorBrush(Color.FromArgb(160, 255, 255, 255)), Math.Max(1.2, radius * .09));
        var fill = new RadialGradientBrush(Color.FromArgb(8, 255, 255, 255), Color.FromArgb(60, 200, 245, 255)) { Center = new(.4, .35), GradientOrigin = new(.4, .35) };
        var baked = Bake(new Rect(center.X - size / 2, center.Y - size / 2, size, size), 1, dc =>
        {
            dc.DrawEllipse(fill, rim, center, radius, radius);
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(190, 255, 255, 255)), null, new Point(center.X - radius * .35, center.Y - radius * .4), radius * .17, radius * .11);
        });
        var move = new TranslateTransform();
        Loop(move, TranslateTransform.YProperty, 0, -(h + radius * 8 + 40), seconds, phase);
        Animate(move, TranslateTransform.XProperty, -radius * 1.6, radius * 1.6, seconds * .28, phase);
        var group = new DrawingGroup { Transform = move };
        group.Children.Add(baked);
        return group;
    }

    // Endless one-way motion; a negative begin time starts each layer part-way along its journey.
    private static void Loop(Animatable target, DependencyProperty property, double from, double to, double seconds, double phase)
    {
        var travel = new DoubleAnimation(from, to, TimeSpan.FromSeconds(seconds)) { RepeatBehavior = RepeatBehavior.Forever, BeginTime = TimeSpan.FromSeconds(-seconds * (phase % 1)) };
        Timeline.SetDesiredFrameRate(travel, FrameRate);
        var clock = travel.CreateClock();
        target.ApplyAnimationClock(property, clock);
        if (!running) clock.Controller?.Pause();
        Clocks.Add(clock);
    }

    // The five-petal sea flowers that float across the Bikini Bottom sky, turning slowly.
    private static Drawing Flower(double w, double h, double x, double y, double radius, Color ink, double turn, double period, double phase)
    {
        var center = new Point(x * w, y * h); var size = radius * 2.5;
        var stroke = new SolidColorBrush(Color.FromArgb(205, ink.R, ink.G, ink.B));
        var pen = new Pen(stroke, Math.Max(2.2, radius * .075)) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        // Five soft, uneven lobes: a rounded 5-fold curve with a little wobble so it looks drawn by hand.
        var outline = new StreamGeometry();
        using (var ctx = outline.Open())
        {
            const int steps = 140;
            for (int i = 0; i <= steps; i++)
            {
                double a = i * 2 * Math.PI / steps, lobe = Math.Pow((1 + Math.Cos(5 * a)) / 2, .85);
                double r = radius * (.38 + .62 * lobe) * (1 + .07 * Math.Sin(2 * a + phase * 9) + .05 * Math.Sin(3 * a + phase * 5));
                var p = new Point(center.X + r * Math.Sin(a), center.Y - r * Math.Cos(a));
                if (i == 0) ctx.BeginFigure(p, false, true); else ctx.LineTo(p, true, true);
            }
        }
        outline.Freeze();
        var baked = Bake(new Rect(center.X - size / 2, center.Y - size / 2, size, size), .8, dc =>
        {
            dc.PushTransform(new RotateTransform(turn, center.X, center.Y));
            dc.DrawGeometry(null, pen, outline);
            dc.DrawEllipse(stroke, null, new Point(center.X, center.Y - radius * .05), radius * .075, radius * .1);
            dc.DrawEllipse(stroke, null, new Point(center.X, center.Y + radius * .2), radius * .03, radius * .03);
            dc.Pop();
        });
        var spin = new RotateTransform(0, center.X, center.Y);
        Animate(spin, RotateTransform.AngleProperty, -9, 9, period, phase);
        var group = new DrawingGroup { Transform = new TransformGroup { Children = { spin, Drift(w * .03, h * .04, period * 1.1, period * .8, phase) } } };
        group.Children.Add(baked);
        Animate(group, DrawingGroup.OpacityProperty, .7, 1, period * .7, phase + .2);
        return group;
    }

    // A pink jellyfish that pulses upward and sinks again, trailing wavy tentacles.
    private static Drawing Jellyfish(double w, double h, double x, double y, double radius, double period, double phase)
    {
        var center = new Point(x * w, y * h);
        var area = new Rect(center.X - radius * 1.5, center.Y - radius * 1.2, radius * 3, radius * 3.9);
        var dome = new StreamGeometry();
        using (var ctx = dome.Open())
        {
            ctx.BeginFigure(new Point(center.X - radius, center.Y), true, true);
            ctx.ArcTo(new Point(center.X + radius, center.Y), new Size(radius, radius * .9), 0, false, SweepDirection.Clockwise, true, true);
            ctx.QuadraticBezierTo(new Point(center.X, center.Y + radius * .34), new Point(center.X - radius, center.Y), true, true);
        }
        dome.Freeze();
        var body = new RadialGradientBrush(Color.FromArgb(150, 255, 190, 225), Color.FromArgb(70, 255, 120, 190)) { Center = new(.5, .8), GradientOrigin = new(.5, .8), RadiusX = .8, RadiusY = .9 };
        var rim = new Pen(new SolidColorBrush(Color.FromArgb(190, 255, 215, 238)), Math.Max(1.6, radius * .05));
        var thread = new Pen(new SolidColorBrush(Color.FromArgb(150, 255, 190, 225)), Math.Max(2, radius * .06)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        var baked = Bake(area, 1, dc =>
        {
            for (int i = 0; i < 5; i++)
            {
                double tx = center.X + (i - 2) * radius * .38, sway = (i % 2 == 0 ? 1 : -1) * radius * .22;
                var line = new StreamGeometry();
                using (var ctx = line.Open())
                {
                    ctx.BeginFigure(new Point(tx, center.Y + radius * .12), false, false);
                    ctx.BezierTo(new Point(tx + sway, center.Y + radius * .9), new Point(tx - sway, center.Y + radius * 1.6), new Point(tx + sway * .6, center.Y + radius * 2.4), true, false);
                }
                line.Freeze(); dc.DrawGeometry(null, thread, line);
            }
            dc.DrawGeometry(body, rim, dome);
            foreach (var (dx, dy) in new[] { (-.4, -.35), (.1, -.55), (.45, -.25) })
                dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(120, 255, 235, 245)), null, new Point(center.X + dx * radius, center.Y + dy * radius), radius * .1, radius * .1);
        });
        var group = new DrawingGroup { Transform = Drift(w * .035, h * .13, period * 1.3, period, phase) };
        group.Children.Add(baked);
        Animate(group, DrawingGroup.OpacityProperty, .65, 1, period * .6, phase + .4);
        return group;
    }

    // A soap bubble: a faint disc with a bright rim and a small highlight, rising and falling slowly.
    private static Drawing Bubble(double w, double h, double x, double y, double radius, double period, double phase)
    {
        var center = new Point(x * w, y * h); var size = radius * 2 + 8;
        var rim = new Pen(new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)), Math.Max(1.4, radius * .07));
        var fill = new RadialGradientBrush(Color.FromArgb(10, 255, 255, 255), Color.FromArgb(70, 200, 245, 255)) { Center = new(.4, .35), GradientOrigin = new(.4, .35) };
        var area = new Rect(center.X - size / 2, center.Y - size / 2, size, size);
        var baked = Bake(area, 1, dc =>
        {
            dc.DrawEllipse(fill, rim, center, radius, radius);
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(190, 255, 255, 255)), null, new Point(center.X - radius * .35, center.Y - radius * .4), radius * .17, radius * .11);
        });
        var group = new DrawingGroup { Transform = Drift(w * .008, h * .09, period * 1.3, period, phase) };
        group.Children.Add(baked);
        Animate(group, DrawingGroup.OpacityProperty, .6, 1, period * .8, phase + .3);
        return group;
    }

    private static DrawingGroup Build()
    {
        double w = Canvas.Width, h = Canvas.Height;
        var root = new DrawingGroup();
        var bounds = new Rect(-200, -200, w + 400, h + 400);
        var sky = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute, StartPoint = new(0, 0), EndPoint = new(w, h) };
        sky.GradientStops.Add(new(Color.FromRgb(9, 9, 9), 0));
        sky.GradientStops.Add(new(Color.FromRgb(13, 13, 13), .5));
        sky.GradientStops.Add(new(Color.FromRgb(15, 15, 15), 1));
        sky.Freeze();
        root.Children.Add(new GeometryDrawing(sky, null, new RectangleGeometry(bounds)));

        root.Children.Add(Glow(w, h, .18, .14, .62, Color.FromRgb(77, 77, 77), .42, 23, 31, 0));
        root.Children.Add(Glow(w, h, .90, .88, .56, Color.FromRgb(95, 95, 95), .34, 29, 21, .3));
        root.Children.Add(Glow(w, h, .78, .08, .42, Color.FromRgb(88, 88, 88), .20, 19, 27, .6));
        root.Children.Add(Glow(w, h, .04, .94, .46, Color.FromRgb(69, 69, 69), .30, 25, 17, .15));
        root.Children.Add(Glow(w, h, .52, .54, .30, Color.FromRgb(116, 116, 116), .08, 33, 23, .8));

        root.Children.Add(Ribbon(w, h, .80, .30, .64, .10, Color.FromRgb(162, 162, 162), .42, 17, 0));
        root.Children.Add(Ribbon(w, h, .95, .58, .40, .26, Color.FromRgb(151, 151, 151), .32, 23, .4));
        root.Children.Add(Ribbon(w, h, .60, .86, .20, .48, Color.FromRgb(146, 146, 146), .22, 29, .7));

        root.Children.Add(new GeometryDrawing(Grain, null, new RectangleGeometry(bounds)));
        return root;
    }

    private static Drawing Glow(double w, double h, double x, double y, double radius, Color color, double alpha, double periodX, double periodY, double phase)
    {
        var brush = new RadialGradientBrush();
        brush.GradientStops.Add(new(Color.FromArgb((byte)(255 * alpha), color.R, color.G, color.B), 0));
        brush.GradientStops.Add(new(Color.FromArgb((byte)(255 * alpha * .45), color.R, color.G, color.B), .45));
        brush.GradientStops.Add(new(Color.FromArgb(0, color.R, color.G, color.B), 1));
        brush.Freeze();
        var center = new Point(x * w, y * h); var r = radius * w;
        // Soft light survives a low resolution bake.
        var baked = Bake(new Rect(center.X - r, center.Y - r, 2 * r, 2 * r), .3, dc => dc.DrawEllipse(brush, null, center, r, r));
        var group = new DrawingGroup { Transform = Drift(w * .07, h * .08, periodX, periodY, phase) };
        group.Children.Add(baked);
        Animate(group, DrawingGroup.OpacityProperty, .72, 1, periodY * .8, phase + .25);
        return group;
    }

    // A silk ribbon of light. Stacked strokes give a soft glow without a blur pass.
    private static Drawing Ribbon(double w, double h, double y0, double y1, double y2, double y3, Color color, double strength, double period, double phase)
    {
        var path = new StreamGeometry();
        using (var ctx = path.Open())
        {
            ctx.BeginFigure(new Point(-.15 * w, y0 * h), false, false);
            ctx.BezierTo(new Point(.3 * w, y1 * h), new Point(.66 * w, y2 * h), new Point(1.15 * w, y3 * h), true, true);
        }
        path.Freeze();
        // Half resolution: the strokes are soft light, and a quarter of the pixels to blend every frame.
        var baked = Bake(new Rect(-120, -120, w + 240, h + 240), .5, dc =>
        {
            foreach (var (width, a) in new[] { (64d, .03), (30d, .05), (13d, .10), (5d, .22), (1.8d, .85) })
            {
                var brush = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute, StartPoint = new(0, 0), EndPoint = new(w, 0) };
                byte alpha = (byte)(255 * a * strength);
                brush.GradientStops.Add(new(Color.FromArgb(0, color.R, color.G, color.B), 0));
                brush.GradientStops.Add(new(Color.FromArgb(alpha, color.R, color.G, color.B), .28));
                brush.GradientStops.Add(new(Color.FromArgb(alpha, 255, 255, 255), .55));
                brush.GradientStops.Add(new(Color.FromArgb(alpha, color.R, color.G, color.B), .82));
                brush.GradientStops.Add(new(Color.FromArgb(0, color.R, color.G, color.B), 1));
                dc.DrawGeometry(null, new Pen(brush, width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, path);
            }
        });
        var group = new DrawingGroup { Transform = Drift(w * .05, h * .07, period * 1.4, period, phase) };
        group.Children.Add(baked);
        Animate(group, DrawingGroup.OpacityProperty, .55, 1, period * .9, phase + .5);
        return group;
    }

    private static ImageDrawing Bake(Rect area, double scale, Action<DrawingContext> draw)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) { dc.PushTransform(new TranslateTransform(-area.X, -area.Y)); draw(dc); }
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(area.Width * scale), (int)Math.Ceiling(area.Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(visual); bitmap.Freeze();
        return new ImageDrawing(bitmap, area);
    }

    private static TranslateTransform Drift(double ax, double ay, double periodX, double periodY, double phase)
    {
        var move = new TranslateTransform();
        Animate(move, TranslateTransform.XProperty, -ax, ax, periodX, phase);
        Animate(move, TranslateTransform.YProperty, -ay, ay, periodY, phase + .37);
        return move;
    }
    // Endless sine sway; a negative begin time starts each layer at a different point of its cycle.
    private static void Animate(Animatable target, DependencyProperty property, double from, double to, double period, double phase)
    {
        var sway = new DoubleAnimation(from, to, TimeSpan.FromSeconds(period / 2))
        {
            AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            BeginTime = TimeSpan.FromSeconds(-period * (phase % 1))
        };
        // The wallpaper drifts a fraction of a pixel per frame, so a lower rate looks identical and
        // spares a full-window repaint on every display refresh. UI animations keep the full rate.
        Timeline.SetDesiredFrameRate(sway, FrameRate);
        var clock = sway.CreateClock();
        target.ApplyAnimationClock(property, clock);
        if (!running) clock.Controller?.Pause();
        Clocks.Add(clock);
    }

    // Fine noise hides 8-bit banding in the dark gradients.
    private static Brush MakeGrain()
    {
        const int size = 128;
        var pixels = new byte[size * size * 4];
        var random = new Random(7);
        for (int i = 0; i < pixels.Length; i += 4)
        {
            byte a = 9, p = (byte)(random.Next(0, 256) * 9 / 255);
            pixels[i] = p; pixels[i + 1] = p; pixels[i + 2] = p; pixels[i + 3] = a;
        }
        var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Pbgra32, null, pixels, size * 4);
        var brush = new ImageBrush(bitmap) { TileMode = TileMode.Tile, ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, size, size), Stretch = Stretch.None };
        brush.Freeze();
        return brush;
    }
}

// The window background; glass surfaces look it up by the name "AmbientBackdrop".
// It is cached as one GPU bitmap per frame; glass samples that bitmap through CacheBrush instead
// of drawing the animated wallpaper again. Give it a negative margin so the cache extends past the
// window edges, where glass bends light in from just outside its rim.
public sealed class LiveBackdrop : FrameworkElement
{
    public static event Action? Resized;
    public Brush CacheBrush { get; }
    public bool Direct { get; set; }
    public LiveBackdrop()
    {
        IsHitTestVisible = false;
        CacheMode = new BitmapCache(1);
        CacheBrush = new BitmapCacheBrush(this);
    }
    protected override void OnRenderSizeChanged(SizeChangedInfo info) { base.OnRenderSizeChanged(info); Resized?.Invoke(); }
    protected override void OnRender(DrawingContext dc)
    {
        // A direct backdrop only gives the glass a coordinate frame; the glass paints the scene itself.
        if (Direct) return;
        dc.PushClip(new RectangleGeometry(new Rect(RenderSize)));
        dc.PushTransform(new ScaleTransform(RenderSize.Width / Scene.Canvas.Width, RenderSize.Height / Scene.Canvas.Height));
        dc.DrawDrawing(Scene.Drawing);
        dc.Pop(); dc.Pop();
    }
}

// Pixel shader that bends the wallpaper through a rounded glass slab.
public sealed class RefractionEffect : ShaderEffect
{
    private static readonly PixelShader? Shader = Load();
    public static bool Supported => Shader != null && RenderCapability.IsPixelShaderVersionSupported(3, 0);
    private static PixelShader? Load()
    {
        try { var shader = new PixelShader { UriSource = new Uri("pack://application:,,,/RelayProxy;component/Desktop/Glass.ps") }; shader.Freeze(); return shader; }
        catch (Exception ex) { DiagnosticLog.Write("Glass shader unavailable: " + ex.Message); return null; }
    }
    public static readonly DependencyProperty InputProperty = RegisterPixelShaderSamplerProperty("Input", typeof(RefractionEffect), 0);
    public static readonly DependencyProperty GeometryProperty = DependencyProperty.Register("Geometry", typeof(Point4D), typeof(RefractionEffect), new UIPropertyMetadata(new Point4D(1, 1, 0, 0), PixelShaderConstantCallback(0)));
    public static readonly DependencyProperty OpticsProperty = DependencyProperty.Register("Optics", typeof(Point4D), typeof(RefractionEffect), new UIPropertyMetadata(new Point4D(8, 18, .35, .02), PixelShaderConstantCallback(1)));
    public static readonly DependencyProperty LightProperty = DependencyProperty.Register("Light", typeof(Point4D), typeof(RefractionEffect), new UIPropertyMetadata(new Point4D(.25, .1, 0, 1), PixelShaderConstantCallback(2)));
    public static readonly DependencyProperty ToneProperty = DependencyProperty.Register("Tone", typeof(Point4D), typeof(RefractionEffect), new UIPropertyMetadata(new Point4D(1.25, 1.05, 0, 0), PixelShaderConstantCallback(3)));
    public RefractionEffect()
    {
        PixelShader = Shader;
        UpdateShaderValue(InputProperty); UpdateShaderValue(GeometryProperty); UpdateShaderValue(OpticsProperty);
        UpdateShaderValue(LightProperty); UpdateShaderValue(ToneProperty);
    }
    public Brush Input { get => (Brush)GetValue(InputProperty); set => SetValue(InputProperty, value); }
    public Point4D Geometry { get => (Point4D)GetValue(GeometryProperty); set => SetValue(GeometryProperty, value); }
    public Point4D Optics { get => (Point4D)GetValue(OpticsProperty); set => SetValue(OpticsProperty, value); }
    public Point4D Light { get => (Point4D)GetValue(LightProperty); set => SetValue(LightProperty, value); }
    public Point4D Tone { get => (Point4D)GetValue(ToneProperty); set => SetValue(ToneProperty, value); }
}
