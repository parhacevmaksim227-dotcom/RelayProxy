using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using RelayProxy.Core;
using RelayProxy.Desktop;

namespace RelayProxy;

public partial class MainWindow : Window
{
    public static readonly DependencyProperty CanEditApplicationsProperty = DependencyProperty.Register(nameof(CanEditApplications), typeof(bool), typeof(MainWindow), new PropertyMetadata(true));
    public static readonly DependencyProperty CanEditSitesProperty = DependencyProperty.Register(nameof(CanEditSites), typeof(bool), typeof(MainWindow), new PropertyMetadata(true));
    public bool CanEditSites { get => (bool)GetValue(CanEditSitesProperty); private set => SetValue(CanEditSitesProperty, value); }
    private readonly System.Windows.Threading.DispatcherTimer siteTimer = new() { Interval = TimeSpan.FromMilliseconds(900) };
    public bool CanEditApplications { get => (bool)GetValue(CanEditApplicationsProperty); private set => SetValue(CanEditApplicationsProperty, value); }
    public ObservableCollection<AppRule> Rules { get; } = [];
    public ObservableCollection<string> Sites { get; } = [];
    public ObservableCollection<SiteSuggestion> Suggestions { get; } = [];
    public sealed class SiteSuggestion : System.ComponentModel.INotifyPropertyChanged
    {
        private ImageSource? icon;
        public SiteSuggestion(string domain, string reason) { Domain = domain; Reason = reason; }
        public string Domain { get; }
        public string Reason { get; }
        public ImageSource? Icon { get => icon; set { icon = value; PropertyChanged?.Invoke(this, new(nameof(Icon))); } }
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }
    private readonly System.Windows.Threading.DispatcherTimer suggestTimer = new() { Interval = TimeSpan.FromMilliseconds(550) };
    private CancellationTokenSource? suggestCancel;
    private string suggestedFor = "";
    private readonly EngineRunner engine = new();
    private readonly CancellationTokenSource lifetime = new();
    private bool busy, connected, closing, canClose;
    private readonly Queue<string> logs = new();
    private string secret = "";
    private Task? operation;
    private FrameworkElement? shownPage;
    private bool chromeMode, failed;
    private string visualState = "idle";
    private TrayController? tray;
    private bool exitRequested;
    private bool savingPreferences;
    public DesktopPreferences Preferences { get; private set; } = new();
    public bool IsInTray => !IsVisible && tray != null;

    public MainWindow(bool autoConnect, bool autoChrome = false, bool startup = false, bool manualLaunch = false)
    {
        InitializeComponent(); DataContext = this;
        DiagnosticLog.Write($"Relay {ReleaseNotes.Version} starting; administrator=" + EngineRunner.IsAdministrator);
        BrandVersion.Text = "Relay " + ReleaseNotes.Short;
        DiagnosticLog.Write($"{Platform.Name} (build {Platform.Build}); Windows {Environment.OSVersion.Version}; render tier {System.Windows.Media.RenderCapability.Tier >> 16}; shader {(RefractionEffect.Supported ? "on" : "off")}; DriverInstalled={ProcessRouter.DriverInstalled}");
        engine.Log += AddLog;
        engine.UnexpectedExit += () => Dispatcher.BeginInvoke(() =>
        {
            if (closing) return;
            connected = false; failed = true; UpdateControls();
            StatusText.Text = "Ядро остановилось";
            StatusDetail.Text = "Маршрутизация выключена. Смотрите журнал; прямое подключение восстановлено.";
        });
        try
        {
            var profile = ProfileStore.Load(); FillProxy(profile);
            DnsBox.IsChecked = profile.ProxyDns; UdpBox.IsChecked = profile.EnableUdp;
            foreach (var rule in profile.Apps) Rules.Add(rule);
            foreach (var site in profile.Sites) if (Profile.NormalizeSite(site) is { } clean && !Sites.Contains(clean)) Sites.Add(clean);
            SitesOnlyBox.IsChecked = profile.SitesOnly;
        }
        catch { AddLog("Не удалось прочитать сохранённый профиль или расшифровать пароль. Введите настройки заново."); }
        try { Preferences = DesktopPreferences.Load(); }
        catch { AddLog("Настройки окна не прочитаны; применены значения по умолчанию."); }
        MinimizeTrayBox.IsChecked = Preferences.MinimizeToTray; CloseTrayBox.IsChecked = Preferences.CloseToTray;
        StartHiddenBox.IsChecked = Preferences.StartHidden; AutoConnectBox.IsChecked = Preferences.ConnectAtStartup;
        ReduceMotionBox.IsChecked = Preferences.ReduceMotion; AutoElevateBox.IsChecked = Preferences.AutoElevate;
        GlassMotion.Enabled = !Preferences.ReduceMotion;
        UpdateWhatsNewLink();
        try { AutoStartBox.IsChecked = StartupService.Read().Enabled; }
        catch { PreferencesResult.Text = "Не удалось прочитать состояние автозапуска Windows."; }
        tray = new TrayController(
            () => Dispatcher.BeginInvoke(RestoreFromTray),
            () => Dispatcher.BeginInvoke(() => BeginOperation(ConnectAsync)),
            () => Dispatcher.BeginInvoke(() => { RestoreFromTray(); PreferencesNav.IsChecked = true; }),
            () => Dispatcher.BeginInvoke(ExitApplication));
        Closed += (_, _) => tray.Dispose();
        SourceInitialized += (_, _) =>
        {
            try { TaskbarIdentity.Apply(this, Preferences.Sponge); } catch (Exception ex) { DiagnosticLog.Write("Taskbar identity: " + ex.Message); }
            var handle = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(handle)?.AddHook(WindowHook);
            // The window is transparent so its corners can be round; the dark colour lives on RootGrid.
            if (HwndSource.FromHwnd(handle)?.CompositionTarget is { } target) target.BackgroundColor = Colors.Transparent;
            // The system minimize/restore animation draws the window as a square frame, which pokes out
            // past the rounded corners; it is switched off and replaced by the fade in StateChanged.
            int dark = 1, off = 1;
            _ = DwmSetWindowAttribute(handle, 3, ref off, sizeof(int));
            // Dark mode attribute: 20 on Windows 10 2004+ and 11, 19 on the earlier 1809/1903 builds.
            if (Platform.Build >= 17763) _ = DwmSetWindowAttribute(handle, Platform.Build >= 19041 ? 20 : 19, ref dark, sizeof(int));
            // Caption and border colours only exist on Windows 11; Windows 10 would ignore them anyway.
            if (Platform.IsWindows11)
            {
                int caption = 0x2B180E, border = 0x2B180E;
                _ = DwmSetWindowAttribute(handle, 35, ref caption, sizeof(int));
                _ = DwmSetWindowAttribute(handle, 34, ref border, sizeof(int));
            }
            // Without hardware shaders the page would take seconds to paint, so keep it calm.
            if (System.Windows.Media.RenderCapability.Tier >> 16 == 0) { GlassMotion.Enabled = false; DiagnosticLog.Write("Software rendering detected; animations reduced."); }
        };
        UpdateRules(); UpdateProtocol(); UpdateSites();
        UpdateControls(); ShowPage("proxy");
        if (Preferences.Sponge) SpongeTheme.Apply(this, true, false);
        // The light sweep follows the card's rounded outline; a plain rectangle would show square corners.
        StatusSweep.SizeChanged += (_, e) => StatusSweep.Clip = new RectangleGeometry(new Rect(e.NewSize), 27, 27);
        StatusBubbles.SizeChanged += (_, e) => StatusBubbles.Clip = new RectangleGeometry(new Rect(e.NewSize), 27, 27);
        StatusTrack.SizeChanged += (_, e) => StatusTrack.Clip = new RectangleGeometry(new Rect(e.NewSize), 2, 2);
        IsVisibleChanged += (_, _) => UpdateScene();
        Activated += (_, _) => UpdateScene(); Deactivated += (_, _) => UpdateScene();
        Closed += (_, _) => Scene.Run(false);
        Loaded += (_, _) => Reveal();
        foreach (var block in new[] { StatusText, StatusDetail })
            System.ComponentModel.DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock)).AddValueChanged(block, (s, _) => GlassMotion.Nudge((FrameworkElement)s!));
        AddLog("Готово. Прокси выключен; сетевые настройки ещё не изменялись.");
        if (autoConnect) Loaded += (_, _) => BeginOperation(ConnectAsync);
        else if (autoChrome) Loaded += (_, _) => BeginOperation(StartChromeAsync);
        // Connect on every launch when the user asked for it, but only if the profile is actually filled in.
        else if ((startup || manualLaunch) && Preferences.ConnectAtStartup && ProfileReady()) Loaded += (_, _) => BeginOperation(StartupConnectAsync);
        siteTimer.Tick += (_, _) => { siteTimer.Stop(); if (busy) { siteTimer.Start(); return; } if (connected && !chromeMode) BeginOperation(ReapplyAsync); };
        Sites.CollectionChanged += (_, _) => SitesChanged();
        suggestTimer.Tick += (_, _) => { suggestTimer.Stop(); _ = LoadSuggestionsAsync(); };
        SitesOnlyBox.Checked += (_, _) => SitesChanged(); SitesOnlyBox.Unchecked += (_, _) => SitesChanged();
    }

    private Profile ReadProfile(bool requireApps)
    {
        NormalizeEndpoint();
        if (!int.TryParse(PortBox.Text.Trim(), out var port)) throw new ArgumentException("Укажите числовой порт от 1 до 65535.");
        var p = new Profile { Protocol = ((ComboBoxItem)ProtocolBox.SelectedItem).Content.ToString()!, Host = HostBox.Text.Trim(), Port = port,
            Username = UsernameBox.Text, Password = PasswordInput.Password, TlsServerName = TlsNameBox.Text.Trim(), ProxyDns = DnsBox.IsChecked == true,
            EnableUdp = UdpBox.IsChecked == true, Apps = Rules.ToList(), Sites = Sites.ToList(), SitesOnly = SitesOnlyBox.IsChecked == true };
        if (p.Protocol == "SOCKS4") p.Password = "";
        secret = p.Password;
        p.Validate(requireApps); return p;
    }
    private bool NormalizeEndpoint(string? text = null)
    {
        if (HostBox == null || PortBox == null || !ServerEndpoint.TrySplit(text ?? HostBox.Text, out var host, out var port)) return false;
        HostBox.Text = host; PortBox.Text = port.ToString(); HostBox.CaretIndex = host.Length;
        return true;
    }
    private void Host_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (!e.DataObject.GetDataPresent(DataFormats.UnicodeText)) return;
        if (e.DataObject.GetData(DataFormats.UnicodeText) is not string text) return;
        var candidate = HostBox.Text.Remove(HostBox.SelectionStart, HostBox.SelectionLength).Insert(HostBox.SelectionStart, text);
        if (NormalizeEndpoint(candidate)) e.CancelCommand();
    }
    private void Host_LostFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e) => NormalizeEndpoint();
    private void FillProxy(Profile p)
    {
        ProtocolBox.SelectedIndex = Array.IndexOf(new[] { "HTTP", "HTTPS", "SOCKS4", "SOCKS5" }, p.Protocol);
        if (ProtocolBox.SelectedIndex < 0) ProtocolBox.SelectedIndex = 3;
        HostBox.Text = p.Host; PortBox.Text = p.Port.ToString(); UsernameBox.Text = p.Username; PasswordInput.Password = p.Password; TlsNameBox.Text = p.TlsServerName;
    }
    private void UpdateProtocol()
    {
        if (ProtocolHint == null) return;
        var type = ((ComboBoxItem)ProtocolBox.SelectedItem)?.Content.ToString();
        PasswordInput.IsEnabled = type != "SOCKS4"; UdpBox.IsEnabled = type == "SOCKS5";
        TlsPanel.Visibility = type == "HTTPS" ? Visibility.Visible : Visibility.Collapsed;
        ProtocolHint.Text = type switch {
            "HTTP" => "HTTP CONNECT: TCP через IPv4, включая HTTPS-сайты. UDP блокируется; IPv4 используется для совместимости с прокси.",
            "HTTPS" => "HTTP CONNECT внутри TLS к прокси. Сертификат проверяется. TCP через IPv4; UDP блокируется.",
            "SOCKS4" => "SOCKS4: TCP и IPv4. Логин — User ID; пароль не поддерживается. UDP и IPv6 не передаются.",
            _ => "SOCKS5: TCP и, если сервер поддерживает, UDP. Авторизация по логину и паролю." };
        if (ProtocolPill != null) ProtocolPill.Text = type ?? "Прокси";
    }
    private void Protocol_Changed(object sender, SelectionChangedEventArgs e) => UpdateProtocol();
    private void Paste_Click(object sender, RoutedEventArgs e)
    {
        try { var text = Clipboard.GetText(); if (!NormalizeEndpoint(text)) FillProxy(Profile.ParseUri(text)); AddLog("Прокси вставлен из буфера обмена."); }
        catch (Exception ex) { ShowError(ex); }
    }
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try { ProfileStore.Save(ReadProfile(false)); StatusText.Text = "Настройки сохранены"; StatusDetail.Text = "Добавьте приложения и включите прокси, когда будете готовы."; AddLog("Настройки сохранены. Пароль зашифрован для текущего пользователя Windows."); }
        catch (Exception ex) { ShowError(ex); }
    }
    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Программы Windows (*.exe)|*.exe", Multiselect = true, Title = "Выберите приложения для прокси" };
        if (dialog.ShowDialog(this) == true) foreach (var path in dialog.FileNames) AddRule(path);
    }
    private void AddRule(string path)
    {
        if (string.Equals(path, EngineRunner.EnginePath, StringComparison.OrdinalIgnoreCase) || string.Equals(path, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) return;
        if (!Rules.Any(a => a.Path.Equals(path, StringComparison.OrdinalIgnoreCase))) Rules.Add(new() { Path = path });
        UpdateRules();
    }
    private void Running_Click(object sender, RoutedEventArgs e)
    {
        _ = ShowPicker();
    }

    public RunningAppsPanel AppPicker => Picker;
    private TaskCompletionSource<string[]?>? pickerResult;
    // Opens the process picker as a sheet over the window and adds whatever the user selects.
    public async Task ShowPicker()
    {
        if (PickerLayer.Visibility == Visibility.Visible) return;
        pickerResult = new TaskCompletionSource<string[]?>();
        void Done(string[]? paths) => pickerResult?.TrySetResult(paths);
        Picker.Finished += Done;
        PickerLayer.Visibility = Visibility.Visible;
        if (GlassMotion.CanAnimate)
        {
            PickerScrim.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)));
            var scale = new ScaleTransform(.94, .94); PickerCard.RenderTransform = scale;
            var ease = new System.Windows.Media.Animation.QuinticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new System.Windows.Media.Animation.DoubleAnimation(.94, 1, TimeSpan.FromMilliseconds(520)) { EasingFunction = ease });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new System.Windows.Media.Animation.DoubleAnimation(.94, 1, TimeSpan.FromMilliseconds(520)) { EasingFunction = ease });
            PickerCard.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300)));
        }
        _ = Picker.LoadAsync();
        var chosen = await pickerResult.Task;
        Picker.Finished -= Done;
        if (GlassMotion.CanAnimate)
        {
            var fade = new System.Windows.Media.Animation.DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(180));
            var gone = new TaskCompletionSource();
            fade.Completed += (_, _) => gone.TrySetResult();
            PickerScrim.BeginAnimation(OpacityProperty, null); PickerCard.BeginAnimation(OpacityProperty, null);
            PickerScrim.Opacity = 0; PickerCard.Opacity = 0;
            PickerCard.BeginAnimation(OpacityProperty, fade);
            await gone.Task;
            PickerScrim.Opacity = 1; PickerCard.Opacity = 1; PickerCard.RenderTransform = Transform.Identity;
        }
        PickerLayer.Visibility = Visibility.Collapsed;
        pickerResult = null;
        if (chosen != null) foreach (var path in chosen) AddRule(path);
    }
    private void PickerScrim_Down(object sender, System.Windows.Input.MouseButtonEventArgs e) => Picker.Cancel();

    // "Что нового?": the release notes open as a sheet inside the window, like the process picker.
    private void UpdateWhatsNewLink()
    {
        bool unseen = Preferences.LastSeenVersion != ReleaseNotes.Version;
        WhatsNewLink.Text = unseen ? "● что нового?" : "· что нового?";
        WhatsNewLink.FontWeight = unseen ? FontWeights.SemiBold : FontWeights.Medium;
        WhatsNewLink.Foreground = new SolidColorBrush(unseen ? Colors.White : Color.FromRgb(196, 196, 196));
    }
    private void WhatsNew_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => ShowNotes();
    private void NotesClose_Click(object sender, RoutedEventArgs e) => HideNotes();
    private void NotesScrim_Down(object sender, System.Windows.Input.MouseButtonEventArgs e) => HideNotes();
    private void ShowNotes()
    {
        if (NotesLayer.Visibility == Visibility.Visible) return;
        NotesList.Children.Clear();
        foreach (var release in ReleaseNotes.All)
        {
            var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, NotesList.Children.Count == 0 ? 0 : 26, 0, 10) };
            header.Children.Add(new TextBlock { Text = "Версия " + release.Version, FontSize = 17, FontWeight = FontWeights.SemiBold });
            header.Children.Add(new TextBlock { Text = release.Date, FontSize = 12, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Bottom, Foreground = (Brush)FindResource("Muted") });
            NotesList.Children.Add(header);
            foreach (var change in release.Changes)
            {
                var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) }); row.ColumnDefinitions.Add(new ColumnDefinition());
                row.Children.Add(new TextBlock { Text = "•", Foreground = (Brush)FindResource("Muted") });
                var text = new TextBlock { Text = change, FontSize = 13.5, LineHeight = 19, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(232, 232, 232)) };
                Grid.SetColumn(text, 1); row.Children.Add(text);
                NotesList.Children.Add(row);
            }
        }
        Preferences.LastSeenVersion = ReleaseNotes.Version;
        try { Preferences.Save(); } catch (Exception ex) { AddLog("Не удалось запомнить прочитанную версию: " + ex.Message); }
        UpdateWhatsNewLink();
        NotesLayer.Visibility = Visibility.Visible;
        if (!GlassMotion.CanAnimate) return;
        NotesScrim.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)));
        var scale = new ScaleTransform(.94, .94); NotesCard.RenderTransform = scale;
        var ease = new System.Windows.Media.Animation.QuinticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new System.Windows.Media.Animation.DoubleAnimation(.94, 1, TimeSpan.FromMilliseconds(520)) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new System.Windows.Media.Animation.DoubleAnimation(.94, 1, TimeSpan.FromMilliseconds(520)) { EasingFunction = ease });
        NotesCard.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300)));
    }
    private void HideNotes()
    {
        if (NotesLayer.Visibility != Visibility.Visible) return;
        if (!GlassMotion.CanAnimate) { NotesLayer.Visibility = Visibility.Collapsed; return; }
        var fade = new System.Windows.Media.Animation.DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(180));
        fade.Completed += (_, _) =>
        {
            NotesLayer.Visibility = Visibility.Collapsed;
            NotesScrim.Opacity = 1; NotesCard.Opacity = 1; NotesCard.RenderTransform = Transform.Identity;
        };
        NotesScrim.BeginAnimation(OpacityProperty, null); NotesCard.BeginAnimation(OpacityProperty, null);
        NotesScrim.Opacity = 0; NotesCard.Opacity = 0;
        NotesCard.BeginAnimation(OpacityProperty, fade);
    }
    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape && NotesLayer.Visibility == Visibility.Visible) { HideNotes(); e.Handled = true; return; }
        if (e.Key == System.Windows.Input.Key.Escape && PickerLayer.Visibility == Visibility.Visible) { Picker.Cancel(); e.Handled = true; return; }
        base.OnPreviewKeyDown(e);
    }
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // Adds one or several domains, separated by spaces, commas or lines.
    private void SiteAdd_Click(object sender, RoutedEventArgs e) => AddSites(SiteBox.Text);
    private void SiteBox_TextChanged(object sender, TextChangedEventArgs e) { if (IsLoaded) { suggestTimer.Stop(); suggestTimer.Start(); } }
    // While a domain is being typed, offer its relatives found online: same name in other zones and hosts the site depends on.
    private async Task LoadSuggestionsAsync()
    {
        var token = SiteBox.Text.Split([' ', ',', ';', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
        var domain = Profile.NormalizeSite(token);
        if (domain == null) { suggestCancel?.Cancel(); suggestedFor = ""; ShowSuggestions(false); return; }
        if (domain == suggestedFor) return;
        suggestCancel?.Cancel(); var cancel = suggestCancel = new CancellationTokenSource(); suggestedFor = domain;
        SuggestTitle.Text = "Ищем похожие домены…"; ShowSuggestions(true);
        try
        {
            var found = await SiteSuggester.RelatedAsync(domain, Sites.ToHashSet(StringComparer.OrdinalIgnoreCase), cancel.Token);
            if (cancel.IsCancellationRequested) return;
            Suggestions.Clear();
            foreach (var item in found) Suggestions.Add(new SiteSuggestion(item.Domain, item.Reason));
            SuggestTitle.Text = found.Count > 0 ? $"Похожие на {domain}: нажмите, чтобы добавить" : $"Для {domain} похожих доменов не нашлось";
            foreach (var suggestion in Suggestions.ToArray()) _ = LoadIconAsync(suggestion, cancel.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { DiagnosticLog.Write("Site suggestions: " + ex.Message); if (!cancel.IsCancellationRequested) ShowSuggestions(false); }
    }
    private async Task LoadIconAsync(SiteSuggestion suggestion, CancellationToken token)
    {
        try
        {
            var bytes = await SiteSuggester.IconAsync(suggestion.Domain, token);
            if (bytes == null || token.IsCancellationRequested) return;
            var image = new System.Windows.Media.Imaging.BitmapImage();
            using (var stream = new System.IO.MemoryStream(bytes)) { image.BeginInit(); image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit(); }
            image.Freeze(); suggestion.Icon = image;
        }
        catch { /* no icon: the globe placeholder stays */ }
    }
    private void ShowSuggestions(bool visible)
    {
        SuggestPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible) Suggestions.Clear();
    }
    private void SuggestionAdd_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not string site) return;
        if (!Sites.Contains(site)) Sites.Add(site);
        if (Suggestions.FirstOrDefault(s => s.Domain == site) is { } done) Suggestions.Remove(done);
        UpdateSites();
    }
    private void SiteBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e) { if (e.Key == System.Windows.Input.Key.Enter) { AddSites(SiteBox.Text); e.Handled = true; } }
    private void AddSites(string text)
    {
        var rejected = new List<string>();
        foreach (var part in text.Split([' ', ',', ';', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (Profile.NormalizeSite(part) is { } site) { if (!Sites.Contains(site)) Sites.Add(site); }
            else rejected.Add(part);
        }
        SiteBox.Text = string.Join(" ", rejected);
        SiteHint.Text = rejected.Count > 0 ? "Не похоже на домен: " + string.Join(", ", rejected) + ". Пример: example.com" : "";
        UpdateSites(); SiteBox.Focus();
    }
    private void SiteRemove_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is string site) Sites.Remove(site);
        UpdateSites();
    }
    private void UpdateSites()
    {
        SitesEmpty.Visibility = Sites.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SitesCount.Text = Sites.Count == 0 ? "Список пуст" : $"Сайтов: {Sites.Count}";
    }
    private async Task ApplyElevationAsync(bool enabled)
    {
        var ready = ElevationService.IsReady();
        if (enabled == ready || (enabled && !EngineRunner.IsAdministrator)) return;
        if (EngineRunner.IsAdministrator) { ElevationService.Configure(enabled); return; }
        // Removing the launcher needs administrator rights once.
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        info.ArgumentList.Add("--set-elevation=off");
        using var helper = Process.Start(info) ?? throw new InvalidOperationException("Не удалось изменить запуск с правами администратора.");
        await helper.WaitForExitAsync();
    }
    private void Remove_Click(object sender, RoutedEventArgs e) { foreach (var rule in AppsList.SelectedItems.Cast<AppRule>().ToArray()) Rules.Remove(rule); UpdateRules(); }
    private void Rule_Changed(object sender, RoutedEventArgs e) => Dispatcher.BeginInvoke(UpdateRules);
    private void UpdateRules()
    {
        CountText.Text = $"{Rules.Count(a => a.Enabled)} выбрано";
        SelectedPill.Text = $"Приложений: {Rules.Count(a => a.Enabled)}";
        var view = CollectionViewSource.GetDefaultView(Rules);
        EmptyState.Visibility = view.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
        EmptyTitle.Text = Rules.Count == 0 ? "Добавьте ваши приложения" : "Ничего не найдено";
    }
    private void AppSearch_Changed(object sender, TextChangedEventArgs e)
    {
        if (AppsList == null) return;
        var search = AppSearchBox.Text.Trim();
        CollectionViewSource.GetDefaultView(Rules).Filter = value => value is AppRule rule && rule.Path.Contains(search, StringComparison.OrdinalIgnoreCase);
        UpdateRules();
    }

    private void Connect_Click(object sender, RoutedEventArgs e) => BeginOperation(ConnectAsync);
    private void Test_Click(object sender, RoutedEventArgs e) => BeginOperation(TestAsync);
    private void BeginOperation(Func<Task> action)
    {
        if (busy || closing) return;
        failed = false;
        operation = PerformAsync(action);
    }
    private async Task PerformAsync(Func<Task> action)
    {
        busy = true; UpdateControls();
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!closing) ShowError(ex); }
        finally { busy = false; if (!closing) UpdateControls(); }
    }
    private async Task ConnectAsync()
    {
        if (connected && !chromeMode)
        {
            StatusText.Text = "Отключение…"; await engine.StopAsync(); connected = false; chromeMode = false;
            StatusText.Text = "Прокси выключен"; StatusDetail.Text = "Обычное подключение восстановлено. Настройки сохранены."; AddLog("Маршрутизация остановлена."); return;
        }
        var profile = ReadProfile(true); ProfileStore.Save(profile);
        StatusText.Text = "Проверяем сервер перед подключением…";
        await EngineRunner.ProbeAsync(profile, lifetime.Token, AddLog);
        if (!EngineRunner.IsAdministrator)
        {
            StatusText.Text = "Требуются права администратора";
            StatusDetail.Text = "Подтвердите запрос Windows для перехвата выбранных процессов.";
            // After the first approval Relay starts elevated through its scheduled task, without another prompt.
            if (ElevationService.IsReady() && ElevationService.TryLaunch(new[] { "--connect", "--handoff" }))
            {
                await engine.StopAsync(); connected = false; canClose = true; Close(); return;
            }
            var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", WorkingDirectory = AppContext.BaseDirectory };
            psi.ArgumentList.Add("--connect");
            psi.ArgumentList.Add("--handoff");
            try
            {
                Process.Start(psi);
                // Release the local Chrome listener before the elevated instance starts per-process redirection.
                await engine.StopAsync(); connected = false;
                canClose = true; Close();
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { StatusText.Text = "Запуск отменён"; StatusDetail.Text = "Прокси выключен. Для перенаправления трафика нужны права администратора."; }
            return;
        }
        StatusText.Text = "Включение маршрутизации…";
        chromeMode = false;
        connected = false;
        await engine.StartApplicationsAsync(profile, lifetime.Token);
        connected = true; StatusText.Text = "Маршрутизация включена";
        // Connections opened before the interceptor started stay direct, so name the programs that need a restart.
        var running = RunningSelected(profile);
        StatusDetail.Text = running.Length > 0
            ? "Перезапустите программы, запущенные до включения, иначе они останутся без прокси: " + string.Join(", ", running) + "."
            : $"Выбранные программы ({profile.Apps.Count(a => a.Enabled)}) пойдут через прокси. Запускайте их сейчас.";
        AddLog("Через прокси направлены только выбранные EXE и их помощники. Общий TUN и системный DNS не изменяются.");
    }
    private static string[] RunningSelected(Profile profile)
    {
        var selected = profile.Apps.Where(a => a.Enabled).Select(a => System.IO.Path.GetFileNameWithoutExtension(a.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            using (process) { if (selected.Contains(process.ProcessName)) names.Add(process.ProcessName); }
        }
        return names.ToArray();
    }
    private bool ProfileReady() => !string.IsNullOrWhiteSpace(HostBox.Text) && (Rules.Any(r => r.Enabled) || SitesOnlyBox.IsChecked == true && Sites.Count > 0);
    // A changed site list takes effect by itself: the running connection is restarted once, shortly after the last edit.
    private void SitesChanged()
    {
        UpdateSites();
        if (IsLoaded && connected && !chromeMode) { siteTimer.Stop(); siteTimer.Start(); }
    }
    private async Task ReapplyAsync()
    {
        StatusText.Text = "Применяем список сайтов…";
        await engine.StopAsync(); connected = false; chromeMode = false;
        await ConnectAsync();
    }
    // Best of a few TCP handshakes with the proxy server: the round-trip time every new connection pays several times over.
    private static async Task<int?> MeasureLatencyAsync(string host, int port)
    {
        int? best = null;
        for (int i = 0; i < 4; i++)
        {
            try
            {
                using var client = new System.Net.Sockets.TcpClient(); var watch = Stopwatch.StartNew();
                using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await client.ConnectAsync(host, port, cancel.Token);
                var elapsed = (int)watch.ElapsedMilliseconds; best = best is int b ? Math.Min(b, elapsed) : elapsed;
            }
            catch { }
        }
        return best;
    }
    private async Task TestAsync()
    {
        var profile = ReadProfile(false); StatusText.Text = "Проверяем прокси…";
        StatusDetail.Text = "HTTPS-запрос к Cloudflare через выбранный сервер. Маршруты Windows не меняются.";
        var result = await EngineRunner.ProbeAsync(profile, lifetime.Token, AddLog);
        var rtt = await MeasureLatencyAsync(profile.Host, profile.Port);
        var latency = rtt is int ms ? $" Задержка до прокси: {ms} мс." + (ms > 150 ? " Это главный источник пинга: чем дальше прокси, тем он выше. Выберите сервер ближе к вам или к нужному сервису." : "") : "";
        StatusText.Text = "Проверка пройдена"; StatusDetail.Text = result + latency; AddLog(result + latency);
    }
    private void Chrome_Click(object sender, RoutedEventArgs e) => BeginOperation(StartChromeAsync);
    private async Task StartChromeAsync()
    {
        if (ChromeLauncher.Find() == null) throw new InvalidOperationException("Google Chrome не найден.");
        var profile = ReadProfile(false);
        ProfileStore.Save(profile);
        StatusText.Text = "Проверяем прокси для Chrome…";
        var result = await EngineRunner.ProbeAsync(profile, lifetime.Token, AddLog);
        AddLog(result);
        try
        {
            await engine.StartAsync(ConfigBuilder.Build(profile, EngineRunner.EnginePath, Environment.ProcessPath!, ChromeLauncher.Port), profile.Password, lifetime.Token);
            ChromeLauncher.Open();
            connected = true; chromeMode = true;
            StatusText.Text = "Работает отдельное окно Chrome";
            StatusDetail.Text = "Режим Chrome. Чтобы подключить Telegram и другие программы из списка, нажмите «Включить для приложений».";
        }
        catch { await engine.StopAsync(); throw; }
    }
    private void UpdateControls()
    {
        ProxyPage.IsEnabled = !busy && !connected;
        AppsPage.IsEnabled = true;
        CanEditApplications = !busy && !connected; CanEditSites = !busy;
        ConnectButton.IsEnabled = !busy;
        ConnectButton.Content = busy ? "Подождите…" : connected && !chromeMode ? "Отключить приложения" : "Включить для приложений";
        StopButton.Visibility = connected && chromeMode ? Visibility.Visible : Visibility.Collapsed;
        StatePill.Text = busy ? "Подключение…" : connected ? chromeMode ? "Только Chrome" : "Подключено" : failed ? "Ошибка" : "Выключено";
        GlassMotion.FadeColor(StatePillBorder, failed && !connected && !busy ? Color.FromArgb(77, 143, 143, 143) : connected ? Color.FromArgb(77, 159, 159, 159) : busy ? Color.FromArgb(77, 153, 153, 153) : Color.FromArgb(31, 255, 255, 255));
        StatePill.Foreground = new SolidColorBrush(failed && !connected && !busy ? Color.FromRgb(201, 201, 201) : connected ? Color.FromRgb(215, 215, 215) : Color.FromRgb(213, 213, 213));
        SitesNotice.Visibility = connected && !chromeMode ? Visibility.Collapsed : Visibility.Visible;
        SetStatusVisual(busy ? "busy" : connected ? "live" : failed ? "error" : "idle");
        tray?.Update(connected, chromeMode, busy);
    }
    // Connection feedback: a coloured aura around the card, a light sweep on success, a shake on error.
    private void SetStatusVisual(string state)
    {
        if (state == visualState) return;
        var previous = visualState; visualState = state;
        StatusIcon.SetState(state);
        var colour = Scene.Sponge
            ? state switch { "live" => Color.FromRgb(255, 214, 48), "error" => Color.FromRgb(255, 96, 110), "busy" => Color.FromRgb(96, 220, 255), _ => Colors.White }
            : state switch { "live" => Color.FromRgb(159, 159, 159), "error" => Color.FromRgb(143, 143, 143), "busy" => Color.FromRgb(153, 153, 153), _ => Colors.White };
        byte rest = state switch { "live" => 0x70, "error" => 0xB0, "busy" => 0x66, _ => 0x00 };
        var brush = new SolidColorBrush(Color.FromArgb(rest, colour.R, colour.G, colour.B));
        if (!IsLoaded || !GlassMotion.CanAnimate)
        {
            StatusGlow.BorderBrush = brush; StatusTrack.Opacity = state == "busy" ? 1 : 0; return;
        }
        var from = (StatusGlow.BorderBrush as SolidColorBrush)?.Color ?? Colors.Transparent;
        if (from.A == 0) from = Color.FromArgb(0, colour.R, colour.G, colour.B);
        StatusGlow.BorderBrush = brush;
        var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
        var glow = new System.Windows.Media.Animation.ColorAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(state is "live" or "error" ? 1100 : 450) };
        glow.KeyFrames.Add(new System.Windows.Media.Animation.EasingColorKeyFrame(from, System.Windows.Media.Animation.KeyTime.FromPercent(0)));
        if (state is "live" or "error")
            glow.KeyFrames.Add(new System.Windows.Media.Animation.EasingColorKeyFrame(Color.FromArgb(0xFF, colour.R, colour.G, colour.B), System.Windows.Media.Animation.KeyTime.FromPercent(.25), ease));
        glow.KeyFrames.Add(new System.Windows.Media.Animation.EasingColorKeyFrame(brush.Color, System.Windows.Media.Animation.KeyTime.FromPercent(1), new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut }));
        brush.BeginAnimation(SolidColorBrush.ColorProperty, glow);

        // Indeterminate progress while connecting.
        StatusTrack.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(state == "busy" ? 1 : 0, TimeSpan.FromMilliseconds(300)));
        BarMove.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
        if (state == "busy")
            BarMove.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, new System.Windows.Media.Animation.DoubleAnimation(-150, Math.Max(300, StatusTrack.ActualWidth), TimeSpan.FromMilliseconds(1300))
            { RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever, EasingFunction = new System.Windows.Media.Animation.SineEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut } });

        if (Scene.Sponge && (state == "live" || state == "error" || (state == "idle" && previous == "live")))
            SpongeTheme.Burst(StatusBubbles, state == "idle" ? "pop" : state);
        if (state == "live" && previous == "busy")
        {
            SweepBand.Opacity = 1;
            var sweep = new System.Windows.Media.Animation.DoubleAnimation(-320, Math.Max(600, StatusCard.ActualWidth), TimeSpan.FromMilliseconds(1000)) { EasingFunction = new System.Windows.Media.Animation.QuadraticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut } };
            sweep.Completed += (_, _) => SweepBand.Opacity = 0;
            SweepMove.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, sweep);
            Pop(1.025);
        }
        else if (state == "error") Shake();
        else if (state == "idle" && previous == "live") Pop(.985);
    }
    private void Pop(double peak)
    {
        var scale = new System.Windows.Media.ScaleTransform(1, 1); StatusCard.RenderTransform = scale;
        foreach (var property in new[] { System.Windows.Media.ScaleTransform.ScaleXProperty, System.Windows.Media.ScaleTransform.ScaleYProperty })
        {
            var spring = new System.Windows.Media.Animation.DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(760) };
            spring.KeyFrames.Add(new System.Windows.Media.Animation.EasingDoubleKeyFrame(peak, System.Windows.Media.Animation.KeyTime.FromPercent(.32), new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }));
            spring.KeyFrames.Add(new System.Windows.Media.Animation.EasingDoubleKeyFrame(1, System.Windows.Media.Animation.KeyTime.FromPercent(1), new System.Windows.Media.Animation.ElasticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut, Oscillations = 1, Springiness = 6 }));
            spring.Completed += (_, _) => { if (StatusCard.RenderTransform == scale) StatusCard.RenderTransform = System.Windows.Media.Transform.Identity; };
            scale.BeginAnimation(property, spring);
        }
    }
    private void Shake()
    {
        var move = new System.Windows.Media.TranslateTransform(); StatusCard.RenderTransform = move;
        var shake = new System.Windows.Media.Animation.DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(560) };
        double[] steps = { 0, -11, 9, -7, 5, -3, 0 };
        for (int i = 0; i < steps.Length; i++)
            shake.KeyFrames.Add(new System.Windows.Media.Animation.EasingDoubleKeyFrame(steps[i], System.Windows.Media.Animation.KeyTime.FromPercent(i / (double)(steps.Length - 1)), new System.Windows.Media.Animation.SineEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut }));
        shake.Completed += (_, _) => { if (StatusCard.RenderTransform == move) StatusCard.RenderTransform = System.Windows.Media.Transform.Identity; };
        move.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, shake);
    }
    private void Stop_Click(object sender, RoutedEventArgs e) => BeginOperation(async () =>
    {
        await engine.StopAsync(); connected = false; chromeMode = false;
        StatusText.Text = "Прокси выключен";
        StatusDetail.Text = "Для программ из списка включите общий режим кнопкой «Включить для приложений».";
    });
    private void ShowError(Exception ex)
    {
        failed = true;
        var message = ex is HttpRequestException ? "Не удалось выполнить HTTPS-запрос через прокси. Проверьте адрес, данные входа, доступ к интернету и поддержку CONNECT." : ex is TimeoutException or TaskCanceledException ? "Время ожидания истекло. Проверьте доступность прокси." : ex.Message;
        if (secret.Length > 0) message = message.Replace(secret, "••••", StringComparison.Ordinal);
        StatusText.Text = "Не удалось выполнить действие"; StatusDetail.Text = message; AddLog("Ошибка: " + message);
        if (IsInTray) tray?.Notify(message.Length > 220 ? message[..220] : message);
    }
    private void AddLog(string text)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => AddLog(text)); return; }
        if (secret.Length > 0) text = text.Replace(secret, "••••", StringComparison.Ordinal);
        DiagnosticLog.Write(text);
        logs.Enqueue($"{DateTime.Now:HH:mm:ss}  {text}");
        while (logs.Count > 200) logs.Dequeue();
        LogBox.Text = string.Join(Environment.NewLine, logs); LogBox.ScrollToEnd();
    }
    private void ClearLog_Click(object sender, RoutedEventArgs e) { logs.Clear(); LogBox.Clear(); }
    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (ProxyPage != null && sender is RadioButton { Tag: string page }) ShowPage(page);
    }
    private FrameworkElement PageFor(string page) => page switch { "apps" => AppsPage, "sites" => SitesPage, "journal" => JournalPage, "settings" => PreferencesPage, _ => ProxyPage };
    private void ShowPage(string page)
    {
        var next = PageFor(page); var previous = shownPage; shownPage = next;
        foreach (var other in new FrameworkElement[] { ProxyPage, AppsPage, SitesPage, JournalPage, PreferencesPage })
        {
            if (other == next || other == previous) continue;
            other.BeginAnimation(OpacityProperty, null); other.Opacity = 1; other.IsHitTestVisible = true; other.Visibility = Visibility.Collapsed;
        }
        var title = page switch { "apps" => "Приложения", "sites" => "Сайты", "journal" => "Журнал", "settings" => "Настройки", _ => "Подключение" };
        var subtitle = page switch
        {
            "apps" => "Выберите, какие программы будут использовать прокси",
            "sites" => "Какие сайты открывать через прокси",
            "journal" => "События подключения и диагностика сети",
            "settings" => "Автозапуск, поведение окна и работа в фоне",
            _ => "Один прокси для ваших приложений"
        };
        var titleChanged = PageTitle.Text != title;
        PageTitle.Text = title; PageSubtitle.Text = subtitle;
        if (IsLoaded && titleChanged) { GlassMotion.Nudge(PageTitle); GlassMotion.Nudge(PageSubtitle); }
        if (previous != null && previous != next)
        {
            if (IsLoaded && GlassMotion.CanAnimate && previous.Visibility == Visibility.Visible)
            {
                previous.IsHitTestVisible = false;
                GlassMotion.Dismiss(previous, () =>
                {
                    previous.Opacity = 1; previous.IsHitTestVisible = true;
                    if (previous != shownPage) previous.Visibility = Visibility.Collapsed;
                });
            }
            else previous.Visibility = Visibility.Collapsed;
        }
        next.IsHitTestVisible = true; next.Visibility = Visibility.Visible;
        if (IsLoaded)
        {
            var offset = page switch { "apps" => 51d, "sites" => 102d, "journal" => 153d, "settings" => 204d, _ => 0d };
            Scene.Hold(1300);
            if (pill != null) pill.Flow(offset); else GlassMotion.MoveIndicator(NavigationLens, offset);
            GlassMotion.Present(next);
        }
    }
    // The wallpaper only moves while someone can see it.
    public void RefreshScene() { UpdateScene(); if (IsInTray) TrimMemory(); }
    private void UpdateScene() => Scene.Run(GlassMotion.CanAnimate && IsVisible && IsActive && WindowState != WindowState.Minimized);
    private void Reveal()
    {
        UpdateScene(); SetIdleMotion(IsVisible);
        if (GlassMotion.CanAnimate)
        {
            RootGrid.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(420)) { EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }, FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop });
            RootGrid.Opacity = 1;
        }
        else RootGrid.Opacity = 1;
        GlassMotion.Present(shownPage ?? ProxyPage);
    }
    private const string SupportUrl = "https://pay.cloudtips.ru/p/b83c07cf";
    private void Support_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(SupportUrl) { UseShellExecute = true }); }
        catch (Exception ex) { AddLog("Не удалось открыть страницу поддержки: " + ex.Message); }
    }
    private void GoApps_Click(object sender, RoutedEventArgs e) => AppsNav.IsChecked = true;
    private void OpenLog_Click(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo(ProfileStore.DirectoryPath) { UseShellExecute = true });
    public void SendToTray()
    {
        if (closing || tray == null) return;
        SetIdleMotion(false); Hide(); ShowInTaskbar = false;
        UpdateScene(); TrimMemory();
    }
    // In the tray nothing is drawn, so give back everything that can be rebuilt on demand.
    private System.Windows.Threading.DispatcherTimer? trimTimer;
    private void TrimMemory()
    {
        Picker.ReleaseItems(); LogBox.ScrollToHome();
        // Nothing is on screen: stop the wallpaper clocks and free its baked bitmaps and cache.
        Scene.Suspend();
        trimTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        trimTimer.Tick -= TrimNow; trimTimer.Tick += TrimNow;
        trimTimer.Stop(); trimTimer.Start();
    }
    private void TrimNow(object? sender, EventArgs e)
    {
        trimTimer?.Stop();
        if (IsVisible) return;
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Aggressive, true, true); GC.WaitForPendingFinalizers(); GC.Collect(2, GCCollectionMode.Forced, true, true);
        using var self = Process.GetCurrentProcess(); SetProcessWorkingSetSize(self.Handle, new IntPtr(-1), new IntPtr(-1));
        EmptyWorkingSet(self.Handle);
    }
    [DllImport("kernel32.dll")] private static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr min, IntPtr max);
    [DllImport("psapi.dll")] private static extern bool EmptyWorkingSet(IntPtr process);
    public void RestoreFromTray()
    {
        if (closing) return;
        trimTimer?.Stop();
        ShowInTaskbar = true; Show(); WindowState = WindowState.Normal; Activate(); UpdateScene();
        AmbientBackdrop.InvalidateVisual(); SetIdleMotion(true);
        if (IsLoaded) GlassMotion.Present(new FrameworkElement[] { ProxyPage, AppsPage, SitesPage, JournalPage, PreferencesPage }.First(p => p.Visibility == Visibility.Visible));
    }
    // Rounded window corners; a maximized window fills the screen with square ones.
    private const double WindowRadius = 30;
    private void Root_SizeChanged(object sender, SizeChangedEventArgs e) { UpdateCorners(); Scene.Hold(300); }
    private void UpdateCorners()
    {
        double radius = WindowState == WindowState.Maximized ? 0 : WindowRadius;
        RootGrid.Clip = new RectangleGeometry(new Rect(RootGrid.RenderSize), radius, radius);
        WindowRim.CornerRadius = new CornerRadius(radius);
    }
    private void Window_StateChanged(object? sender, EventArgs e)
    {
        UpdateCorners();
        MaximizeGlyph.Data = Geometry.Parse(WindowState == WindowState.Maximized ? "M2.5,0.7 H9.3 V7.5 M0.7,2.5 H7.5 V9.3 H0.7 Z" : "M0.7,0.7 H9.3 V9.3 H0.7 Z");
        if (WindowState == WindowState.Minimized && Preferences.MinimizeToTray && tray != null) SendToTray();
        // A plain minimize keeps the window on the taskbar; nothing animates while it is out of sight.
        else if (WindowState == WindowState.Minimized) SetIdleMotion(false);
        else if (IsVisible)
        {
            SetIdleMotion(true);
            // The system restore animation is off (it drew a square frame), so the window fades back in.
            if (GlassMotion.CanAnimate && IsLoaded)
                RootGrid.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(.3, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }, FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop });
        }
        UpdateScene();
    }
    public void ExitApplication() { exitRequested = true; Close(); }
    private void Exit_Click(object sender, RoutedEventArgs e) => ExitApplication();
    public void HandleInstanceCommand(string command)
    {
        if (command == "exit") { ExitApplication(); return; }
        RestoreFromTray();
        if (command == "connect" && (!connected || chromeMode)) BeginOperation(ConnectAsync);
        else if (command == "chrome" && !connected) BeginOperation(StartChromeAsync);
    }
    private async Task StartupConnectAsync()
    {
        // Wi-Fi may become ready after the logon trigger has fired.
        for (var attempt = 0; ; attempt++)
        {
            try { await ConnectAsync(); return; }
            catch (Exception ex) when (attempt < 2 && ex is not (OperationCanceledException or ArgumentException))
            {
                AddLog("Сеть ещё не готова. Повтор подключения через 10 секунд.");
                await Task.Delay(TimeSpan.FromSeconds(10), lifetime.Token);
            }
        }
    }
    private async void SavePreferences_Click(object sender, RoutedEventArgs e)
    {
        if (savingPreferences) return;
        savingPreferences = true; SavePreferencesButton.IsEnabled = false;
        var next = new DesktopPreferences
        {
            MinimizeToTray = MinimizeTrayBox.IsChecked == true, CloseToTray = CloseTrayBox.IsChecked == true,
            StartHidden = StartHiddenBox.IsChecked == true, ConnectAtStartup = AutoConnectBox.IsChecked == true,
            ReduceMotion = ReduceMotionBox.IsChecked == true, AutoElevate = AutoElevateBox.IsChecked == true,
            Sponge = Preferences.Sponge, LastSeenVersion = Preferences.LastSeenVersion
        };
        try
        {
            await StartupService.ApplyAsync(AutoStartBox.IsChecked == true);
            await ApplyElevationAsync(next.AutoElevate);
            next.Save(); Preferences = next;
            GlassMotion.Enabled = !next.ReduceMotion;
            UpdateScene();
            PreferencesResult.Text = AutoStartBox.IsChecked == true ? "Сохранено. Relay запустится при следующем входе в Windows." : "Сохранено. Автозапуск Windows выключен.";
            AddLog("Настройки окна и автозапуска сохранены.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { PreferencesResult.Text = "Изменение отменено в запросе Windows."; }
        catch (Exception ex) { PreferencesResult.Text = "Не удалось сохранить настройки: " + ex.Message; }
        finally { savingPreferences = false; SavePreferencesButton.IsEnabled = true; }
    }
    // Endless decorative motion (the floating empty-state icon, the bobbing sponge). Every running clock
    // keeps the UI thread ticking, so all of it stops while the window sits in the tray.
    public void SetIdleMotion(bool on)
    {
        on &= GlassMotion.CanAnimate;
        var hover = new System.Windows.Media.Animation.DoubleAnimation(-4, 4, TimeSpan.FromSeconds(3)) { AutoReverse = true, RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever, EasingFunction = new System.Windows.Media.Animation.SineEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut } };
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(hover, 30);
        EmptyFloat.BeginAnimation(TranslateTransform.YProperty, on ? hover : null);
        if (on && Scene.Sponge)
        {
            var bob = new RotateTransform(0, 23, 23); BrandLogo.RenderTransform = bob;
            var sway = new System.Windows.Media.Animation.DoubleAnimation(-6, 6, TimeSpan.FromSeconds(2.6)) { AutoReverse = true, RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever, EasingFunction = new System.Windows.Media.Animation.SineEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut } };
            System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(sway, 30);
            bob.BeginAnimation(RotateTransform.AngleProperty, sway);
        }
        else BrandLogo.RenderTransform = Transform.Identity;
    }
    // The water theme turns the navigation pill into a springy drop and makes pages rise like liquid.
    private LiquidPill? pill;
    public void SetLiquid(bool on)
    {
        GlassMotion.Liquid = on;
        if (on && pill == null) { pill = new LiquidPill(NavigationLens); pill.Landed += offset => SpongeTheme.Ripple(NavRipples, offset + 25.5); }
        else if (!on && pill != null)
        {
            // Hand the pill back to the plain indicator exactly where it stands, without sliding in from the top.
            var offset = pill.Offset; pill.Release(); pill = null;
            NavigationLens.RenderTransformOrigin = new Point(.5, .5);
            NavigationLens.RenderTransform = new TransformGroup { Children = { new ScaleTransform(1, 1), new TranslateTransform(0, offset) } };
        }
    }
    public void RefreshBrandText() => tray?.Rebrand();
    private void CodeBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) { ApplyCode_Click(sender, e); e.Handled = true; }
    }
    // The secret code switches the GuSHin Proxy look on; entering it again restores the normal one.
    private void ApplyCode_Click(object sender, RoutedEventArgs e)
    {
        var code = CodeBox.Text.Trim(); CodeBox.Clear();
        if (code.Length == 0) { CodeResult.Text = "Введите код."; return; }
        if (!SpongeTheme.IsCode(code)) { CodeResult.Text = "Неверный код."; return; }
        var on = !Preferences.Sponge;
        Preferences.Sponge = on;
        try { Preferences.Save(); } catch (Exception ex) { AddLog("Не удалось сохранить оформление: " + ex.Message); }
        SpongeTheme.Apply(this, on, true);
        CodeResult.Text = on ? "Код принят. Включено оформление GuSHin Proxy." : "Код принят. Возвращено обычное оформление.";
        AddLog(on ? "Включено оформление GuSHin Proxy." : "Возвращено обычное оформление.");
    }
    // A transparent borderless window would maximize over the taskbar; keep it inside the monitor's work area.
    private IntPtr WindowHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != 0x0024) return IntPtr.Zero;
        var monitor = MonitorFromWindow(hwnd, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return IntPtr.Zero;
        var dpi = VisualTreeHelper.GetDpi(this);
        var limits = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        limits.MaxPositionX = info.Work.Left - info.Monitor.Left; limits.MaxPositionY = info.Work.Top - info.Monitor.Top;
        limits.MaxSizeX = limits.MaxTrackX = info.Work.Right - info.Work.Left; limits.MaxSizeY = limits.MaxTrackY = info.Work.Bottom - info.Work.Top;
        limits.MinTrackX = (int)(MinWidth * dpi.DpiScaleX); limits.MinTrackY = (int)(MinHeight * dpi.DpiScaleY);
        Marshal.StructureToPtr(limits, lParam, true);
        handled = true;
        return IntPtr.Zero;
    }
    [StructLayout(LayoutKind.Sequential)] private struct MinMaxInfo { public int ReservedX, ReservedY, MaxSizeX, MaxSizeY, MaxPositionX, MaxPositionY, MinTrackX, MinTrackY, MaxTrackX, MaxTrackY; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public int Flags; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, int flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (canClose) return;
        if (!exitRequested && Preferences.CloseToTray) { e.Cancel = true; SendToTray(); return; }
        e.Cancel = true; if (closing) return;
        closing = true; IsEnabled = false; lifetime.Cancel();
        try { if (operation != null) await operation; await engine.StopAsync(); }
        catch { /* Closing the job handle still terminates this app's engine. */ }
        finally
        {
            canClose = true;
            // Closing can complete synchronously. Re-enter Close only after WPF
            // has finished dispatching the original Closing event.
            _ = Dispatcher.BeginInvoke(Close);
        }
    }
}
