using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace RelayProxy.Desktop;

// A clear glass slab. Layer 1 redraws the live wallpaper behind the element and bends it through
// RefractionEffect in real time; layer 2 adds tint and rim light; the Border's child sits on top.
public sealed class LiquidGlass : Border
{
    private static readonly Point RestLight = new(.22, .1);
    private static readonly Brush Glaze = Freeze(Linear(new(0, 0), new(.25, 1),
        (0, 30, 255, 255, 255), (.38, 6, 255, 255, 255), (.7, 0, 255, 255, 255), (1, 14, 197, 197, 197)));
    private static readonly Pen Rim = new(Freeze(Linear(new(0, 0), new(1, 1),
        (0, 150, 255, 255, 255), (.3, 34, 243, 243, 243), (.65, 14, 222, 222, 222), (1, 96, 224, 224, 224))), 1);
    private static readonly Pen Inner = new(Freeze(Linear(new(0, 0), new(1, 1),
        (0, 40, 255, 255, 255), (.5, 0, 255, 255, 255), (1, 18, 224, 224, 224))), .6);
    static LiquidGlass() { Rim.Freeze(); Inner.Freeze(); }

    private readonly Lens lens;
    private readonly Sheen sheen;
    private readonly RefractionEffect? refraction;
    private Point light = RestLight, target = RestLight;
    private double glow, targetGlow;
    private bool ticking;
    private long lastTick;

    public LiquidGlass()
    {
        SnapsToDevicePixels = true;
        lens = new Lens(this); sheen = new Sheen(this);
        if (RefractionEffect.Supported) lens.Effect = refraction = new RefractionEffect();
        AddVisualChild(lens); AddVisualChild(sheen);
        Loaded += (_, _) =>
        {
            lens.Attach(); Scene.Frame += Watch; LiveBackdrop.Resized += Redraw; UpdateOptics(RenderSize);
        };
        Unloaded += (_, _) => { Stop(); Scene.Frame -= Watch; LiveBackdrop.Resized -= Redraw; };
        IsVisibleChanged += (_, _) => { if (!IsVisible) Stop(); else lens.InvalidateVisual(); };
        MouseMove += (_, e) =>
        {
            var p = e.GetPosition(this);
            target = new(Math.Clamp(p.X / Math.Max(1, ActualWidth), 0, 1), Math.Clamp(p.Y / Math.Max(1, ActualHeight), 0, 1));
            targetGlow = 1; Start();
        };
        MouseLeave += (_, _) => { target = RestLight; targetGlow = 0; Start(); };
    }

    // A flat slab shows only its tint and rim, never the wallpaper bent behind it. Small moving
    // pieces such as the navigation pill use it so scenery does not ride along inside them.
    private bool flat;
    public bool Flat { get => flat; set { if (flat == value) return; flat = value; lens.InvalidateVisual(); } }

    // Margin of wallpaper drawn outside the slab, so the rim can pull in light from beyond the edge.
    internal double Spill => Math.Ceiling(Optics().Refraction + 4);
    private (double Refraction, double Thickness, double Zoom) Optics()
    {
        var side = Math.Max(1, Math.Min(ActualWidth, ActualHeight));
        var thickness = Math.Clamp(side * .34, 9, 30);
        return (thickness * .62, thickness, side < 70 ? .035 : .012);
    }
    // Large panels are frosted so text stays readable; small controls stay clear. Glass resting on
    // glass inherits the frost of the panel beneath it.
    private double Frost()
    {
        for (var parent = VisualTreeHelper.GetParent(this); parent != null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is LiquidGlass panel) return panel.Frost();
        var side = Math.Min(ActualWidth, ActualHeight);
        return side > 110 ? 7 : side > 60 ? 2.5 : 0;
    }
    private void Redraw() { if (IsVisible) lens.InvalidateVisual(); }
    // Glass slides, scales and scrolls through render transforms that never re-run OnRender,
    // so each frame it checks where it sits over the wallpaper and redraws only when that changed.
    // Position of a visual inside its window by layout alone. Render transforms (slide-ins, hover
    // scale, springs) are ignored on purpose: the refracted wallpaper stays pinned to the card while
    // it animates, so the shader does not have to run again for every frame of a transition.
    internal static Point LayoutOffset(Visual visual)
    {
        double x = 0, y = 0;
        for (Visual? v = visual; v != null; v = VisualTreeHelper.GetParent(v) as Visual)
        {
            var offset = VisualTreeHelper.GetOffset(v); x += offset.X; y += offset.Y;
        }
        return new Point(x, y);
    }
    private Point seen = new(double.NaN, double.NaN);
    private void Watch()
    {
        if (!IsVisible || lens.Backdrop is not { } backdrop) return;
        var now = LayoutOffset(this) - new Vector(LayoutOffset(backdrop).X, LayoutOffset(backdrop).Y);
        if (now == seen) return;
        seen = now; lens.InvalidateVisual();
    }

    protected override int VisualChildrenCount => base.VisualChildrenCount + 2;
    protected override Visual GetVisualChild(int index) => index switch { 0 => lens, 1 => sheen, _ => base.GetVisualChild(index - 2) };
    protected override Size MeasureOverride(Size constraint)
    {
        lens.Measure(constraint); sheen.Measure(constraint);
        return base.MeasureOverride(constraint);
    }
    protected override Size ArrangeOverride(Size size)
    {
        var result = base.ArrangeOverride(size);
        lens.Arrange(new Rect(size)); sheen.Arrange(new Rect(size));
        UpdateOptics(size);
        return result;
    }
    private void UpdateOptics(Size size)
    {
        if (refraction == null) return;
        var (bend, thickness, zoom) = Optics(); var spill = Spill;
        refraction.Geometry = new Point4D(size.Width + 2 * spill, size.Height + 2 * spill, spill, Radius(size));
        refraction.Optics = new Point4D(bend, thickness, .32, zoom);
        refraction.Tone = new Point4D(1.28, 1.04, Frost(), 0);
        UpdateLight();
    }
    private void UpdateLight() { if (refraction != null) refraction.Light = new Point4D(light.X, light.Y, glow, 1); }
    internal double Radius(Size size) => Math.Min(CornerRadius.TopLeft, Math.Min(size.Width, size.Height) / 2);
    protected override void OnRender(DrawingContext dc) { /* Layers draw the glass; the Border itself stays empty. */ }

    private void Start()
    {
        if (!GlassMotion.CanAnimate) { Stop(); glow = 0; UpdateLight(); return; }
        if (ticking || !IsVisible) return;
        ticking = true; lastTick = Stopwatch.GetTimestamp(); CompositionTarget.Rendering += Frame;
    }
    private void Stop() { CompositionTarget.Rendering -= Frame; ticking = false; }
    private void Frame(object? sender, EventArgs args)
    {
        if (!GlassMotion.CanAnimate || !IsVisible) { glow = 0; Stop(); UpdateLight(); return; }
        var now = Stopwatch.GetTimestamp();
        var dt = Math.Min(.05, (now - lastTick) / (double)Stopwatch.Frequency); lastTick = now;
        // Critically damped follow: no overshoot, framerate independent.
        var blend = 1 - Math.Exp(-10 * dt);
        light = new(light.X + (target.X - light.X) * blend, light.Y + (target.Y - light.Y) * blend);
        glow += (targetGlow - glow) * blend;
        UpdateLight();
        if (refraction == null) sheen.InvalidateVisual();
        if (Math.Abs(light.X - target.X) + Math.Abs(light.Y - target.Y) + Math.Abs(glow - targetGlow) < .002) Stop();
    }

    // Draws the moment of the wallpaper that lies behind the slab, in the slab's own coordinates.
    private sealed class Lens(LiquidGlass owner) : FrameworkElement
    {
        private FrameworkElement? backdrop;
        public FrameworkElement? Backdrop => backdrop;
        public void Attach() { backdrop = Window.GetWindow(owner)?.FindName("AmbientBackdrop") as FrameworkElement; IsHitTestVisible = false; InvalidateVisual(); }
        protected override void OnRender(DrawingContext dc)
        {
            if (ActualWidth < 2 || ActualHeight < 2 || owner.flat) return;
            var spill = owner.Spill;
            var area = new Rect(-spill, -spill, ActualWidth + 2 * spill, ActualHeight + 2 * spill);
            Matrix toLocal;
            try
            {
                if (backdrop is not { ActualWidth: > 0 }) throw new InvalidOperationException();
                var here = LayoutOffset(this); var there = LayoutOffset(backdrop);
                toLocal = new Matrix(1, 0, 0, 1, there.X - here.X, there.Y - here.Y);
            }
            catch (InvalidOperationException)
            {
                // Not connected yet: a calm tint until the wallpaper is reachable.
                var clipFallback = new RectangleGeometry(new Rect(RenderSize), owner.Radius(RenderSize), owner.Radius(RenderSize));
                dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(48, 81, 81, 81)), null, clipFallback);
                return;
            }
            if (owner.refraction != null) dc.PushClip(new RectangleGeometry(area));
            else { var r = owner.Radius(RenderSize); dc.PushClip(new RectangleGeometry(new Rect(RenderSize), r, r)); }
            dc.PushTransform(new MatrixTransform(toLocal));
            if (backdrop is LiveBackdrop { Direct: true })
            {
                // Windows without a visible wallpaper draw the shared scene straight into the glass.
                dc.PushTransform(new ScaleTransform(backdrop.RenderSize.Width / Scene.Canvas.Width, backdrop.RenderSize.Height / Scene.Canvas.Height));
                dc.DrawDrawing(Scene.Drawing); dc.Pop();
            }
            else if (backdrop is LiveBackdrop live) dc.DrawRectangle(live.CacheBrush, null, new Rect(backdrop.RenderSize));
            dc.Pop(); dc.Pop();
        }
    }

    // Tint, glaze and rim light on top of the refracted wallpaper. Also the hit-test surface.
    private sealed class Sheen(LiquidGlass owner) : FrameworkElement
    {
        protected override void OnRender(DrawingContext dc)
        {
            if (ActualWidth < 2 || ActualHeight < 2) return;
            var rect = new Rect(.5, .5, ActualWidth - 1, ActualHeight - 1);
            var radius = owner.Radius(rect.Size);
            var shape = new RectangleGeometry(rect, radius, radius);
            dc.DrawGeometry(owner.Background ?? Brushes.Transparent, null, shape);
            dc.PushClip(shape);
            dc.DrawRectangle(Glaze, null, rect);
            if (owner.refraction == null && owner.glow > .01)
            {
                var spot = new RadialGradientBrush(Color.FromArgb((byte)(36 * owner.glow), 255, 255, 255), Colors.Transparent) { Center = owner.light, GradientOrigin = owner.light, RadiusX = .75, RadiusY = .95 };
                dc.DrawRectangle(spot, null, rect);
            }
            dc.Pop();
            dc.DrawRoundedRectangle(null, Rim, rect, radius, radius);
            var inner = rect; inner.Inflate(-1.5, -1.5);
            if (inner.Width > 0 && inner.Height > 0) dc.DrawRoundedRectangle(null, Inner, inner, Math.Max(0, radius - 1.5), Math.Max(0, radius - 1.5));
        }
    }

    // Background (the tint) is drawn by the sheen layer, so a change must reach it.
    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == BackgroundProperty || e.Property == CornerRadiusProperty) { sheen?.InvalidateVisual(); lens?.InvalidateVisual(); if (e.Property == CornerRadiusProperty) UpdateOptics(RenderSize); }
    }

    private static Brush Freeze(Brush brush) { brush.Freeze(); return brush; }
    private static LinearGradientBrush Linear(Point start, Point end, params (double Offset, byte A, byte R, byte G, byte B)[] stops)
    {
        var brush = new LinearGradientBrush { StartPoint = start, EndPoint = end };
        foreach (var stop in stops) brush.GradientStops.Add(new(Color.FromArgb(stop.A, stop.R, stop.G, stop.B), stop.Offset));
        return brush;
    }
}
