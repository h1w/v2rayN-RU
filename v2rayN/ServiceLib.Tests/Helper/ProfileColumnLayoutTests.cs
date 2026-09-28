using AwesomeAssertions;
using Xunit;

namespace ServiceLib.Tests.Helper;

public class ProfileColumnLayoutTests
{
    [Fact]
    public void Restore_OldHiddenTotalsMoveBesideSpeedWithoutResettingOtherWidths()
    {
        var restored = ProfileColumnLayout.Restore(
            [Column("Remarks", 0, 240), Column("SpeedVal", 1, 90), Column("IpInfo", 2, 300),
             Column("TotalUp", 3, -1), Column("TotalDown", 4, -1)], Defaults());

        restored.Select(column => column.Name).Should().Equal(
            "Remarks", "SpeedVal", "TotalDown", "TotalUp", "CurrentDown", "CurrentUp", "IpInfo");
        restored.Single(column => column.Name == "Remarks").Width.Should().Be(240);
        restored.Single(column => column.Name == "IpInfo").Width.Should().Be(300);
        restored.Where(column => column.Name is "TotalDown" or "TotalUp" or "CurrentDown" or "CurrentUp")
            .Should().OnlyContain(column => column.Width >= 120);
    }

    [Fact]
    public void Restore_ModernLayoutKeepsUserOrderAndRepairsHiddenRequiredColumns()
    {
        var saved = Defaults();
        saved.Single(column => column.Name == "CurrentDown").Index = -1;
        saved.Single(column => column.Name == "TotalUp").Width = -1;
        var restored = ProfileColumnLayout.Restore(saved, Defaults());

        restored[0].Name.Should().Be("CurrentDown");
        restored.Single(column => column.Name == "TotalUp").Width.Should().BeGreaterThanOrEqualTo(120);
        ProfileColumnLayout.Restore(restored, Defaults()).Select(column => (column.Name, column.Index, column.Width))
            .Should().Equal(restored.Select(column => (column.Name, column.Index, column.Width)));
    }

    private static List<ColumnItem> Defaults() =>
        [Column("Remarks", 0, 100), Column("SpeedVal", 1, 100), Column("TotalDown", 2, 140),
         Column("TotalUp", 3, 140), Column("CurrentDown", 4, 140), Column("CurrentUp", 5, 140), Column("IpInfo", 6, 100)];

    private static ColumnItem Column(string name, int index, int width) => new() { Name = name, Index = index, Width = width };
}
