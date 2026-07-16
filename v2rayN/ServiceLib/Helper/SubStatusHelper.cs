using ServiceLib.Models.Entities;

namespace ServiceLib.Helper;

public enum SubStatusLevel { Normal, Warn, Crit }

public record SubStatusInfo(
    bool HasExpire, int RemainDays, bool Expired,
    bool Unlimited, long UsedBytes, long TotalBytes, double Percent,
    SubStatusLevel Level);

public static class SubStatusHelper
{
    private const int WarnDays = 3;
    private const double WarnPercent = 0.9;

    public static SubStatusInfo? Compute(SubItem? sub, DateTimeOffset now)
    {
        if (sub is null)
        {
            return null;
        }

        var hasTotal = sub.Total is > 0;
        var hasExpire = sub.Expire is > 0;
        if (!hasTotal && !hasExpire)
        {
            return null;
        }

        var used = (sub.Upload ?? 0) + (sub.Download ?? 0);
        var total = sub.Total ?? 0;
        var unlimited = !hasTotal;
        var percent = unlimited || total <= 0 ? 0 : Math.Clamp((double)used / total, 0, 1);

        var expired = false;
        var remainDays = 0;
        if (hasExpire)
        {
            var seconds = sub.Expire!.Value - now.ToUnixTimeSeconds();
            expired = seconds <= 0;
            remainDays = expired ? 0 : (int)Math.Ceiling(seconds / 86400.0);
        }

        var level = SubStatusLevel.Normal;
        if (expired || (!unlimited && percent >= 1.0))
        {
            level = SubStatusLevel.Crit;
        }
        else if ((hasExpire && remainDays <= WarnDays) || (!unlimited && percent >= WarnPercent))
        {
            level = SubStatusLevel.Warn;
        }

        return new SubStatusInfo(hasExpire, remainDays, expired, unlimited, used, total, percent, level);
    }
}
