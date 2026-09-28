namespace ServiceLib.Services.Statistics;

/// <summary>Reads distinct ingress or routed outbound counters, without counting transport hops twice.</summary>
public static class StatisticsXrayService
{
    public static string EnableCounters(string json, int port)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        root["stats"] ??= new JsonObject();
        var metrics = root["metrics"] as JsonObject ?? new JsonObject();
        if (root["metrics"] == null) root["metrics"] = metrics;
        metrics["listen"] = $"{Global.Loopback}:{port}";
        var policy = root["policy"] as JsonObject ?? new JsonObject();
        if (root["policy"] == null) root["policy"] = policy;
        var system = policy["system"] as JsonObject ?? new JsonObject();
        if (policy["system"] == null) policy["system"] = system;
        system["statsInboundUplink"] = true;
        system["statsInboundDownlink"] = true;
        system["statsOutboundUplink"] = true;
        system["statsOutboundDownlink"] = true;
        if (root["inbounds"] is JsonArray inbounds)
        {
            var tags = inbounds.OfType<JsonObject>().Select(i => i["tag"]?.GetValue<string>()).ToHashSet();
            var index = 0;
            foreach (var inbound in inbounds.OfType<JsonObject>())
            {
                if (!string.IsNullOrEmpty(inbound["tag"]?.GetValue<string>())) continue;
                string tag;
                do { tag = $"v2rayn-stat-in-{index++}"; } while (!tags.Add(tag));
                inbound["tag"] = tag;
            }
        }
        return root.ToJsonString();
    }

    public static HashSet<string> GetIngressTags(string json)
    {
        var root = JsonNode.Parse(json)!;
        var apiTag = root["api"]?["tag"]?.GetValue<string>();
        var apiInbounds = new HashSet<string>(StringComparer.Ordinal);
        if (apiTag != null && root["routing"]?["rules"] is JsonArray rules)
        {
            foreach (var rule in rules.OfType<JsonObject>())
            {
                if (rule["outboundTag"]?.GetValue<string>() == apiTag && rule["inboundTag"] is JsonArray apiTags)
                    foreach (var tag in apiTags) if (tag?.GetValue<string>() is { } value) apiInbounds.Add(value);
            }
        }
        var tags = new HashSet<string>(StringComparer.Ordinal);
        if (root["inbounds"] is not JsonArray inbounds) return tags;
        foreach (var inbound in inbounds.OfType<JsonObject>())
        {
            var tag = inbound["tag"]?.GetValue<string>();
            if (string.IsNullOrEmpty(tag) || tag == apiTag || apiInbounds.Contains(tag) || tag == "v2rayn-own-in") continue;
            tags.Add(tag);
        }
        return tags;
    }

    public static IReadOnlyDictionary<string, TrafficCounter>? ParseCounters(string json, ISet<string> tags,
        bool outbound = false)
    {
        var root = JsonNode.Parse(json);
        if (root?["stats"]?[outbound ? "outbound" : "inbound"] is not JsonObject inbounds || tags.Count == 0) return null;
        var result = new Dictionary<string, TrafficCounter>(StringComparer.Ordinal);
        foreach (var tag in tags)
        {
            // A counter may not exist until its inbound/outbound has carried traffic.
            if (inbounds[tag] is not JsonObject inbound) continue;
            if (inbound["uplink"] is not JsonValue up || !up.TryGetValue<long>(out var upload)
                || inbound["downlink"] is not JsonValue down || !down.TryGetValue<long>(out var download)
                || upload < 0 || download < 0) return null;
            result[tag] = new(upload, download);
        }
        return result;
    }
}
