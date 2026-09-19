using ServiceLib.Models.Dto;

namespace ServiceLib.Handler;

/// <summary>
/// Готовит конфиг для цепочечного ядра: берёт .json целевого профиля и заменяет его
/// inbounds одним socks-входом на выделенном порту. При разрешённом редактировании
/// применяет сохранённый порядок и включение собственных JSON-правил без локальных
/// токенов. Остальные настройки — outbounds, балансеры, dns — остаются нетронутыми:
/// решения внутри цели принимает её собственное ядро.
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
    public static string? Build(string? rawJson, ECoreType coreType, int port,
        string? customRuleState = null, bool enableCustomRuleEditing = false,
        IEnumerable<string>? ownedConfigs = null)
    {
        try
        {
            // Apply once against source ordinals, before rewriting the managed inbound.
            // Local tokens are ignored by the ordinal/enabled helpers used by the composer.
            if (enableCustomRuleEditing)
            {
                var state = JsonUtils.Deserialize<List<CustomRuleStateItem>>(customRuleState);
                rawJson = CustomConfigComposer.ApplyCustomRuleState(rawJson, coreType, state);
            }
            var root = JsonUtils.ParseJson(rawJson);
            var outbounds = root?["outbounds"]?.AsArray();
            if (root is null || outbounds is null || outbounds.Count == 0)
            {
                Logging.SaveLog($"{_tag}: целевой JSON без пригодной секции outbounds, цепочка не собрана");
                return null;
            }

            // A single owned core keeps its APIs/cache/endpoints unchanged. Reject only
            // resource claims also present in another configuration in this launch.
            root["inbounds"] = new JsonArray(BuildSocksInbound(coreType, port));
            var resources = ResourceClaims(root);
            if (HasConflictingResources(root)
                || (ownedConfigs ?? []).Any(json => resources.Intersect(ResourceClaims(JsonUtils.ParseJson(json)), StringComparer.OrdinalIgnoreCase).Any()))
            {
                Logging.SaveLog($"{_tag}: refusing child JSON with conflicting owned resources");
                return null;
            }

            return root.ToJsonString(_writeOptions);
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            return null;
        }
    }

    public static bool AreLaunchResourcesCompatible(IEnumerable<string> configs)
    {
        try
        {
            var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var json in configs)
            {
                var root = JsonUtils.ParseJson(json);
                if (root == null) return false;
                foreach (var resource in ResourceClaims(root))
                    if (!claimed.Add(resource)) return false;
            }
            return true;
        }
        catch { return false; }
    }

    internal static bool HasConflictingResources(JsonNode root)
    {
        var claims = ResourceClaims(root);
        return claims.Count != claims.Distinct(StringComparer.OrdinalIgnoreCase).Count();
    }

    private static List<string> ResourceClaims(JsonNode? root)
    {
        var claims = new List<string>();
        if (root == null) return claims;
        foreach (var value in new[] { root["experimental"]?["clash_api"]?["external_controller"],
            root["experimental"]?["v2ray_api"]?["listen"], root["api"]?["listen"], root["metrics"]?["listen"] })
        {
            var address = value?.GetValue<string>();
            if (!string.IsNullOrEmpty(address))
            {
                // Conservatively reserve the TCP port across bind addresses, including wildcard binds.
                claims.Add("listen:" + address[(address.LastIndexOf(':') + 1)..]);
            }
        }
        if (root["experimental"]?["cache_file"]?["enabled"]?.GetValue<bool>() == true)
        {
            var path = root["experimental"]!["cache_file"]!["path"]?.GetValue<string>();
            claims.Add("cache:" + Path.GetFullPath(string.IsNullOrEmpty(path) ? "cache.db" : path, Utils.GetBinConfigPath()));
        }
        foreach (var endpoint in (root["endpoints"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (endpoint["name"]?.GetValue<string>() is { Length: > 0 } name)
                claims.Add("interface:" + name);
            if (endpoint["listen_port"]?.ToString() is { Length: > 0 } port)
                claims.Add("listen:" + port);
        }
        foreach (var inbound in (root["inbounds"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var port = inbound["listen_port"] ?? inbound["port"];
            if (port != null) claims.Add("listen:" + port.ToString());
        }
        return claims;
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
