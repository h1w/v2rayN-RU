namespace ServiceLib.Services.Statistics;

public static class StatisticsSingboxService
{
    /// <summary>Only for an owned child with a single replaced SOCKS ingress and no own-routing loop.</summary>
    public static string EnableCounters(string json, int port)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        var experimental = root["experimental"] as JsonObject ?? new JsonObject();
        if (root["experimental"] == null) root["experimental"] = experimental;
        var api = experimental["clash_api"] as JsonObject ?? new JsonObject();
        if (experimental["clash_api"] == null) experimental["clash_api"] = api;
        api["external_controller"] = $"{Global.Loopback}:{port}";
        return root.ToJsonString();
    }

    /// <summary>
    /// Clash API lifetime totals include closed connections. The connections array is
    /// deliberately ignored: summing visible rows loses short-lived traffic and repeats bytes.
    /// Only valid for a single main native core without a same-process own-routing loop.
    /// </summary>
    public static IReadOnlyDictionary<string, TrafficCounter>? ParseCounters(string json)
    {
        var root = JsonNode.Parse(json);
        if (root?["uploadTotal"] is not JsonValue up || !up.TryGetValue<long>(out var upload)
            || root["downloadTotal"] is not JsonValue down || !down.TryGetValue<long>(out var download)
            || upload < 0 || download < 0) return null;
        return new Dictionary<string, TrafficCounter> { ["main"] = new(upload, download) };
    }
}
