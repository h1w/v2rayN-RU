using System.Text.Json;
using System.Text.Json.Nodes;

namespace ServiceLib.Handler;

public static class CustomSpeedtestConfigBuilder
{
    private static readonly string _tag = "CustomSpeedtestConfigBuilder";
    private static readonly JsonSerializerOptions _writeOptions = new() { WriteIndented = true };

    public static string Build(string? rawJson, ECoreType coreType, IReadOnlyList<(OutboundTestTarget target, int port)> targets)
    {
        try
        {
            var root = JsonUtils.ParseJson(rawJson);
            if (root is null || targets.Count == 0)
            {
                return string.Empty;
            }
            return coreType == ECoreType.sing_box
                ? BuildSingbox(root, targets)
                : BuildXray(root, targets);
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            return string.Empty;
        }
    }

    private static string BuildXray(JsonNode root, IReadOnlyList<(OutboundTestTarget target, int port)> targets)
    {
        var inbounds = new JsonArray();
        var rules = new JsonArray();
        foreach (var (target, port) in targets)
        {
            var inTag = "in-" + target.Tag;
            inbounds.Add(new JsonObject
            {
                ["tag"] = inTag,
                ["listen"] = Global.Loopback,
                ["port"] = port,
                ["protocol"] = "socks",
                ["settings"] = new JsonObject { ["udp"] = true, ["auth"] = "noauth" },
            });
            rules.Add(new JsonObject
            {
                ["type"] = "field",
                ["inboundTag"] = new JsonArray(inTag),
                ["outboundTag"] = target.Tag,
            });
        }
        root["inbounds"] = inbounds;
        var routing = root["routing"] as JsonObject ?? new JsonObject();
        routing["rules"] = rules;
        root["routing"] = routing;
        return root.ToJsonString(_writeOptions);
    }

    private static string BuildSingbox(JsonNode root, IReadOnlyList<(OutboundTestTarget target, int port)> targets)
    {
        var inbounds = new JsonArray();
        var rules = new JsonArray();
        foreach (var (target, port) in targets)
        {
            var inTag = "in-" + target.Tag;
            inbounds.Add(new JsonObject
            {
                ["type"] = "socks",
                ["tag"] = inTag,
                ["listen"] = Global.Loopback,
                ["listen_port"] = port,
            });
            rules.Add(new JsonObject
            {
                ["inbound"] = new JsonArray(inTag),
                ["outbound"] = target.Tag,
            });
        }
        root["inbounds"] = inbounds;
        var route = root["route"] as JsonObject ?? new JsonObject();
        route["rules"] = rules;   // other route keys (final, default_domain_resolver, ...) preserved
        root["route"] = route;
        return root.ToJsonString(_writeOptions);
    }
}
