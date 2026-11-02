using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace RelayProxy.Desktop;

// The tray menu: a small glass sheet in the same style and motion as the main window.
public partial class TrayMenu : Window
{
    private readonly System.Drawing.Point anchor;
    private Action? chosen;
    private bool closing;
    private readonly Action restore, toggle, settings, exit;

    public TrayMenu(System.Drawing.Point anchor, string state, bool connected, bool busy, bool showDisconnect, Action restore, Action toggle, Action settings, Action exit)
    {
        InitializeComponent();
        this.anchor = anchor; this.restore = restore; this.toggle = toggle; this.settings = settings; this.exit = exit;
        StateText.Text = state; OpenText.Text = "Открыть " + Brand.Name;
        Dot.Fill = new SolidColorBrush(connected ? Color.FromRgb(236, 236, 238) : busy ? Color.FromRgb(168, 168, 174) : Color.FromRgb(120, 120, 126));
        ConnectText.Text = showDisconnect ? "Отключить приложения" : "Подключить приложения";
        ConnectItem.IsEnabled = !busy;
        Loaded += (_, _) => Place();
        ContentRendered += (_, _) => Appear();
        Deactivated += (_, _) => Dismiss();
        Closed += (_, _) => (Application.Current?.MainWindow as MainWindow)?.RefreshScene();
        Scene.Run(GlassMotion.CanAnimate);
    }

    // Opens above the pointer, the way tray menus do, kept inside the work area.
    private void Place()
    {
        var dpi = VisualTreeHelper.GetDpi(this); var area = System.Windows.Forms.Screen.FromPoint(anchor).WorkingArea;
        double Dip(double px, double scale) => px / scale;
        double left = Dip(anchor.X, dpi.DpiScaleX) - ActualWidth + 44, top = Dip(anchor.Y, dpi.DpiScaleY) - ActualHeight + 40;
        left = Math.Clamp(left, Dip(area.Left, dpi.DpiScaleX) - 30, Dip(area.Right, dpi.DpiScaleX) - ActualWidth + 30);
        top = Math.Clamp(top, Dip(area.Top, dpi.DpiScaleY) - 24, Dip(area.Bottom, dpi.DpiScaleY) - ActualHeight + 40);
        Left = left; Top = top;
    }

    private void Appear()
    {
        Activate(); OpenItem.Focus();
        if (!GlassMotion.CanAnimate) { Root.Opacity = 1; return; }
        var scale = new ScaleTransform(.92, .9); Panel.RenderTransformOrigin = new Point(.85, 1); Panel.RenderTransform = scale;
        var ease = new QuinticEase { EasingMode = EasingMode.EaseOut };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(.92, 1, TimeSpan.FromMilliseconds(420)) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(.9, 1, TimeSpan.FromMilliseconds(420)) { EasingFunction = ease });
        Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)) { FillBehavior = FillBehavior.Stop });
        Root.Opacity = 1;
        // Items settle in one after another.
        int index = 0;
        foreach (var item in new FrameworkElement[] { Header, OpenItem, ConnectItem, SettingsItem, ExitItem })
        {
            // Hidden until its turn; the end state is held and then cleared, so nothing flashes first.
            var move = new TranslateTransform(0, 8); item.RenderTransform = move; item.Opacity = 0; var delay = TimeSpan.FromMilliseconds(40 + index++ * 32);
            var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)) { BeginTime = delay, FillBehavior = FillBehavior.HoldEnd };
            fade.Completed += (_, _) => { item.BeginAnimation(OpacityProperty, null); item.Opacity = 1; };
            item.BeginAnimation(OpacityProperty, fade);
            move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(420)) { BeginTime = delay, EasingFunction = ease, FillBehavior = FillBehavior.HoldEnd });
        }
    }

    private void Dismiss()
    {
        if (closing) return; closing = true;
        if (!GlassMotion.CanAnimate) { Finish(); return; }
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(140)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        fade.Completed += (_, _) => Finish();
        Root.BeginAnimation(OpacityProperty, fade);
        if (Panel.RenderTransform is ScaleTransform scale)
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(.96, TimeSpan.FromMilliseconds(140)));
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(.94, TimeSpan.FromMilliseconds(140)));
        }
    }
    private void Finish() { Close(); chosen?.Invoke(); }
    private void Choose(Action action) { chosen = action; Dismiss(); }
    private void Open_Click(object sender, RoutedEventArgs e) => Choose(restore);
    private void Connect_Click(object sender, RoutedEventArgs e) => Choose(toggle);
    private void Settings_Click(object sender, RoutedEventArgs e) => Choose(settings);
    private void Exit_Click(object sender, RoutedEventArgs e) => Choose(exit);
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Dismiss(); e.Handled = true; return; }
        if (e.Key is Key.Down or Key.Up) { MoveFocus(new TraversalRequest(e.Key == Key.Down ? FocusNavigationDirection.Next : FocusNavigationDirection.Previous)); e.Handled = true; return; }
        base.OnPreviewKeyDown(e);
    }
}
