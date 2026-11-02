using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using RelayProxy.Core;

if (args.Contains("--restore-running"))
{
    var sent = RelayProxy.Desktop.InstanceChannel.Send("show");
    Console.WriteLine(sent ? "PASS existing tray instance restored across elevation" : "FAIL cannot contact elevated instance");
    Environment.ExitCode = sent ? 0 : 1;
    return;
}

if (args.Contains("--startup-state"))
{
    try { var state = StartupService.Read(); Console.WriteLine("Startup enabled=" + state.Enabled); }
    catch (Exception ex) { Console.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--verify-tun") || args.Contains("--verify-process-routing"))
{
    var resultPath = Path.Combine(ProfileStore.DirectoryPath, "process-routing-test-result.txt");
    File.WriteAllText(resultPath, "STARTING per-process routing test\n");
    try
    {
        if (!EngineRunner.IsAdministrator) throw new Exception("Administrator required for TUN test");
        var profile = ProfileStore.Load();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(70));
        using var baselineHandler = new HttpClientHandler { UseProxy = false };
        using var baselineHttp = new HttpClient(baselineHandler) { Timeout = TimeSpan.FromSeconds(15) };
        var baselineIp = (await baselineHttp.GetStringAsync("https://api.ipify.org", deadline.Token)).Trim();
        string NetworkState() => string.Join("|", System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().OrderBy(n => n.Id).Select(n => n.Id + ":" + string.Join(",", n.GetIPProperties().DnsAddresses) + ":" + string.Join(",", n.GetIPProperties().GatewayAddresses.Select(a => a.Address))));
        var networkBefore = NetworkState();
        var expected = await EngineRunner.ProbeAsync(profile, deadline.Token);
        var ip = System.Text.RegularExpressions.Regex.Match(expected, @"IP ([0-9a-fA-F.:]+)").Groups[1].Value;
        var curlPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "curl.exe");
        profile.Apps = [new() { Path = curlPath, IncludeHelpers = false }];
        await using (var runner = new EngineRunner())
        {
            var routingLogs = new System.Collections.Concurrent.ConcurrentQueue<string>();
            runner.Log += line => { routingLogs.Enqueue(line); File.AppendAllText(resultPath, line + "\n"); };
            await runner.StartApplicationsAsync(profile, deadline.Token);
            if (NetworkState() != networkBefore) throw new Exception("Network adapters, gateways or DNS changed");
            File.AppendAllText(resultPath, "PASS network adapters, gateways and DNS unchanged during routing\n");
            var info = new System.Diagnostics.ProcessStartInfo(curlPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "--noproxy", "*", "--http1.1", "--connect-timeout", "10", "--max-time", "25", "--silent", "--show-error", "https://api.ipify.org" }) info.ArgumentList.Add(arg);
            using var child = new System.Diagnostics.Process { StartInfo = info };
            child.Start(); var output = child.StandardOutput.ReadToEndAsync(); var error = child.StandardError.ReadToEndAsync();
            try { await child.WaitForExitAsync(deadline.Token); }
            finally { if (!child.HasExited) { child.Kill(true); await child.WaitForExitAsync(); } }
            var actual = (await output).Trim(); var stderr = await error;
            if (child.ExitCode != 0 || actual != ip) throw new Exception("Selected application did not use proxy IP. " + stderr);
            if (!routingLogs.Any(l => l.Contains("outbound/http[proxy]") && !l.Contains("ERROR"))) throw new Exception("No proxy routing log found");
            File.AppendAllText(resultPath, "PASS selected application's direct TCP request was transparently proxied\n");
            using var handler = new HttpClientHandler { UseProxy = false };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
            var direct = (await http.GetStringAsync("https://api.ipify.org", deadline.Token)).Trim();
            if (!IPAddress.TryParse(direct, out _) || direct != baselineIp) throw new Exception("Unselected application did not preserve baseline direct egress");
            File.AppendAllText(resultPath, "PASS unselected application retains its original direct egress IP\n");
        }
        if (NetworkState() != networkBefore) throw new Exception("Network state changed after stop");
        File.AppendAllText(resultPath, "PASS process router stopped; network state preserved\nALL PROCESS ROUTING CHECKS PASSED\n");
    }
    catch (Exception ex) { File.AppendAllText(resultPath, "FAIL " + ex.Message + "\n"); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--repair-and-verify-chrome"))
{
    var saved = ProfileStore.Load();
    saved.Protocol = "HTTP";
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
    var result = await EngineRunner.ProbeAsync(saved, deadline.Token);
    var chromePath = ChromeLauncher.Find() ?? throw new Exception("Chrome not found");
    if (!saved.Apps.Any(a => a.Path.Equals(chromePath, StringComparison.OrdinalIgnoreCase))) saved.Apps.Add(new() { Path = chromePath });
    else saved.Apps.First(a => a.Path.Equals(chromePath, StringComparison.OrdinalIgnoreCase)).Enabled = true;
    File.Copy(ProfileStore.FilePath, ProfileStore.FilePath + ".before-1.1", true);
    ProfileStore.Save(saved);
    Console.WriteLine("PASS Saved HTTP profile and enabled installed Chrome");
    await using var runner = new EngineRunner();
    await runner.StartAsync(ConfigBuilder.Build(saved, EngineRunner.EnginePath, Environment.ProcessPath!, ChromeLauncher.Port), saved.Password, deadline.Token);
    var browserInfo = ChromeLauncher.CreateStartInfo(Path.Combine(Path.GetTempPath(), "RelayProxy-ChromeTest-" + Guid.NewGuid().ToString("N")));
    browserInfo.UseShellExecute = false;
    browserInfo.RedirectStandardOutput = true; browserInfo.RedirectStandardError = true; browserInfo.CreateNoWindow = true;
    browserInfo.ArgumentList.Add("--headless=new"); browserInfo.ArgumentList.Add("--dump-dom"); browserInfo.ArgumentList.Add("--disable-gpu"); browserInfo.ArgumentList.Add("--timeout=15000");
    using var chrome = new System.Diagnostics.Process { StartInfo = browserInfo };
    chrome.Start();
    var output = chrome.StandardOutput.ReadToEndAsync(deadline.Token);
    var error = chrome.StandardError.ReadToEndAsync(deadline.Token);
    try { await chrome.WaitForExitAsync(deadline.Token); }
    finally { if (!chrome.HasExited) { chrome.Kill(true); await chrome.WaitForExitAsync(); } }
    var html = await output; await error;
    var ipMatch = System.Text.RegularExpressions.Regex.Match(result, @"IP ([0-9a-fA-F.:]+)");
    if (!ipMatch.Success || !html.Contains(ipMatch.Groups[1].Value)) throw new Exception("Chrome did not return the tested proxy's exit IP");
    Console.WriteLine("PASS Chrome request used the same proxy exit IP as the sing-box probe");
    return;
}

if (args.Contains("--diagnose-profile"))
{
    var saved = ProfileStore.Load();
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
    foreach (var type in new[] { saved.Protocol, saved.Protocol == "HTTPS" ? "HTTP" : saved.Protocol }.Distinct())
    {
        saved.Protocol = type;
        try { Console.WriteLine(type + ": " + await EngineRunner.ProbeAsync(saved, deadline.Token, line => { if (line.Contains("ERROR") || line.Contains("FATAL")) Console.WriteLine(line.Replace(saved.Host, "[proxy]")); })); }
        catch (Exception ex) { Console.WriteLine(type + ": " + ex.GetBaseException().GetType().Name + " / " + ex.GetBaseException().Message.Replace(saved.Host, "[proxy]")); }
    }
    return;
}

int checks = 0;
void Assert(bool value, string name) { if (!value) throw new Exception(name); Console.WriteLine("PASS " + name); checks++; }
var prefsPath = Path.Combine(Path.GetTempPath(), "RelayProxy-Preferences-" + Guid.NewGuid().ToString("N") + ".json");
try
{
    new DesktopPreferences { CloseToTray = false, MinimizeToTray = true, StartHidden = false, ConnectAtStartup = true, ReduceMotion = true }.Save(prefsPath);
    var restored = DesktopPreferences.Load(prefsPath);
    Assert(!restored.CloseToTray && restored.MinimizeToTray && !restored.StartHidden && restored.ConnectAtStartup && restored.ReduceMotion, "Desktop and motion preferences persist independently of proxy credentials");
}
finally { File.Delete(prefsPath); }
var channelName = "RelayProxy-Test-" + Guid.NewGuid().ToString("N");
var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
using (var channel = new RelayProxy.Desktop.InstanceChannel(command => received.TrySetResult(command), channelName))
{
    Assert(RelayProxy.Desktop.InstanceChannel.Send("show", channelName), "Second launch can send restore command");
    Assert(await received.Task.WaitAsync(TimeSpan.FromSeconds(3)) == "show", "Running instance receives restore command");
}
var helperRule = new AppRule { Path = @"C:\Apps\Antigravity\Antigravity.exe" };
var helperPatterns = ApplicationRules.Patterns([helperRule]);
bool Matches(string path, string[] patterns) => patterns.Any(pattern => System.Text.RegularExpressions.Regex.IsMatch(path, pattern));
Assert(Matches(@"c:\apps\antigravity\resources\bin\language_server.exe", helperPatterns), "Antigravity helper process matches selected app folder");
Assert(!Matches(@"C:\Apps\Other\other.exe", helperPatterns), "Unrelated app does not match helper rule");
helperRule.IncludeHelpers = false;
Assert(!Matches(@"C:\Apps\Antigravity\resources\bin\language_server.exe", ApplicationRules.Patterns([helperRule])), "Helper matching can be disabled");
var desktopApp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Antigravity-x64.exe");
Assert(ApplicationRules.Patterns([new() { Path = desktopApp }]).Length == 1, "Desktop app does not capture unrelated Desktop executables");
var storeRule = new AppRule { Path = @"C:\Program Files\WindowsApps\38833FF26BA1D.UnigramPreview_12.10.7.0_x64__g9c9v27vpyspw\Telegram.exe" };
Assert(Matches(@"C:\Program Files\WindowsApps\38833FF26BA1D.UnigramPreview_12.11.0.0_x64__g9c9v27vpyspw\Telegram.exe", ApplicationRules.Patterns([storeRule])), "Telegram Store update preserves package rule");
Assert(!Matches(@"C:\Program Files\WindowsApps\OtherPublisher.App_12.11.0.0_x64__g9c9v27vpyspw\Telegram.exe", ApplicationRules.Patterns([storeRule])), "Different Store package is excluded");
new Profile { Host = "127.0.0.1", Apps = [new() { Path = @"C:\missing-updated-app.exe" }] }.Validate(false);
Assert(true, "Proxy check independent of missing selected EXE");
var parsed = Profile.ParseUri("socks5://u:p%40ss%3Aword@[::1]:1081");
Assert(parsed.Host == "::1" && parsed.Password == "p@ss:word" && parsed.Port == 1081, "URI / IPv6 / encoded password");
foreach (var bad in new[] { "ftp://localhost:21", "http://localhost:80/path", "socks5://u@localhost:1080", "http://localhost:80/#secret" })
{
    try { Profile.ParseUri(bad); throw new Exception("Accepted invalid URI: " + bad); } catch (ArgumentException) { checks++; }
}
var appPath = Environment.ProcessPath!;
foreach (var protocol in new[] { "HTTP", "HTTPS", "SOCKS4", "SOCKS5" })
{
    var profile = new Profile { Protocol = protocol, Host = "127.0.0.1", Port = 12345, Username = "alice", Password = "secret", Apps = [new() { Path = appPath }, new() { Path = @"C:\disabled.exe", Enabled = false }] };
    profile.Validate(true);
    var config = ConfigBuilder.Build(profile, EngineRunner.EnginePath, @"C:\RelayProxy.exe");
    await EngineRunner.CheckAsync(config);
    var root = JsonNode.Parse(config)!;
    Assert(root["route"]!["final"]!.GetValue<string>() == "direct", protocol + " TUN config accepted, default direct");
    Assert(!config.Contains("disabled.exe") && config.Contains("(?i)^"), protocol + " enabled paths only, case-insensitive exact match");
    Assert(!config.Contains("insecure"), protocol + " TLS never insecure");
    if (protocol == "SOCKS4") Assert(!config.Contains("secret"), "SOCKS4 password omitted");
}

// Production per-process mode must never create a TUN or a catch-all process rule.
foreach (var type in new[] { "HTTP", "HTTPS", "SOCKS4", "SOCKS5" })
{
    var p = new Profile { Protocol = type, Host = "192.0.2.10", Port = 8080, Password = "DO-NOT-WRITE-UPSTREAM-SECRET", Apps = [new() { Path = @"C:\Apps\Selected\selected.exe", IncludeHelpers = false }, new() { Path = @"C:\Apps\Other\other.exe", Enabled = false }] };
    var bridge = JsonNode.Parse(ConfigBuilder.Build(p, EngineRunner.EnginePath, appPath, ChromeLauncher.Port))!;
    await EngineRunner.CheckAsync(bridge.ToJsonString());
    Assert(bridge["inbounds"]!.AsArray().All(n => n!["type"]!.GetValue<string>() == "mixed" && n["listen"]!.GetValue<string>() == "127.0.0.1"), type + " application mode only listens on loopback without TUN");
    var routerJson = ProcessRouter.Configuration(p, appPath, EngineRunner.EnginePath);
    var patterns = JsonNode.Parse(routerJson)!["proxies"]![0]!["appNames"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
    Assert(patterns.SequenceEqual(new[] { @"C:\Apps\Selected\selected.exe" }) && !routerJson.Contains(p.Password) && !routerJson.Contains(p.Host), type + " router only includes selected full paths and stores no upstream secrets");
}

// Site mode: listed domains use the proxy, the rest goes direct, and the engine accepts the config.
foreach (var type in new[] { "HTTP", "SOCKS5" })
{
    var p = new Profile { Protocol = type, Host = "192.0.2.10", Port = 8080, Apps = [new() { Path = @"C:\Apps\Selected\selected.exe", IncludeHelpers = false }], Sites = ["example.com", "sub.example.org"], SitesOnly = true };
    p.Validate(true);
    var json = ConfigBuilder.Build(p, EngineRunner.EnginePath, appPath, ChromeLauncher.Port);
    await EngineRunner.CheckAsync(json);
    var node = JsonNode.Parse(json)!;
    Assert(node["route"]!["rules"]!.AsArray().Any(r => r!["inbound"] != null && r["action"]?.GetValue<string>() == "route" && r["outbound"]?.GetValue<string>() == "direct" && r["domain_suffix"] == null), type + " site listener sends unlisted sites direct");
    Assert(json.Contains("\"sniff\"") && json.Contains("example.com"), type + " site mode sniffs and matches domains");
    var probe = ConfigBuilder.Build(p, EngineRunner.EnginePath, appPath, 54321);
    Assert(JsonNode.Parse(probe)!["route"]!["final"]!.GetValue<string>() == "proxy" && !probe.Contains("example.com"), type + " proxy probe ignores site mode");
}
{
    var split = new Profile { Protocol = "HTTP", Host = "192.0.2.10", Port = 8080, Apps = [new() { Path = @"C:\Program Files\Google\Chrome\Application\chrome.exe", IncludeHelpers = false }, new() { Path = @"C:\Telegram\Telegram.exe", IncludeHelpers = false }], Sites = ["example.com"], SitesOnly = true };
    var routerJson = JsonNode.Parse(ProcessRouter.Configuration(split, appPath, EngineRunner.EnginePath))!["proxies"]!.AsArray();
    Assert(routerJson.Count == 2, "site mode splits browsers from other programs");
    foreach (var entry in routerJson)
    {
        var names = entry!["appNames"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
        var endpoint = entry["socks5ProxyEndpoint"]!.GetValue<string>();
        if (names.Any(n => n.EndsWith("chrome.exe"))) Assert(endpoint.EndsWith(":" + ConfigBuilder.SitesPort) && names.All(ApplicationRules.IsBrowser), "browsers use the filtering listener");
        else Assert(endpoint.EndsWith(":" + ChromeLauncher.Port) && names.Single().EndsWith("Telegram.exe"), "other programs use the plain listener");
    }
    var cfg = JsonNode.Parse(ConfigBuilder.Build(split, EngineRunner.EnginePath, appPath, ChromeLauncher.Port))!;
    Assert(cfg["route"]!["final"]!.GetValue<string>() == "proxy" && cfg["inbounds"]!.AsArray().Count == 2, "plain listener proxies everything, second listener filters");
    split.SitesOnly = false;
    Assert(JsonNode.Parse(ProcessRouter.Configuration(split, appPath, EngineRunner.EnginePath))!["proxies"]!.AsArray().Count == 1, "no site mode keeps a single listener");
}
foreach (var (raw, expected) in new (string, string?)[] { ("https://www.Example.com:8443/path?q=1", "example.com"), ("*.sub.site.org", "sub.site.org"), ("пример.рф", "xn--e1afmkfd.xn--p1ai"), ("localhost", null), ("bad_host.com", null), ("a b.com", null), ("", null) })
    Assert(Profile.NormalizeSite(raw) == expected, "site normalisation: " + raw);
try { new Profile { Host = "192.0.2.10", Apps = [new() { Path = appPath }], SitesOnly = true }.Validate(true); throw new Exception("Empty site list accepted"); } catch (ArgumentException) { checks++; }

// Do not overwrite the user's profile: DPAPI test uses the implementation via reflection.
var protect = typeof(ProfileStore).GetMethod("Protect", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
var encrypted = (byte[])protect.Invoke(null, [Encoding.UTF8.GetBytes("my-test-secret"), true])!;
var decrypted = (byte[])protect.Invoke(null, [encrypted.ToArray(), false])!;
Assert(Encoding.UTF8.GetString(decrypted) == "my-test-secret" && !Encoding.UTF8.GetString(encrypted).Contains("my-test-secret"), "Windows DPAPI round trip");
Assert(!System.Text.Json.JsonSerializer.Serialize(new Profile { Password = "my-test-secret" }).Contains("my-test-secret"), "plaintext password excluded from settings JSON");

using var key = RSA.Create(2048);
var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("localhost"); request.CertificateExtensions.Add(san.Build());
request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
using var temporaryCert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));
using var certificate = X509CertificateLoader.LoadPkcs12(temporaryCert.Export(X509ContentType.Pfx), null);

foreach (var protocol in new[] { "HTTP", "HTTPS", "SOCKS4", "SOCKS5" })
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    var ct = deadline.Token;
    var proxy = new TcpListener(IPAddress.Loopback, 0); proxy.Start();
    var proxyPort = ((IPEndPoint)proxy.LocalEndpoint).Port;
    var local = new TcpListener(IPAddress.Loopback, 0); local.Start();
    var localPort = ((IPEndPoint)local.LocalEndpoint).Port; local.Stop();
    var p = new Profile { Protocol = protocol, Host = "127.0.0.1", Port = proxyPort, Username = "alice", Password = "test-password", TlsServerName = "localhost" };
    var configNode = JsonNode.Parse(ConfigBuilder.Build(p, EngineRunner.EnginePath, appPath, localPort))!;
    if (protocol == "HTTPS") configNode["outbounds"]![0]!["tls"]!["certificate"] = JsonValue.Create(certificate.ExportCertificatePem());
    var serverTask = Task.Run(async () =>
    {
        using var client = await proxy.AcceptTcpClientAsync(ct);
        Stream stream = client.GetStream();
        if (protocol == "HTTPS") { var tls = new SslStream(stream); await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, ct); stream = tls; }
        await using (stream)
        {
            if (protocol is "HTTP" or "HTTPS")
            {
                var header = await ReadHeader(stream, ct);
                Assert(header.StartsWith("CONNECT 93.184.216.34:80 "), protocol + " CONNECT destination");
                Assert(header.Contains("Proxy-Authorization: Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("alice:test-password")), StringComparison.OrdinalIgnoreCase), protocol + " authentication");
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection established\r\n\r\n"), ct);
            }
            else if (protocol == "SOCKS4")
            {
                var head = await ReadN(stream, 8, ct); var user = await ReadZero(stream, ct);
                Assert(head[0] == 4 && head[1] == 1 && head[3] == 80 && head[4] == 93 && user == "alice", "SOCKS4 connect and User ID");
                await stream.WriteAsync(new byte[] { 0, 90, 0, 80, 93, 184, 216, 34 }, ct);
            }
            else
            {
                var head = await ReadN(stream, 2, ct); await ReadN(stream, head[1], ct);
                await stream.WriteAsync(new byte[] { 5, 2 }, ct);
                var authHead = await ReadN(stream, 2, ct); var user = Encoding.UTF8.GetString(await ReadN(stream, authHead[1], ct));
                var length = (await ReadN(stream, 1, ct))[0]; var pass = Encoding.UTF8.GetString(await ReadN(stream, length, ct));
                Assert(user == "alice" && pass == "test-password", "SOCKS5 username/password authentication");
                await stream.WriteAsync(new byte[] { 1, 0 }, ct);
                var conn = await ReadN(stream, 4, ct);
                Assert(conn[0] == 5 && conn[1] == 1 && conn[3] == 1, "SOCKS5 connect request");
                await ReadN(stream, 6, ct);
                await stream.WriteAsync(new byte[] { 5, 0, 0, 1, 127, 0, 0, 1, 0, 80 }, ct);
            }
            var data = await ReadHeader(stream, ct);
            Assert(data.StartsWith("GET /relay-test "), protocol + " payload reaches upstream");
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 8\r\nConnection: close\r\n\r\nrelay-ok"), ct);
        }
    }, ct);
    await using (var runner = new EngineRunner())
    {
        runner.Log += line => { if (line.Contains("ERROR") || line.Contains("FATAL")) Console.WriteLine(line); };
        await runner.StartAsync(configNode.ToJsonString(), p.Password, ct);
        using var handler = new HttpClientHandler { Proxy = new WebProxy($"http://127.0.0.1:{localPort}"), UseProxy = true };
        using var http = new HttpClient(handler);
        string result;
        try { result = await http.GetStringAsync("http://93.184.216.34/relay-test", ct); }
        catch { if (serverTask.IsFaulted) Console.WriteLine(serverTask.Exception); throw; }
        Assert(result == "relay-ok", protocol + " actual sing-box tunnel round trip");
        await serverTask;
        await runner.StopAsync(); Assert(!runner.IsRunning, protocol + " engine stopped");
    }
    proxy.Stop();
}

// Invalid HTTPS certificates must fail closed in production config.
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
    var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
    int upstreamPort = ((IPEndPoint)listener.LocalEndpoint).Port;
    var temporary = new TcpListener(IPAddress.Loopback, 0); temporary.Start(); int port = ((IPEndPoint)temporary.LocalEndpoint).Port; temporary.Stop();
    var server = Task.Run(async () => { using var tcp = await listener.AcceptTcpClientAsync(deadline.Token); using var ssl = new SslStream(tcp.GetStream()); try { await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, deadline.Token); _ = await ssl.ReadAsync(new byte[1], deadline.Token); } catch (Exception ex) when (ex is IOException or System.Security.Authentication.AuthenticationException) { } });
    await using var runner = new EngineRunner();
    var p = new Profile { Protocol = "HTTPS", Host = "127.0.0.1", Port = upstreamPort, TlsServerName = "localhost" };
    await runner.StartAsync(ConfigBuilder.Build(p, EngineRunner.EnginePath, appPath, port), "", deadline.Token);
    using var handler = new HttpClientHandler { Proxy = new WebProxy($"http://127.0.0.1:{port}"), UseProxy = true };
    using var client = new HttpClient(handler);
    bool rejected = false;
    try { using var response = await client.GetAsync("http://93.184.216.34/", deadline.Token); rejected = !response.IsSuccessStatusCode; } catch (HttpRequestException) { rejected = true; }
    Assert(rejected, "HTTPS untrusted certificate rejected");
    await server; listener.Stop();
}
// Exercise the actual public probe on a proxy requiring authentication.
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    var proxy = new TcpListener(IPAddress.Loopback, 0); proxy.Start();
    var ct = deadline.Token;
    var server = Task.Run(async () => {
        try { while (!ct.IsCancellationRequested) {
            using var tcp = await proxy.AcceptTcpClientAsync(ct);
            await ReadHeader(tcp.GetStream(), ct);
            await tcp.GetStream().WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=\"RelayTest\"\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), ct);
        } } catch (OperationCanceledException) { }
    }, ct);
    bool explained = false;
    try { await EngineRunner.ProbeAsync(new Profile { Protocol = "HTTP", Host = "127.0.0.1", Port = ((IPEndPoint)proxy.LocalEndpoint).Port }, ct); }
    catch (InvalidOperationException ex) { explained = ex.Message.Contains("407") && ex.Message.Contains("логин"); }
    finally { deadline.Cancel(); await server; proxy.Stop(); }
    Assert(explained, "Real HTTP 407 reported as missing proxy credentials");
}
Console.WriteLine($"ALL {checks} CHECKS PASSED");

static async Task<byte[]> ReadN(Stream s, int n, CancellationToken ct) { var b = new byte[n]; await s.ReadExactlyAsync(b, ct); return b; }
static async Task<string> ReadZero(Stream s, CancellationToken ct) { var b = new List<byte>(); for (int i=0;i<1024;i++) { var v=(await ReadN(s,1,ct))[0]; if(v==0) return Encoding.UTF8.GetString(b.ToArray()); b.Add(v); } throw new IOException("User ID too long"); }
static async Task<string> ReadHeader(Stream s, CancellationToken ct) { var b = new List<byte>(); while(b.Count<16384) { b.Add((await ReadN(s,1,ct))[0]); if(b.Count>=4 && b[^4]==13 && b[^3]==10 && b[^2]==13 && b[^1]==10) return Encoding.ASCII.GetString(b.ToArray()); } throw new IOException("Header too long"); }
