namespace ServiceLib.Models.Dto;

[Serializable]
public class ProfileItemModel : ReactiveObject
{
    public bool IsActive { get; set; }
    public string IndexId { get; set; }
    public EConfigType ConfigType { get; set; }
    public string Remarks { get; set; }
    public string Address { get; set; }
    public int Port { get; set; }
    public string Network { get; set; }
    public string StreamSecurity { get; set; }
    public string Subid { get; set; }
    public string SubRemarks { get; set; }
    public int Sort { get; set; }

    [Reactive]
    public int Delay { get; set; }

    public decimal Speed { get; set; }

    [Reactive]
    public string DelayVal { get; set; }

    [Reactive]
    public string SpeedVal { get; set; }

    [Reactive]
    public string IpInfo { get; set; }

    [Reactive]
    public string TodayUp { get; set; }

    [Reactive]
    public string TodayDown { get; set; }

    [Reactive]
    public string TotalUp { get; set; } = string.Empty;

    [Reactive]
    public string TotalDown { get; set; } = string.Empty;

    [Reactive]
    public string CurrentDown { get; set; } = string.Empty;

    [Reactive]
    public string CurrentUp { get; set; } = string.Empty;

    public void ApplyStatistics(ServerSpeedItem update, IReadOnlySet<string> activeProfileIds)
    {
        var isRunning = activeProfileIds.Contains(IndexId);
        if (!isRunning)
        {
            CurrentDown = string.Empty;
            CurrentUp = string.Empty;
        }

        if (IndexId != update.IndexId || string.IsNullOrEmpty(update.IndexId))
        {
            return;
        }

        TodayDown = Utils.HumanFy(update.TodayDown);
        TodayUp = Utils.HumanFy(update.TodayUp);
        TotalDown = Utils.HumanFyBytes(update.TotalDown * 1024d + update.TotalDownBytesRemainder);
        TotalUp = Utils.HumanFyBytes(update.TotalUp * 1024d + update.TotalUpBytesRemainder);
        if (isRunning)
        {
            CurrentDown = FormatRate(update.ProxyDownRate);
            CurrentUp = FormatRate(update.ProxyUpRate);
        }
    }

    private static string FormatRate(double? rate) => rate is >= 0 && double.IsFinite(rate.Value)
        ? $"{Utils.HumanFyBytes(rate.Value)}/s"
        : string.Empty;

    public string GetSummary()
    {
        var summary = $"[{ConfigType}] {Remarks}";
        if (!ConfigType.IsComplexType())
        {
            summary += $"({Address}:{Port})";
        }

        return summary;
    }
}
