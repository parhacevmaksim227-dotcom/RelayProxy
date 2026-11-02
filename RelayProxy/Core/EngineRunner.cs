using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace RelayProxy.Core;

public sealed class EngineRunner : IAsyncDisposable
{
    public static string EnginePath => Path.Combine(AppContext.BaseDirectory, "engine", "sing-box.exe");
    public static bool IsAdministrator => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    private Process? process;
    private ProcessRouter? router;
    private IntPtr job;
    private bool stopping;
    private readonly SemaphoreSlim stopLock = new(1, 1);
    public event Action<string>? Log;
    public event Action? UnexpectedExit;
    public bool IsRunning => process is { HasExited: false };

    public static async Task CheckAsync(string config, CancellationToken token = default)
    {
        using var p = new Process { StartInfo = StartInfo("check", true) };
        p.Start();
        var error = p.StandardError.ReadToEndAsync(token);
        var output = p.StandardOutput.ReadToEndAsync(token);
        try
        {
            await p.StandardInput.WriteAsync(config.AsMemory(), token); p.StandardInput.Close();
            await p.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(15), token);
            var errorText = await error; await output;
            // Never echo the config or parser's source snippets: they can contain credentials.
            if (p.ExitCode != 0) throw new InvalidOperationException("Ядро отклонило настройки. Проверьте адрес, тип прокси и наличие комплектного sing-box 1.14.4.");
        }
        finally { if (!p.HasExited) { p.Kill(true); await p.WaitForExitAsync(); } }
    }

    private static ProcessStartInfo StartInfo(string command, bool noWindow) => new(EnginePath)
    {
        ArgumentList = { command, "--disable-color", "-c", "stdin" },
        WorkingDirectory = Path.GetDirectoryName(EnginePath)!,
        UseShellExecute = false, CreateNoWindow = noWindow, WindowStyle = ProcessWindowStyle.Hidden,
        RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
    };

    public async Task StartAsync(string config, string password, CancellationToken token = default)
    {
        if (!File.Exists(EnginePath)) throw new FileNotFoundException("Не найден engine/sing-box.exe. Распакуйте весь архив приложения.");
        await StopAsync();
        await CheckAsync(config, token);
        stopping = false;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        process = new Process { StartInfo = StartInfo("run", false), EnableRaisingEvents = true };
        var lastError = "Ядро завершилось до запуска.";
        void Receive(object sender, DataReceivedEventArgs args)
        {
            if (string.IsNullOrWhiteSpace(args.Data)) return;
            var line = args.Data;
            if (password.Length > 0) line = line.Replace(password, "••••", StringComparison.Ordinal);
            if (line.Contains("FATAL") || line.Contains("ERROR")) lastError = line;
            Log?.Invoke(line);
            if (line.Contains("sing-box started", StringComparison.Ordinal)) ready.TrySetResult();
        }
        process.OutputDataReceived += Receive;
        process.ErrorDataReceived += Receive;
        process.Exited += (_, _) => { ready.TrySetException(new InvalidOperationException(lastError)); if (!stopping) UnexpectedExit?.Invoke(); };
        try
        {
            job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            var info = new JobInfo { Basic = new BasicLimits { Flags = 0x2000 } }; // KILL_ON_JOB_CLOSE
            if (!SetInformationJobObject(job, 9, ref info, (uint)Marshal.SizeOf<JobInfo>())) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            process.Start();
            // Config is withheld until ownership is established; no TUN can start in the gap.
            if (!AssignProcessToJobObject(job, process.Handle)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            process.BeginOutputReadLine(); process.BeginErrorReadLine();
            await process.StandardInput.WriteAsync(config.AsMemory(), token); process.StandardInput.Close();
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(25), token);
            if (process.HasExited) throw new InvalidOperationException(lastError);
        }
        catch { await StopAsync(); throw; }
    }

    public void OwnChild(Process child)
    {
        if (job == IntPtr.Zero || !AssignProcessToJobObject(job, child.Handle)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }
    public async Task StartApplicationsAsync(Profile profile, CancellationToken token = default)
    {
        // The only sing-box inbound is loopback. No virtual adapter, routes or DNS changes.
        await StartAsync(ConfigBuilder.Build(profile, EnginePath, Environment.ProcessPath!, ChromeLauncher.Port), profile.Password, token);
        router = new ProcessRouter();
        router.Log += text => Log?.Invoke(text);
        router.UnexpectedExit += () => { if (!stopping) UnexpectedExit?.Invoke(); };
        try { await router.StartAsync(profile, this, token); }
        catch { await StopAsync(); throw; }
    }
    public static async Task RequestGracefulStopAsync(Process child)
    {
        if (!AttachConsole((uint)child.Id)) return;
        SetConsoleCtrlHandler(IntPtr.Zero, true);
        try { GenerateConsoleCtrlEvent(0, 0); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(6)); }
        catch (TimeoutException) { }
        finally { FreeConsole(); SetConsoleCtrlHandler(IntPtr.Zero, false); }
    }

    public async Task StopAsync()
    {
        await stopLock.WaitAsync();
        try
        {
            stopping = true;
            if (router != null) { await router.DisposeAsync(); router = null; }
            var p = process;
            if (p != null)
            {
                try
                {
                    if (!p.HasExited)
                    {
                        // Attach only to our child's hidden console and let sing-box remove its TUN/routes.
                        if (AttachConsole((uint)p.Id))
                        {
                            SetConsoleCtrlHandler(IntPtr.Zero, true);
                            try
                            {
                                GenerateConsoleCtrlEvent(0, 0);
                                await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(6));
                            }
                            catch (TimeoutException) { }
                            finally { FreeConsole(); SetConsoleCtrlHandler(IntPtr.Zero, false); }
                        }
                        if (!p.HasExited) { Log?.Invoke("Штатная остановка недоступна: завершаем собственное ядро."); p.Kill(true); await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                    }
                }
                catch (InvalidOperationException) { }
                finally { p.Dispose(); process = null; }
            }
        }
        finally { if (job != IntPtr.Zero) { CloseHandle(job); job = IntPtr.Zero; } stopLock.Release(); }
    }
    public async ValueTask DisposeAsync() => await StopAsync();

    public static async Task<string> ProbeAsync(Profile profile, CancellationToken token, Action<string>? log = null)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        await using var runner = new EngineRunner();
        var errors = new System.Collections.Concurrent.ConcurrentQueue<string>();
        runner.Log += line => { log?.Invoke(line); if (line.Contains("ERROR") || line.Contains("FATAL")) errors.Enqueue(line); };
        var config = ConfigBuilder.Build(profile, EnginePath, Environment.ProcessPath!, port);
        await runner.StartAsync(config, profile.Password, token);
        using var handler = new HttpClientHandler { Proxy = new WebProxy($"http://127.0.0.1:{port}"), UseProxy = true, AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        var timer = Stopwatch.StartNew();
        HttpResponseMessage response;
        try { response = await client.GetAsync("https://www.cloudflare.com/cdn-cgi/trace", token); }
        catch (HttpRequestException ex)
        {
            // Wait briefly for stderr's asynchronous reader to deliver the actual proxy error.
            await Task.Delay(150, token);
            var detail = string.Join("\n", errors.TakeLast(2));
            throw new InvalidOperationException(ProxyFailure.Describe(detail + "\n" + ex.GetBaseException().Message, profile.Protocol), ex);
        }
        using var responseLifetime = response;
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(token);
        var ipText = body.Split('\n').FirstOrDefault(l => l.StartsWith("ip="))?[3..].Trim();
        if (!IPAddress.TryParse(ipText, out _)) throw new InvalidOperationException("Прокси ответил, но тестовый сервер не вернул IP.");
        return $"Прокси работает · IP {ipText} · {timer.ElapsedMilliseconds} мс";
    }

    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits { public long ProcessTime, JobTime; public uint Flags; public UIntPtr MinWorking, MaxWorking; public uint ActiveProcessLimit; public UIntPtr Affinity; public uint Priority, Scheduling; }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadCount, WriteCount, OtherCount, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct JobInfo { public BasicLimits Basic; public IoCounters Io; public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JobInfo info, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AttachConsole(uint processId);
    [DllImport("kernel32.dll")] private static extern bool FreeConsole();
    [DllImport("kernel32.dll")] private static extern bool GenerateConsoleCtrlEvent(uint ctrlEvent, uint processGroupId);
    [DllImport("kernel32.dll")] private static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);
}
