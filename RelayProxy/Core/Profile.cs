using System.IO;
using System.Text;
using System.Text.Json.Serialization;

namespace RelayProxy.Core;

public sealed class AppRule
{
    public string Path { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public bool IncludeHelpers { get; set; } = true;
    [JsonIgnore] public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);
}

public sealed class Profile
{
    public string Protocol { get; set; } = "SOCKS5";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 1080;
    public string Username { get; set; } = "";
    [JsonIgnore] public string Password { get; set; } = "";
    public string ProtectedPassword { get; set; } = "";
    public string TlsServerName { get; set; } = "";
    public bool ProxyDns { get; set; } = true;
    public bool EnableUdp { get; set; } = true;
    public List<AppRule> Apps { get; set; } = [];
    // Domains (with all subdomains) that use the proxy; with SitesOnly everything else goes direct.
    public List<string> Sites { get; set; } = [];
    public bool SitesOnly { get; set; }

    public void Validate(bool requireApps)
    {
        if (!new[] { "HTTP", "HTTPS", "SOCKS4", "SOCKS5" }.Contains(Protocol)) throw new ArgumentException("Выберите тип прокси.");
        Host = Host.Trim().Trim('[', ']');
        if (string.IsNullOrWhiteSpace(Host) || Uri.CheckHostName(Host) == UriHostNameType.Unknown)
            throw new ArgumentException("Укажите IP-адрес или домен прокси без протокола и порта.");
        if (Port is < 1 or > 65535) throw new ArgumentException("Порт должен быть от 1 до 65535.");
        if (Username.IndexOfAny(['\r', '\n', '\0']) >= 0 || Password.IndexOfAny(['\r', '\n', '\0']) >= 0)
            throw new ArgumentException("Логин и пароль не должны содержать переводы строк или нулевые символы.");
        if (Protocol is "HTTP" or "HTTPS" && Username.Contains(':')) throw new ArgumentException("Логин HTTP-прокси не должен содержать двоеточие.");
        if (Protocol == "SOCKS5" && (Encoding.UTF8.GetByteCount(Username) > 255 || Encoding.UTF8.GetByteCount(Password) > 255))
            throw new ArgumentException("Логин и пароль SOCKS5 должны быть не длиннее 255 байт каждый.");
        if (Protocol != "SOCKS4" && ((Username.Length == 0) != (Password.Length == 0)))
            throw new ArgumentException("Введите и логин, и пароль, либо оставьте оба поля пустыми.");
        if (Protocol == "HTTPS" && TlsServerName.Length > 0 && Uri.CheckHostName(TlsServerName) == UriHostNameType.Unknown)
            throw new ArgumentException("Неверное имя сервера TLS.");
        // In site mode the installed browsers are enough on their own.
        if (requireApps && !Apps.Any(a => a.Enabled) && !(SitesOnly && Sites.Count > 0)) throw new ArgumentException("Добавьте и отметьте хотя бы одно приложение или включите режим сайтов.");
        if (requireApps && SitesOnly && Sites.Count == 0) throw new ArgumentException("Добавьте хотя бы один сайт или выключите режим «Только указанные сайты».");
        foreach (var app in Apps.Where(a => requireApps && a.Enabled))
            if (!System.IO.Path.IsPathFullyQualified(app.Path) || !app.Path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"Укажите полный путь к .exe: {app.Path}");
    }

    // "https://www.Example.com:8443/path" -> "example.com"; null when it is not a usable domain.
    public static string? NormalizeSite(string input)
    {
        var text = input.Trim().ToLowerInvariant();
        var scheme = text.IndexOf("://", StringComparison.Ordinal); if (scheme >= 0) text = text[(scheme + 3)..];
        var at = text.LastIndexOf('@'); if (at >= 0) text = text[(at + 1)..];
        text = text.Split('/', '?', '#')[0];
        var colon = text.LastIndexOf(':'); if (colon >= 0 && text[(colon + 1)..].All(char.IsDigit)) text = text[..colon];
        text = text.TrimStart('*').Trim('.');
        if (text.StartsWith("www.", StringComparison.Ordinal)) text = text[4..];
        if (text.Length is 0 or > 253 || !text.Contains('.')) return null;
        try { text = new System.Globalization.IdnMapping().GetAscii(text); } catch (ArgumentException) { return null; }
        return Uri.CheckHostName(text) == UriHostNameType.Dns && !text.Any(c => c is ' ' or '_' or '*') ? text : null;
    }

    public static Profile ParseUri(string input)
    {
        if (!Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri)) throw new ArgumentException("Формат: socks5://логин:пароль@сервер:1080");
        var protocol = uri.Scheme.ToUpperInvariant();
        if (!new[] { "HTTP", "HTTPS", "SOCKS4", "SOCKS5" }.Contains(protocol)) throw new ArgumentException("Поддерживаются http://, https://, socks4:// и socks5://.");
        if (uri.AbsolutePath is not ("" or "/") || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new ArgumentException("Ссылка должна содержать только сервер, порт и данные входа. Спецсимволы в пароле кодируйте как %XX.");
        var auth = uri.UserInfo.Split(':', 2);
        var p = new Profile { Protocol = protocol, Host = uri.Host.Trim('[', ']'), Port = uri.Port > 0 ? uri.Port : protocol == "HTTPS" ? 443 : protocol == "HTTP" ? 80 : 1080,
            Username = Uri.UnescapeDataString(auth[0]), Password = auth.Length > 1 ? Uri.UnescapeDataString(auth[1]) : "" };
        p.Validate(false);
        return p;
    }
}
