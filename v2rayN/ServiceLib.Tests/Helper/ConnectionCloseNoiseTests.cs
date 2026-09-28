using ServiceLib.Helper;
using Xunit;

namespace ServiceLib.Tests.Helper;

public class ConnectionCloseNoiseTests
{
    private const string Close = "+0700 2026-09-28 13:30:55 ERROR [87077946 1.61s] connection: connection download closed: close tcp 172.18.0.1:64631->81.163.22.220:5012: endpoint not connected";

    [Theory]
    [InlineData(Close)]
    [InlineData(Close + "\r\n")]
    [InlineData("connection: connection download closed: close tcp [::1]:64631->[2001:db8::1]:443: endpoint not connected")]
    public void RecognizesOnlyTheTerminalTcpDownloadClose(string message)
    {
        Assert.True(ConnectionCloseNoise.IsNoise(message));
    }

    [Theory]
    [InlineData("dial tcp 81.163.22.220:5012: endpoint not connected")]
    [InlineData("connect: endpoint not connected")]
    [InlineData("TLS handshake: endpoint not connected")]
    [InlineData("connection download closed: read tcp 172.18.0.1:64631->81.163.22.220:5012: endpoint not connected")]
    [InlineData("connection upload closed: close tcp 172.18.0.1:64631->81.163.22.220:5012: endpoint not connected")]
    [InlineData("connection download closed: close tcp 172.18.0.1:64631->81.163.22.220:5012: i/o timeout")]
    [InlineData("connection download closed: close tcp 172.18.0.1:64631->81.163.22.220:5012: endpoint not connected: TLS failure")]
    [InlineData("connection download closed: close tcp not-an-endpoint: endpoint not connected")]
    [InlineData(Close + "\nTLS handshake timeout")]
    [InlineData("dial tcp failed; " + Close)]
    public void LeavesOtherErrorsAndMixedMessagesVisible(string message)
    {
        Assert.False(ConnectionCloseNoise.IsNoise(message));
    }

    [Fact]
    public void CountsAddressVariantsTogetherAndDrainsOnlyAfterWindow()
    {
        var clock = new ManualTimeProvider();
        var noise = new ConnectionCloseNoise(clock);
        Assert.True(noise.TryCollapse(Close, explicitFilter: false));
        Assert.True(noise.TryCollapse(Close.Replace("64631", "64632").Replace("81.163.22.220", "192.0.2.8"), explicitFilter: false));
        clock.Advance(TimeSpan.FromSeconds(29));
        Assert.Equal(0L, noise.TakeSummaryCount());
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(2L, noise.TakeSummaryCount());
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(0L, noise.TakeSummaryCount());
        Assert.True(noise.TryCollapse(Close, explicitFilter: false));
        Assert.Equal(0L, noise.TakeSummaryCount());
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(1L, noise.TakeSummaryCount());
    }

    [Fact]
    public void ExplicitFilterShowsRawFutureMessagesWithoutCountingThem()
    {
        var clock = new ManualTimeProvider();
        var noise = new ConnectionCloseNoise(clock);
        Assert.False(noise.TryCollapse(Close, explicitFilter: true));
        Assert.False(noise.TryCollapse("TLS handshake timeout", explicitFilter: false));
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(0L, noise.TakeSummaryCount());
        Assert.True(noise.TryCollapse(Close, explicitFilter: false));
        Assert.False(noise.TryCollapse(Close, explicitFilter: true));
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(1L, noise.TakeSummaryCount());
    }

    [Fact]
    public void ConcurrentProducersAndSummaryDrainDoNotLoseCounts()
    {
        var clock = new ManualTimeProvider();
        var noise = new ConnectionCloseNoise(clock);
        long total = 0;
        Parallel.For(0, 2000, _ =>
        {
            noise.TryCollapse(Close, explicitFilter: false);
            clock.Advance(TimeSpan.FromSeconds(30));
            Interlocked.Add(ref total, noise.TakeSummaryCount());
        });
        clock.Advance(TimeSpan.FromSeconds(30));
        total += noise.TakeSummaryCount();
        Assert.Equal(2000L, total);
    }

    [Fact]
    public void DisabledDiagnosticLoggingLeavesOriginalVisibleAndDoesNotCount()
    {
        var clock = new ManualTimeProvider();
        var noise = new ConnectionCloseNoise(clock);
        using (NLog.LogManager.SuspendLogging())
        {
            Assert.False(noise.TryCollapse(Close, explicitFilter: false));
            clock.Advance(TimeSpan.FromSeconds(30));
            Assert.Equal(0L, noise.TakeSummaryCount());
        }
        Assert.True(noise.TryCollapse(Close, explicitFilter: false));
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(1L, noise.TakeSummaryCount());
    }

    [Fact]
    public void DiagnosticLogRetainsExactOriginalsWithAndWithoutCollapse()
    {
        var previous = NLog.LogManager.Configuration;
        var target = new NLog.Targets.MemoryTarget("close-noise-test") { Layout = "${message}" };
        var configuration = new NLog.Config.LoggingConfiguration();
        configuration.AddRuleForAllLevels(target);
        try
        {
            NLog.LogManager.Configuration = configuration;
            var noise = new ConnectionCloseNoise();
            var original = Close + "\r\n";
            Assert.True(noise.TryCollapse(original, explicitFilter: false));
            Assert.False(noise.TryCollapse(original, explicitFilter: true));
            Assert.False(noise.TryCollapse("TLS handshake timeout", explicitFilter: false));
            NLog.LogManager.Flush();
            Assert.Equal(new[] { original, original }, target.Logs);
        }
        finally
        {
            NLog.LogManager.Configuration = previous;
            target.Dispose();
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public void Advance(TimeSpan duration) => Interlocked.Add(ref _ticks, duration.Ticks);
    }
}
