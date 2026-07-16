namespace ServiceLib.Models.Entities;

[Serializable]
public class SubItem
{
    [PrimaryKey]
    public string Id { get; set; }

    public string Remarks { get; set; }

    public string Url { get; set; }

    public string MoreUrl { get; set; }

    public bool Enabled { get; set; } = true;

    public string UserAgent { get; set; } = string.Empty;

    public int Sort { get; set; }

    public string? Filter { get; set; }

    public int AutoUpdateInterval { get; set; }

    public long UpdateTime { get; set; }

    public string? ConvertTarget { get; set; }

    public string? PrevProfile { get; set; }

    public string? NextProfile { get; set; }

    public int? PreSocksPort { get; set; }

    public string? Memo { get; set; }

    // Subscription-Userinfo (bytes; Expire = Unix seconds). Null = not reported.
    public long? Upload { get; set; }

    public long? Download { get; set; }

    public long? Total { get; set; }

    public long? Expire { get; set; }

    // True when Remarks is auto-managed from the server's Profile-Title.
    // Default false => existing groups are treated as manually named.
    public bool AutoRemark { get; set; }
}
