namespace ServiceLib.Models.Entities;

[Serializable]
public class ServerStatItem
{
    [PrimaryKey]
    public string IndexId { get; set; }

    public long TotalUp { get; set; }

    public long TotalDown { get; set; }

    public long TodayUp { get; set; }

    public long TodayDown { get; set; }

    public long DateNow { get; set; }

    // Totals above retain their historical KiB units; these columns preserve sub-KiB bytes.
    public long TotalUpBytesRemainder { get; set; }
    public long TotalDownBytesRemainder { get; set; }
    public long TodayUpBytesRemainder { get; set; }
    public long TodayDownBytesRemainder { get; set; }
}
