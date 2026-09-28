namespace ServiceLib.Models.Dto;

/// <summary>Inventory and helper-observed traffic for a running Xray outbound, not lifetime or per-user accounting.</summary>
public sealed class XrayProxyRow : ReactiveObject
{
    public string Key { get; init; } = string.Empty;
    public string Tag { get; init; } = string.Empty;
    public string Protocol { get; init; } = string.Empty;
    public string Endpoint { get; init; } = string.Empty;
    public string Groups { get; init; } = string.Empty;
    public string Chain { get; init; } = string.Empty;
    public bool IsService { get; init; }
    public bool IsBalancer { get; init; }
    public string DisplayProtocol => IsBalancer ? ResUI.XrayProxyBalancer : Protocol;

    [Reactive] public int ConnectionCount { get; set; }
    [Reactive] public string Upload { get; set; } = "—";
    [Reactive] public string Download { get; set; } = "—";
    [Reactive] public string UploadRate { get; set; } = "—";
    [Reactive] public string DownloadRate { get; set; } = "—";
    [Reactive] public string Status { get; set; } = string.Empty;
    [Reactive] public string Badge { get; set; } = string.Empty;

    public string Details => $"{Tag} · {DisplayProtocol}\n{ResUI.TbSortingHost}: {Endpoint}\n{ResUI.XrayProxyGroups}: {Groups}\n{ResUI.TbSortingChain}: {Chain}\n{ResUI.XrayProxyStatus}: {Status}";

    public void UpdateTraffic(XrayProxyRow row)
    {
        var statusChanged = Status != row.Status;
        ConnectionCount = row.ConnectionCount;
        Upload = row.Upload;
        Download = row.Download;
        UploadRate = row.UploadRate;
        DownloadRate = row.DownloadRate;
        Status = row.Status;
        Badge = row.Badge;
        if (statusChanged) this.RaisePropertyChanged(nameof(Details));
    }
}
