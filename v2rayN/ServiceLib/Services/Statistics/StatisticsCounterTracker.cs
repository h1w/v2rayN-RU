namespace ServiceLib.Services.Statistics;

public readonly record struct TrafficCounter(long Up, long Down);
public readonly record struct TrafficSample(long UpBytes, long DownBytes, double? UpRate, double? DownRate);

/// <summary>One counter namespace and one running process session. Bytes never pass through a rate accumulator.</summary>
public sealed class StatisticsCounterTracker(double startedAtSeconds)
{
    private readonly Dictionary<string, TrafficCounter> _previous = new(StringComparer.Ordinal);
    private double _previousTime = startedAtSeconds;
    private bool _discardNext;

    public void ResetAfterUnavailableClear() => _discardNext = true;

    public TrafficSample Sample(IReadOnlyDictionary<string, TrafficCounter> counters, double nowSeconds)
    {
        if (_discardNext)
        {
            _previous.Clear();
            foreach (var (tag, counter) in counters) _previous[tag] = counter;
            _previousTime = nowSeconds;
            _discardNext = false;
            return new TrafficSample(0, 0, null, null);
        }
        long up = 0, down = 0;
        var rollback = false;
        foreach (var (tag, counter) in counters)
        {
            _previous.TryGetValue(tag, out var previous);
            rollback |= counter.Up < previous.Up || counter.Down < previous.Down;
            up += counter.Up >= previous.Up ? counter.Up - previous.Up : counter.Up;
            down += counter.Down >= previous.Down ? counter.Down - previous.Down : counter.Down;
            _previous[tag] = counter;
        }
        var elapsed = nowSeconds - _previousTime;
        _previousTime = nowSeconds;
        return new TrafficSample(up, down,
            !rollback && elapsed > 0 ? up / elapsed : null,
            !rollback && elapsed > 0 ? down / elapsed : null);
    }

    public static void Accumulate(ServerStatItem stat, TrafficSample sample, long dateTicks)
    {
        if (stat.DateNow != dateTicks)
        {
            stat.TodayUp = stat.TodayDown = 0;
            stat.TodayUpBytesRemainder = stat.TodayDownBytesRemainder = 0;
            stat.DateNow = dateTicks;
        }
        // Existing databases store whole KiB. Persist the remainder separately rather than
        // reinterpret old values or discard every small transfer at a polling boundary.
        var up = sample.UpBytes + stat.TotalUpBytesRemainder;
        var down = sample.DownBytes + stat.TotalDownBytesRemainder;
        stat.TotalUp += up / 1024;
        stat.TotalDown += down / 1024;
        stat.TotalUpBytesRemainder = up % 1024;
        stat.TotalDownBytesRemainder = down % 1024;
        up = sample.UpBytes + stat.TodayUpBytesRemainder;
        down = sample.DownBytes + stat.TodayDownBytesRemainder;
        stat.TodayUp += up / 1024;
        stat.TodayDown += down / 1024;
        stat.TodayUpBytesRemainder = up % 1024;
        stat.TodayDownBytesRemainder = down % 1024;
    }
}
