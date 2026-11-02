using System.Windows;
using System.Windows.Threading;

internal static class Program
{
    private static bool appsListEnabled(RelayProxy.MainWindow window) => ((FrameworkElement)window.FindName("AppsList")).IsEnabled;
    private static T? Descendant<T>(DependencyObject node) where T : DependencyObject
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(node, i);
            if (child is T match) return match;
            if (Descendant<T>(child) is T nested) return nested;
        }
        return null;
    }
    [STAThread]
    private static int Main()
    {
        var app = new RelayProxy.App();
        RelayProxy.App.IsTestMode = true;
        app.InitializeComponent();
        bool closed = false;
        app.Startup += (_, _) => app.Dispatcher.BeginInvoke(async () =>
        {
          try {
            var window = (RelayProxy.MainWindow)app.MainWindow;
            window.Closed += (_, _) => closed = true;
            var taskbarIcon = RelayProxy.Desktop.TaskbarIdentity.ReadIconResource(window);
            // The icon follows the saved look: Relay by default, the sponge when the secret theme is on.
            if (!(taskbarIcon.EndsWith("relay-1.8.ico,0", StringComparison.OrdinalIgnoreCase) || taskbarIcon.EndsWith("gushin-2.0.ico,0", StringComparison.OrdinalIgnoreCase)) ||!System.IO.File.Exists(taskbarIcon[..^2])) throw new Exception("Taskbar did not receive the current icon resource");
            // The release notes must describe the version being built, newest first.
            if (RelayProxy.Desktop.ReleaseNotes.All[0].Version != RelayProxy.Desktop.ReleaseNotes.Version) throw new Exception($"Release notes ({RelayProxy.Desktop.ReleaseNotes.All[0].Version}) do not match the build version ({RelayProxy.Desktop.ReleaseNotes.Version})");
            if (window.FindName("AccessText") != null) throw new Exception("Windows badge was not removed");
            var hostInput = (System.Windows.Controls.TextBox)window.FindName("HostBox");
            var portInput = (System.Windows.Controls.TextBox)window.FindName("PortBox");
            var originalHost = hostInput.Text; var originalPort = portInput.Text;
            foreach (var sample in new[] { ("192.0.2.10:8080", "192.0.2.10", "8080"), (" proxy.example:3128 ", "proxy.example", "3128"), ("[2001:db8::1]:1080", "2001:db8::1", "1080") })
            {
                hostInput.SelectAll();
                var paste = new DataObjectPastingEventArgs(new DataObject(DataFormats.UnicodeText, sample.Item1), false, DataFormats.UnicodeText) { RoutedEvent = DataObject.PastingEvent };
                hostInput.RaiseEvent(paste);
                if (!paste.CommandCancelled || hostInput.Text != sample.Item2 || portInput.Text != sample.Item3) throw new Exception("Pasted endpoint was not split correctly");
            }
            foreach (var input in new[] { "2001:db8::1", "192.0.2.1:0", "host:65536", "host:abc", "https://host:443", "user:pass@host:8080" })
                if (RelayProxy.Core.ServerEndpoint.TrySplit(input, out _, out _)) throw new Exception("Ambiguous or invalid endpoint accepted");
            hostInput.Text = originalHost; portInput.Text = originalPort;
            var settingsNav = (System.Windows.Controls.RadioButton)window.FindName("PreferencesNav");
            var proxyNav = (System.Windows.Controls.RadioButton)window.FindName("ProxyNav");
            foreach (var name in new[] { "AutoStartBox", "AutoConnectBox", "StartHiddenBox", "MinimizeTrayBox", "CloseTrayBox", "ReduceMotionBox" })
            {
                var toggle = (System.Windows.Controls.CheckBox)window.FindName(name);
                var original = toggle.IsChecked;
                toggle.IsChecked = true; // Changed while the settings page is hidden.
                settingsNav.IsChecked = true; window.UpdateLayout();
                await Task.Delay(120);
                var knob = (System.Windows.Shapes.Ellipse)toggle.Template.FindName("Knob", toggle);
                var track = (System.Windows.Controls.Border)toggle.Template.FindName("Track", toggle);
                var travel = track.ActualWidth - track.BorderThickness.Left - track.BorderThickness.Right - track.Padding.Left - track.Padding.Right - knob.Width;
                if (knob.ActualHeight > track.ActualHeight - 2 * track.Padding.Top - 2 * track.BorderThickness.Top || Math.Abs(((System.Windows.Media.TranslateTransform)knob.RenderTransform).X - travel) > .1)
                    throw new Exception($"Toggle does not fit or did not synchronize on page entry {name} knobH={knob.ActualHeight} trackH={track.ActualHeight} trackW={track.ActualWidth} travel={travel} x={((System.Windows.Media.TranslateTransform)knob.RenderTransform).X} checked={toggle.IsChecked}");
                for (int i = 0; i < 6; i++)
                {
                    toggle.IsChecked = i % 2 == 0 ? false : true;
                    await Task.Delay(30);
                    var x = ((System.Windows.Media.TranslateTransform)knob.RenderTransform).X;
                    if (x < -.01 || x > travel + .01) throw new Exception("Rapid toggle reversal overshot track");
                }
                proxyNav.IsChecked = true; await Task.Delay(700); toggle.IsChecked = false;
                settingsNav.IsChecked = true; window.UpdateLayout();
                if (Math.Abs(((System.Windows.Media.TranslateTransform)knob.RenderTransform).X) > .1) throw new Exception("Page reopen replayed old toggle animation");
                RelayProxy.Desktop.GlassMotion.Enabled = false; toggle.IsChecked = true;
                if (Math.Abs(((System.Windows.Media.TranslateTransform)knob.RenderTransform).X - travel) > .1) throw new Exception("Reduced motion toggle did not settle instantly");
                toggle.IsChecked = original; RelayProxy.Desktop.GlassMotion.Enabled = true;
                proxyNav.IsChecked = true; await Task.Delay(700);
            }
            var page = (FrameworkElement)window.FindName("ProxyPage");
            RelayProxy.Desktop.GlassMotion.Enabled = true;
            RelayProxy.Desktop.GlassMotion.Present(page);
            await Task.Delay(650);
            if (page.Opacity < .99) throw new Exception("Page animation did not complete");
            RelayProxy.Desktop.GlassMotion.Enabled = false;
            RelayProxy.Desktop.GlassMotion.Present(page);
            if (page.Opacity != 1) throw new Exception("Reduced motion left hidden content");
            RelayProxy.Desktop.GlassMotion.Enabled = true;
            ((System.Windows.Controls.RadioButton)window.FindName("AppsNav")).IsChecked = true;
            var appsPage = (FrameworkElement)window.FindName("AppsPage");
            var connectedField = typeof(RelayProxy.MainWindow).GetField("connected", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            var update = typeof(RelayProxy.MainWindow).GetMethod("UpdateControls", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            connectedField.SetValue(window, true); update.Invoke(window, null);
            if (!appsPage.IsEnabled || !appsListEnabled(window) || window.CanEditApplications) throw new Exception("Connected list must remain browsable with editing locked");
            for (int i = 0; i < 20; i++) window.Rules.Add(new RelayProxy.Core.AppRule { Path = $"C:\\RelayTest\\Test-{i}.exe" });
            var appsList = (System.Windows.Controls.ListBox)window.FindName("AppsList");
            window.UpdateLayout(); appsList.ApplyTemplate();
            if (appsList.Template.FindName("ListSurface", appsList) is not System.Windows.Controls.Border surface || surface.Background is not System.Windows.Media.SolidColorBrush brush || brush.Color.A != 0)
                throw new Exception("Disabled application list lost its transparent surface");
            var scroll = Descendant<System.Windows.Controls.ScrollViewer>(appsList)!;
            if (scroll.ScrollableHeight <= 0) throw new Exception("Test list does not scroll");
            RelayProxy.Desktop.SmoothScroll.ScrollBy(scroll, 180);
            // Sample every few milliseconds: a smooth scroll passes through intermediate offsets, an instant jump does not.
            bool intermediate = false;
            for (int i = 0; i < 60 && scroll.VerticalOffset < 179; i++) { await Task.Delay(8); if (scroll.VerticalOffset > 0 && scroll.VerticalOffset < 179) intermediate = true; }
            if (RelayProxy.Desktop.GlassMotion.CanAnimate && !intermediate) throw new Exception("Wheel did not move through intermediate offsets");
            RelayProxy.Desktop.SmoothScroll.ScrollBy(scroll, 180);
            await Task.Delay(700);
            if (Math.Abs(scroll.VerticalOffset - 360) > 3) throw new Exception($"Wheel accumulation failed: {scroll.VerticalOffset}");
            RelayProxy.Desktop.SmoothScroll.ScrollBy(scroll, -100);
            await Task.Delay(700);
            if (Math.Abs(scroll.VerticalOffset - 260) > 3) throw new Exception("Reverse scrolling failed");
            RelayProxy.Desktop.GlassMotion.Enabled = false;
            RelayProxy.Desktop.SmoothScroll.ScrollBy(scroll, -10000);
            await Task.Delay(30);
            if (scroll.VerticalOffset > 1) throw new Exception("Reduced motion or bounds clamp failed");
            RelayProxy.Desktop.GlassMotion.Enabled = true;
            var connectedSearch = (System.Windows.Controls.TextBox)window.FindName("AppSearchBox");
            connectedSearch.Text = "chrome"; window.UpdateLayout();
            if (appsList.Items.Count == 0 || !connectedSearch.IsEnabled) throw new Exception("Connected search unavailable");
            connectedSearch.Clear(); window.UpdateLayout();
            await Task.Delay(200);
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(window);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using (var file = System.IO.File.Create("outputs/ui-disabled-list.png")) encoder.Save(file);
            await Task.Delay(550);
            RelayProxy.Desktop.GlassMotion.Enabled = false;
            RelayProxy.Desktop.GlassMotion.Enabled = true;
            connectedField.SetValue(window, false); update.Invoke(window, null);
            var icon = RelayProxy.Desktop.ApplicationIcon.Get(Environment.ProcessPath!);
            if (!icon.IsFrozen || icon.Width <= 0) throw new Exception("Executable icon is not usable across threads");
            if (RelayProxy.Desktop.ApplicationIcon.Get("Z:\\missing-app.exe") == null) throw new Exception("Missing icon has no fallback");
            _ = window.ShowPicker();
            var picker = window.AppPicker;
            var list = (System.Windows.Controls.ListBox)picker.FindName("ProcessList");
            for (int i = 0; i < 100 && list.ItemsSource == null; i++) await Task.Delay(100);
            if (list.Items.Count == 0) throw new Exception("Running process enumeration failed");
            if (list.Items.Cast<RelayProxy.Desktop.RunningAppsPanel.RunningApp>().Any(a => !a.Icon.IsFrozen)) throw new Exception("Process icons are not frozen");
            list.SelectedIndex = 0;
            var add = (System.Windows.Controls.Button)picker.FindName("AddButton");
            if (!add.IsEnabled) throw new Exception("Process selection did not enable Add");
            var search = (System.Windows.Controls.TextBox)picker.FindName("Search");
            search.Text = "__no_such_relay_application__";
            if (list.Items.Count != 0 || add.IsEnabled) throw new Exception("Filter retained a hidden selection");
            search.Clear();
            if (list.Items.Count == 0) throw new Exception("Filter reset failed");
            picker.Cancel(); await Task.Delay(500);
            if (((FrameworkElement)window.FindName("PickerLayer")).Visibility == Visibility.Visible) throw new Exception("Picker sheet did not close");
            var before = System.Diagnostics.Process.GetCurrentProcess().WorkingSet64 / 1048576;
            window.SendToTray();
            await Task.Delay(3500);
            System.Diagnostics.Process.GetCurrentProcess().Refresh();
            Console.WriteLine($"MEM visible={before} MB, in tray={System.Diagnostics.Process.GetCurrentProcess().WorkingSet64 / 1048576} MB");
            if (!window.IsInTray || window.ShowInTaskbar) throw new Exception("Tray did not hide window");
            window.RestoreFromTray();
            if (!window.IsVisible || !window.ShowInTaskbar) throw new Exception("Tray restore failed");
            window.Preferences.CloseToTray = true;
            window.Close();
            if (!window.IsInTray || closed) throw new Exception("Close-to-tray terminated the application");
            window.RestoreFromTray();
            window.Preferences.MinimizeToTray = true;
            window.WindowState = WindowState.Minimized;
            if (!window.IsInTray) throw new Exception("Minimize-to-tray failed");
            window.RestoreFromTray();
            ((System.Windows.Controls.RadioButton)window.FindName("ProxyNav")).IsChecked = true;
            var visual = typeof(RelayProxy.MainWindow).GetMethod("SetStatusVisual", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            foreach (var state in new[] { "busy", "live", "error", "idle" })
            {
                visual.Invoke(window, new object[] { state });
                await Task.Delay(state == "live" ? 1500 : 800);
                var shot = new System.Windows.Media.Imaging.RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                shot.Render(window);
                var png = new System.Windows.Media.Imaging.PngBitmapEncoder(); png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(shot));
                using var output = System.IO.File.Create($"outputs/ui-state-{state}.png"); png.Save(output);
            }
            window.ExitApplication();
          } catch (Exception ex) { Console.WriteLine("FAIL " + ex.Message); app.Shutdown(1); }
        }, DispatcherPriority.ApplicationIdle);
        app.DispatcherUnhandledException += (_, e) => { Console.WriteLine("FAIL " + e.Exception.Message); e.Handled = true; app.Shutdown(1); };
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(120) };
        timer.Tick += (_, _) => { timer.Stop(); app.Shutdown(1); };
        timer.Start();
        var code = app.Run();
        timer.Stop();
        Console.WriteLine(closed && code == 0 ? "PASS connected list browsing/search, editing lock, smooth wheel accumulation/reversal/bounds, reduced motion, icons, process picker, tray" : "FAIL WPF lifecycle");
        return closed && code == 0 ? 0 : 1;
    }
}
