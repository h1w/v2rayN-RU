namespace ServiceLib.Handler;

/// <summary>
/// Готовит конфиг для цепочечного ядра: берёт .json целевого профиля и заменяет его
/// inbounds одним socks-входом на выделенном порту. Всё остальное — outbounds, routing,
/// балансеры, dns — остаётся нетронутым, потому что решения внутри цели принимает
/// её собственное ядро.
///
/// Чистая функция: не выделяет порты, не пишет файлы, не трогает процессы.
/// </summary>
public static class ChainConfigBuilder
{
    private static readonly string _tag = "ChainConfigBuilder";
    private static readonly JsonSerializerOptions _writeOptions = new() { WriteIndented = true };

    /// <summary>
    /// Возвращает конфиг цепочечного ядра либо null, если целевой JSON непригоден.
    /// </summary>
    public static string? Build(string? rawJson, ECoreType coreType, int port)
    {
        try
        {
            var root = JsonUtils.ParseJson(rawJson);
            var outbounds = root?["outbounds"]?.AsArray();
            if (root is null || outbounds is null || outbounds.Count == 0)
            {
                Logging.SaveLog($"{_tag}: целевой JSON без пригодной секции outbounds, цепочка не собрана");
                return null;
            }

            // Собственные inbounds цели заменяем целиком: цепочка существует ровно для
            // приёма трафика от главного ядра, а её родные порты конфликтовали бы с ним.
            root["inbounds"] = new JsonArray(BuildSocksInbound(coreType, port));

            return root.ToJsonString(_writeOptions);
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            return null;
        }
    }

    private static JsonObject BuildSocksInbound(ECoreType coreType, int port)
    {
        if (coreType == ECoreType.sing_box)
        {
            return new JsonObject
            {
                ["type"] = "socks",
                ["tag"] = "chain-in",
                ["listen"] = Global.Loopback,
                ["listen_port"] = port,
            };
        }
        return new JsonObject
        {
            ["tag"] = "chain-in",
            ["listen"] = Global.Loopback,
            ["port"] = port,
            ["protocol"] = "socks",
            ["settings"] = new JsonObject { ["udp"] = true, ["auth"] = "noauth" },
        };
    }
}
