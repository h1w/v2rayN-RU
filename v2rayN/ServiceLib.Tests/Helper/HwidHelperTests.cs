using AwesomeAssertions;
using ServiceLib.Helper;
using Xunit;

namespace ServiceLib.Tests.Helper;

public class HwidHelperTests
{
    [Theory]
    [InlineData("4998793c-3ce5-4ac9-9dfa-0ef5417b00fa")]
    [InlineData("4998793c3ce54ac99dfa0ef5417b00fa")]
    [InlineData("890877189e13ca8b")]
    [InlineData("UE42LJXu4DbiCaBv")]
    [InlineData("AAAAAAAAAA")]
    [InlineData("abc=def-123")]
    public void IsValidHwid_ShouldAcceptValuesMatchingPanelRegex(string hwid)
    {
        HwidHelper.IsValidHwid(hwid).Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("tooshort")]
    [InlineData("has spaces here")]
    [InlineData("has_underscore_x")]
    [InlineData("привет-мир-1234")]
    public void IsValidHwid_ShouldRejectInvalidValues(string? hwid)
    {
        HwidHelper.IsValidHwid(hwid).Should().BeFalse();
    }

    [Fact]
    public void IsValidHwid_ShouldRejectValueLongerThan64Chars()
    {
        var tooLong = new string('a', 65);

        HwidHelper.IsValidHwid(tooLong).Should().BeFalse();
    }

    [Fact]
    public void IsValidHwid_ShouldAcceptValueOfExactly64Chars()
    {
        var maxLength = new string('a', 64);

        HwidHelper.IsValidHwid(maxLength).Should().BeTrue();
    }

    [Fact]
    public void GenerateHwid_WithHyphens_ShouldProduceValidUuid()
    {
        var hwid = HwidHelper.GenerateHwid(false);

        hwid.Should().MatchRegex("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$");
        HwidHelper.IsValidHwid(hwid).Should().BeTrue();
    }

    [Fact]
    public void GenerateHwid_WithoutHyphens_ShouldProduceValidHexString()
    {
        var hwid = HwidHelper.GenerateHwid(true);

        hwid.Should().MatchRegex("^[0-9a-f]{32}$");
        HwidHelper.IsValidHwid(hwid).Should().BeTrue();
    }

    [Fact]
    public void GenerateHwid_ShouldProduceUniqueValues()
    {
        var first = HwidHelper.GenerateHwid(false);
        var second = HwidHelper.GenerateHwid(false);

        first.Should().NotBe(second);
    }
}
