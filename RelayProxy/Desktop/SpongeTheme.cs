using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace RelayProxy.Desktop;

// The name shown in the window, the tray and notifications; the secret code swaps it.
public static class Brand
{
    public static string Name { get; set; } = "Relay";
    public static bool Sponge { get; set; }
}

// The "GuSHin Proxy" look: an undersea wallpaper, a sponge logo and warm yellow accents.
// It is switched at runtime by a code typed into the settings page and remembered between launches.
public static class SpongeTheme
{
    // SHA-256 of the code, so the code itself is not spelled out in the published source.
    private const string CodeHash = "472428c31d84e3ab166857b3f2414572b914f32da51c008b76b37cc6567eb257";
    public static bool IsCode(string text) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text.Trim()))).Equals(CodeHash, StringComparison.OrdinalIgnoreCase);
    private static readonly ConditionalWeakTable<LiquidGlass, Brush> Originals = new();
    private static readonly ConditionalWeakTable<TextBlock, Brush> Captions = new();
    // Marks a caption whose colour came from its style: switching back clears the override.
    private static readonly Brush FromStyle = new SolidColorBrush();
    private static readonly FontFamily Round = new("Comic Sans MS, Segoe UI");

    public static void Apply(MainWindow window, bool on, bool animate)
    {
        Brand.Name = on ? "GuSHin Proxy" : "Relay"; Brand.Sponge = on;
        Scene.UseSponge(on);
        window.AmbientBackdrop.InvalidateVisual();

        window.Title = on ? "GuSHin Proxy — прокси для приложений" : "Relay — прокси для приложений";
        window.BrandName.Text = on ? "GuSHin" : "relay";
        window.BrandTag.Text = on ? "PROXY · ПОД ВОДОЙ" : "PERSONAL PROXY";
        window.BrandVersion.Text = (on ? "GuSHin Proxy " : "Relay ") + ReleaseNotes.Short;
        window.BrandName.FontFamily = on ? Round : window.FontFamily;
        window.PageTitle.FontFamily = on ? Round : window.FontFamily;
        window.BrandLogo.Source = on ? SpongeLogo.Image : new BitmapImage(new Uri("pack://application:,,,/RelayProxy;component/relay.png"));
        window.RootGrid.Background = new SolidColorBrush(on ? Color.FromRgb(7, 58, 108) : Color.FromRgb(15, 15, 15));
        // The icon follows the look everywhere: window, taskbar, Alt-Tab and the tray (see RefreshBrandText).
        try
        {
            window.Icon = on ? BitmapFrame.Create(new System.IO.MemoryStream(SpongeLogo.IconBytes()), BitmapCreateOptions.None, BitmapCacheOption.OnLoad) : new BitmapImage(new Uri("pack://application:,,,/RelayProxy;component/relay.ico"));
            if (new System.Windows.Interop.WindowInteropHelper(window).Handle != IntPtr.Zero) TaskbarIdentity.Apply(window, on);
        }
        catch (Exception ex) { RelayProxy.Core.DiagnosticLog.Write("Icon switch: " + ex.Message); }

        var resources = Application.Current.Resources;
        if (on)
        {
            resources["PrimaryFill"] = new LinearGradientBrush(Color.FromArgb(0xEE, 255, 224, 64), Color.FromArgb(0xEE, 245, 184, 0), 45);
            resources["PrimaryText"] = new SolidColorBrush(Color.FromRgb(74, 44, 0));
            resources["PopupFill"] = new SolidColorBrush(Color.FromArgb(0xF2, 10, 60, 104));
            resources["TipFill"] = new SolidColorBrush(Color.FromArgb(0xF4, 10, 60, 104));
            resources["ChromeInk"] = new SolidColorBrush(Colors.White);
            resources["NavInk"] = new SolidColorBrush(Color.FromRgb(200, 226, 240));
        }
        else foreach (var key in new[] { "PrimaryFill", "PrimaryText", "PopupFill", "TipFill", "ChromeInk", "NavInk" }) resources.Remove(key);

        // The moving navigation pill shows plain tinted glass, so no scenery rides along inside it.
        window.NavigationLens.Flat = on;
        window.SetLiquid(on);
        window.NavigationLens.Background = new SolidColorBrush(on ? Color.FromArgb(0x5A, 190, 232, 255) : Color.FromArgb(0x20, 255, 255, 255));
        // Water-coloured sweep and progress bar for the connection card, and a bobbing logo.
        plainSweep ??= window.SweepBand.Fill; plainBar ??= window.StatusBar.Background;
        window.SweepBand.Fill = on ? Streak(255, 226, 80, .5) : plainSweep;
        window.StatusBar.Background = on ? Streak(120, 225, 255, 1) : plainBar;
        window.StatusIcon.InvalidateVisual();
        window.SetIdleMotion(window.IsVisible || !window.IsLoaded);

        Tint(window, on);
        window.RefreshBrandText();

        if (animate && GlassMotion.CanAnimate)
            window.RootGrid.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(.15, 1, TimeSpan.FromMilliseconds(650)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop });
    }

    private static Brush? plainSweep, plainBar;
    private static LinearGradientBrush Streak(byte r, byte g, byte b, double peak)
    {
        var brush = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(1, 0) };
        brush.GradientStops.Add(new(Color.FromArgb(0, r, g, b), 0));
        brush.GradientStops.Add(new(Color.FromArgb((byte)(255 * peak), r, g, b), .5));
        brush.GradientStops.Add(new(Color.FromArgb(0, r, g, b), 1));
        return brush;
    }

    // Water-themed status effects that sit on top of the connection card: bubbles rising when the
    // proxy comes up, bubbles popping when it goes down, and coral tears sinking on error.
    public static void Burst(Canvas canvas, string kind)
    {
        if (!GlassMotion.CanAnimate || canvas.ActualWidth < 40 || canvas.ActualHeight < 20) return;
        double width = canvas.ActualWidth, height = canvas.ActualHeight;
        var random = new Random();
        int count = kind == "live" ? 18 : kind == "pop" ? 12 : 9;
        for (int i = 0; i < count; i++)
        {
            double size = kind == "error" ? 5 + random.NextDouble() * 5 : 8 + random.NextDouble() * 20;
            Brush stroke = kind == "error" ? new SolidColorBrush(Color.FromArgb(230, 255, 120, 130)) : new SolidColorBrush(Color.FromArgb(210, 255, 255, 255));
            Brush fill = kind == "error" ? new SolidColorBrush(Color.FromArgb(120, 255, 100, 115)) : new RadialGradientBrush(Color.FromArgb(10, 255, 255, 255), Color.FromArgb(70, 200, 245, 255));
            var bubble = new System.Windows.Shapes.Ellipse { Width = size, Height = size, Stroke = stroke, StrokeThickness = 1.3, Fill = fill, Opacity = 0, RenderTransformOrigin = new Point(.5, .5), RenderTransform = new ScaleTransform(1, 1) };
            double left = random.NextDouble() * (width - size), delay = random.NextDouble() * (kind == "live" ? 450 : 320);
            Canvas.SetLeft(bubble, left);
            canvas.Children.Add(bubble);
            var begin = TimeSpan.FromMilliseconds(delay);
            if (kind == "live")
            {
                Canvas.SetTop(bubble, height);
                var rise = new DoubleAnimation(height, -size, TimeSpan.FromMilliseconds(1300 + random.NextDouble() * 900)) { BeginTime = begin, EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut } };
                bubble.BeginAnimation(Canvas.TopProperty, rise);
                bubble.BeginAnimation(Canvas.LeftProperty, new DoubleAnimation(left, left + (random.NextDouble() - .5) * 60, rise.Duration.TimeSpan) { BeginTime = begin, EasingFunction = new SineEase() });
                Fade(bubble, begin, rise.Duration.TimeSpan, .95, canvas);
            }
            else if (kind == "pop")
            {
                Canvas.SetTop(bubble, random.NextDouble() * (height - size));
                var grow = new DoubleAnimation(.5, 1.7, TimeSpan.FromMilliseconds(420)) { BeginTime = begin, EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
                ((ScaleTransform)bubble.RenderTransform).BeginAnimation(ScaleTransform.ScaleXProperty, grow);
                ((ScaleTransform)bubble.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, grow);
                Fade(bubble, begin, TimeSpan.FromMilliseconds(420), 1, canvas);
            }
            else
            {
                Canvas.SetTop(bubble, -size);
                var fall = new DoubleAnimation(-size, height * .8, TimeSpan.FromMilliseconds(900 + random.NextDouble() * 500)) { BeginTime = begin, EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };
                bubble.BeginAnimation(Canvas.TopProperty, fall);
                Fade(bubble, begin, fall.Duration.TimeSpan, .9, canvas);
            }
        }
    }

    // Two rings spread out from where the navigation drop lands, like a drop hitting still water.
    public static void Ripple(Canvas canvas, double centreY)
    {
        if (!GlassMotion.CanAnimate || canvas.ActualWidth < 40) return;
        double width = canvas.ActualWidth - 8;
        for (int i = 0; i < 2; i++)
        {
            var ring = new System.Windows.Shapes.Ellipse
            {
                Width = width, Height = 45, Stroke = new SolidColorBrush(Color.FromArgb(150, 220, 245, 255)), StrokeThickness = 1.6 - i * .5, Opacity = 0,
                RenderTransformOrigin = new Point(.5, .5), RenderTransform = new ScaleTransform(.7, .7)
            };
            Canvas.SetLeft(ring, 4); Canvas.SetTop(ring, centreY - 22.5);
            canvas.Children.Add(ring);
            var begin = TimeSpan.FromMilliseconds(i * 140); var length = TimeSpan.FromMilliseconds(900);
            // Rings stay inside the sidebar: they widen only up to the width of the navigation column.
            var spread = new DoubleAnimation(.6, 1, length) { BeginTime = begin, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            ((ScaleTransform)ring.RenderTransform).BeginAnimation(ScaleTransform.ScaleXProperty, spread);
            ((ScaleTransform)ring.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(.7, 1.5 + i * .35, length) { BeginTime = begin, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            Fade(ring, begin, length, .9 - i * .3, canvas);
        }
    }

    private static void Fade(UIElement element, TimeSpan begin, TimeSpan length, double peak, Canvas canvas)
    {
        var fade = new DoubleAnimationUsingKeyFrames { BeginTime = begin, Duration = length };
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0)));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(peak, KeyTime.FromPercent(.2)));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1)));
        fade.Completed += (_, _) => canvas.Children.Remove(element);
        element.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    // Dark glass tints turn deep-sea blue; the originals are kept so the plain look comes back exactly.
    private static void Tint(DependencyObject root, bool on)
    {
        if (root is LiquidGlass glass)
        {
            if (on)
            {
                if (glass.Background is SolidColorBrush { Color: { R: < 0x30, G: < 0x30, B: < 0x30 } color })
                {
                    Originals.AddOrUpdate(glass, glass.Background);
                    glass.Background = new SolidColorBrush(Color.FromArgb(color.A, 6, 52, 96));
                }
            }
            else if (Originals.TryGetValue(glass, out var original)) { glass.Background = original; Originals.Remove(glass); }
        }
        // Small grey captions would vanish against the bright water, so they turn pale for the theme.
        // Only colours the text sets itself (directly or through its style) are touched: text that
        // inherits from a navigation item or a button must keep following its hover/selected state.
        if (root is TextBlock text)
        {
            if (on)
            {
                var source = DependencyPropertyHelper.GetValueSource(text, TextBlock.ForegroundProperty).BaseValueSource;
                if (source is BaseValueSource.Local or BaseValueSource.Style
                    && text.Foreground is SolidColorBrush { Color: { A: 255 } grey } && grey.R == grey.G && grey.G == grey.B && grey.R is > 0x50 and < 0xE0)
                {
                    Captions.AddOrUpdate(text, source == BaseValueSource.Local ? text.Foreground : FromStyle);
                    text.Foreground = new SolidColorBrush(Color.FromRgb(226, 242, 250));
                }
            }
            else if (Captions.TryGetValue(text, out var original))
            {
                if (original == FromStyle) text.ClearValue(TextBlock.ForegroundProperty); else text.Foreground = original;
                Captions.Remove(text);
            }
        }
        foreach (var child in LogicalTreeHelper.GetChildren(root))
            if (child is DependencyObject next) Tint(next, on);
    }
}

// A cartoon sponge drawn from shapes, so no picture file ships with the app.
public static class SpongeLogo
{
    private static DrawingImage? image;
    public static DrawingImage Image => image ??= Build();

    public static BitmapSource Icon(int size)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) { dc.PushTransform(new ScaleTransform(size / 100.0, size / 100.0)); dc.DrawDrawing(Image.Drawing); }
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual); bitmap.Freeze();
        return bitmap;
    }

    // A multi-size .ico (PNG frames) so the taskbar, Alt-Tab, tray and window title all get a sharp sponge.
    public static byte[] IconBytes()
    {
        int[] sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
        var frames = sizes.Select(size =>
        {
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(Icon(size)));
            using var memory = new System.IO.MemoryStream(); encoder.Save(memory); return memory.ToArray();
        }).ToList();
        using var output = new System.IO.MemoryStream(); using var writer = new System.IO.BinaryWriter(output);
        writer.Write((short)0); writer.Write((short)1); writer.Write((short)sizes.Length);
        int offset = 6 + 16 * sizes.Length;
        for (int i = 0; i < sizes.Length; i++)
        {
            writer.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i])); writer.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            writer.Write((byte)0); writer.Write((byte)0); writer.Write((short)1); writer.Write((short)32);
            writer.Write(frames[i].Length); writer.Write(offset); offset += frames[i].Length;
        }
        foreach (var frame in frames) writer.Write(frame);
        return output.ToArray();
    }

    private static SolidColorBrush Fill(byte r, byte g, byte b, byte a = 255) { var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b)); brush.Freeze(); return brush; }
    private static Pen Line(byte r, byte g, byte b, double width) { var pen = new Pen(Fill(r, g, b), width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }; pen.Freeze(); return pen; }
    private static Drawing Shape(Brush? fill, Pen? pen, Geometry geometry) { geometry.Freeze(); return new GeometryDrawing(fill, pen, geometry); }
    private static Drawing Dot(Brush? fill, Pen? pen, double x, double y, double rx, double ry) => Shape(fill, pen, new EllipseGeometry(new Point(x, y), rx, ry));

    private static DrawingImage Build()
    {
        var group = new DrawingGroup();
        group.Children.Add(Shape(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 100, 100))));
        var body = new LinearGradientBrush(Color.FromRgb(255, 232, 76), Color.FromRgb(246, 190, 16), 90); body.Freeze();
        group.Children.Add(Shape(body, Line(176, 124, 0, 2), new RectangleGeometry(new Rect(10, 8, 80, 82), 11, 11)));
        // Shirt and trousers along the bottom edge.
        group.Children.Add(Shape(Fill(248, 248, 248), null, Geometry.Parse("M11,66 H89 V72 H11 Z")));
        group.Children.Add(Shape(Fill(142, 90, 42), null, Geometry.Parse("M11,72 H89 V81 A9,9 0 0 1 80,90 H20 A9,9 0 0 1 11,81 Z")));
        // Pores.
        foreach (var (x, y, r) in new[] { (23d, 22d, 3.6), (79d, 28d, 4.2), (74d, 55d, 3d), (20d, 52d, 3.4), (60d, 14d, 2.6) })
            group.Children.Add(Dot(Fill(226, 172, 0, 200), null, x, y, r, r * .85));
        // Eyes.
        var outline = Line(86, 64, 24, 1.6);
        foreach (var x in new[] { 36d, 64d })
        {
            group.Children.Add(Dot(Fill(255, 255, 255), outline, x, 36, 12.5, 13));
            group.Children.Add(Dot(Fill(66, 156, 214), null, x + (x < 50 ? 2 : -2), 38, 5.6, 5.6));
            group.Children.Add(Dot(Fill(10, 10, 14), null, x + (x < 50 ? 2 : -2), 38, 2.6, 2.6));
            group.Children.Add(Dot(Fill(255, 255, 255), null, x + (x < 50 ? .5 : -3.5), 35.5, 1.2, 1.2));
            group.Children.Add(Shape(null, Line(70, 50, 10, 1.6), Geometry.Parse(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"M{x - 6},22 L{x - 8},17.5 M{x},21.5 L{x},16.5 M{x + 6},22 L{x + 8},17.5"))));
        }
        group.Children.Add(Dot(Fill(236, 176, 0), null, 50, 49, 2.8, 5));
        group.Children.Add(Dot(Fill(255, 140, 110, 190), null, 22, 57, 6.5, 5));
        group.Children.Add(Dot(Fill(255, 140, 110, 190), null, 78, 57, 6.5, 5));
        // Smile with teeth.
        group.Children.Add(Shape(Fill(150, 40, 36), Line(110, 40, 14, 1.8), Geometry.Parse("M29,58 Q50,80 71,58 Q50,67 29,58 Z")));
        group.Children.Add(Shape(Fill(255, 255, 255), null, new RectangleGeometry(new Rect(44.2, 62.6, 5.2, 5.4), 1, 1)));
        group.Children.Add(Shape(Fill(255, 255, 255), null, new RectangleGeometry(new Rect(50.6, 62.6, 5.2, 5.4), 1, 1)));
        var result = new DrawingImage(group); result.Freeze();
        return result;
    }
}
