using ServiceLib.Common;
using ServiceLib.Handler.Fmt;
using Xunit;

namespace ServiceLib.Tests.Fmt;

public class ShareUriQueryTests
{
    [Fact]
    public void ParseQueryString_PreservesEqualsSignInsideValue()
    {
        var query = "?key=value=with=equals&foo=bar";
        var parsed = Utils.ParseQueryString(query);

        Assert.Equal("value=with=equals", parsed["key"]);
        Assert.Equal("bar", parsed["foo"]);
    }

    [Fact]
    public void Hysteria2Fmt_DefaultsPortTo443WhenOmitted()
    {
        var uri = "hysteria2://password@example.com?insecure=0#remarks";
        var profile = Hysteria2Fmt.Resolve(uri, out _);

        Assert.NotNull(profile);
        Assert.Equal("example.com", profile.Address);
        Assert.Equal(443, profile.Port);
    }

    [Fact]
    public void Hysteria2Fmt_PreservesExplicitPort()
    {
        var uri = "hysteria2://password@example.com:8443?insecure=0#remarks";
        var profile = Hysteria2Fmt.Resolve(uri, out _);

        Assert.NotNull(profile);
        Assert.Equal("example.com", profile.Address);
        Assert.Equal(8443, profile.Port);
    }
}
