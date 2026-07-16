using AwesomeAssertions;
using ServiceLib.Helper;
using Xunit;

namespace ServiceLib.Tests.Helper;

public class SubscriptionInfoHelperTests
{
    [Fact]
    public void ParseUserinfo_ParsesAllFields()
    {
        var info = SubscriptionInfoHelper.ParseUserinfo("upload=100; download=200; total=1000; expire=1700000000");
        info.Should().NotBeNull();
        info!.Upload.Should().Be(100);
        info.Download.Should().Be(200);
        info.Total.Should().Be(1000);
        info.Expire.Should().Be(1700000000);
    }

    [Fact]
    public void ParseUserinfo_ParsesSubset()
    {
        var info = SubscriptionInfoHelper.ParseUserinfo("download=50; total=500");
        info.Should().NotBeNull();
        info!.Upload.Should().BeNull();
        info.Download.Should().Be(50);
        info.Total.Should().Be(500);
        info.Expire.Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("garbage-without-known-keys")]
    public void ParseUserinfo_ReturnsNullForNoData(string? header)
    {
        SubscriptionInfoHelper.ParseUserinfo(header).Should().BeNull();
    }

    [Fact]
    public void DecodeProfileTitle_DecodesBase64Prefix()
    {
        // "My Group" => TXkgR3JvdXA=
        SubscriptionInfoHelper.DecodeProfileTitle("base64:TXkgR3JvdXA=").Should().Be("My Group");
    }

    [Fact]
    public void DecodeProfileTitle_ReturnsPlainAsIs()
    {
        SubscriptionInfoHelper.DecodeProfileTitle("Plain Name").Should().Be("Plain Name");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void DecodeProfileTitle_ReturnsNullForEmpty(string? header)
    {
        SubscriptionInfoHelper.DecodeProfileTitle(header).Should().BeNull();
    }
}
