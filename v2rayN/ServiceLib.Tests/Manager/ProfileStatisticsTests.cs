using System.Threading.Channels;
using AwesomeAssertions;
using Xunit;

namespace ServiceLib.Tests.Manager;

public class ProfileStatisticsTests
{
    [Fact]
    public void Counters_NormalizeElapsedTime_AndRetainBytesBelowOneKilobyte()
    {
        var tracker = new StatisticsCounterTracker(10);
        var first = tracker.Sample(new Dictionary<string, TrafficCounter> { ["socks"] = new(600, 900) }, 12);
        first.UpRate.Should().Be(300);
        first.DownRate.Should().Be(450);
        var stat = new ServerStatItem { IndexId = "profile" };
        StatisticsCounterTracker.Accumulate(stat, first, 123);
        StatisticsCounterTracker.Accumulate(stat,
            tracker.Sample(new Dictionary<string, TrafficCounter> { ["socks"] = new(1200, 1800) }, 14), 123);
        stat.TotalUp.Should().Be(1);
        stat.TotalUpBytesRemainder.Should().Be(176);
        stat.TotalDown.Should().Be(1);
        stat.TotalDownBytesRemainder.Should().Be(776);
    }

    [Fact]
    public void CounterRollback_IsIndependentPerDirectionAndInbound()
    {
        var tracker = new StatisticsCounterTracker(0);
        tracker.Sample(new Dictionary<string, TrafficCounter> { ["a"] = new(1000, 2000), ["b"] = new(500, 700) }, 1);
        var next = tracker.Sample(new Dictionary<string, TrafficCounter> { ["a"] = new(100, 2200), ["b"] = new(600, 800) }, 3);
        next.UpBytes.Should().Be(200);
        next.DownBytes.Should().Be(300);
        next.UpRate.Should().BeNull();
        next.DownRate.Should().BeNull();
    }

    [Fact]
    public void NewSession_DoesNotSubtractPreviousProfileCounters()
    {
        var previous = new StatisticsCounterTracker(0);
        previous.Sample(new Dictionary<string, TrafficCounter> { ["socks"] = new(9000, 5000) }, 1);
        var current = new StatisticsCounterTracker(10);
        var sample = current.Sample(new Dictionary<string, TrafficCounter> { ["socks"] = new(100, 300) }, 12);
        sample.UpBytes.Should().Be(100);
        sample.DownRate.Should().Be(150);
    }

    [Fact]
    public void Persistence_RetainsHistoricalUnitsAndSubKilobyteRemainders()
    {
        using var db = new SQLiteConnection(":memory:");
        db.CreateTable<ServerStatItem>();
        var stat = new ServerStatItem { IndexId = "a", TotalUp = 7, TotalDown = 9, DateNow = 1 };
        StatisticsCounterTracker.Accumulate(stat, new TrafficSample(800, 900, 400, 450), 1);
        db.Insert(stat);
        stat = db.Find<ServerStatItem>("a");
        StatisticsCounterTracker.Accumulate(stat, new TrafficSample(300, 200, 300, 200), 2);
        db.Update(stat);
        var restored = db.Find<ServerStatItem>("a");
        restored.TotalUp.Should().Be(8);
        restored.TotalDown.Should().Be(10);
        restored.TotalUpBytesRemainder.Should().Be(76);
        restored.TodayUp.Should().Be(0);
        restored.TodayUpBytesRemainder.Should().Be(300);
        restored.TodayDownBytesRemainder.Should().Be(200);
    }

    [Fact]
    public void Xray_UsesOnlyMainUserIngress_NotOutboundOrOwnRoutingLoopCounters()
    {
        var tags = StatisticsXrayService.GetIngressTags("""
            {"inbounds":[{"tag":"socks","protocol":"mixed"},{"tag":"custom","protocol":"http"},
              {"tag":"v2rayn-own-in","protocol":"socks"},{"tag":"api","protocol":"dokodemo-door"}],"api":{"tag":"api"}}
            """);
        var counters = StatisticsXrayService.ParseCounters("""
            {"stats":{"inbound":{"socks":{"uplink":100,"downlink":200},"custom":{"uplink":50,"downlink":80},
              "v2rayn-own-in":{"uplink":100,"downlink":200},"api":{"uplink":999,"downlink":999}},
              "outbound":{"proxy":{"uplink":150,"downlink":280},"transport":{"uplink":200,"downlink":330}}}}
            """, tags)!;
        var sample = new StatisticsCounterTracker(0).Sample(counters, 1);
        sample.UpBytes.Should().Be(150);
        sample.DownBytes.Should().Be(280);
    }

    [Fact]
    public void Singbox_UsesAuthoritativeLifetimeTotals_NotTransientConnectionRows()
    {
        var counters = StatisticsSingboxService.ParseCounters("""
            {"uploadTotal":5000,"downloadTotal":7000,"connections":[{"upload":8000,"download":9000}]}
            """)!;
        var sample = new StatisticsCounterTracker(0).Sample(counters, 2);
        sample.UpBytes.Should().Be(5000);
        sample.DownBytes.Should().Be(7000);
        StatisticsSingboxService.ParseCounters("{\"connections\":[]}").Should().BeNull();
    }

    [Fact]
    public void XrayStatisticsInjection_PreservesCustomPoliciesAndEnablesInboundCounters()
    {
        var json = StatisticsXrayService.EnableCounters("""
            {"policy":{"levels":{"0":{"handshake":8}},"system":{"statsOutboundUplink":true}},"inbounds":[]}
            """, 12345);
        var root = JsonNode.Parse(json)!;
        root["policy"]!["levels"]!["0"]!["handshake"]!.GetValue<int>().Should().Be(8);
        root["policy"]!["system"]!["statsInboundUplink"]!.GetValue<bool>().Should().BeTrue();
        root["policy"]!["system"]!["statsInboundDownlink"]!.GetValue<bool>().Should().BeTrue();
        root["metrics"]!["listen"]!.GetValue<string>().Should().Be("127.0.0.1:12345");
    }

    [Fact]
    public void ClearDuringApiOutage_DoesNotResurrectOldTraffic()
    {
        var tracker = new StatisticsCounterTracker(0);
        tracker.Sample(new Dictionary<string, TrafficCounter> { ["socks"] = new(1000, 2000) }, 1);
        tracker.ResetAfterUnavailableClear();
        var baseline = tracker.Sample(new Dictionary<string, TrafficCounter> { ["socks"] = new(1500, 2600) }, 4);
        baseline.UpBytes.Should().Be(0);
        baseline.UpRate.Should().BeNull();
        var next = tracker.Sample(new Dictionary<string, TrafficCounter> { ["socks"] = new(1600, 2800) }, 5);
        next.UpBytes.Should().Be(100);
        next.DownBytes.Should().Be(200);
    }

    [Fact]
    public void ExistingDatabase_GainsByteRemaindersWithoutReinterpretingTotals()
    {
        using var db = new SQLiteConnection(":memory:");
        db.Execute("CREATE TABLE ServerStatItem (IndexId varchar PRIMARY KEY, TotalUp bigint, TotalDown bigint, TodayUp bigint, TodayDown bigint, DateNow bigint)");
        db.Execute("INSERT INTO ServerStatItem VALUES ('old', 123, 456, 10, 20, 1)");
        db.CreateTable<ServerStatItem>();
        var restored = db.Find<ServerStatItem>("old");
        restored.TotalUp.Should().Be(123);
        restored.TotalDown.Should().Be(456);
        restored.TotalUpBytesRemainder.Should().Be(0);
        restored.TotalDownBytesRemainder.Should().Be(0);
    }

    [Fact]
    public async Task Manager_AttributesUnavailableAndStopEventsToRunningSession_NotSelectedProfile()
    {
        SQLiteHelper.Instance.CreateTable<ProfileItem>();
        SQLiteHelper.Instance.CreateTable<ServerStatItem>();
        var config = CoreConfig.CoreConfigTestFactory.CreateConfig();
        var profile = new ProfileItem { IndexId = Guid.NewGuid().ToString("N") };
        var saved = new ServerStatItem { IndexId = profile.IndexId, TotalDown = 77, DateNow = DateTime.Now.Date.Ticks };
        await SQLiteHelper.Instance.InsertAsync(profile);
        await SQLiteHelper.Instance.InsertAsync(saved);
        var manager = new StatisticsManager();
        var updates = new List<ServerSpeedItem>();
        try
        {
            await manager.Init(config, update => { updates.Add(update); return Task.CompletedTask; });
            config.IndexId = "a-different-selection";
            await manager.StartSession(profile.IndexId, ECoreType.Xray, "{}", false, () => true, 0);
            manager.ActiveProfileId.Should().Be(profile.IndexId);
            updates.Last().IndexId.Should().Be(profile.IndexId);
            updates.Last().TotalDown.Should().Be(77);
            updates.Last().ProxyDownRate.Should().BeNull();
            config.IndexId = "another-selection";
            await manager.StopSession();
            manager.ActiveProfileId.Should().BeNull();
            updates.Last().IndexId.Should().Be(profile.IndexId);
            updates.Last().TotalDown.Should().Be(77);
            updates.Last().ProxyDownRate.Should().BeNull();
            var persisted = await SQLiteHelper.Instance.TableAsync<ServerStatItem>()
                .FirstOrDefaultAsync(s => s.IndexId == profile.IndexId);
            persisted.TotalDown.Should().Be(77);
        }
        finally
        {
            manager.Close();
            await SQLiteHelper.Instance.DeleteAsync(saved);
            await SQLiteHelper.Instance.DeleteAsync(profile);
        }
    }

    [Fact]
    public async Task Manager_AcceptsMihomoYaml_AndClearsStoppedProcessWithDisposedHandle()
    {
        SQLiteHelper.Instance.CreateTable<ProfileItem>();
        SQLiteHelper.Instance.CreateTable<ServerStatItem>();
        var config = CoreConfig.CoreConfigTestFactory.CreateConfig();
        config.GuiItem.DisplayRealTimeSpeed = true;
        var appConfig = typeof(AppManager).GetField("_config", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var statePort = typeof(AppManager).GetField("_statePort2", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var previousConfig = appConfig.GetValue(AppManager.Instance);
        var previousPort = statePort.GetValue(AppManager.Instance);
        CoreConfig.CoreConfigTestFactory.BindAppManagerConfig(config);
        statePort.SetValue(AppManager.Instance, 2);
        var manager = new StatisticsManager();
        var updates = new List<ServerSpeedItem>();
        var disposed = false;
        try
        {
            await manager.Init(config, update => { updates.Add(update); return Task.CompletedTask; });
            await manager.StartSession("mihomo-session", ECoreType.mihomo,
                "external-controller: 127.0.0.1:1\nsecret: test-secret\n", false,
                () => disposed ? throw new InvalidOperationException("Disposed process") : true, 0);
            updates.Last().IndexId.Should().Be("mihomo-session");
            disposed = true;
            await manager.StopSession();
            updates.Last().IndexId.Should().Be("mihomo-session");
            updates.Last().ProxyUpRate.Should().BeNull();
            updates.Last().ProxyDownRate.Should().BeNull();
        }
        finally
        {
            manager.Close();
            await manager.StopSession();
            appConfig.SetValue(AppManager.Instance, previousConfig);
            statePort.SetValue(AppManager.Instance, previousPort);
        }
    }

    [Fact]
    public async Task Manager_PollsAndPersistsLiveTraffic_WhenLegacyDisplaySwitchesAreOff()
    {
        SQLiteHelper.Instance.CreateTable<ProfileItem>();
        SQLiteHelper.Instance.CreateTable<ServerStatItem>();
        var config = CoreConfig.CoreConfigTestFactory.CreateConfig();
        var profile = new ProfileItem { IndexId = Guid.NewGuid().ToString("N") };
        await SQLiteHelper.Instance.InsertAsync(profile);
        await using var server = new StatisticsHttpServer();
        var statePort = typeof(AppManager).GetField("_statePort", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var previousPort = statePort.GetValue(AppManager.Instance);
        statePort.SetValue(AppManager.Instance, server.Port);
        var manager = new StatisticsManager();
        var updates = Channel.CreateUnbounded<ServerSpeedItem>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await manager.Init(config, update =>
            {
                updates.Writer.TryWrite(update);
                return Task.CompletedTask;
            });
            var json = StatisticsXrayService.EnableCounters(
                """{"inbounds":[{"tag":"socks","protocol":"socks"}]}""", server.Port);
            await manager.StartSession(profile.IndexId, ECoreType.Xray, json, false, () => true,
                System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency);

            async Task<ServerSpeedItem> NextSample()
            {
                while (true)
                {
                    var update = await updates.Reader.ReadAsync(timeout.Token);
                    if (update.ProxyDownRate != null) return update;
                }
            }

            var first = await NextSample();
            first.TotalUp.Should().Be(1);
            first.TotalDown.Should().Be(2);
            first.ProxyDownRate.Should().BeGreaterThan(0);
            server.Advance();
            ServerSpeedItem second;
            do { second = await NextSample(); } while (second.TotalDown == 2);
            second.TotalUp.Should().Be(2);
            second.TotalDown.Should().Be(4);
            second.ProxyDownRate.Should().BeGreaterThan(0);

            await manager.StopSession();
            manager.ActiveProfileId.Should().BeNull();
            var persisted = await SQLiteHelper.Instance.TableAsync<ServerStatItem>()
                .FirstOrDefaultAsync(s => s.IndexId == profile.IndexId);
            persisted.TotalUp.Should().Be(2);
            persisted.TotalDown.Should().Be(4);
            config.GuiItem.EnableStatistics.Should().BeFalse();
            config.GuiItem.DisplayRealTimeSpeed.Should().BeFalse();
        }
        finally
        {
            manager.Close();
            await manager.StopSession();
            statePort.SetValue(AppManager.Instance, previousPort);
            await SQLiteHelper.Instance.DeleteAsync(new ServerStatItem { IndexId = profile.IndexId });
            await SQLiteHelper.Instance.DeleteAsync(profile);
        }
    }

    [Fact]
    public async Task Manager_TracksAggregateAndTwoExactAuxiliaryIdentities_WithoutDuplicateTags()
    {
        SQLiteHelper.Instance.CreateTable<ProfileItem>();
        SQLiteHelper.Instance.CreateTable<ServerStatItem>();
        var profiles = Enumerable.Range(0, 3).Select(_ => new ProfileItem { IndexId = Guid.NewGuid().ToString("N") }).ToArray();
        foreach (var profile in profiles) await SQLiteHelper.Instance.InsertAsync(profile);
        await using var server = new StatisticsHttpServer();
        var statePort = typeof(AppManager).GetField("_statePort", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var previousPort = statePort.GetValue(AppManager.Instance);
        statePort.SetValue(AppManager.Instance, server.Port);
        var manager = new StatisticsManager();
        var alive = true;
        try
        {
            await manager.Init(CoreConfig.CoreConfigTestFactory.CreateConfig(), _ => Task.CompletedTask);
            var json = StatisticsXrayService.EnableCounters("""{"inbounds":[{"tag":"socks"}]}""", server.Port);
            var tags = new Dictionary<string, string> { ["aux-a"] = profiles[1].IndexId, ["aux-a-second"] = profiles[1].IndexId, ["aux-b"] = profiles[2].IndexId };
            await manager.StartSession(profiles[0].IndexId, ECoreType.Xray, json, false, () => true, 0,
                tags, new Dictionary<string, Func<bool>> { [profiles[2].IndexId] = () => alive });
            manager.ActiveProfileIds.Should().BeEquivalentTo(profiles.Select(p => p.IndexId));
            await manager.StopSession();
            manager.ActiveProfileIds.Should().BeEmpty();
            manager.LatestStatistics.Should().BeEmpty();
            manager.ServerStat.Single(s => s.IndexId == profiles[0].IndexId).TotalDown.Should().Be(2);
            manager.ServerStat.Single(s => s.IndexId == profiles[1].IndexId).TotalDownBytesRemainder.Should().Be(600);
            manager.ServerStat.Single(s => s.IndexId == profiles[2].IndexId).TotalDownBytesRemainder.Should().Be(500);
            alive = false;
            await manager.StartSession(profiles[0].IndexId, ECoreType.Xray, json, false, () => true, 0,
                tags, new Dictionary<string, Func<bool>> { [profiles[2].IndexId] = () => alive });
            manager.LatestStatistics[profiles[2].IndexId].ProxyDownRate.Should().BeNull();
            await manager.StopSession();
            manager.ServerStat.Single(s => s.IndexId == profiles[1].IndexId).TotalDown.Should().Be(1);
            manager.ServerStat.Single(s => s.IndexId == profiles[1].IndexId).TotalDownBytesRemainder.Should().Be(176);
            manager.ServerStat.Single(s => s.IndexId == profiles[2].IndexId).TotalDownBytesRemainder.Should().Be(500);
            var persistedId = profiles[1].IndexId;
            var persisted = await SQLiteHelper.Instance.TableAsync<ServerStatItem>().FirstOrDefaultAsync(s => s.IndexId == persistedId);
            persisted.TotalDown.Should().Be(1);
        }
        finally
        {
            manager.Close();
            await manager.StopSession();
            statePort.SetValue(AppManager.Instance, previousPort);
            foreach (var profile in profiles)
            {
                await SQLiteHelper.Instance.DeleteAsync(new ServerStatItem { IndexId = profile.IndexId });
                await SQLiteHelper.Instance.DeleteAsync(profile);
            }
        }
    }

    [Fact]
    public async Task Manager_AuxiliaryOutageDoesNotClearHealthySources_AndSessionSwitchFlushesAll()
    {
        SQLiteHelper.Instance.CreateTable<ProfileItem>();
        SQLiteHelper.Instance.CreateTable<ServerStatItem>();
        var ids = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid().ToString("N")).ToArray();
        foreach (var id in ids) await SQLiteHelper.Instance.InsertAsync(new ProfileItem { IndexId = id });
        await using var server = new StatisticsHttpServer();
        var statePort = typeof(AppManager).GetField("_statePort", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var previousPort = statePort.GetValue(AppManager.Instance);
        statePort.SetValue(AppManager.Instance, server.Port);
        var manager = new StatisticsManager();
        var updates = Channel.CreateUnbounded<ServerSpeedItem>();
        var childAlive = true;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await manager.Init(CoreConfig.CoreConfigTestFactory.CreateConfig(), update =>
            {
                updates.Writer.TryWrite(update);
                return Task.CompletedTask;
            });
            var json = StatisticsXrayService.EnableCounters("""{"inbounds":[{"tag":"socks"}]}""", server.Port);
            await manager.StartSession(ids[0], ECoreType.Xray, json, false, () => true, 0,
                new Dictionary<string, string> { ["aux-a"] = ids[1], ["aux-b"] = ids[2] },
                new Dictionary<string, Func<bool>> { [ids[2]] = () => childAlive });
            async Task<ServerSpeedItem> Next(Func<ServerSpeedItem, bool> predicate)
            {
                while (true)
                {
                    var update = await updates.Reader.ReadAsync(timeout.Token);
                    if (predicate(update)) return update;
                }
            }
            await Next(s => s.IndexId == ids[2] && s.ProxyDownRate.HasValue);
            childAlive = false;
            await Next(s => s.IndexId == ids[2] && !s.ProxyDownRate.HasValue);
            manager.LatestStatistics[ids[0]].ProxyDownRate.Should().NotBeNull();
            manager.LatestStatistics[ids[1]].ProxyDownRate.Should().NotBeNull();
            manager.LatestStatistics[ids[2]].TotalDownBytesRemainder.Should().Be(500);
            manager.ActiveProfileIds.Should().NotContain(ids[2]);
            server.Advance();
            await manager.StartSession(ids[3], ECoreType.Xray, "{}", false, () => true, 0);
            manager.ActiveProfileIds.Should().BeEquivalentTo([ids[3]]);
            manager.LatestStatistics.Keys.Should().BeEquivalentTo([ids[3]]);
            manager.ServerStat.Single(s => s.IndexId == ids[0]).TotalDown.Should().Be(4);
            manager.ServerStat.Single(s => s.IndexId == ids[1]).TotalDownBytesRemainder.Should().Be(400);
            manager.ServerStat.Single(s => s.IndexId == ids[2]).TotalDownBytesRemainder.Should().Be(500);
        }
        finally
        {
            manager.Close();
            await manager.StopSession();
            statePort.SetValue(AppManager.Instance, previousPort);
            foreach (var id in ids)
            {
                await SQLiteHelper.Instance.DeleteAsync(new ServerStatItem { IndexId = id });
                await SQLiteHelper.Instance.DeleteAsync(new ProfileItem { IndexId = id });
            }
        }
    }

    [Fact]
    public async Task NativeSingbox_LeavesInlineAttributionUnavailable_ButCountsOwnedChildIngress()
    {
        SQLiteHelper.Instance.CreateTable<ProfileItem>();
        SQLiteHelper.Instance.CreateTable<ServerStatItem>();
        var ids = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid().ToString("N")).ToArray();
        foreach (var id in ids) await SQLiteHelper.Instance.InsertAsync(new ProfileItem { IndexId = id });
        await using var server = new StatisticsHttpServer();
        var manager = new StatisticsManager();
        var updates = new List<ServerSpeedItem>();
        try
        {
            var config = CoreConfig.CoreConfigTestFactory.CreateConfig();
            await manager.Init(config, update => { updates.Add(update); return Task.CompletedTask; });
            var childJson = StatisticsXrayService.EnableCounters("""{"inbounds":[{"tag":"socks"}]}""", server.Port);
            await manager.StartSession(ids[0], ECoreType.sing_box, "{}", true, () => true, 0,
                new Dictionary<string, string> { ["inline-standard"] = ids[1], ["custom-handoff"] = ids[2] },
                childSources: [new(ids[2], ECoreType.Xray, childJson, () => true, 0, server.Port)]);
            await manager.StopSession();
            manager.ServerStat.Should().NotContain(s => s.IndexId == ids[1]);
            updates.Where(s => s.IndexId == ids[1]).Should().OnlyContain(s => s.ProxyDownRate == null);
            manager.ServerStat.Single(s => s.IndexId == ids[2]).TotalDown.Should().Be(2);
        }
        finally
        {
            manager.Close();
            await manager.StopSession();
            foreach (var id in ids)
            {
                await SQLiteHelper.Instance.DeleteAsync(new ServerStatItem { IndexId = id });
                await SQLiteHelper.Instance.DeleteAsync(new ProfileItem { IndexId = id });
            }
        }
    }

    private sealed class StatisticsHttpServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cancellation = new();
        private readonly Task _serverTask;
        private int _sample = 1;
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public StatisticsHttpServer()
        {
            _listener.Start();
            _serverTask = ServeAsync();
        }

        public void Advance() => Interlocked.Increment(ref _sample);

        private async Task ServeAsync()
        {
            var token = _cancellation.Token;
            while (!token.IsCancellationRequested)
            {
                using var client = await _listener.AcceptTcpClientAsync(token);
                await using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                while (await reader.ReadLineAsync(token) is { Length: > 0 }) { }
                var sample = Volatile.Read(ref _sample);
                var body = JsonSerializer.Serialize(new
                {
                    stats = new
                    {
                        inbound = new { socks = new { uplink = sample * 1024, downlink = sample * 2048 } },
                        outbound = new Dictionary<string, object>
                        {
                            ["aux-a"] = new { uplink = sample * 100, downlink = sample * 200 },
                            ["aux-a-second"] = new { uplink = sample * 300, downlink = sample * 400 },
                            ["aux-b"] = new { uplink = sample * 250, downlink = sample * 500 },
                            ["unrelated"] = new { uplink = 999999, downlink = 999999 }
                        }
                    }
                });
                var response = $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(response), token);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _cancellation.CancelAsync();
            _listener.Stop();
            try { await _serverTask; }
            catch (OperationCanceledException) { }
            finally { _cancellation.Dispose(); }
        }
    }
}
