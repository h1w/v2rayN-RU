namespace ServiceLib.Manager;

/// <summary>An independently owned child core, used only when the main core cannot expose its outbound counters.</summary>
public sealed record StatisticsSourceConfiguration(string IndexId, ECoreType CoreType, string RunningJson,
    Func<bool> IsAlive, double StartedAtSeconds, int Port);

public class StatisticsManager
{
    private static readonly Lazy<StatisticsManager> instance = new(() => new());
    public static StatisticsManager Instance => instance.Value;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HttpClient _httpClient = new(new SocketsHttpHandler { UseProxy = false })
    {
        Timeout = TimeSpan.FromSeconds(2)
    };
    private Config? _config;
    private List<ServerStatItem> _lstServerStat = [];
    private Func<ServerSpeedItem, Task>? _updateFunc;
    private CancellationTokenSource? _pollCancellation;
    private Session? _session;
    private IReadOnlySet<string> _activeProfileIds = new HashSet<string>();
    private IReadOnlyDictionary<string, ServerSpeedItem> _latestStatistics = new Dictionary<string, ServerSpeedItem>();
    private const string _tag = "StatisticsHandler";
    public List<ServerStatItem> ServerStat => _lstServerStat;
    public string? ActiveProfileId => Volatile.Read(ref _session)?.IndexId;
    public IReadOnlySet<string> ActiveProfileIds => Volatile.Read(ref _activeProfileIds);
    public IReadOnlyDictionary<string, ServerSpeedItem> LatestStatistics => Volatile.Read(ref _latestStatistics);

    private sealed class Session(string indexId, Func<bool> isAlive)
    {
        public string IndexId { get; } = indexId;
        public bool IsAlive() => IsProcessAlive(isAlive);
        public List<Source> Sources { get; } = [];
    }

    private sealed class Source(string indexId, ECoreType coreType, string? url,
        HashSet<string> tags, string? secret, Func<bool> isAlive, double startedAtSeconds, bool outbound = false)
    {
        public string IndexId { get; } = indexId;
        public ECoreType CoreType { get; } = coreType;
        public string? Url { get; } = url;
        public HashSet<string> Tags { get; } = tags;
        public string? Secret { get; } = secret;
        public bool Outbound { get; } = outbound;
        public bool IsAlive() => IsProcessAlive(isAlive);
        public StatisticsCounterTracker Counters { get; } = new(startedAtSeconds);
    }

    private static double NowSeconds => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    private static bool IsProcessAlive(Func<bool> isAlive)
    {
        try { return isAlive(); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    public async Task Init(Config config, Func<ServerSpeedItem, Task> updateFunc)
    {
        await _gate.WaitAsync();
        try
        {
            _config = config;
            _updateFunc = updateFunc;
            await SQLiteHelper.Instance.ExecuteAsync("delete from ServerStatItem where indexId not in (select indexId from ProfileItem)");
            var ticks = DateTime.Now.Date.Ticks;
            await SQLiteHelper.Instance.ExecuteAsync($"update ServerStatItem set todayUp=0,todayDown=0,todayUpBytesRemainder=0,todayDownBytesRemainder=0,dateNow={ticks} where dateNow<>{ticks}");
            _lstServerStat = await SQLiteHelper.Instance.TableAsync<ServerStatItem>().ToListAsync();
            _pollCancellation?.Cancel();
            _pollCancellation = new CancellationTokenSource();
            var cancellationToken = _pollCancellation.Token;
            _ = Task.Run(() => PollAsync(cancellationToken));
        }
        finally { _gate.Release(); }
    }

    public void Close() => _pollCancellation?.Cancel();

    /// <summary>Main ingress is the aggregate; auxiliary rows are non-additive breakdowns keyed by original profile identity.</summary>
    public async Task StartSession(string indexId, ECoreType coreType, string runningJson,
        bool hasOwnRoutingLoop, Func<bool> isAlive, double startedAtSeconds,
        IReadOnlyDictionary<string, string>? outboundProfiles = null,
        IReadOnlyDictionary<string, Func<bool>>? auxiliaryLiveness = null,
        IReadOnlyList<StatisticsSourceConfiguration>? childSources = null)
    {
        await _gate.WaitAsync();
        try
        {
            if (_session != null) await StopSessionLocked();
            if (_config == null || string.IsNullOrEmpty(indexId) || !IsProcessAlive(isAlive)) return;
            var session = new Session(indexId, isAlive);
            var main = CreateSource(indexId, coreType, runningJson, hasOwnRoutingLoop, isAlive, startedAtSeconds);
            session.Sources.Add(main);
            foreach (var group in (outboundProfiles ?? new Dictionary<string, string>()).GroupBy(p => p.Value, StringComparer.Ordinal))
            {
                if (string.IsNullOrEmpty(group.Key) || group.Key == indexId) continue;
                var childAlive = auxiliaryLiveness?.GetValueOrDefault(group.Key);
                Func<bool> sourceAlive = () => IsProcessAlive(isAlive) && (childAlive == null || IsProcessAlive(childAlive));
                var child = coreType != ECoreType.Xray ? childSources?.FirstOrDefault(s => s.IndexId == group.Key) : null;
                session.Sources.Add(child != null
                    ? CreateSource(child.IndexId, child.CoreType, child.RunningJson, false,
                        () => sourceAlive() && IsProcessAlive(child.IsAlive), child.StartedAtSeconds, child.Port)
                    : new Source(group.Key, coreType, coreType == ECoreType.Xray ? main.Url : null,
                        group.Select(p => p.Key).ToHashSet(StringComparer.Ordinal), main.Secret, sourceAlive, startedAtSeconds, true));
            }
            _session = session;
            RefreshActiveProfiles(session);
            foreach (var source in session.Sources) await Publish(source, null);
        }
        catch (Exception ex) { Logging.SaveLog(_tag, ex); }
        finally { _gate.Release(); }
    }

    private static Source CreateSource(string indexId, ECoreType coreType, string runningJson,
        bool hasOwnRoutingLoop, Func<bool> isAlive, double startedAtSeconds, int? port = null)
    {
        string? url = null, secret = null;
        HashSet<string> tags = [];
        if (coreType == ECoreType.Xray)
        {
            var root = JsonNode.Parse(runningJson);
            tags = StatisticsXrayService.GetIngressTags(runningJson);
            var listen = root?["metrics"]?["listen"]?.GetValue<string>();
            if (listen != null && listen == $"{Global.Loopback}:{port ?? AppManager.Instance.StatePort}")
                url = $"http://{listen}/debug/vars";
        }
        else if (coreType == ECoreType.sing_box && !hasOwnRoutingLoop)
        {
            var root = JsonNode.Parse(runningJson);
            var api = root?["experimental"]?["clash_api"];
            var listen = api?["external_controller"]?.GetValue<string>();
            if (listen != null && listen == $"{Global.Loopback}:{port ?? AppManager.Instance.StatePort2}")
            {
                url = $"http://{listen}/connections";
                secret = api?["secret"]?.GetValue<string>();
            }
        }
        else if (coreType == ECoreType.mihomo)
        {
            var yaml = YamlUtils.FromYaml<Dictionary<string, object>>(runningJson);
            var listen = yaml?.GetValueOrDefault("external-controller")?.ToString();
            if (listen != null && listen == $"{Global.Loopback}:{port ?? AppManager.Instance.StatePort2}")
            {
                url = $"http://{listen}/connections";
                secret = yaml?.GetValueOrDefault("secret")?.ToString();
            }
        }
        return new(indexId, coreType, url, tags, secret, isAlive, startedAtSeconds);
    }

    /// <summary>Final reads and SQLite flush happen before CoreManager kills any owned process.</summary>
    public async Task StopSession()
    {
        await _gate.WaitAsync();
        try { await StopSessionLocked(); }
        finally { _gate.Release(); }
    }

    private async Task StopSessionLocked()
    {
        if (_session is not { } session) return;
        await Sample(session);
        _session = null;
        _activeProfileIds = new HashSet<string>();
        await SaveLocked();
        foreach (var source in session.Sources) await Publish(source, null);
        _latestStatistics = new Dictionary<string, ServerSpeedItem>();
    }

    private void RefreshActiveProfiles(Session session) =>
        _activeProfileIds = session.Sources.Where(s => s.IsAlive()).Select(s => s.IndexId).ToHashSet(StringComparer.Ordinal);

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await _gate.WaitAsync(cancellationToken);
                try
                {
                    if (_session is not { } session) continue;
                    if (!session.IsAlive()) await StopSessionLocked();
                    else await Sample(session);
                }
                catch (Exception ex) { Logging.SaveLog(_tag, ex); }
                finally { _gate.Release(); }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task Sample(Session session, bool clearing = false)
    {
        // All profiles sharing a core use one cumulative snapshot, not one HTTP request per row.
        var snapshots = new Dictionary<(string Url, string? Secret), string?>();
        RefreshActiveProfiles(session);
        foreach (var source in session.Sources)
        {
            TrafficSample? sample = null;
            try
            {
                if (source.Url != null && source.IsAlive())
                {
                    var key = (source.Url, source.Secret);
                    if (!snapshots.TryGetValue(key, out var json))
                    {
                        snapshots[key] = null;
                        using var request = new HttpRequestMessage(HttpMethod.Get, source.Url);
                        if (!string.IsNullOrEmpty(source.Secret)) request.Headers.Authorization = new("Bearer", source.Secret);
                        using var response = await _httpClient.SendAsync(request);
                        response.EnsureSuccessStatusCode();
                        json = await response.Content.ReadAsStringAsync();
                        snapshots[key] = json;
                    }
                    var counters = json == null ? null : source.CoreType == ECoreType.Xray
                        ? StatisticsXrayService.ParseCounters(json, source.Tags, source.Outbound)
                        : StatisticsSingboxService.ParseCounters(json);
                    if (counters != null && source.IsAlive())
                    {
                        sample = source.Counters.Sample(counters, NowSeconds);
                        var stat = GetStat(source.IndexId);
                        StatisticsCounterTracker.Accumulate(stat, sample.Value, DateTime.Now.Date.Ticks);
                        await SQLiteHelper.Instance.ReplaceAsync(stat);
                    }
                }
            }
            catch (Exception ex)
            {
                // Keep baselines: a subsequent cumulative read recovers bytes missed during an outage.
                if (ex is not (HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException))
                    Logging.SaveLog(_tag, ex);
            }
            if (clearing && !sample.HasValue) source.Counters.ResetAfterUnavailableClear();
            await Publish(source, sample);
        }
    }

    private ServerStatItem GetStat(string indexId)
    {
        var stat = _lstServerStat.FirstOrDefault(s => s.IndexId == indexId);
        if (stat != null) return stat;
        stat = new ServerStatItem { IndexId = indexId, DateNow = DateTime.Now.Date.Ticks };
        _lstServerStat = [.. _lstServerStat, stat];
        return stat;
    }

    private async Task Publish(Source source, TrafficSample? sample)
    {
        var stat = _lstServerStat.FirstOrDefault(s => s.IndexId == source.IndexId);
        var update = new ServerSpeedItem
        {
            IndexId = source.IndexId,
            ProxyUpRate = sample?.UpRate,
            ProxyDownRate = sample?.DownRate,
            TotalUp = stat?.TotalUp ?? 0,
            TotalDown = stat?.TotalDown ?? 0,
            TodayUp = stat?.TodayUp ?? 0,
            TodayDown = stat?.TodayDown ?? 0,
            TotalUpBytesRemainder = stat?.TotalUpBytesRemainder ?? 0,
            TotalDownBytesRemainder = stat?.TotalDownBytesRemainder ?? 0,
            TodayUpBytesRemainder = stat?.TodayUpBytesRemainder ?? 0,
            TodayDownBytesRemainder = stat?.TodayDownBytesRemainder ?? 0,
        };
        _latestStatistics = new Dictionary<string, ServerSpeedItem>(_latestStatistics, StringComparer.Ordinal) { [source.IndexId] = update };
        if (_updateFunc != null) await _updateFunc(update);
    }

    public async Task ClearAllServerStatistics()
    {
        await _gate.WaitAsync();
        try
        {
            if (_session != null) await Sample(_session, clearing: true);
            await SQLiteHelper.Instance.ExecuteAsync("delete from ServerStatItem");
            _lstServerStat = [];
            if (_session != null)
                foreach (var source in _session.Sources) await Publish(source, null);
        }
        finally { _gate.Release(); }
    }

    public async Task SaveTo()
    {
        await _gate.WaitAsync();
        try { await SaveLocked(); }
        finally { _gate.Release(); }
    }

    private async Task SaveLocked()
    {
        try { await SQLiteHelper.Instance.UpdateAllAsync(_lstServerStat); }
        catch (Exception ex) { Logging.SaveLog(_tag, ex); }
    }

    public async Task CloneServerStatItem(string indexId, string toIndexId)
    {
        if (indexId == toIndexId) return;
        await _gate.WaitAsync();
        try
        {
            var stat = _lstServerStat.FirstOrDefault(s => s.IndexId == indexId);
            if (stat == null) return;
            var copy = JsonUtils.DeepCopy(stat);
            copy.IndexId = toIndexId;
            await SQLiteHelper.Instance.ReplaceAsync(copy);
            _lstServerStat = [.. _lstServerStat.Where(s => s.IndexId != toIndexId), copy];
        }
        finally { _gate.Release(); }
    }
}
