using System.Text.Json.Nodes;

namespace ServiceLib.Handler;

public static class CustomConfigParser
{
    private static readonly string _tag = "CustomConfigParser";

    public static List<RulesItem> ParseDisplayRules(string? json, ECoreType coreType)
    {
        var result = new List<RulesItem>();
        try
        {
            var root = JsonUtils.ParseJson(json);
            if (root is null)
            {
                return result;
            }
            return coreType == ECoreType.sing_box
                ? ParseSingboxDisplayRules(root)
                : ParseXrayDisplayRules(root);
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            return result;
        }
    }

    private static List<RulesItem> ParseXrayDisplayRules(JsonNode root)
    {
        var result = new List<RulesItem>();
        var rules = root["routing"]?["rules"]?.AsArray();
        if (rules is null)
        {
            return result;
        }
        foreach (var node in rules)
        {
            if (node is null)
            {
                continue;
            }
            result.Add(new RulesItem
            {
                Enabled = true,
                OutboundTag = node["outboundTag"]?.GetValue<string>() ?? node["balancerTag"]?.GetValue<string>(),
                Port = GetStringLoose(node["port"]),
                Network = GetStringLoose(node["network"]),
                InboundTag = ToStringList(node["inboundTag"]),
                Ip = ToStringList(node["ip"]),
                Domain = ToStringList(node["domain"]),
                Protocol = ToStringList(node["protocol"]),
                Process = ToStringList(node["process"]),
            });
        }
        return result;
    }

    private static List<RulesItem> ParseSingboxDisplayRules(JsonNode root)
    {
        // Implemented in Task 3.
        return new List<RulesItem>();
    }

    private static List<string>? ToStringList(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }
        if (node is JsonArray arr)
        {
            var list = new List<string>();
            foreach (var it in arr)
            {
                var s = GetStringLoose(it);
                if (s.IsNotEmpty())
                {
                    list.Add(s);
                }
            }
            return list.Count > 0 ? list : null;
        }
        var single = GetStringLoose(node);
        return single.IsNotEmpty() ? new List<string> { single } : null;
    }

    // port can be a JSON string ("443", "0-65535") or a number; normalize to string.
    private static string GetStringLoose(JsonNode? node)
    {
        if (node is null)
        {
            return string.Empty;
        }
        try
        {
            if (node is JsonValue val)
            {
                if (val.TryGetValue<string>(out var s))
                {
                    return s;
                }
                if (val.TryGetValue<long>(out var l))
                {
                    return l.ToString();
                }
                if (val.TryGetValue<double>(out var d))
                {
                    return d.ToString();
                }
                if (val.TryGetValue<bool>(out var b))
                {
                    return b.ToString().ToLowerInvariant();
                }
            }
            return node.ToString();
        }
        catch
        {
            return string.Empty;
        }
    }
}
