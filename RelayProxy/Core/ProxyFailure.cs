namespace RelayProxy.Core;

public static class ProxyFailure
{
    public static string Describe(string details, string protocol)
    {
        var text = details.ToLowerInvariant();
        if (text.Contains("authentication required") || text.Contains("407"))
            return "Прокси требует авторизацию (HTTP 407). Введите логин и пароль от вашего прокси и повторите проверку.";
        if (text.Contains("authentication failed") || text.Contains("auth failed") || text.Contains("username/password"))
            return "Прокси отклонил логин или пароль. Проверьте данные входа.";
        if (text.Contains("certificate") || text.Contains("x509"))
            return "Не удалось проверить TLS-сертификат прокси. Проверьте адрес и имя TLS-сервера.";
        if (text.Contains("refused") || text.Contains("actively refused") || text.Contains("отверг"))
            return "Сервер отклонил соединение. Проверьте адрес и порт прокси.";
        if (text.Contains("deadline") || text.Contains("timed out") || text.Contains("timeout"))
            return "Прокси не ответил вовремя. Проверьте адрес, порт и тип подключения." + (protocol == "HTTPS" ? " Если это HTTP CONNECT-прокси, выберите HTTP: через него также работают HTTPS-сайты." : "");
        return "Не удалось передать запрос через прокси. Проверьте тип подключения и данные входа; подробности — в журнале.";
    }
}
