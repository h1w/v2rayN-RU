using System.Text.Json.Nodes;

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

            JsonArray? ownRules = null;
            if (context.SharedRoutingPort != null)
            {
                var section = coreType == ECoreType.sing_box ? "route" : "routing";
                var state = context.AppConfig.UiItem.EnableCustomRuleEditing
                    ? JsonUtils.Deserialize<List<CustomRuleStateItem>>(context.Node.CustomRuleState) : null;
                var ownRoot = JsonUtils.ParseJson(ApplyCustomRuleState(rawJson, coreType, state))!;
                ownRules = ownRoot[section]?["rules"]?.DeepClone() as JsonArray ?? [];
                // Before anything is guarded: the inbounds these rules name are the ones
                // shared routing replaces, so their predicate is meaningless, not hostile.
                DropReplacedInboundTags(ownRules, root["inbounds"] as JsonArray, coreType);
                DropReplacedInboundTags(root[section]?["rules"] as JsonArray, root["inbounds"] as JsonArray, coreType);
                ValidateSharedRules(ownRules, coreType, HasSniffAction(ownRules));
                context = NormalizeSelfTargets(context);
            }
            var tags = CollectTags(outbounds);
            if (root["endpoints"] is JsonArray nativeEndpoints)
            {
                tags.UnionWith(CollectTags(nativeEndpoints));
            }
            var mainProxyTag = context.SharedRoutingPort != null
                ? MakeUniqueTag("v2rayn-own-out", tags)
                : ResolveMainProxyTag(rawJson, coreType);
            if (mainProxyTag.IsNullOrEmpty())
            {
                Logging.SaveLog($"{_tag}: в custom JSON не найден proxy-выход, слияние пропущено");
                return result;
            }

            if (context.SharedRoutingPort != null)
            {
                tags.Add(mainProxyTag!); // Reserve before merging generated local outbounds.
            }
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

            // Недосягаемость считаем по ФИНАЛЬНОМУ массиву правил (после сборки), а не
            // по исходному файлу: при чередовании (unified) порядок задаёт пользователь,
            // и «catch-all в конце файла» больше не означает, что локальные правила
            // недосягаемы. Правило недосягаемо, только если стоит ПОСЛЕ catch-all в
            // итоговом routing.rules / route.rules. Корректно и для append-, и для unified-пути.
            var finalRules = coreType == ECoreType.sing_box
                ? root["route"]?["rules"]?.AsArray()
                : root["routing"]?["rules"]?.AsArray();
            result.CatchAllDetected = HasRuleAfterCatchAll(finalRules, coreType);

            if (ownRules != null)
            {
                PrepareSharedRouting(root, coreType, context, ownRules, mainProxyTag!);
            }
            result.Json = root.ToJsonString(_writeOptions);
            return result;
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            result.Json = null;
            if (context.SharedRoutingPort != null)
            {
                result.Error = ex.Message;
            }
            return result;
        }
    }

    private static CoreConfigContext NormalizeSelfTargets(CoreConfigContext context)
    {
        var rules = JsonUtils.Deserialize<List<RulesItem>>(context.RoutingItem?.RuleSet) ?? [];
        foreach (var rule in rules)
        {
            if (!Global.OutboundTags.Contains(rule.OutboundTag)
                && (rule.OutboundTag == context.Node.Remarks
                    || context.AllProxiesMap.GetValueOrDefault($"remark:{rule.OutboundTag}")?.IndexId == context.Node.IndexId))
            {
                rule.OutboundTag = Global.ProxyTag;
            }
        }
        if (context.RoutingItem == null)
        {
            return context;
        }
        var routing = JsonUtils.DeepCopy(context.RoutingItem);
        routing.RuleSet = JsonUtils.Serialize(rules);
        return context with { RoutingItem = routing };
    }

    /// <summary>
    /// Отклоняет предикаты, которые описывают ИСХОДНОГО клиента: SOCKS-хоп подменяет
    /// его на loopback и собственный процесс приложения, поэтому такие условия молча
    /// перестали бы совпадать. `protocol` в этот список НЕ входит: он выводится
    /// сниффингом полезной нагрузки, а нагрузка через loopback-SOCKS идёт без изменений.
    /// Для sing-box сниффинг задаётся правилом `action: "sniff"`, поэтому `protocol`
    /// принимается только когда такое правило в конфиге действительно есть.
    /// </summary>
    private static void ValidateSharedRules(JsonArray rules, ECoreType coreType, bool sniffed)
    {
        // SOCKS creates a new connection: these predicates cannot be preserved.
        string[] forbidden = coreType == ECoreType.Xray
            ? ["inboundTag", "source", "sourceIP", "sourcePort", "localIP", "localPort", "process", "user", "vlessRoute", "attrs"]
            : ["inbound", "source_ip_cidr", "source_ip_is_private", "source_port", "source_port_range", "source_geoip",
               "process_name", "process_path", "process_path_regex", "auth_user", "user", "user_id", "package_name", "package_name_regex",
               "source_mac_address", "source_hostname", "client"];
        foreach (var rule in rules.OfType<JsonObject>())
        {
            var unsupported = forbidden.FirstOrDefault(rule.ContainsKey);
            if (unsupported != null)
            {
                throw new InvalidOperationException($"Shared native routing cannot preserve '{unsupported}' across SOCKS.");
            }
            if (coreType == ECoreType.sing_box)
            {
                if (!sniffed && rule.ContainsKey("protocol"))
                {
                    throw new InvalidOperationException(
                        "Shared native routing needs an 'action: sniff' rule to preserve 'protocol'; the replaced inbounds cannot sniff on their own.");
                }
                var action = rule["action"]?.GetValue<string>() ?? "route";
                if (action is not ("route" or "reject" or "hijack-dns" or "sniff" or "resolve"))
                {
                    throw new InvalidOperationException($"Shared native routing does not preserve nonterminal/context-dependent action '{action}'.");
                }
                if (rule["rules"] is JsonArray nested)
                {
                    ValidateSharedRules(nested, coreType, sniffed);
                }
            }
        }
    }

    /// <summary>
    /// true, если правила сами устанавливают сниффинг (`action: "sniff"`), включая
    /// вложенные logical-правила. Только тогда `protocol` у sing-box может совпасть
    /// после замены входов.
    /// </summary>
    private static bool HasSniffAction(JsonArray rules) =>
        rules.OfType<JsonObject>().Any(rule => rule["action"]?.GetValue<string>() == "sniff"
            || (rule["rules"] is JsonArray nested && HasSniffAction(nested)));

    /// <summary>
    /// Убирает inbound-предикат, если он целиком указывает на входы, которые общий
    /// нативный роутинг заменяет своими: такое условие в исходном конфиге означало
    /// «трафик из клиентских входов», то есть ровно то, что теперь приходит на
    /// управляемый вход. Предикат, ссылающийся хоть на один незнакомый тег, остаётся
    /// на месте и будет отклонён валидацией — молча менять его смысл нельзя.
    /// </summary>
    private static void DropReplacedInboundTags(JsonArray? rules, JsonArray? nativeInbounds, ECoreType coreType)
    {
        if (rules is null)
        {
            return;
        }
        var replaced = CollectTags(nativeInbounds ?? []);
        var key = coreType == ECoreType.Xray ? "inboundTag" : "inbound";
        foreach (var rule in rules.OfType<JsonObject>())
        {
            if (rule["rules"] is JsonArray nested)
            {
                DropReplacedInboundTags(nested, nativeInbounds, coreType);
            }
            if (rule[key] is not JsonArray tags || tags.Count == 0)
            {
                continue;
            }
            if (tags.All(t => t is not null && replaced.Contains(t.GetValue<string>())))
            {
                rule.Remove(key);
            }
        }
    }

    private static void PrepareSharedRouting(JsonNode root, ECoreType coreType, CoreConfigContext context,
        JsonArray ownRules, string loopTag)
    {
        const string frontTag = "v2rayn-front-in";
        const string ownTag = "v2rayn-own-in";
        var port = context.SharedRoutingPort!.Value;
        var frontPort = context.Node.PreSocksPort is > 0 and <= 65535
            ? context.Node.PreSocksPort.Value : context.AppConfig.Inbound.First().LocalPort;
        if (frontPort == port)
        {
            throw new InvalidOperationException("Front and own routing listeners require distinct ports.");
        }
        var inItem = context.AppConfig.Inbound.First();
        JsonObject Inbound(string tag, int listenPort, bool ownSide)
        {
            if (coreType == ECoreType.sing_box)
            {
                // sing-box ≥1.11 sniffs through route `action: "sniff"`, not on the inbound.
                return new JsonObject { ["type"] = "socks", ["tag"] = tag, ["listen"] = Global.Loopback, ["listen_port"] = listenPort };
            }
            // An empty list leaves Xray with no sniffer to run at all, so the own side
            // falls back to the same default a fresh inbound carries.
            var protocols = inItem.DestOverride is { Count: > 0 } configured ? configured
                : ownSide ? ["http", "tls"] : new List<string>();
            var destOverride = new JsonArray();
            foreach (var p in protocols)
            {
                destOverride.Add(p);
            }
            // Front mirrors the app inbound a regular profile would get. Own side always
            // sniffs so the JSON's own `protocol` rules keep matching, but routeOnly keeps
            // the address its outbounds receive exactly as the front passed it on.
            return new JsonObject
            {
                ["protocol"] = "socks", ["tag"] = tag, ["listen"] = Global.Loopback, ["port"] = listenPort,
                ["settings"] = new JsonObject { ["udp"] = true, ["auth"] = "noauth" },
                ["sniffing"] = new JsonObject
                {
                    ["enabled"] = ownSide || inItem.SniffingEnabled,
                    ["destOverride"] = destOverride,
                    ["routeOnly"] = ownSide || inItem.RouteOnly,
                },
            };
        }
        root["inbounds"] = new JsonArray(Inbound(frontTag, frontPort, false), Inbound(ownTag, port, true));
        if (ChainConfigBuilder.HasConflictingResources(root))
        {
            throw new InvalidOperationException("Shared native routing has conflicting listener or state resources.");
        }
        if (coreType == ECoreType.Xray && root["routing"]?["balancers"] is JsonArray balancers
            && balancers.OfType<JsonObject>().Any(b => b["selector"] is JsonArray selectors
                && selectors.Any(s => loopTag.StartsWith(s!.GetValue<string>(), StringComparison.Ordinal))))
        {
            throw new InvalidOperationException("Shared routing balancer selector includes the own-routing loopback outbound.");
        }
        var outbounds = root["outbounds"]!.AsArray();
        outbounds.Add(coreType == ECoreType.sing_box
            ? new JsonObject { ["type"] = "socks", ["tag"] = loopTag, ["server"] = Global.Loopback, ["server_port"] = port, ["version"] = "5" }
            : new JsonObject { ["protocol"] = "socks", ["tag"] = loopTag,
                ["settings"] = new JsonObject { ["servers"] = new JsonArray(new JsonObject { ["address"] = Global.Loopback, ["port"] = port }) } });
        var section = coreType == ECoreType.sing_box ? "route" : "routing";
        root[section] ??= new JsonObject();
        var frontRules = root[section]!["rules"] as JsonArray ?? [];
        var combined = new JsonArray();
        foreach (var rule in frontRules.OfType<JsonObject>())
        {
            combined.Add(GuardRule(rule, frontTag, coreType));
        }
        foreach (var rule in ownRules.OfType<JsonObject>())
        {
            combined.Add(GuardRule(rule, ownTag, coreType));
        }
        root[section]!["rules"] = combined;
        // No catch-all is added: preserving native default also preserves Xray's
        // IPIfNonMatch second pass, which a synthetic catch-all would suppress.
    }

    private static JsonObject GuardRule(JsonObject rule, string inbound, ECoreType coreType)
    {
        var clone = (JsonObject)rule.DeepClone();
        if (coreType == ECoreType.Xray)
        {
            if (clone.ContainsKey("inboundTag"))
            {
                throw new InvalidOperationException("Xray inboundTag intersection is unsupported in shared routing.");
            }
            clone["inboundTag"] = new JsonArray(inbound);
            return clone;
        }
        // Action fields belong on the outer logical rule, never on a predicate child.
        string[] actionFields = ["action", "outbound", "method", "no_drop", "override_address", "override_port",
            "network_strategy", "fallback_network_type", "fallback_delay", "udp_disable_domain_unmapping", "udp_connect",
            "udp_timeout", "tls_fragment", "tls_fragment_fallback_delay", "tls_record_fragment", "tls_spoof", "tls_spoof_method"];
        var guarded = new JsonObject { ["type"] = "logical", ["mode"] = "and" };
        foreach (var key in actionFields)
        {
            if (clone.Remove(key, out var value))
            {
                guarded[key] = value;
            }
        }
        if (clone.Count == 0)
        {
            // A rule that was nothing but an action (e.g. a bare `action: "sniff"`) has no
            // predicate left to intersect: a logical wrapper around an empty object is not
            // a valid rule, so gate it on the inbound directly.
            guarded.Remove("type");
            guarded.Remove("mode");
            guarded["inbound"] = new JsonArray(inbound);
            return guarded;
        }
        guarded["rules"] = new JsonArray(new JsonObject { ["inbound"] = new JsonArray(inbound) }, clone);
        return guarded;
    }

    /// <summary>Legacy composition only; runtime shared routing never uses the test-outbound parser.</summary>
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
        // Only generated local rules are remapped. Native rules/outbounds retain their tags.
        if (fragment.Rules.Any(r => r.outboundTag == Global.BlockTag))
        {
            var blackhole = outbounds.FirstOrDefault(o =>
                o?["tag"]?.GetValue<string>() == Global.BlockTag
                && o?["protocol"]?.GetValue<string>() == "blackhole");
            var blockTag = Global.BlockTag;
            if (blackhole == null)
            {
                blockTag = MakeUniqueTag(Global.BlockTag, tags);
                outbounds.Add(new JsonObject { ["protocol"] = "blackhole", ["tag"] = blockTag });
                tags.Add(blockTag);
            }
            foreach (var rule in fragment.Rules.Where(r => r.outboundTag == Global.BlockTag))
            {
                rule.outboundTag = blockTag;
            }
        }
        var unified = context.AppConfig.UiItem.EnableCustomRuleEditing;
        if (!unified && fragment.Rules.Count == 0 && fragment.ExtraOutbounds.Count == 0)
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

        // Editing-on: собираем единый порядок routing.rules по CustomRuleState
        // (JSON-ordinal и локальные токены вперемешку) — см. BuildUnifiedRules.
        // Editing-off: путь ниже (else) не тронут — JSON-правила остаются как
        // есть, локальные дописываются следом, байт-в-байт как раньше.
        if (unified)
        {
            var localNodes = new List<JsonNode>();
            var localIds = new List<string>();
            for (var k = 0; k < fragment.Rules.Count; k++)
            {
                var rule = fragment.Rules[k];
                if (rule.outboundTag == Global.ProxyTag)
                {
                    rule.outboundTag = mainProxyTag;
                }
                else if (rule.outboundTag.IsNotEmpty() && renames.TryGetValue(rule.outboundTag, out var renamed))
                {
                    rule.outboundTag = renamed;
                }
                var node = JsonUtils.SerializeToNode(rule, _mergeNodeOptions);
                if (node == null)
                {
                    continue;
                }
                localNodes.Add(node);
                localIds.Add(k < fragment.RuleSourceIds.Count ? fragment.RuleSourceIds[k] : string.Empty);
            }

            var tokens = JsonUtils.Deserialize<List<CustomRuleStateItem>>(context.Node.CustomRuleState) ?? [];
            routing["rules"] = BuildUnifiedRules(routing["rules"] as JsonArray, localNodes, localIds, tokens);
        }
        else
        {
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
        }
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
        var unified = context.AppConfig.UiItem.EnableCustomRuleEditing;
        if (!unified && fragment.Rules.Count == 0 && fragment.ExtraServers.Count == 0)
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

        // Как в MergeXray: editing-on собирает единый порядок через
        // BuildUnifiedRules, editing-off идёт по нетронутому старому пути.
        if (unified)
        {
            var localNodes = new List<JsonNode>();
            var localIds = new List<string>();
            for (var k = 0; k < fragment.Rules.Count; k++)
            {
                var rule = fragment.Rules[k];
                if (rule.outbound == Global.ProxyTag)
                {
                    rule.outbound = mainProxyTag;
                }
                else if (rule.outbound.IsNotEmpty() && renames.TryGetValue(rule.outbound, out var renamed))
                {
                    rule.outbound = renamed;
                }
                var node = JsonUtils.SerializeToNode(rule, _mergeNodeOptions);
                if (node == null)
                {
                    continue;
                }
                localNodes.Add(node);
                localIds.Add(k < fragment.RuleSourceIds.Count ? fragment.RuleSourceIds[k] : string.Empty);
            }

            var tokens = JsonUtils.Deserialize<List<CustomRuleStateItem>>(context.Node.CustomRuleState) ?? [];
            route["rules"] = BuildUnifiedRules(route["rules"] as JsonArray, localNodes, localIds, tokens);
        }
        else
        {
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
        }
        root["route"] = route;
        return fragment.UnsupportedCustomTargets;
    }

    /// <summary>
    /// Строит единый порядок routing/route.rules при EnableCustomRuleEditing==true:
    /// проходит токены CustomRuleState по порядку — JSON-токен (LocalId==null)
    /// эмиттит клон правила из файла по ordinal, если включён; локальный токен
    /// эмиттит все узлы, которые породило локальное правило с этим Id. Правила,
    /// на которые нет токена вовсе (новые локальные/JSON-правила, добавленные
    /// уже после последнего сохранения порядка), дописываются в конец: сначала
    /// непомянутые JSON-ordinal'ы (порядок файла), затем непомянутые локальные
    /// id (порядок фрагмента) — иначе они молча не попадали бы в core.
    /// Дубли одного и того же ordinal/Id в state берётся только первое
    /// вхождение — так же, как CustomRuleStateHelper.OrderedOrdinals.
    /// </summary>
    private static JsonArray BuildUnifiedRules(JsonArray? fileRules, List<JsonNode> localNodes, List<string> localIds, List<CustomRuleStateItem> tokens)
    {
        var jsonByOrdinal = new List<JsonNode>();
        if (fileRules != null)
        {
            foreach (var node in fileRules)
            {
                if (node is null)
                {
                    continue;
                }
                var clone = JsonNode.Parse(node.ToJsonString());
                if (clone != null)
                {
                    jsonByOrdinal.Add(clone);
                }
            }
        }

        var localById = new Dictionary<string, List<JsonNode>>(StringComparer.Ordinal);
        var localOrder = new List<string>();
        for (var k = 0; k < localNodes.Count; k++)
        {
            var id = k < localIds.Count ? localIds[k] : string.Empty;
            if (!localById.TryGetValue(id, out var list))
            {
                list = [];
                localById[id] = list;
                localOrder.Add(id);
            }
            list.Add(localNodes[k]);
        }

        var result = new JsonArray();
        var seenJsonOrdinals = new HashSet<int>();
        var seenLocalIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var token in tokens)
        {
            if (token.LocalId == null)
            {
                if (token.Index < 0 || token.Index >= jsonByOrdinal.Count || !seenJsonOrdinals.Add(token.Index))
                {
                    continue;
                }
                if (token.Enabled)
                {
                    result.Add(jsonByOrdinal[token.Index]);
                }
            }
            else
            {
                if (!seenLocalIds.Add(token.LocalId))
                {
                    continue;
                }
                if (localById.TryGetValue(token.LocalId, out var nodes))
                {
                    foreach (var n in nodes)
                    {
                        result.Add(n);
                    }
                }
            }
        }

        // Leftover-ы: не помянуты ни одним токеном вовсе (не путать с
        // выключенными JSON-токенами — те уже в seenJsonOrdinals и сюда не попадут).
        for (var i = 0; i < jsonByOrdinal.Count; i++)
        {
            if (seenJsonOrdinals.Contains(i))
            {
                continue;
            }
            result.Add(jsonByOrdinal[i]);
        }
        foreach (var id in localOrder)
        {
            if (seenLocalIds.Contains(id))
            {
                continue;
            }
            foreach (var n in localById[id])
            {
                result.Add(n);
            }
        }

        return result;
    }

    private static readonly string[] _xrayNarrowingKeys =
        ["domain", "ip", "source", "port", "sourcePort", "process", "inboundTag", "protocol", "user", "attrs"];

    private static readonly string[] _singboxNarrowingKeys =
        ["domain", "domain_suffix", "domain_keyword", "domain_regex", "geosite", "geoip", "ip_cidr", "ip_is_private",
         "source_ip_cidr", "source_ip_is_private", "port", "port_range", "source_port", "source_port_range",
         "process_name", "process_path", "inbound", "protocol", "rule_set", "clash_mode", "rules", "network_type",
         "network_is_expensive", "network_is_constrained", "wifi_ssid", "wifi_bssid"];

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

    /// <summary>
    /// true, если в ИТОГОВОМ массиве правил есть хоть одно правило ПОСЛЕ catch-all
    /// (правила, не сужающего трафик ни одним ключом) — такие правила недосягаемы.
    /// Считается по финальной сборке, поэтому верно и для append-, и для unified-порядка:
    /// «catch-all последним» само по себе недосягаемости не создаёт.
    /// </summary>
    private static bool HasRuleAfterCatchAll(JsonArray? rules, ECoreType coreType)
    {
        if (rules is null || rules.Count < 2)
        {
            return false;
        }
        var keys = coreType == ECoreType.sing_box ? _singboxNarrowingKeys : _xrayNarrowingKeys;
        for (var i = 0; i < rules.Count - 1; i++)
        {
            if (rules[i] is JsonObject rule && !keys.Any(k => rule.ContainsKey(k)))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Пересобирает массив правил custom JSON согласно сохранённому состоянию
    /// (порядок + вкл/выкл). Ordinal считается среди не-null правил — синхронно
    /// с CustomConfigParser.ParseDisplayRules. Пустой state / отсутствие правил /
    /// ошибка — возвращает исходный JSON без изменений.
    /// </summary>
    public static string ApplyCustomRuleState(string? rawJson, ECoreType coreType, List<CustomRuleStateItem>? state)
    {
        if (state is null || state.Count == 0)
        {
            return rawJson ?? string.Empty;
        }
        try
        {
            var root = JsonUtils.ParseJson(rawJson);
            var rules = coreType == ECoreType.sing_box
                ? root?["route"]?["rules"]?.AsArray()
                : root?["routing"]?["rules"]?.AsArray();
            if (root is null || rules is null || rules.Count == 0)
            {
                return rawJson ?? string.Empty;
            }

            // ordinal (позиция среди не-null правил) -> узел
            var byOrdinal = new List<JsonNode>();
            foreach (var node in rules)
            {
                if (node is not null)
                {
                    byOrdinal.Add(node);
                }
            }

            var newArr = new JsonArray();
            foreach (var ord in CustomRuleStateHelper.OrderedOrdinals(byOrdinal.Count, state))
            {
                if (!CustomRuleStateHelper.IsEnabled(ord, state))
                {
                    continue;
                }
                var clone = JsonNode.Parse(byOrdinal[ord].ToJsonString());
                if (clone is not null)
                {
                    newArr.Add(clone);
                }
            }

            if (coreType == ECoreType.sing_box)
            {
                root["route"]!["rules"] = newArr;
            }
            else
            {
                root["routing"]!["rules"] = newArr;
            }
            return root.ToJsonString(_writeOptions);
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ApplyCustomRuleState", ex);
            return rawJson ?? string.Empty;
        }
    }
}
