using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RelayProxy.Core;

public sealed class ProcessRouter : IAsyncDisposable
{
    private Process? process;
    private bool stopping;
    public event Action<string>? Log;
    public event Action? UnexpectedExit;
    // The copy that ships with Relay wins; an installed ProxiFyre is only a fallback.
    private static readonly string Bundled = Path.Combine(AppContext.BaseDirectory, "engine", "router");
    private static readonly string Installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ProxiFyre");
    private static string Source => File.Exists(Path.Combine(Bundled, "ProxiFyre.exe")) ? Bundled : Installed;
    private static readonly string DriverPackage = Path.Combine(AppContext.BaseDirectory, "engine", "driver", "WindowsPacketFilter.msi");
    public static bool DriverInstalled => Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\ndisrd") != null;
    // Installs the bundled Windows Packet Filter driver the first time it is needed.
    private async Task EnsureDriverAsync(CancellationToken token)
    {
        if (DriverInstalled) return;
        if (!File.Exists(DriverPackage)) throw new FileNotFoundException("Не найден драйвер Windows Packet Filter (engine\\driver). Распакуйте весь архив приложения.");
        Log?.Invoke("Устанавливаем драйвер Windows Packet Filter (один раз)…");
        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "msiexec.exe")) { UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add("/i"); info.ArgumentList.Add(DriverPackage); info.ArgumentList.Add("/qn"); info.ArgumentList.Add("/norestart");
        using var installer = Process.Start(info) ?? throw new InvalidOperationException("Не удалось запустить установку драйвера.");
        await installer.WaitForExitAsync(token).WaitAsync(TimeSpan.FromMinutes(3), token);
        // 0 = done, 3010 = done but Windows wants a restart before the driver can filter traffic.
        if (installer.ExitCode == 3010) throw new InvalidOperationException("Драйвер установлен. Перезагрузите компьютер и включите Relay снова.");
        if (installer.ExitCode != 0 || !DriverInstalled) throw new InvalidOperationException($"Не удалось установить драйвер Windows Packet Filter (код {installer.ExitCode}).");
        Log?.Invoke("Драйвер установлен.");
    }
    public static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RelayProxy", "ProcessRouter");
    // The Visual C++ runtime ProxiFyre's native part links against, shipped next to it so a clean Windows works.
    private static readonly string[] AppLocal = ["vcruntime140.dll", "vcruntime140_1.dll", "msvcp140.dll", "msvcp140_atomic_wait.dll"];
    // ProxiFyre is a .NET Framework 4.7.2 program (release key 461808); every current Windows 10/11 has it.
    private static bool HasFramework472()
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full");
        return key?.GetValue("Release") is int release && release >= 461808;
    }
    private static readonly string[] Payload = ["ProxiFyre.exe", "ProxiFyre.exe.config", "ProxiFyre.Configuration.dll", "socksify.dll", "Newtonsoft.Json.dll", "NLog.dll", "Topshelf.dll"];
    // With a site list, browsers use the filtering listener and all other programs the plain one.
    private static object[] Proxies(Profile profile)
    {
        var families = profile.Protocol == "SOCKS5" ? new[] { "IPv4", "IPv6" } : new[] { "IPv4" };
        object Entry(string[] apps, int port) => new { appNames = apps, socks5ProxyEndpoint = $"127.0.0.1:{port}", supportedProtocols = new[] { "TCP", "UDP" }, supportedAddressFamilies = families };
        if (!(profile.SitesOnly && profile.Sites.Count > 0)) return [Entry(Applications(profile), ChromeLauncher.Port)];
        var result = new List<object>();
        var filtered = Applications(profile, a => ApplicationRules.IsBrowser(a.Path)).Concat(ApplicationRules.InstalledBrowsers()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var whole = Applications(profile, a => !ApplicationRules.IsBrowser(a.Path));
        if (filtered.Length > 0) result.Add(Entry(filtered, ConfigBuilder.SitesPort));
        if (whole.Length > 0) result.Add(Entry(whole, ChromeLauncher.Port));
        return result.ToArray();
    }
    public static string[] Applications(Profile profile) => Applications(profile, _ => true);
    public static string[] Applications(Profile profile, Func<AppRule, bool> filter)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in profile.Apps.Where(a => a.Enabled && filter(a)).SelectMany(StoreVersions))
        {
            if (!Path.IsPathFullyQualified(rule.Path) || !rule.Path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Нужен полный путь к выбранному EXE.");
            paths.Add(rule.Path);
            // Enumerate only folders allowed by the same narrow helper rules as before.
            if (ApplicationRules.PatternsFor(rule).Count() > 1 && Path.GetDirectoryName(rule.Path) is string directory && Directory.Exists(directory))
            {
                foreach (var helper in Directory.EnumerateFiles(directory, "*.exe", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                {
                    paths.Add(helper);
                    if (paths.Count > 10000) throw new InvalidOperationException("Слишком широкая папка приложения. Выключите включение вспомогательных EXE.");
                }
            }
        }
        return paths.ToArray();
    }
    private static IEnumerable<AppRule> StoreVersions(AppRule rule)
    {
        var result = new List<AppRule> { rule };
        var package = Regex.Match(rule.Path, @"(?i)^(.*\\WindowsApps\\)([^\\]+)\\(.+)$");
        if (!package.Success) return result;
        var versioned = Regex.Match(package.Groups[2].Value, @"^(.*)_\d+\.\d+\.\d+\.\d+(_.*)$");
        if (!versioned.Success) return result;
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(package.Groups[1].Value, versioned.Groups[1].Value + "_*" + versioned.Groups[2].Value))
            {
                var path = Path.Combine(directory, package.Groups[3].Value);
                if (File.Exists(path)) result.Add(new AppRule { Path = path, IncludeHelpers = rule.IncludeHelpers });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return result;
    }
    public static string Configuration(Profile profile, string app, string engine) => JsonSerializer.Serialize(new
    {
        logLevel = "Info", bypassLan = true,
        proxies = Proxies(profile),
        excludes = new[] { app, engine, Path.Combine(Folder, "ProxiFyre.exe") }
    }, new JsonSerializerOptions { WriteIndented = true });

    public async Task StartAsync(Profile profile, EngineRunner owner, CancellationToken token)
    {
        if (!EngineRunner.IsAdministrator) throw new InvalidOperationException("Для перехвата выбранных процессов нужны права администратора.");
        await EnsureDriverAsync(token);
        var existing = Process.GetProcessesByName("ProxiFyre");
        try { if (existing.Length > 0) throw new InvalidOperationException("ProxiFyre уже работает отдельно. Остановите его перед включением Relay, чтобы правила не конфликтовали."); }
        finally { foreach (var item in existing) item.Dispose(); }
        foreach (var file in Payload) if (!File.Exists(Path.Combine(Source, file))) throw new FileNotFoundException("Не найдены файлы перехвата (engine\\router). Распакуйте весь архив приложения целиком.");
        if (!HasFramework472()) throw new InvalidOperationException("Для перехвата нужен .NET Framework 4.7.2 или новее. Обновите Windows 10 до версии 1803 и выше.");
        Directory.CreateDirectory(Folder);
        foreach (var file in Payload) File.Copy(Path.Combine(Source, file), Path.Combine(Folder, file), true);
        foreach (var file in AppLocal) if (File.Exists(Path.Combine(Source, file))) File.Copy(Path.Combine(Source, file), Path.Combine(Folder, file), true);
        await ConfigureFirewallAsync(token);
        // This private copy does not alter ProxiFyre's service or its configuration.
        await File.WriteAllTextAsync(Path.Combine(Folder, "app-config.json"), Configuration(profile, Environment.ProcessPath!, EngineRunner.EnginePath), token);
        await File.WriteAllTextAsync(Path.Combine(Folder, "NLog.config"), """
            <nlog xmlns="http://www.nlog-project.org/schemas/NLog.xsd" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"><targets><target name="console" xsi:type="Console" layout="${message}" /></targets><rules><logger name="*" minlevel="Info" writeTo="console" /></rules></nlog>
            """, token);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        process = new Process { StartInfo = new ProcessStartInfo(Path.Combine(Folder, "ProxiFyre.exe")) { WorkingDirectory = Folder, UseShellExecute = false, CreateNoWindow = false, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true }, EnableRaisingEvents = true };
        string lastError = "Не удалось запустить перехват выбранных процессов.";
        void Receive(object sender, DataReceivedEventArgs args)
        {
            if (string.IsNullOrWhiteSpace(args.Data)) return;
            Log?.Invoke("Процессы: " + args.Data);
            if (args.Data.Contains("SOCKS5 Local Router instance started successfully", StringComparison.OrdinalIgnoreCase)) ready.TrySetResult();
            if (args.Data.Contains("Error", StringComparison.OrdinalIgnoreCase) || args.Data.Contains("Failed", StringComparison.OrdinalIgnoreCase)) lastError = args.Data;
        }
        process.OutputDataReceived += Receive; process.ErrorDataReceived += Receive;
        process.Exited += (sender, _) =>
        {
            // Use the exited process itself: the field is cleared on a normal stop, which can happen before this event arrives.
            if (stopping) { ready.TrySetCanceled(); return; }
            uint code = 0;
            try { if (sender is Process exited) code = unchecked((uint)exited.ExitCode); } catch (InvalidOperationException) { }
            // 0xC0000135 / 0xC000007B: a required library is missing or of the wrong kind.
            var detail = code is 0xC0000135 or 0xC000007B ? "Не хватает системной библиотеки (Visual C++). Установите «Распространяемый пакет Visual C++ 2015–2022 x64» с сайта Microsoft." : lastError;
            ready.TrySetException(new InvalidOperationException($"{detail} (код 0x{code:X})"));
            UnexpectedExit?.Invoke();
        };
        try
        {
            process.Start(); owner.OwnChild(process);
            process.BeginOutputReadLine(); process.BeginErrorReadLine();
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(25), token);
            if (process.HasExited) throw new InvalidOperationException(lastError);
        }
        catch { await DisposeAsync(); throw; }
    }
    private static async Task ConfigureFirewallAsync(CancellationToken token)
    {
        var executable = Path.Combine(Folder, "ProxiFyre.exe").Replace("'", "''");
        var script = "$ErrorActionPreference='Stop'; $exe='" + executable + "'; foreach($protocol in @('TCP','UDP')) { $name='RelayProxy.ProcessRouter.'+$protocol; if(-not(Get-NetFirewallRule -Name $name -ErrorAction SilentlyContinue)) { New-NetFirewallRule -Name $name -DisplayName ('Relay: selected applications ('+$protocol+')') -Group RelayProxy -Direction Inbound -Action Allow -Program $exe -Protocol $protocol -Profile Any -EdgeTraversalPolicy Block | Out-Null } }";
        using var helper = new Process { StartInfo = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true } };
        helper.StartInfo.ArgumentList.Add("-NoProfile"); helper.StartInfo.ArgumentList.Add("-NonInteractive"); helper.StartInfo.ArgumentList.Add("-Command"); helper.StartInfo.ArgumentList.Add(script);
        helper.Start(); var errors = helper.StandardError.ReadToEndAsync(token); var output = helper.StandardOutput.ReadToEndAsync(token);
        try { await helper.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(30), token); if (helper.ExitCode != 0) throw new InvalidOperationException("Не удалось создать правила брандмауэра для движка перехвата."); }
        finally { if (!helper.HasExited) helper.Kill(true); await output; await errors; }
    }
    public async ValueTask DisposeAsync()
    {
        stopping = true;
        if (process is not Process child) return;
        process = null;
        try
        {
            if (!child.HasExited)
            {
                await EngineRunner.RequestGracefulStopAsync(child);
                if (!child.HasExited) { child.Kill(true); await child.WaitForExitAsync(); }
            }
        }
        finally { child.Dispose(); }
    }
}
