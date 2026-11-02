namespace RelayProxy.Core;

public static class ServerEndpoint
{
    public static bool TrySplit(string input, out string host, out int port)
    {
        host = ""; port = 0;
        var text = input.Trim();
        int separator;
        if (text.StartsWith('['))
        {
            int bracket = text.IndexOf(']');
            if (bracket < 2 || bracket + 1 >= text.Length || text[bracket + 1] != ':') return false;
            host = text[1..bracket]; separator = bracket + 1;
            if (Uri.CheckHostName(host) != UriHostNameType.IPv6) return false;
        }
        else
        {
            separator = text.IndexOf(':');
            if (separator < 1 || separator != text.LastIndexOf(':')) return false;
            host = text[..separator];
            if (Uri.CheckHostName(host) is not (UriHostNameType.IPv4 or UriHostNameType.Dns)) return false;
        }
        var number = text[(separator + 1)..];
        return number.Length > 0 && number.All(c => c is >= '0' and <= '9') && int.TryParse(number, out port) && port is >= 1 and <= 65535;
    }
}
