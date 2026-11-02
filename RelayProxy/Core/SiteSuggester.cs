using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace RelayProxy.Core;

public sealed record SiteSuggestionInfo(string Domain, string Reason);

// Finds domains related to the one being typed, from the live internet: the same name under other
// top-level domains (checked in DNS) and the other hosts the site itself pulls resources from.
public static class SiteSuggester
{
    private static readonly string[] Zones = ["com", "net", "org", "io", "app", "co", "ru", "ai", "me", "dev", "tv", "so", "info", "online", "xyz", "de", "uk", "fr", "es", "it", "pl", "ua", "kz", "by"];
    private static readonly HashSet<string> Compound = ["co", "com", "net", "org", "gov", "edu", "ac"];
    private static readonly string[] Noise = ["w3.org", "schema.org", "example.com", "apache.org", "mozilla.org", "gnu.org"];
    private static readonly HttpClient Http = Create();
    private static readonly Regex Hosts = new(@"(?i)(?:https?:)?//([a-z0-9](?:[a-z0-9-]*[a-z0-9])?(?:\.[a-z0-9](?:[a-z0-9-]*[a-z0-9])?)+)(?=[/:""'\s?#)])", RegexOptions.Compiled);

    private static HttpClient Create()
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 4, AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Relay/2.0");
        return client;
    }

    // "app.miro.com" -> "miro.com", "shop.example.co.uk" -> "example.co.uk"
    public static string Registrable(string host)
    {
        var parts = host.Split('.');
        if (parts.Length <= 2) return host;
        var take = parts[^2].Length <= 3 && Compound.Contains(parts[^2]) && parts.Length >= 3 ? 3 : 2;
        return string.Join('.', parts[^take..]);
    }
    public static string Label(string host)
    {
        var registrable = Registrable(host).Split('.');
        return registrable[0];
    }

    public static async Task<List<SiteSuggestionInfo>> RelatedAsync(string domain, ISet<string> exclude, CancellationToken token)
    {
        var own = Registrable(domain);
        var label = Label(domain);
        var result = new List<SiteSuggestionInfo>();
        var variants = Task.WhenAll(Zones.Select(zone => $"{label}.{zone}").Where(d => !string.Equals(d, own, StringComparison.OrdinalIgnoreCase)).Select(d => ResolvesAsync(d, token)));
        var assets = AssetHostsAsync(domain, own, token);
        string?[] found;
        try { found = await variants; } catch (OperationCanceledException) { throw; } catch { found = []; }
        foreach (var name in found.Where(n => n != null && !exclude.Contains(n)).Take(8)) result.Add(new(name!, "то же имя"));
        try
        {
            foreach (var host in await assets)
                if (!exclude.Contains(host) && result.All(r => r.Domain != host)) result.Add(new(host, "использует сайт"));
        }
        catch (OperationCanceledException) { throw; }
        catch { }
        return result;
    }

    private static async Task<string?> ResolvesAsync(string name, CancellationToken token)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(2500);
            var addresses = await Dns.GetHostAddressesAsync(name, timeout.Token);
            return addresses.Length > 0 ? name : null;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { return null; }
    }

    // Other registrable domains mentioned in the site's front page: its CDNs, API, sister brands.
    private static async Task<List<string>> AssetHostsAsync(string domain, string own, CancellationToken token)
    {
        foreach (var url in new[] { "https://" + domain, "https://www." + domain })
        {
            try
            {
                using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
                await using var stream = await response.Content.ReadAsStreamAsync(token);
                var buffer = new byte[600_000]; int total = 0, read;
                while (total < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(total), token)) > 0) total += read;
                var html = System.Text.Encoding.UTF8.GetString(buffer, 0, total);
                var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (Match m in Hosts.Matches(html))
                {
                    var host = Registrable(m.Groups[1].Value.ToLowerInvariant());
                    if (host == own || host.Length > 60 || !host.Contains('.') || Noise.Contains(host)) continue;
                    counts[host] = counts.GetValueOrDefault(host) + 1;
                }
                return counts.OrderByDescending(p => p.Value).Select(p => p.Key).Take(8).ToList();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch { /* blocked or unreachable: try the next form, then give up quietly */ }
        }
        return [];
    }

    // Small site icon from public favicon services; null when none answers.
    public static async Task<byte[]?> IconAsync(string domain, CancellationToken token)
    {
        foreach (var url in new[] { $"https://www.google.com/s2/favicons?domain={domain}&sz=64", $"https://icons.duckduckgo.com/ip3/{domain}.ico" })
        {
            try
            {
                using var response = await Http.GetAsync(url, token);
                if (!response.IsSuccessStatusCode) continue;
                var bytes = await response.Content.ReadAsByteArrayAsync(token);
                if (bytes.Length > 100) return bytes;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch { }
        }
        return null;
    }
}
