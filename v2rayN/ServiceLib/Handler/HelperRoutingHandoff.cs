namespace ServiceLib.Handler;

/// <summary>
/// Keeps sing-box's rule selection while moving the selected transport into the owned
/// Xray process. Each SOCKS ingress has an unconditional, first-priority route to the
/// already generated target; forwarded sockets never run through user rules again.
/// </summary>
public static class HelperRoutingHandoff
{
    public sealed record Result(string MainJson, CoreConfigContext HelperContext);

    public static Result Prepare(CoreConfigContext main, CoreConfigContext helper, string mainJson,
        Func<int>? allocatePort = null)
    {
        if (main.RunCoreType != ECoreType.Xray || helper.RunCoreType != ECoreType.sing_box)
            return new(mainJson, helper);
        if (main.SharedRoutingPort != null)
            return PrepareShared(main, helper, mainJson, allocatePort);
        if (helper.RoutingItem == null)
            return new(mainJson, helper);

        var targets = (JsonUtils.Deserialize<List<RulesItem>>(helper.RoutingItem.RuleSet) ?? [])
            .Where(r => r.Enabled && r.RuleType != ERuleType.DNS && !string.IsNullOrEmpty(r.OutboundTag)
                && !Global.OutboundTags.Contains(r.OutboundTag))
            .Select(r => r.OutboundTag!).Distinct(StringComparer.Ordinal).ToArray();
        if (targets.Length == 0) return new(mainJson, helper);

        var root = JsonNode.Parse(mainJson)!.AsObject();
        var inbounds = root["inbounds"]!.AsArray();
        var routing = root["routing"]!.AsObject();
        var rules = routing["rules"]!.AsArray();
        var outboundTags = root["outbounds"]!.AsArray().OfType<JsonObject>()
            .Select(o => o["tag"]?.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        var balancerTags = (routing["balancers"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(b => b["tag"]?.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        var inboundTags = inbounds.OfType<JsonObject>().Select(i => i["tag"]?.GetValue<string>())
            .ToHashSet(StringComparer.Ordinal);
        var handoffPorts = new Dictionary<string, int>(StringComparer.Ordinal);
        var portsByTarget = new Dictionary<string, int>(StringComparer.Ordinal);
        var reservedPorts = main.ChainCores.Select(c => c.Port).ToHashSet();
        reservedPorts.UnionWith(main.AppConfig.Inbound.Select(i => i.LocalPort));
        reservedPorts.Add(AppManager.Instance.StatePort);
        reservedPorts.Add(AppManager.Instance.StatePort2);
        if (main.Node.PreSocksPort is { } prePort) reservedPorts.Add(prePort);
        if (main.ManagedIngressPort is { } managedPort) reservedPorts.Add(managedPort);
        var nextRule = 0;
        foreach (var target in targets)
        {
            if (!main.RoutingOutboundTags.TryGetValue(target, out var tag))
                throw new InvalidOperationException($"Cannot hand off routing target '{target}' to the main core.");
            var balancer = balancerTags.Contains(tag + Global.BalancerTagSuffix);
            if (!balancer && !outboundTags.Contains(tag))
                throw new InvalidOperationException($"Main routing target '{target}' has no generated outbound.");
            if (!portsByTarget.TryGetValue(tag, out var port))
            {
                port = allocatePort != null ? allocatePort() : Utils.GetFreePort();
                if (port is <= 0 or > 65535 or 59090 || !reservedPorts.Add(port))
                    throw new InvalidOperationException("Unable to allocate a distinct routing handoff SOCKS port.");
                var inboundTag = $"v2rayn-handoff-{port}";
                if (!inboundTags.Add(inboundTag))
                    throw new InvalidOperationException("Routing handoff inbound tag is already in use.");
                inbounds.Add(new JsonObject
                {
                    ["tag"] = inboundTag,
                    ["listen"] = Global.Loopback,
                    ["port"] = port,
                    ["protocol"] = "socks",
                    ["settings"] = new JsonObject { ["udp"] = true, ["auth"] = "noauth" },
                });
                rules.Insert(nextRule++, new JsonObject
                {
                    ["type"] = "field",
                    ["inboundTag"] = new JsonArray(JsonValue.Create(inboundTag)),
                    [balancer ? "balancerTag" : "outboundTag"] = balancer ? tag + Global.BalancerTagSuffix : tag,
                });
                portsByTarget.Add(tag, port);
            }
            handoffPorts.Add(target, port);
        }
        var json = root.ToJsonString();
        if (!ChainConfigBuilder.AreLaunchResourcesCompatible([json]))
            throw new InvalidOperationException("Routing handoff conflicts with a main-core listener.");
        return new(json, helper with { RoutingOutboundPorts = handoffPorts });
    }

    // Classify only process identity in sing-box. Every destination decision remains in
    // Xray, including native conjunctions, extensions, sniffing and IPIfNonMatch passes.
    private static Result PrepareShared(CoreConfigContext main, CoreConfigContext helper, string mainJson,
        Func<int>? allocatePort)
    {
        var frontRules = main.SharedProcessRoutingRules;
        if (frontRules.Count == 0) return new(mainJson, helper);
        var root = JsonNode.Parse(mainJson)!.AsObject();
        var inbounds = root["inbounds"]!.AsArray();
        var front = inbounds.OfType<JsonObject>().Single(i => i["tag"]?.GetValue<string>() == "v2rayn-front-in");
        var rules = root["routing"]!["rules"]!.AsArray();
        var processRules = frontRules.Where(r => r["process"] is JsonArray { Count: > 0 }).ToArray();
        var names = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        var paths = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        for (var index = 0; index < processRules.Length; index++)
        {
            foreach (var process in processRules[index]["process"]!.AsArray())
            {
                var (value, isPath) = NormalizeProcess(process!.GetValue<string>());
                var matches = isPath ? paths : names;
                if (!matches.TryGetValue(value, out var members)) matches[value] = members = [];
                members.Add(index);
            }
        }

        var reservedPorts = main.ChainCores.Select(c => c.Port).ToHashSet();
        reservedPorts.UnionWith(main.AppConfig.Inbound.Select(i => i.LocalPort));
        reservedPorts.UnionWith(inbounds.OfType<JsonObject>().Select(i => i["port"]?.GetValue<int>() ?? 0));
        reservedPorts.Add(main.SharedRoutingPort!.Value);
        reservedPorts.Add(AppManager.Instance.StatePort);
        reservedPorts.Add(AppManager.Instance.StatePort2);
        if (main.Node.PreSocksPort is { } prePort) reservedPorts.Add(prePort);
        if (main.ManagedIngressPort is { } managedPort) reservedPorts.Add(managedPort);
        var helperRules = new List<Rule4Sbox>();
        var helperOutbounds = new List<Outbound4Sbox>();
        var branches = new Dictionary<string, string>(StringComparer.Ordinal);
        var inboundTags = inbounds.OfType<JsonObject>().Select(i => i["tag"]?.GetValue<string>()).ToHashSet(StringComparer.Ordinal);

        // A path and a process name are separate OS properties (e.g. Linux comm can
        // be changed). Never infer a name from the basename. Exact intersections go
        // before path-only and name-only fallbacks; equal membership shares ingress.
        foreach (var (path, pathMembers) in paths)
        {
            foreach (var (name, nameMembers) in names)
            {
                if (nameMembers.IsSubsetOf(pathMembers)) continue;
                var members = new HashSet<int>(pathMembers);
                members.UnionWith(nameMembers);
                AddRule(new Rule4Sbox
                {
                    type = "logical", mode = "and",
                    rules = [new() { process_path = [path] }, new() { process_name = [name] }],
                }, members);
            }
            AddRule(new Rule4Sbox { process_path = [path] }, pathMembers);
        }
        foreach (var (name, members) in names)
            AddRule(new Rule4Sbox { process_name = [name] }, members);

        var json = root.ToJsonString();
        if (!ChainConfigBuilder.AreLaunchResourcesCompatible([json]))
            throw new InvalidOperationException("Shared process routing conflicts with a main-core listener.");
        return new(json, helper with
        {
            RoutingItem = null,
            SharedProcessRules = helperRules,
            SharedProcessOutbounds = helperOutbounds,
        });

        void AddRule(Rule4Sbox match, HashSet<int> members)
        {
            var key = string.Join(",", members.Order());
            if (!branches.TryGetValue(key, out var tag))
            {
                var port = allocatePort != null ? allocatePort() : Utils.GetFreePort();
                if (port is <= 0 or > 65535 or 59090 || !reservedPorts.Add(port))
                    throw new InvalidOperationException("Unable to allocate a distinct shared process SOCKS port.");
                tag = $"v2rayn-handoff-{port}";
                if (!inboundTags.Add(tag))
                    throw new InvalidOperationException("Shared process inbound tag is already in use.");
                var inbound = (JsonObject)front.DeepClone();
                inbound["tag"] = tag;
                inbound["port"] = port;
                inbounds.Add(inbound);
                var processIndex = 0;
                foreach (var rule in frontRules)
                {
                    if (rule["process"] is JsonArray { Count: > 0 } && !members.Contains(processIndex++)) continue;
                    var branchRule = (JsonObject)rule.DeepClone();
                    branchRule.Remove("process");
                    branchRule["inboundTag"] = new JsonArray(tag);
                    rules.Add(branchRule);
                }
                helperOutbounds.Add(new Outbound4Sbox
                {
                    type = "socks", tag = tag, server = Global.Loopback, server_port = port, version = "5",
                });
                branches.Add(key, tag);
            }
            match.outbound = tag;
            helperRules.Add(match);
        }
    }

    internal static (string Value, bool IsPath) NormalizeProcess(string process)
    {
        if (process is "self/" or "xray/") return (Utils.GetExeName("sing-box"), false);
        if (process.Contains('/') || process.Contains('\\'))
            return (Utils.IsWindows() ? process.Replace('/', '\\') : process, true);
        return (Utils.GetExeName(process), false);
    }
}
