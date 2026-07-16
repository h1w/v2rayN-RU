using AwesomeAssertions;
using ServiceLib.Helper;
using ServiceLib.Models.Entities;
using Xunit;

namespace ServiceLib.Tests.Helper;

public class SubStatusHelperTests
{
    private static readonly DateTimeOffset Now = new(2026, 07, 16, 0, 0, 0, TimeSpan.Zero);

    private static long Unix(DateTimeOffset t) => t.ToUnixTimeSeconds();

    [Fact]
    public void Compute_ReturnsNull_WhenNoData()
    {
        SubStatusHelper.Compute(new SubItem(), Now).Should().BeNull();
        SubStatusHelper.Compute(null, Now).Should().BeNull();
    }

    [Fact]
    public void Compute_Normal_WithQuotaAndExpiry()
    {
        var sub = new SubItem
        {
            Upload = 100, Download = 200, Total = 1000,
            Expire = Unix(Now.AddDays(23))
        };
        var info = SubStatusHelper.Compute(sub, Now)!;
        info.Should().NotBeNull();
        info.UsedBytes.Should().Be(300);
        info.TotalBytes.Should().Be(1000);
        info.Unlimited.Should().BeFalse();
        info.Percent.Should().BeApproximately(0.3, 0.0001);
        info.HasExpire.Should().BeTrue();
        info.RemainDays.Should().Be(23);
        info.Expired.Should().BeFalse();
        info.Level.Should().Be(SubStatusLevel.Normal);
    }

    [Fact]
    public void Compute_Unlimited_WhenNoTotal()
    {
        var sub = new SubItem { Download = 500, Expire = Unix(Now.AddDays(10)) };
        var info = SubStatusHelper.Compute(sub, Now)!;
        info.Unlimited.Should().BeTrue();
        info.Percent.Should().Be(0);
        info.Level.Should().Be(SubStatusLevel.Normal);
    }

    [Fact]
    public void Compute_Warn_WhenFewDaysLeft()
    {
        var sub = new SubItem { Total = 1000, Download = 100, Expire = Unix(Now.AddDays(2)) };
        var info = SubStatusHelper.Compute(sub, Now)!;
        info.RemainDays.Should().Be(2);
        info.Level.Should().Be(SubStatusLevel.Warn);
    }

    [Fact]
    public void Compute_Warn_WhenNearQuota()
    {
        var sub = new SubItem { Total = 1000, Download = 950, Expire = Unix(Now.AddDays(30)) };
        var info = SubStatusHelper.Compute(sub, Now)!;
        info.Percent.Should().BeApproximately(0.95, 0.0001);
        info.Level.Should().Be(SubStatusLevel.Warn);
    }

    [Fact]
    public void Compute_Crit_WhenExpired()
    {
        var sub = new SubItem { Total = 1000, Download = 100, Expire = Unix(Now.AddDays(-1)) };
        var info = SubStatusHelper.Compute(sub, Now)!;
        info.Expired.Should().BeTrue();
        info.RemainDays.Should().Be(0);
        info.Level.Should().Be(SubStatusLevel.Crit);
    }

    [Fact]
    public void Compute_Crit_WhenOverQuota()
    {
        var sub = new SubItem { Total = 1000, Upload = 600, Download = 600 };
        var info = SubStatusHelper.Compute(sub, Now)!;
        info.Percent.Should().Be(1.0);
        info.Level.Should().Be(SubStatusLevel.Crit);
        info.HasExpire.Should().BeFalse();
    }
}
