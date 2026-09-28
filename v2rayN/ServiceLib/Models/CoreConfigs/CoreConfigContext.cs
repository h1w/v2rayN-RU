namespace ServiceLib.Models.CoreConfigs;

public record CoreConfigContext
{
    public required ProfileItem Node { get; init; }
    /// <summary>Original profile identity, before resolving virtual chains or group nodes.</summary>
    public string? StatisticsProfileId { get; init; }
    public required ECoreType RunCoreType { get; init; }
    public RoutingItem? RoutingItem { get; init; }
    public DNSItem? RawDnsItem { get; init; }
    public SimpleDNSItem SimpleDnsItem { get; init; } = new();
    public Dictionary<string, string> CustomOutboundContent { get; init; } = new();
    public Dictionary<string, ProfileItem> AllProxiesMap { get; init; } = new();
    /// <summary>Rule lookup key to the original profile, before group/chain resolution.</summary>
    public Dictionary<string, string> RoutingProfileIds { get; init; } = new(StringComparer.Ordinal);
    /// <summary>Exact generated entry outbound tag to persisted profile identity. Never inferred from a tag.</summary>
    public Dictionary<string, string> StatisticsOutboundProfiles { get; init; } = new(StringComparer.Ordinal);
    /// <summary>Resolved named rule target to its Xray outbound or balancer selector base.</summary>
    public Dictionary<string, string> RoutingOutboundTags { get; init; } = new(StringComparer.Ordinal);
    /// <summary>Launch-owned SOCKS handoffs used only by the sing-box routing helper.</summary>
    public Dictionary<string, int> RoutingOutboundPorts { get; init; } = new(StringComparer.Ordinal);
    /// <summary>Process-only classification for shared Xray routing; destination predicates stay in Xray.</summary>
    public List<Rule4Sbox> SharedProcessRules { get; init; } = [];
    public List<Outbound4Sbox> SharedProcessOutbounds { get; init; } = [];
    /// <summary>Final ordered shared front rules before removing helper-only process predicates.</summary>
    public List<JsonObject> SharedProcessRoutingRules { get; init; } = [];
    /// <summary>Process predicates are evaluated by the helper, not on forwarded Xray sockets.</summary>
    public bool IsProcessRoutingDelegated { get; init; }
    /// <summary>
    /// Цепочечные ядра, которые надо поднять для правил, указывающих на .json-профили.
    /// Наполняется CoreConfigContextBuilder, исполняется CoreManager.
    /// </summary>
    public List<ChainCoreDescriptor> ChainCores { get; init; } = [];
    /// <summary>Own-only SOCKS ingress in the active native process, never a child process.</summary>
    public int? SharedRoutingPort { get; set; }
    /// <summary>Resolved front ingress; private when a pre-core owns the public listener.</summary>
    public int? ManagedIngressPort { get; set; }
    public Config AppConfig { get; init; } = new();
    public FullConfigTemplateItem? FullConfigTemplate { get; init; } = new();

    // Test ServerTestItem Map
    public Dictionary<string, string> ServerTestItemMap { get; init; } = new();

    // Generation Context
    public Dictionary<object, string> CustomOutboundMap { get; init; } = new();

    // TUN Compatibility
    public bool IsTunEnabled { get; init; } = false;
    public HashSet<string> ProtectDomainList { get; init; } = [];
    /// <summary>Exact direct transport destinations; never a port-wide or host-wide bypass.</summary>
    public HashSet<TransportEndpoint> ProtectTransportEndpoints { get; init; } = [];
    // Typically, it is the core of the outbound chain
    public HashSet<ECoreType> ProtectCoreTypeList { get; init; } = [];

    public bool IsWindows { get; init; }
    public bool IsMacOS { get; init; }

    // Defaults to true so that a context built without this flag keeps routing IPv6 into the
    // tunnel; only a positive detection of the host having no global IPv6 address turns it off.
    public bool HasGlobalIPv6Address { get; init; } = true;
}

public sealed record TransportEndpoint(string Address, int Port);
