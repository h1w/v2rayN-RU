using ServiceLib.Manager;
using ServiceLib.Models.Dto;
using Xunit;

namespace ServiceLib.Tests.Manager;

public class XrayProxyPanelManagerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 0, 0, 0, TimeSpan.Zero);
    private const string Single = """{"outbounds":[{"tag":"one","protocol":"vless","settings":{"vnext":[{"address":"192.0.2.1","port":443}]}}]}""";

    [Fact]
    public void DuplicateEndpointsRemainAmbiguousWithoutDuplicatingTraffic()
    {
        var manager = new XrayProxyPanelManager();
        manager.Activate("""{"outbounds":[{"tag":"one","protocol":"vless","settings":{"vnext":[{"address":"192.0.2.1","port":443}]}},{"tag":"two","protocol":"trojan","settings":{"servers":[{"address":"192.0.2.1","port":443}]}}]}""");
        var connection = Connection("a", 100, 200);
        Assert.Equal(new[] { "one", "two" }, manager.MatchTags(connection));
        var rows = manager.GetRows([connection], Now);
        Assert.All(rows, row =>
        {
            Assert.Equal(0, row.ConnectionCount);
            Assert.Equal("—", row.Upload);
            Assert.Equal("—", row.DownloadRate);
        });
        Assert.Contains("one", manager.DescribeConnection(connection));
        Assert.Contains("two", manager.DescribeConnection(connection));
    }

    [Fact]
    public void MatchesEveryServerAndPreservesUnsupportedAndServiceOutbounds()
    {
        var manager = new XrayProxyPanelManager();
        manager.Activate("""
            {"outbounds":[
              {"tag":"many","protocol":"socks","settings":{"servers":[{"address":"192.0.2.1","port":443},{"address":"192.0.2.2","port":8443}]}},
              {"tag":"flat","protocol":"future","settings":{"address":"flat.example","port":"1234"}},
              {"tag":"opaque","protocol":"future","settings":{"custom":{"secret":"do-not-display"}}},
              {"tag":"direct","protocol":"freedom"},{"tag":"block","protocol":"blackhole"},
              {"protocol":"new"},null],
             "routing":{"balancers":[{"tag":"pool","selector":["ma"],"fallbackTag":"flat"}]}}
            """);
        var connection = Connection("a", 100, 200, "192.0.2.2", "8443");
        Assert.Equal(new[] { "many" }, manager.MatchTags(connection));
        Assert.Equal(new[] { "flat" }, manager.MatchTags(Connection("b", 0, 0, "flat.example", "1234")));
        var rows = manager.GetRows([connection], Now);
        Assert.Equal(new[] { "many", "flat", "opaque", "direct", "block", "(outbound 6)", "(outbound 7)", "pool" }, rows.Select(row => row.Tag));
        Assert.Contains("pool", rows[0].Groups);
        Assert.Contains("192.0.2.1:443", rows[0].Endpoint);
        Assert.Contains("192.0.2.2:8443", rows[0].Endpoint);
        Assert.Equal(1, rows[0].ConnectionCount);
        Assert.Equal("—", rows[2].Upload);
        Assert.DoesNotContain("do-not-display", string.Join(" ", rows.Select(row => row.Endpoint)));
    }

    [Fact]
    public void DomainsRequireExactDestinationHostAndPortWithoutSniffingOrDnsGuessing()
    {
        var manager = new XrayProxyPanelManager();
        manager.Activate("""{"outbounds":[{"tag":"domain","protocol":"trojan","settings":{"servers":[{"address":"Proxy.Example.","port":443}]}}]}""");
        var connection = Connection("a", 0, 0);
        connection.metadata!.sniffHost = "proxy.example";
        Assert.Empty(manager.MatchTags(connection));
        connection.metadata.host = "other.proxy.example";
        Assert.Empty(manager.MatchTags(connection));
        connection.metadata.host = "PROXY.EXAMPLE";
        Assert.Equal(new[] { "domain" }, manager.MatchTags(connection));
        connection.metadata.destinationPort = "444";
        Assert.Empty(manager.MatchTags(connection));
    }

    [Fact]
    public void IpAndHostCandidatesAreCombinedInsteadOfChoosingArbitraryAttribution()
    {
        var manager = new XrayProxyPanelManager();
        manager.Activate("""{"outbounds":[{"tag":"ip","address":"192.0.2.1","port":443},{"tag":"domain","address":"proxy.example","port":443}]}""");
        var connection = Connection("a", 0, 0);
        connection.metadata!.host = "proxy.example";
        Assert.Equal(new[] { "ip", "domain" }, manager.MatchTags(connection));
        Assert.All(manager.GetRows([connection], Now), row => Assert.Equal(0, row.ConnectionCount));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"outbounds\":{}}")]
    public void InvalidConfigClearsPreviousSession(string json)
    {
        var manager = new XrayProxyPanelManager();
        manager.Activate(Single);
        var version = manager.Version;
        manager.Activate(json);
        Assert.False(manager.IsActive);
        Assert.True(manager.Version > version);
        Assert.Empty(manager.GetRows([], Now));
        Assert.Empty(manager.MatchTags(Connection("a", 0, 0)));
    }

    [Fact]
    public void MissingAndInvalidEndpointsStayVisibleButNeverMatch()
    {
        var manager = new XrayProxyPanelManager();
        manager.Activate("""{"outbounds":[{"tag":"missing"},{"tag":"invalid","settings":{"address":"192.0.2.1","port":70000}},{"tag":"typed","settings":{"address":123,"port":443}}]}""");
        var connections = new List<ConnectionItem> { new(), Connection("a", 100, 100, "192.0.2.1", "70000") };
        Assert.All(connections, connection => Assert.Empty(manager.MatchTags(connection)));
        Assert.Equal(new[] { "missing", "invalid", "typed" }, manager.GetRows(connections, Now).Select(row => row.Tag));
    }

    [Fact]
    public void LiveTotalsAndRatesExcludeClosedConnectionsAndNewConnectionHistoricalBytes()
    {
        var manager = new XrayProxyPanelManager();
        manager.Activate(Single);
        var first = manager.GetRows([Connection("a", 100, 200), Connection("closed", 500, 600)], Now).Single();
        Assert.Equal("600 B", first.Upload);
        Assert.Equal("—", first.UploadRate);
        var second = manager.GetRows([Connection("a", 160, 280), Connection("new", 300, 400)], Now.AddSeconds(2)).Single();
        Assert.Equal(2, second.ConnectionCount);
        Assert.Equal("460 B", second.Upload);
        Assert.Equal("680 B", second.Download);
        Assert.Equal("30 B/s", second.UploadRate);
        Assert.Equal("40 B/s", second.DownloadRate);
        var empty = manager.GetRows([], Now.AddSeconds(3)).Single();
        Assert.Equal("0 B", empty.Upload);
        Assert.Equal("0 B/s", empty.UploadRate);
    }

    [Fact]
    public void ReusedIdsCounterResetsAndMissingIdsDoNotProduceSpikes()
    {
        var manager = new XrayProxyPanelManager();
        manager.Activate(Single);
        manager.GetRows([Connection("same", 100, 200)], Now);
        var reused = Connection("same", 900, 950);
        reused.start = reused.start.AddSeconds(1);
        Assert.Equal("0 B/s", manager.GetRows([reused], Now.AddSeconds(1)).Single().UploadRate);
        reused.upload = 10;
        Assert.Equal("0 B/s", manager.GetRows([reused], Now.AddSeconds(2)).Single().UploadRate);
        reused.upload = 20;
        Assert.Equal("10 B/s", manager.GetRows([reused], Now.AddSeconds(3)).Single().UploadRate);
        var missing = Connection("", 1000, 1000);
        manager.GetRows([missing], Now.AddSeconds(4));
        missing.upload = 2000;
        Assert.Equal("0 B/s", manager.GetRows([missing], Now.AddSeconds(5)).Single().UploadRate);
    }

    [Fact]
    public void UnavailableApiAndReloadResetRateBaselines()
    {
        var manager = new XrayProxyPanelManager();
        manager.Activate(Single);
        manager.GetRows([Connection("a", 100, 200)], Now);
        var unavailable = manager.GetRows(null, Now.AddSeconds(1)).Single();
        Assert.Equal("—", unavailable.Upload);
        Assert.Equal("—", manager.GetRows([Connection("a", 900, 1000)], Now.AddSeconds(2)).Single().UploadRate);
        var version = manager.Version;
        manager.Activate(Single);
        Assert.True(manager.Version > version);
        Assert.Equal("—", manager.GetRows([Connection("a", 950, 1000)], Now.AddSeconds(3)).Single().UploadRate);
        manager.Reset();
        Assert.False(manager.IsActive);
        Assert.Empty(manager.GetRows([], Now.AddSeconds(4)));
    }

    [Fact]
    public void OldGenerationSampleCannotReplaceNewSessionBaseline()
    {
        var manager = new XrayProxyPanelManager();
        manager.Activate(Single);
        var oldVersion = manager.Version;
        manager.Activate(Single);
        manager.GetRows([Connection("a", 100, 200)], Now);
        Assert.Empty(manager.GetRows([Connection("a", 900, 1000)], Now.AddSeconds(1), oldVersion));
        Assert.Equal("10 B/s", manager.GetRows([Connection("a", 120, 240)], Now.AddSeconds(2), manager.Version).Single().UploadRate);
    }

    [Fact]
    public void OwnedProcessExitAndCancelledActivationLeaveNoStaleInventory()
    {
        var manager = new XrayProxyPanelManager();
        var activate = typeof(XrayProxyPanelManager).GetMethod("Activate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var running = true;
        activate.Invoke(manager, [Single, (Func<bool>)(() => running), manager.Version]);
        Assert.True(manager.IsActive);
        var activeVersion = manager.Version;
        running = false;
        Assert.False(manager.IsActive);
        Assert.True(manager.Version > activeVersion);
        Assert.Empty(manager.GetRows([], Now));
        running = true;
        activate.Invoke(manager, [Single, (Func<bool>)(() => running), activeVersion]);
        Assert.False(manager.IsActive);
    }

    [Fact]
    public void Ipv6AndRepeatedServersMatchOnceAndDuplicateApiRowsDoNotInflateTotals()
    {
        var manager = new XrayProxyPanelManager();
        manager.Activate("""{"outbounds":[{"tag":"v6","protocol":"socks","settings":{"servers":[{"address":"2001:db8::1","port":443},{"address":"[2001:db8::1]","port":443}]}}]}""");
        var connection = Connection("a", 100, 200, "2001:0db8:0:0:0:0:0:1");
        Assert.Equal(new[] { "v6" }, manager.MatchTags(connection));
        var row = manager.GetRows([connection, connection], Now).Single();
        Assert.Equal(1, row.ConnectionCount);
        Assert.Equal("100 B", row.Upload);
        Assert.Equal("[2001:db8::1]:443", row.Endpoint);
    }

    private static ConnectionItem Connection(string id, ulong upload, ulong download, string address = "192.0.2.1", string port = "443") => new()
    {
        id = id, upload = upload, download = download, start = Now.UtcDateTime,
        metadata = new MetadataItem { destinationIP = address, destinationPort = port, sourceIP = "127.0.0.1", sourcePort = "12345", network = "tcp" }
    };
}
