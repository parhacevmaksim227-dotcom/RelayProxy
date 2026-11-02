using System.Text.Json;

namespace RelayProxy.Core;

public static class ConfigBuilder
{
    // Browsers are steered by the site list on this second loopback listener; every other selected program
    // (Telegram, games, ...) keeps using the plain listener and goes through the proxy as a whole.
    public const int SitesPort = 19872;

    public static Dictionary<string, object> Outbound(Profile p)
    {
        var d = new Dictionary<string, object> { ["type"] = p.Protocol.StartsWith("SOCKS") ? "socks" : "http", ["tag"] = "proxy", ["server"] = p.Host, ["server_port"] = p.Port, ["domain_resolver"] = "bootstrap", ["connect_timeout"] = "12s" };
        if (p.Username.Length > 0) d["username"] = p.Username;
        if (p.Password.Length > 0 && p.Protocol != "SOCKS4") d["password"] = p.Password;
        if (p.Protocol.StartsWith("SOCKS")) { d["version"] = p.Protocol == "SOCKS4" ? "4" : "5"; if (p.Protocol == "SOCKS4" || !p.EnableUdp) d["network"] = "tcp"; }
        if (p.Protocol == "HTTPS") d["tls"] = new { enabled = true, server_name = p.TlsServerName.Length > 0 ? p.TlsServerName : p.Host };
        return d;
    }

    private static readonly string[] DohResolvers =
    [
        "1.1.1.1/32", "1.0.0.1/32", "2606:4700:4700::1111/128", "2606:4700:4700::1001/128",
        "8.8.8.8/32", "8.8.4.4/32", "2001:4860:4860::8888/128", "2001:4860:4860::8844/128",
        "9.9.9.9/32", "149.112.112.112/32", "2620:fe::fe/128", "2620:fe::9/128",
        "94.140.14.14/32", "94.140.15.15/32", "208.67.222.222/32", "208.67.220.220/32",
        "104.16.248.249/32", "104.16.249.249/32", "2606:4700::6810:f8f9/128", "2606:4700::6810:f9f9/128"
    ];
    private static readonly string[] DohDomains = ["cloudflare-dns.com", "dns.google", "dns.quad9.net", "dns.adguard.com", "dns.adguard-dns.com", "doh.opendns.com", "dns.nextdns.io"];

    public static string Build(Profile p, string enginePath, string appPath, int? testPort = null, bool includeLocal = false)
    {
        var rules = new List<object>();
        // Site mode applies to the bridge that receives the selected applications, never to the proxy probe.
        var sites = p.SitesOnly && p.Sites.Count > 0 && testPort == ChromeLauncher.Port ? p.Sites.ToArray() : null;
        if (testPort == null)
        {
            if (includeLocal) rules.Add(new { inbound = new[] { "relay-local" }, action = "route", outbound = "proxy" });
            rules.Add(new { process_path = new[] { enginePath, appPath }, action = "route", outbound = "direct" });
            rules.Add(new { port = new[] { 53 }, action = "hijack-dns" });
            var patterns = ApplicationRules.Patterns(p.Apps);
            if (patterns.Length == 0) throw new ArgumentException("Не выбраны приложения.");
            if (p.Protocol != "SOCKS5")
                rules.Add(new { process_path_regex = patterns, ip_version = 6, action = "reject" });
            rules.Add(new { process_path_regex = patterns, network = p.Protocol == "SOCKS5" && p.EnableUdp ? new[] { "icmp" } : new[] { "udp", "icmp" }, action = "reject" });
            rules.Add(new { process_path_regex = patterns, action = "route", outbound = "proxy" });
        }
        if (testPort != null)
        {
            if (sites != null)
            {
                // Name lookups always go through the engine, so listed sites resolve to their real addresses
                // even when the provider's own DNS lies. Without this a blocked site can resolve to a stub page.
                rules.Add(new { port = new[] { 53 }, action = "hijack-dns" });
                // Browsers with "secure DNS" (Chrome, Firefox) ask public resolvers over HTTPS. Those lookups go through the
                // proxy: the answers are then the real addresses instead of whatever the provider's DNS rewrites, and the browser
                // keeps its own DNS working rather than falling back to the system resolver.
                rules.Add(new { inbound = new[] { "sites" }, ip_cidr = DohResolvers, port = new[] { 443, 853 }, action = "route", outbound = "proxy" });
                // Browsers prefer QUIC (UDP 443), whose handshake cannot be steered reliably; refusing it makes them fall back to TCP.
                rules.Add(new { inbound = new[] { "sites" }, network = new[] { "udp" }, port = new[] { 443 }, action = "reject" });
                // Names reach the bridge only as addresses; sniffing the TLS/HTTP/QUIC handshake recovers them.
                rules.Add(new { inbound = new[] { "sites" }, action = "sniff" });
                if (p.Protocol != "SOCKS5" || !p.EnableUdp) rules.Add(new { inbound = new[] { "sites" }, domain_suffix = sites, network = new[] { "udp" }, action = "reject" });
                rules.Add(new { inbound = new[] { "sites" }, domain_suffix = sites.Concat(DohDomains).ToArray(), action = "route", outbound = "proxy" });
                rules.Add(new { inbound = new[] { "sites" }, action = "route", outbound = "direct" });
            }
            if (p.ProxyDns) rules.Add(new { port = new[] { 53 }, action = "hijack-dns" });
            else rules.Add(new { port = new[] { 53 }, action = "route", outbound = "direct" });
            if (sites == null && (p.Protocol != "SOCKS5" || !p.EnableUdp)) rules.Add(new { network = new[] { "udp" }, action = "reject" });
        }
        object inbound = testPort is int port
            ? new { type = "mixed", tag = "probe", listen = "127.0.0.1", listen_port = port }
            : new { type = "tun", tag = "relay-tun", interface_name = "RelayProxy", address = new[] { "172.29.231.1/30", "fd71:7e1a:9b23::1/126" }, mtu = 1500, auto_route = true, strict_route = true, stack = "mixed", dns_mode = "hijack" };
        var inbounds = new List<object> { inbound };
        if (sites != null) inbounds.Add(new { type = "mixed", tag = "sites", listen = "127.0.0.1", listen_port = SitesPort });
        if (testPort == null && includeLocal)
            inbounds.Add(new { type = "mixed", tag = "relay-local", listen = "127.0.0.1", listen_port = ChromeLauncher.Port });
        return JsonSerializer.Serialize(new
        {
            log = new { level = "info", timestamp = true, disabled = false },
            dns = new { servers = new object[] {
                new { type = "https", tag = "bootstrap", server = "1.1.1.1", server_port = 443, path = "/dns-query", tls = new { enabled = true, server_name = "cloudflare-dns.com" } },
                new { type = "https", tag = "remote-dns", server = "1.1.1.1", server_port = 443, path = "/dns-query", tls = new { enabled = true, server_name = "cloudflare-dns.com" }, detour = "proxy" }
            }, rules = sites != null ? new object[] {
                // HTTPS/SVCB records carry Encrypted Client Hello keys; with ECH the site name is hidden and cannot be matched, so they are answered empty.
                new { query_type = new[] { "HTTPS", "SVCB" }, action = "predefined", rcode = "NOERROR" },
                new { domain_suffix = sites, action = "route", server = "remote-dns" } } : Array.Empty<object>(),
            final = p.ProxyDns ? "remote-dns" : "bootstrap", strategy = p.Protocol == "SOCKS5" ? "prefer_ipv4" : "ipv4_only" },
            inbounds,
            outbounds = new object[] { Outbound(p), new { type = "direct", tag = "direct" } },
            route = new { rules, final = testPort != null ? "proxy" : "direct", auto_detect_interface = true, default_domain_resolver = "bootstrap" }
        }, new JsonSerializerOptions { WriteIndented = true });
    }
}
