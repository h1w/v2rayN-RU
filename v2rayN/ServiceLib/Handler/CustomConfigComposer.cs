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
}
