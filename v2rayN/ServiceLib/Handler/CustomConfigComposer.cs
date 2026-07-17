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
    /// Для SerializeToNode при вливании фрагментов: JsonSerializerOptions.Default
    /// (используется, если не передать options явно) null-поля НЕ опускает — они
    /// проверялись эмпирически и попадают в вывод как явные "поле": null. Модели
    /// вроде RulesItem4Ray/Outbounds4Ray почти целиком состоят из необязательных
    /// полей, поэтому без этой настройки итоговый JSON пользователя зарастает
    /// шумом из пустых полей.
    /// </summary>
    private static readonly JsonSerializerOptions _mergeNodeOptions = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

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

            // По исходному rawJson, до вливания локальных правил — иначе последним
            // правилом окажется наше собственное, а не пользовательское.
            result.CatchAllDetected = HasCatchAllLastRule(rawJson, coreType);

            var tags = CollectTags(outbounds);
            var usedTags = CollectUsedOutboundTags(context);
            if (usedTags.Contains(Global.DirectTag))
            {
                EnsureUtilityOutbound(outbounds, tags, Global.DirectTag, coreType);
            }
            if (usedTags.Contains(Global.BlockTag))
            {
                EnsureUtilityOutbound(outbounds, tags, Global.BlockTag, coreType);
            }

            result.UnsupportedCustomTargets = coreType == ECoreType.sing_box
                ? MergeSingbox(root, outbounds, tags, mainProxyTag!, context)
                : MergeXray(root, outbounds, tags, mainProxyTag!, context);

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

    /// <summary>Теги всех выходов конфига — для проверки коллизий и переиспользования.</summary>
    private static HashSet<string> CollectTags(JsonArray outbounds)
    {
        var tags = new HashSet<string>(StringComparer.Ordinal);
        foreach (var o in outbounds)
        {
            var tag = o?["tag"]?.GetValue<string>();
            if (tag.IsNotEmpty())
            {
                tags.Add(tag);
            }
        }
        return tags;
    }

    /// <summary>Теги, реально используемые включёнными локальными правилами.</summary>
    private static HashSet<string> CollectUsedOutboundTags(CoreConfigContext context)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var rules = JsonUtils.Deserialize<List<RulesItem>>(context.RoutingItem?.RuleSet) ?? [];
        foreach (var item in rules)
        {
            if (item.Enabled && item.OutboundTag.IsNotEmpty())
            {
                result.Add(item.OutboundTag);
            }
        }
        return result;
    }

    /// <summary>
    /// Гарантирует наличие служебного выхода с данным тегом. Существующий тег
    /// переиспользуется — тег есть контракт между правилом и выходом.
    /// </summary>
    private static void EnsureUtilityOutbound(JsonArray outbounds, HashSet<string> tags, string tag, ECoreType coreType)
    {
        if (tags.Contains(tag))
        {
            return;
        }
        JsonObject node;
        if (coreType == ECoreType.sing_box)
        {
            // В sing-box выход block отсутствует как класс: блокировка задаётся
            // через action: "reject" в правиле, синтезировать тут нечего.
            if (tag != Global.DirectTag)
            {
                return;
            }
            node = new JsonObject { ["type"] = "direct", ["tag"] = tag };
        }
        else
        {
            node = new JsonObject
            {
                ["protocol"] = tag == Global.DirectTag ? "freedom" : "blackhole",
                ["tag"] = tag,
            };
        }
        outbounds.Add(node);
        tags.Add(tag);
    }

    /// <summary>Разводит сгенерированный тег с тегами, которые пользователь написал сам.</summary>
    private static string MakeUniqueTag(string tag, HashSet<string> tags)
    {
        if (!tags.Contains(tag))
        {
            return tag;
        }
        var i = 2;
        while (tags.Contains($"{tag}-{i}"))
        {
            i++;
        }
        return $"{tag}-{i}";
    }

    /// <summary>
    /// Вливает локальные правила (и то, что они притащили — выходы, балансеры,
    /// observatory) в xray-конфиг пользователя. Правила JSON остаются первыми,
    /// локальные дописываются следом; тег proxy подменяется на главный выход JSON.
    /// </summary>
    private static List<string> MergeXray(JsonNode root, JsonArray outbounds, HashSet<string> tags, string mainProxyTag, CoreConfigContext context)
    {
        var fragment = new CoreConfigV2rayService(context).BuildUserRoutingForCustom();
        if (fragment.Rules.Count == 0 && fragment.ExtraOutbounds.Count == 0)
        {
            return fragment.UnsupportedCustomTargets;
        }

        var renames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var ob in fragment.ExtraOutbounds)
        {
            var unique = MakeUniqueTag(ob.tag, tags);
            if (unique != ob.tag)
            {
                renames[ob.tag] = unique;
                ob.tag = unique;
            }
            tags.Add(ob.tag);
            var node = JsonUtils.SerializeToNode(ob, _mergeNodeOptions);
            if (node != null)
            {
                outbounds.Add(node);
            }
        }

        var routing = root["routing"] as JsonObject ?? new JsonObject();
        var rules = routing["rules"] as JsonArray ?? new JsonArray();
        foreach (var rule in fragment.Rules)
        {
            if (rule.outboundTag == Global.ProxyTag)
            {
                rule.outboundTag = mainProxyTag;
            }
            else if (rule.outboundTag.IsNotEmpty() && renames.TryGetValue(rule.outboundTag, out var renamed))
            {
                rule.outboundTag = renamed;
            }
            var node = JsonUtils.SerializeToNode(rule, _mergeNodeOptions);
            if (node != null)
            {
                rules.Add(node);
            }
        }
        routing["rules"] = rules;
        root["routing"] = routing;

        if (fragment.Balancers?.Count > 0)
        {
            var balancers = routing["balancers"] as JsonArray ?? new JsonArray();
            foreach (var b in fragment.Balancers)
            {
                var node = JsonUtils.SerializeToNode(b, _mergeNodeOptions);
                if (node != null)
                {
                    balancers.Add(node);
                }
            }
            routing["balancers"] = balancers;
        }
        if (fragment.Observatory != null && root["observatory"] is null)
        {
            root["observatory"] = JsonUtils.SerializeToNode(fragment.Observatory, _mergeNodeOptions);
        }
        if (fragment.BurstObservatory != null && root["burstObservatory"] is null)
        {
            root["burstObservatory"] = JsonUtils.SerializeToNode(fragment.BurstObservatory, _mergeNodeOptions);
        }
        return fragment.UnsupportedCustomTargets;
    }

    /// <summary>
    /// Вливает локальные правила в sing-box конфиг пользователя. Серверы, добавленные
    /// правилами, разводятся по outbounds/endpoints согласно рантайм-типу (Endpoints4Sbox
    /// либо обычный outbound) — endpoints и outbounds это разные секции конфига sing-box.
    /// </summary>
    private static List<string> MergeSingbox(JsonNode root, JsonArray outbounds, HashSet<string> tags, string mainProxyTag, CoreConfigContext context)
    {
        var fragment = new CoreConfigSingboxService(context).BuildUserRoutingForCustom();
        if (fragment.Rules.Count == 0 && fragment.ExtraServers.Count == 0)
        {
            return fragment.UnsupportedCustomTargets;
        }

        var renames = new Dictionary<string, string>(StringComparer.Ordinal);
        var endpoints = root["endpoints"] as JsonArray;
        foreach (var srv in fragment.ExtraServers)
        {
            var unique = MakeUniqueTag(srv.tag, tags);
            if (unique != srv.tag)
            {
                renames[srv.tag] = unique;
                srv.tag = unique;
            }
            tags.Add(srv.tag);
            var node = JsonUtils.SerializeToNode(srv, _mergeNodeOptions);
            if (node == null)
            {
                continue;
            }
            if (srv is Endpoints4Sbox)
            {
                endpoints ??= [];
                endpoints.Add(node);
            }
            else
            {
                outbounds.Add(node);
            }
        }
        if (endpoints != null)
        {
            root["endpoints"] = endpoints;
        }

        var route = root["route"] as JsonObject ?? new JsonObject();
        var rules = route["rules"] as JsonArray ?? new JsonArray();
        foreach (var rule in fragment.Rules)
        {
            if (rule.outbound == Global.ProxyTag)
            {
                rule.outbound = mainProxyTag;
            }
            else if (rule.outbound.IsNotEmpty() && renames.TryGetValue(rule.outbound, out var renamed))
            {
                rule.outbound = renamed;
            }
            var node = JsonUtils.SerializeToNode(rule, _mergeNodeOptions);
            if (node != null)
            {
                rules.Add(node);
            }
        }
        route["rules"] = rules;
        root["route"] = route;
        return fragment.UnsupportedCustomTargets;
    }

    private static readonly string[] _xrayNarrowingKeys =
        ["domain", "ip", "port", "sourcePort", "process", "inboundTag", "protocol", "user", "attrs"];

    private static readonly string[] _singboxNarrowingKeys =
        ["domain", "domain_suffix", "domain_keyword", "domain_regex", "geosite", "geoip", "ip_cidr",
         "source_ip_cidr", "port", "port_range", "source_port", "source_port_range", "process_name",
         "process_path", "inbound", "protocol", "rule_set", "clash_mode", "rules"];

    /// <summary>
    /// true, если последнее правило конфига не сужает трафик ничем — тогда всё, что
    /// дописано после него, недостижимо.
    /// </summary>
    public static bool HasCatchAllLastRule(string? rawJson, ECoreType coreType)
    {
        try
        {
            var root = JsonUtils.ParseJson(rawJson);
            var rules = coreType == ECoreType.sing_box
                ? root?["route"]?["rules"]?.AsArray()
                : root?["routing"]?["rules"]?.AsArray();
            if (rules is null || rules.Count == 0)
            {
                return false;
            }
            if (rules[^1] is not JsonObject last)
            {
                return false;
            }
            var keys = coreType == ECoreType.sing_box ? _singboxNarrowingKeys : _xrayNarrowingKeys;
            return !keys.Any(k => last.ContainsKey(k));
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            return false;
        }
    }
}
