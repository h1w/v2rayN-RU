namespace ServiceLib.Models.Dto;

public enum ConnectionRouteKind
{
    Native,
    ProxyTransport,
    AmbiguousTransport,
    ForwardedToXray,
    Direct,
    Unknown
}

public enum ConnectionTrafficFilter
{
    All,
    User,
    ToProxies
}

public class ClashConnectionModel : ReactiveObject
{
    public string? Id { get; set; }
    [Reactive] public string? Network { get; set; }
    [Reactive] public string? Type { get; set; }
    [Reactive] public string? Host { get; set; }
    public ulong Upload { get; set; }
    public ulong Download { get; set; }
    public string? UploadTraffic { get; set; }
    public string? DownloadTraffic { get; set; }
    public double Time { get; set; }
    [Reactive] public string? Elapsed { get; set; }
    [Reactive] public string? Chain { get; set; }
    [Reactive] public string? RawRoute { get; set; }
    [Reactive] public string? ProcessPath { get; set; }
    [Reactive] public string ProcessName { get; set; } = "—";
    [Reactive] public bool HasProcessPath { get; set; }
    [Reactive] public ConnectionRouteKind RouteKind { get; set; }

    public static string GetProcessName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "—";
        // Metadata can describe a different OS than the desktop displaying it.
        var separator = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
        return separator == path.Length - 1 ? "—" : path[(separator + 1)..];
    }

    public bool MatchesTrafficFilter(ConnectionTrafficFilter filter) => filter switch
    {
        ConnectionTrafficFilter.User => RouteKind is ConnectionRouteKind.Native or ConnectionRouteKind.ForwardedToXray or ConnectionRouteKind.Direct,
        ConnectionTrafficFilter.ToProxies => RouteKind is ConnectionRouteKind.ProxyTransport or ConnectionRouteKind.AmbiguousTransport,
        _ => true
    };
}
