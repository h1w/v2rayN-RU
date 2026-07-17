namespace ServiceLib.Handler;

/// <summary>
/// Вливает локальные L7-правила в пользовательский custom JSON, который остаётся
/// основой конфига. Не выделяет порты, не пишет конфиг на диск и не запускает
/// процессы; единственный побочный эффект — служебное логирование через Logging.SaveLog.
/// </summary>
public static class CustomConfigComposer
{
    private static readonly string _tag = "CustomConfigComposer";
    private static readonly JsonSerializerOptions _writeOptions = new() { WriteIndented = true };

    /// <summary>
    /// Сливает локальные правила в custom JSON. Json == null — признак
    /// «фолбэк на дословное копирование».
    /// </summary>
    public static CustomComposeResult Compose(string? rawJson, ECoreType coreType, CoreConfigContext context)
    {
        var result = new CustomComposeResult();
        try
        {
            var root = JsonUtils.ParseJson(rawJson);
            var outbounds = root?["outbounds"]?.AsArray();
            if (root is null || outbounds is null || outbounds.Count == 0)
            {
                Logging.SaveLog($"{_tag}: custom JSON без пригодной секции outbounds, слияние пропущено");
                return result;
            }

            var mainProxyTag = ResolveMainProxyTag(rawJson, coreType);
            if (mainProxyTag.IsNullOrEmpty())
            {
                Logging.SaveLog($"{_tag}: в custom JSON не найден proxy-выход, слияние пропущено");
                return result;
            }

            result.Json = root.ToJsonString(_writeOptions);
            return result;
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            result.Json = null;
            return result;
        }
    }

    /// <summary>Главный proxy-выход JSON — первый непроходной выход, найденный существующим парсером.</summary>
    private static string? ResolveMainProxyTag(string? rawJson, ECoreType coreType)
    {
        var targets = CustomConfigParser.ParseTestableOutbounds(rawJson, coreType);
        return targets.OrderBy(t => t.Order).FirstOrDefault()?.Tag;
    }
}
