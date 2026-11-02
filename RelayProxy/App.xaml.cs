using System.Windows;
using RelayProxy.Core;
using RelayProxy.Desktop;

namespace RelayProxy;

public partial class App : Application
{
    private Mutex? instance;
    private bool ownsMutex;
    private InstanceChannel? channel;
    public static bool IsTestMode { get; set; }
    private bool errorShown;
    protected override void OnStartup(StartupEventArgs e)
    {
        // Nothing may close Relay silently: every failure is logged, and a window explains it once.
        DispatcherUnhandledException += (_, args) =>
        {
            DiagnosticLog.Write("Необработанная ошибка: " + args.Exception);
            args.Handled = true;
            if (errorShown) return; errorShown = true;
            MessageBox.Show("Relay столкнулся с ошибкой и продолжит работу:\n\n" + args.Exception.Message + "\n\nПодробности в файле журнала:\n" + System.IO.Path.Combine(ProfileStore.DirectoryPath, "relay.log"), "Relay", MessageBoxButton.OK, MessageBoxImage.Warning);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => DiagnosticLog.Write("Критическая ошибка: " + args.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, args) => { DiagnosticLog.Write("Ошибка фоновой задачи: " + args.Exception); args.SetObserved(); };
        SmoothScroll.EnableEverywhere();
        try { TaskbarIdentity.InitializeProcess(); } catch (Exception ex) { DiagnosticLog.Write("Taskbar process identity: " + ex.Message); }
        base.OnStartup(e);
        if (e.Args.Any(a => a.StartsWith("--set-startup=")) || e.Args.Contains("--verify-startup"))
        {
            try
            {
                if (e.Args.Contains("--verify-startup")) { StartupService.VerifyRegistration(); DiagnosticLog.Write("PASS startup task registration and cleanup verified."); }
                else StartupService.Configure(e.Args.Contains("--set-startup=on"));
                Shutdown(0);
            }
            catch (Exception ex) { DiagnosticLog.Write("Startup task: " + ex.Message); Shutdown(1); }
            return;
        }
        if (e.Args.Contains("--set-elevation=off") || e.Args.Contains("--set-elevation=on"))
        {
            try { ElevationService.Configure(e.Args.Contains("--set-elevation=on")); Shutdown(0); }
            catch (Exception ex) { DiagnosticLog.Write("Elevation task: " + ex.Message); Shutdown(1); }
            return;
        }
        if (e.Args.Contains("--exit")) { Shutdown(InstanceChannel.Send("exit") ? 0 : 1); return; }
        // The elevated copy started by the task receives the arguments the first copy was given.
        var arguments = e.Args.Where(a => a != ElevationService.LaunchArgument).ToList();
        if (e.Args.Contains(ElevationService.LaunchArgument)) { arguments.AddRange(ElevationService.TakeArguments()); arguments.Add("--handoff"); }
        var preview = arguments.Contains("--preview");
        instance = new Mutex(false, preview ? @"Local\RelayProxy.Preview" : IsTestMode ? @"Local\RelayProxy.UiTests" : @"Local\RelayProxy.Desktop");
        try { ownsMutex = instance.WaitOne(arguments.Contains("--handoff") ? TimeSpan.FromSeconds(15) : TimeSpan.Zero); }
        catch (AbandonedMutexException) { ownsMutex = true; }
        if (!ownsMutex)
        {
            if (arguments.Contains("--startup")) { Shutdown(); return; }
            var command = arguments.Contains("--connect") ? "connect" : arguments.Contains("--chrome") ? "chrome" : "show";
            if (!InstanceChannel.Send(command) && !arguments.Contains("--startup")) MessageBox.Show("Relay уже работает. Откройте его через значок в трее.", "Relay");
            Shutdown(); return;
        }
        if (!IsTestMode && !preview && !EngineRunner.IsAdministrator && StartElevated(arguments)) { Shutdown(); return; }
        var startup = arguments.Contains("--startup");
        var window = new MainWindow(arguments.Contains("--connect"), arguments.Contains("--chrome"), startup, !preview && !IsTestMode);
        MainWindow = window;
        if (preview) window.Preferences.CloseToTray = false;
        if (!IsTestMode && !preview) channel = new InstanceChannel(command => Dispatcher.BeginInvoke(() => window.HandleInstanceCommand(command)));
        window.Show();
        if (startup && window.Preferences.StartHidden) window.SendToTray();
        // An elevated copy leaves the silent-launch task behind, so the next start needs no approval.
        if (!IsTestMode && !preview && EngineRunner.IsAdministrator && window.Preferences.AutoElevate)
            _ = Task.Run(() => { try { if (!ElevationService.IsReady()) { ElevationService.Configure(true); DiagnosticLog.Write("Silent administrator launch enabled."); } } catch (Exception ex) { DiagnosticLog.Write("Silent launch setup: " + ex.Message); } });
    }

    // Returns true when an elevated copy took over (this one must exit); false to keep running as is.
    private bool StartElevated(List<string> arguments)
    {
        DesktopPreferences preferences;
        try { preferences = DesktopPreferences.Load(); } catch { return false; }
        if (!preferences.AutoElevate || arguments.Contains("--startup")) return false;
        instance?.ReleaseMutex(); ownsMutex = false;
        try
        {
            if (ElevationService.IsReady() && ElevationService.TryLaunch(arguments)) return true;
            // First run: one Windows prompt, after which the elevated copy registers the silent launcher.
            var info = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", WorkingDirectory = AppContext.BaseDirectory };
            foreach (var argument in arguments.Where(a => a != "--handoff")) info.ArgumentList.Add(argument);
            info.ArgumentList.Add("--handoff");
            System.Diagnostics.Process.Start(info);
            return true;
        }
        catch (System.ComponentModel.Win32Exception) { /* approval declined: carry on without administrator rights */ }
        catch (Exception ex) { DiagnosticLog.Write("Elevation: " + ex.Message); }
        try { ownsMutex = instance!.WaitOne(TimeSpan.FromSeconds(3)); } catch (AbandonedMutexException) { ownsMutex = true; }
        return false;
    }
    protected override void OnExit(ExitEventArgs e)
    {
        channel?.Dispose();
        if (ownsMutex) instance?.ReleaseMutex();
        instance?.Dispose();
        base.OnExit(e);
    }
}
