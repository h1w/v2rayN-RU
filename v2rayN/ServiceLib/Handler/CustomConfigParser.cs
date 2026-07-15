using System.Text.Json.Nodes;

namespace ServiceLib.Handler;

public static class CustomConfigParser
{
    private static readonly string _tag = "CustomConfigParser";

    private static readonly HashSet<string> _xrayUtility =
        new(StringComparer.OrdinalIgnoreCase) { "freedom", "blackhole", "dns", "loopback" };

    private static readonly HashSet<string> _singboxNonProxy =
        new(StringComparer.OrdinalIgnoreCase) { "direct", "block", "dns", "selector", "urltest" };

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

    public static List<OutboundTestTarget> ParseTestableOutbounds(string? json, ECoreType coreType)
    {
        var result = new List<OutboundTestTarget>();
        try
        {
            var root = JsonUtils.ParseJson(json);
            var outbounds = root?["outbounds"]?.AsArray();
            if (outbounds is null)
            {
                return result;
            }
            var order = 0;
            foreach (var ob in outbounds)
            {
                if (ob is null)
                {
                    continue;
                }
                var tag = ob["tag"]?.GetValue<string>();
                if (tag.IsNullOrEmpty())
                {
                    continue;
                }
                var kind = coreType == ECoreType.sing_box
                    ? ob["type"]?.GetValue<string>()
                    : ob["protocol"]?.GetValue<string>();
                if (kind.IsNullOrEmpty() || !IsTestableKind(kind, coreType))
                {
                    continue;
                }
                result.Add(new OutboundTestTarget(tag, order++, Array.Empty<string>()));
            }
            return result;
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
        var result = new List<RulesItem>();
        var rules = root["route"]?["rules"]?.AsArray();
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

            var item = new RulesItem { Enabled = true };

            var action = node["action"]?.GetValue<string>();
            var outbound = node["outbound"]?.GetValue<string>();
            item.OutboundTag = action == "reject" ? "block" : outbound;

            // ports: sing-box uses number array `port` and string array `port_range` ("1000:2000")
            var ports = new List<string>();
            if (node["port"] is JsonArray portArr)
            {
                foreach (var p in portArr)
                {
                    var s = GetStringLoose(p);
                    if (s.IsNotEmpty())
                    {
                        ports.Add(s);
                    }
                }
            }
            if (node["port_range"] is JsonArray rangeArr)
            {
                foreach (var p in rangeArr)
                {
                    var s = GetStringLoose(p)?.Replace(":", "-");
                    if (s.IsNotEmpty())
                    {
                        ports.Add(s);
                    }
                }
            }
            item.Port = ports.Count > 0 ? string.Join(",", ports) : null;

            item.Network = JoinArray(node["network"]);
            item.InboundTag = ToStringList(node["inbound"]);
            item.Protocol = ToStringList(node["protocol"]);

            // domains: merge the sing-box domain families back into prefixed strings
            var domains = new List<string>();
            AddPrefixed(domains, node["domain"], "full:");
            AddPrefixed(domains, node["domain_suffix"], "domain:");
            AddPrefixed(domains, node["domain_keyword"], "keyword:");
            AddPrefixed(domains, node["domain_regex"], "regexp:");
            AddPrefixed(domains, node["geosite"], "geosite:");
            item.Domain = domains.Count > 0 ? domains : null;

            // ips: geoip + ip_cidr + ip_is_private
            var ips = new List<string>();
            AddPrefixed(ips, node["geoip"], "geoip:");
            AddPrefixed(ips, node["ip_cidr"], "");
            if (node["ip_is_private"]?.GetValue<bool>() == true)
            {
                ips.Add("geoip:private");
            }
            item.Ip = ips.Count > 0 ? ips : null;

            // processes
            var procs = new List<string>();
            AddPrefixed(procs, node["process_name"], "");
            AddPrefixed(procs, node["process_path"], "");
            item.Process = procs.Count > 0 ? procs : null;

            result.Add(item);
        }
        return result;
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

    private static string? JoinArray(JsonNode? node)
    {
        var list = ToStringList(node);
        return list?.Count > 0 ? string.Join(",", list) : null;
    }

    private static void AddPrefixed(List<string> target, JsonNode? node, string prefix)
    {
        var list = ToStringList(node);
        if (list is null)
        {
            return;
        }
        foreach (var s in list)
        {
            target.Add(prefix + s);
        }
    }

    private static bool IsTestableKind(string kind, ECoreType coreType)
    {
        return coreType == ECoreType.sing_box
            ? !_singboxNonProxy.Contains(kind)
            : !_xrayUtility.Contains(kind);
    }
}
