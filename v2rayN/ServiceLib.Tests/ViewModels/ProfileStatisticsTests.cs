using AwesomeAssertions;
using ReactiveUI.Builder;
using Xunit;

namespace ServiceLib.Tests.ViewModels;

public class ProfileStatisticsTests
{
    static ProfileStatisticsTests() => RxAppBuilder.CreateReactiveUIBuilder().WithCoreServices().BuildApp();

    [Fact]
    public void Statistics_UsesRunningSessionWhileConfiguredProfileChanges()
    {
        var config = new Config { IndexId = "pending" };
        var running = new ProfileItemModel { IndexId = "running" };
        var pending = new ProfileItemModel { IndexId = config.IndexId };
        var activeIds = new HashSet<string> { "running" };
        var update = new ServerSpeedItem { IndexId = "running", TotalDown = 4096, ProxyDownRate = 1024 };

        running.ApplyStatistics(update, activeIds);
        pending.ApplyStatistics(update, activeIds);

        running.CurrentDown.Should().Be($"{1.0:f1} KB/s");
        running.TotalDown.Should().Be($"{4.0:f1} MB");
        pending.CurrentDown.Should().BeEmpty();
        pending.CurrentUp.Should().BeEmpty();

        running.ApplyStatistics(update, new HashSet<string>());
        running.CurrentDown.Should().BeEmpty();
        running.TotalDown.Should().Be($"{4.0:f1} MB");
    }

    [Fact]
    public void Statistics_IncludePersistedRemaindersBelowOneKilobyte()
    {
        var row = new ProfileItemModel { IndexId = "running" };
        row.ApplyStatistics(new ServerSpeedItem
        {
            IndexId = "running", TotalDownBytesRemainder = 512, TotalUp = 1, TotalUpBytesRemainder = 512
        }, new HashSet<string> { "running" });

        row.TotalDown.Should().Be($"{512.0:f1} B");
        row.TotalUp.Should().Be($"{1.5:f1} KB");
        row.CurrentDown.Should().BeEmpty();
    }

    [Fact]
    public void Statistics_AlternatingMainAndAuxiliarySamplesKeepEveryRunningRate()
    {
        var main = new ProfileItemModel { IndexId = "main" };
        var aux = new ProfileItemModel { IndexId = "aux" };
        var otherAux = new ProfileItemModel { IndexId = "other-aux" };
        var selected = new ProfileItemModel { IndexId = "selected", TotalDown = "saved total" };
        ProfileItemModel[] rows = [main, aux, otherAux, selected];
        var activeIds = new HashSet<string> { "main", "aux", "other-aux" };
        ServerSpeedItem[] updates =
        [
            new() { IndexId = "main", TotalDown = 2048, TotalUp = 1024, ProxyDownRate = 512, ProxyUpRate = 2048 },
            new() { IndexId = "aux", TotalDown = 512, ProxyDownRate = 256, ProxyUpRate = 128 },
            new() { IndexId = "other-aux", TotalDown = 256, ProxyDownRate = 64, ProxyUpRate = 0 },
            new() { IndexId = "main", TotalDown = 3072, TotalUp = 1024, ProxyDownRate = 1024, ProxyUpRate = 2048 }
        ];
        foreach (var update in updates)
        {
            foreach (var row in rows)
            {
                row.ApplyStatistics(update, activeIds);
            }
        }

        main.TotalDown.Should().Be($"{3.0:f1} MB");
        main.TotalUp.Should().Be($"{1.0:f1} MB");
        main.CurrentDown.Should().Be($"{1.0:f1} KB/s");
        main.CurrentUp.Should().Be($"{2.0:f1} KB/s");
        aux.TotalDown.Should().Be($"{512.0:f1} KB");
        aux.CurrentDown.Should().Be($"{256.0:f1} B/s");
        aux.CurrentUp.Should().Be($"{128.0:f1} B/s");
        otherAux.CurrentDown.Should().Be($"{64.0:f1} B/s");
        otherAux.CurrentUp.Should().Be($"{0.0:f1} B/s");
        selected.CurrentDown.Should().BeEmpty();
        selected.TotalDown.Should().Be("saved total");
    }

    [Fact]
    public void Statistics_UnavailableAuxiliarySampleClearsOnlyItsRates()
    {
        var main = new ProfileItemModel { IndexId = "main" };
        var aux = new ProfileItemModel { IndexId = "aux" };
        var activeIds = new HashSet<string> { "main", "aux" };
        main.ApplyStatistics(new ServerSpeedItem { IndexId = "main", ProxyDownRate = 1024 }, activeIds);
        aux.ApplyStatistics(new ServerSpeedItem { IndexId = "aux", TotalDown = 4096, ProxyDownRate = 0, ProxyUpRate = 0 }, activeIds);
        aux.CurrentDown.Should().Be($"{0.0:f1} B/s");
        aux.CurrentUp.Should().Be($"{0.0:f1} B/s");

        var unavailable = new ServerSpeedItem { IndexId = "aux", TotalDown = 4096 };
        main.ApplyStatistics(unavailable, activeIds);
        aux.ApplyStatistics(unavailable, activeIds);

        main.CurrentDown.Should().Be($"{1.0:f1} KB/s");
        aux.CurrentDown.Should().BeEmpty();
        aux.CurrentUp.Should().BeEmpty();
        aux.TotalDown.Should().Be($"{4.0:f1} MB");
    }

    [Fact]
    public void Statistics_StoppingOneSourceKeepsOtherRatesAndSavedTotals()
    {
        var main = new ProfileItemModel { IndexId = "main" };
        var aux = new ProfileItemModel { IndexId = "aux" };
        var activeIds = new HashSet<string> { "main", "aux" };
        var mainUpdate = new ServerSpeedItem { IndexId = "main", TotalDown = 4096, ProxyDownRate = 1024 };
        main.ApplyStatistics(mainUpdate, activeIds);
        aux.ApplyStatistics(new ServerSpeedItem { IndexId = "aux", TotalDown = 512, ProxyDownRate = 256, ProxyUpRate = 128 }, activeIds);
        activeIds.Remove("aux");

        main.ApplyStatistics(mainUpdate, activeIds);
        aux.ApplyStatistics(mainUpdate, activeIds);
        var stopped = new ServerSpeedItem { IndexId = "aux", TotalDown = 512 };
        main.ApplyStatistics(stopped, activeIds);
        aux.ApplyStatistics(stopped, activeIds);

        main.CurrentDown.Should().Be($"{1.0:f1} KB/s");
        aux.CurrentDown.Should().BeEmpty();
        aux.CurrentUp.Should().BeEmpty();
        aux.TotalDown.Should().Be($"{512.0:f1} KB");
    }

    [Fact]
    public void Statistics_GlobalStopClearsAllRatesWithoutErasingSavedTotals()
    {
        var main = new ProfileItemModel { IndexId = "main" };
        var aux = new ProfileItemModel { IndexId = "aux" };
        var activeIds = new HashSet<string> { "main", "aux" };
        main.ApplyStatistics(new ServerSpeedItem { IndexId = "main", TotalDown = 4096, ProxyDownRate = 1024, ProxyUpRate = 512 }, activeIds);
        aux.ApplyStatistics(new ServerSpeedItem { IndexId = "aux", TotalDown = 512, ProxyDownRate = 256, ProxyUpRate = 128 }, activeIds);
        activeIds.Clear();

        var stopped = new ServerSpeedItem { IndexId = "main", TotalDown = 4096 };
        main.ApplyStatistics(stopped, activeIds);
        aux.ApplyStatistics(stopped, activeIds);

        main.CurrentDown.Should().BeEmpty();
        main.CurrentUp.Should().BeEmpty();
        aux.CurrentDown.Should().BeEmpty();
        aux.CurrentUp.Should().BeEmpty();
        main.TotalDown.Should().Be($"{4.0:f1} MB");
        aux.TotalDown.Should().Be($"{512.0:f1} KB");
    }

    [Fact]
    public void Statistics_ProfileSwitchClearsOldRatesAndLateOldSampleDoesNotClearNewRates()
    {
        var oldRow = new ProfileItemModel { IndexId = "old" };
        var newRow = new ProfileItemModel { IndexId = "new" };
        oldRow.ApplyStatistics(new ServerSpeedItem { IndexId = "old", ProxyDownRate = 1024 }, new HashSet<string> { "old" });
        var activeIds = new HashSet<string> { "new" };
        var update = new ServerSpeedItem { IndexId = "new", ProxyDownRate = 4096 };
        oldRow.ApplyStatistics(update, activeIds);
        newRow.ApplyStatistics(update, activeIds);
        newRow.ApplyStatistics(new ServerSpeedItem { IndexId = "old" }, activeIds);
        oldRow.ApplyStatistics(new ServerSpeedItem { IndexId = "old", ProxyDownRate = 1024 }, activeIds);

        oldRow.CurrentDown.Should().BeEmpty();
        newRow.CurrentDown.Should().Be($"{4.0:f1} KB/s");
    }
}
