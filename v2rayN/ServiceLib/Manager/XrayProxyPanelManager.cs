using System.Globalization;

namespace ServiceLib.Manager;

/// <summary>
/// Matches helper transport connections to exact endpoints in the final Xray JSON.
/// This cannot infer a user's logical outbound through multiplexing, chaining or DNS resolution.
/// </summary>
public sealed class XrayProxyPanelManager
{
    private static readonly Lazy<XrayProxyPanelManager> _instance = new(() => new());
    public static XrayProxyPanelManager Instance => _instance.Value;
    private static readonly string[] ByteUnits = ["B", "KiB", "MiB", "GiB", "TiB", "PiB", "EiB"];
    private static readonly string[] ServerFields = ["vnext", "servers"];
    private readonly object _gate = new();
    private readonly List<Outbound> _outbounds = [];
    private readonly Dictionary<Endpoint, List<int>> _endpoints = [];
    private Dictionary<ConnectionKey, Counter> _previous = [];
    private DateTimeOffset? _sampleTime;
    private Func<bool>? _isRunning;
    private bool _active;
    private long _version;

    public bool IsActive { get { lock (_gate) { CheckRunning(); return _active; } } }
    public long Version { get { lock (_gate) { CheckRunning(); return _version; } } }

    public void Reset()
    {
        lock (_gate) { ResetState(); }
    }

    private void ResetState()
    {
        _active = false;
        _isRunning = null;
        _outbounds.Clear();
        _endpoints.Clear();
        _previous.Clear();
        _sampleTime = null;
        _version++;
    }

    private void CheckRunning()
    {
        if (!_active || _isRunning == null) return;
        try
        {
            if (_isRunning()) return;
        }
        catch (InvalidOperationException) { }
        ResetState();
    }

    public void Activate(string json)
    {
        lock (_gate) { ActivateCore(json); }
    }

    internal void Activate(string json, Func<bool> isRunning, long expectedVersion)
    {
        lock (_gate)
        {
            if (_version != expectedVersion) return;
            ActivateCore(json);
            _isRunning = isRunning;
            CheckRunning();
        }
    }

    private void ActivateCore(string json)
    {
        ResetState();
        if (string.IsNullOrWhiteSpace(json)) return;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var root = document.RootElement;
            var outbounds = Property(root, "outbounds");
            if (outbounds.ValueKind != JsonValueKind.Array) return;
            var index = 0;
            foreach (var item in outbounds.EnumerateArray())
            {
                var tag = Text(item, "tag");
                var endpoints = new HashSet<Endpoint>();
                var settings = Property(item, "settings");
                AddEndpoint(item, endpoints);
                AddEndpoint(settings, endpoints);
                foreach (var field in ServerFields)
                {
                    var servers = Property(settings, field);
                    if (servers.ValueKind != JsonValueKind.Array) continue;
                    foreach (var server in servers.EnumerateArray()) AddEndpoint(server, endpoints);
                }
                var chain = Text(Property(item, "proxySettings"), "tag");
                var dialer = Text(Property(Property(item, "streamSettings"), "sockopt"), "dialerProxy");
                if (!string.IsNullOrEmpty(dialer)) chain = string.IsNullOrEmpty(chain) ? dialer : $"{chain} → {dialer}";
                var protocol = Text(item, "protocol") ?? "unknown";
                _outbounds.Add(new Outbound(new XrayProxyRow
                {
                    Key = $"outbound:{index}:{tag}",
                    Tag = string.IsNullOrWhiteSpace(tag) ? $"(outbound {index + 1})" : tag,
                    Protocol = protocol,
                    IsService = protocol is "freedom" or "blackhole" or "dns" ||
                        protocol == "socks" && endpoints.Count > 0 && endpoints.All(endpoint =>
                            endpoint.Host == "localhost" || IPAddress.TryParse(endpoint.Host, out var address) && IPAddress.IsLoopback(address)),
                    Endpoint = string.Join(", ", endpoints.Select(endpoint => endpoint.Display)),
                    Chain = chain ?? string.Empty
                }, tag, endpoints));
                foreach (var endpoint in endpoints)
                {
                    if (!_endpoints.TryGetValue(endpoint, out var owners)) _endpoints[endpoint] = owners = [];
                    owners.Add(index);
                }
                index++;
            }
            var balancers = Property(Property(root, "routing"), "balancers");
            if (balancers.ValueKind == JsonValueKind.Array)
            {
                index = 0;
                foreach (var balancer in balancers.EnumerateArray())
                {
                    index++;
                    var tag = Text(balancer, "tag") ?? $"(balancer {index})";
                    var selectors = Property(balancer, "selector");
                    var prefixes = selectors.ValueKind == JsonValueKind.Array
                        ? selectors.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String).Select(value => value.GetString()!).ToArray()
                        : [];
                    var fallback = Text(balancer, "fallbackTag");
                    foreach (var outbound in _outbounds.Where(outbound => !outbound.IsBalancer && outbound.RawTag != null))
                    {
                        if (prefixes.Any(prefix => outbound.RawTag!.StartsWith(prefix, StringComparison.Ordinal)) || outbound.RawTag == fallback)
                            outbound.Groups.Add(tag);
                    }
                    _outbounds.Add(new Outbound(new XrayProxyRow
                    {
                        Key = $"balancer:{index}:{tag}", IsBalancer = true,
                        Tag = tag, Protocol = "balancer", Chain = string.Join(", ", prefixes) + (fallback == null ? "" : $"; fallback: {fallback}")
                    }, tag, [], true));
                }
            }
            _active = true;
        }
        catch (JsonException) { ResetState(); }
    }

    public IReadOnlyList<string> MatchTags(ConnectionItem connection)
    {
        lock (_gate)
        {
            CheckRunning();
            return Match(connection).Select(index => _outbounds[index].Row.Tag).Distinct(StringComparer.Ordinal).ToArray();
        }
    }

    public string DescribeConnection(ConnectionItem connection)
    {
        lock (_gate)
        {
            CheckRunning();
            var matches = Match(connection);
            if (matches.Count == 0) return ResUI.XrayPanelUnmatchedConnection;
            var names = string.Join(" / ", matches.Select(index => _outbounds[index].Row.Tag));
            return string.Format(matches.Count == 1 ? ResUI.XrayPanelMatchedConnection : ResUI.XrayPanelAmbiguousConnection, names);
        }
    }

    public List<XrayProxyRow> GetRows(List<ConnectionItem>? connections, DateTimeOffset now, long? expectedVersion = null)
    {
        lock (_gate)
        {
            CheckRunning();
            if (!_active || (expectedVersion.HasValue && expectedVersion != _version)) return [];
            var totals = new Totals[_outbounds.Count];
            for (var i = 0; i < totals.Length; i++) totals[i] = new Totals();
            var current = new Dictionary<ConnectionKey, Counter>();
            var elapsed = _sampleTime.HasValue ? (now - _sampleTime.Value).TotalSeconds : 0;
            var hasBaseline = elapsed > 0;
            // The API normally returns unique IDs. A duplicated row must not inflate live totals.
            var seen = new HashSet<ConnectionKey>();
            foreach (var connection in connections ?? [])
            {
                if (connection == null) continue;
                var matches = Match(connection);
                var key = Key(connection);
                if (key.HasValue && !seen.Add(key.Value)) continue;
                if (matches.Count != 1)
                {
                    foreach (var index in matches) totals[index].Ambiguous++;
                    continue;
                }
                var owner = matches[0];
                var total = totals[owner];
                total.Count++;
                total.Upload += connection.upload;
                total.Download += connection.download;
                if (!key.HasValue) continue;
                var counter = new Counter(owner, connection.upload, connection.download);
                current[key.Value] = counter;
                if (hasBaseline && _previous.TryGetValue(key.Value, out var previous) && previous.Owner == owner)
                {
                    if (counter.Upload >= previous.Upload) total.UploadDelta += counter.Upload - previous.Upload;
                    if (counter.Download >= previous.Download) total.DownloadDelta += counter.Download - previous.Download;
                }
            }
            var rows = new List<XrayProxyRow>(_outbounds.Count);
            for (var i = 0; i < _outbounds.Count; i++)
            {
                var outbound = _outbounds[i];
                var total = totals[i];
                var ambiguous = total.Ambiguous > 0 || outbound.Endpoints.Any(endpoint => _endpoints[endpoint].Count > 1);
                var observable = outbound.Endpoints.Count > 0 && connections != null && !ambiguous;
                rows.Add(new XrayProxyRow
                {
                    Key = outbound.Row.Key,
                    Tag = outbound.Row.Tag,
                    Protocol = outbound.Row.Protocol,
                    Endpoint = outbound.Row.Endpoint,
                    Chain = outbound.Row.Chain,
                    IsBalancer = outbound.Row.IsBalancer,
                    IsService = outbound.Row.IsService,
                    Badge = connections == null ? ResUI.XrayProxyUnavailableBadge
                        : ambiguous ? ResUI.XrayProxyAmbiguousBadge
                        : !outbound.IsBalancer && outbound.Row.Chain.Length > 0 ? ResUI.XrayProxyChainBadge
                        : string.Empty,
                    Groups = string.Join(", ", outbound.Groups),
                    ConnectionCount = total.Count,
                    Upload = observable ? Bytes(total.Upload) : "—",
                    Download = observable ? Bytes(total.Download) : "—",
                    UploadRate = observable && hasBaseline ? Bytes(total.UploadDelta / (decimal)elapsed) + "/s" : "—",
                    DownloadRate = observable && hasBaseline ? Bytes(total.DownloadDelta / (decimal)elapsed) + "/s" : "—",
                    Status = connections == null ? ResUI.XrayPanelApiUnavailable
                        : outbound.IsBalancer ? ResUI.XrayPanelBalancerStatus
                        : ambiguous ? ResUI.XrayPanelAmbiguousStatus
                        : outbound.Endpoints.Count == 0 ? ResUI.XrayPanelNoEndpointStatus
                        : total.Count == 0 ? ResUI.XrayPanelNoMatchesStatus
                        : ResUI.XrayPanelMatchedStatus
                });
            }
            _previous = current;
            _sampleTime = connections == null ? null : now;
            return rows;
        }
    }

    private List<int> Match(ConnectionItem connection)
    {
        var matches = new List<int>();
        if (!_active || connection?.metadata == null || !TryPort(connection.metadata.destinationPort, out var port)) return matches;
        AddMatches(connection.metadata.destinationIP);
        AddMatches(connection.metadata.host);
        matches.Sort();
        return matches;

        void AddMatches(string? host)
        {
            var normalized = NormalizeHost(host);
            if (normalized == null || !_endpoints.TryGetValue(new Endpoint(normalized, port), out var owners)) return;
            foreach (var owner in owners) if (!matches.Contains(owner)) matches.Add(owner);
        }
    }

    private static ConnectionKey? Key(ConnectionItem connection)
    {
        if (string.IsNullOrWhiteSpace(connection.id) || connection.start == default) return null;
        var metadata = connection.metadata;
        return new ConnectionKey(connection.id, connection.start, metadata?.sourceIP, metadata?.sourcePort,
            metadata?.destinationIP, metadata?.host, metadata?.destinationPort, metadata?.network);
    }

    private static void AddEndpoint(JsonElement value, HashSet<Endpoint> endpoints)
    {
        var host = NormalizeHost(Text(value, "address") ?? Text(value, "server"));
        var port = Property(value, "port");
        if (host != null && TryPort(port.ValueKind is JsonValueKind.String or JsonValueKind.Number ? port.ToString() : null, out var number))
            endpoints.Add(new Endpoint(host, number));
    }

    private static JsonElement Property(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) ? property : default;
    private static string? Text(JsonElement value, string name) => Property(value, name) is var property && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    private static bool TryPort(string? text, out int port) => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is > 0 and <= 65535;

    private static string? NormalizeHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return null;
        host = host.Trim();
        if (host.StartsWith('[') && host.EndsWith(']')) host = host[1..^1];
        if (IPAddress.TryParse(host, out var address)) return address.ToString();
        host = host.TrimEnd('.');
        return Uri.CheckHostName(host) == UriHostNameType.Dns ? host.ToLowerInvariant() : null;
    }

    private static string Bytes(decimal value)
    {
        var unit = 0;
        while (value >= 1024 && unit < ByteUnits.Length - 1) { value /= 1024; unit++; }
        return value.ToString("0.##", CultureInfo.InvariantCulture) + " " + ByteUnits[unit];
    }

    private readonly record struct Endpoint(string Host, int Port)
    {
        public string Display => Host.Contains(':') ? $"[{Host}]:{Port}" : $"{Host}:{Port}";
    }
    private sealed class Outbound(XrayProxyRow row, string? rawTag, HashSet<Endpoint> endpoints, bool isBalancer = false)
    {
        public XrayProxyRow Row { get; } = row;
        public string? RawTag { get; } = rawTag;
        public HashSet<Endpoint> Endpoints { get; } = endpoints;
        public bool IsBalancer { get; } = isBalancer;
        public List<string> Groups { get; } = [];
    }
    private sealed class Totals
    {
        public int Count;
        public int Ambiguous;
        public decimal Upload;
        public decimal Download;
        public decimal UploadDelta;
        public decimal DownloadDelta;
    }
    private readonly record struct ConnectionKey(string Id, DateTime Start, string? Source, string? SourcePort, string? Destination, string? Host, string? Port, string? Network);
    private readonly record struct Counter(int Owner, ulong Upload, ulong Download);
}
