using System.Text.Json.Nodes;
using AwesomeAssertions;
using ServiceLib.Common;
using ServiceLib.Handler;
using ServiceLib.Models.Dto;
using Xunit;

namespace ServiceLib.Tests.Handler;

public class CustomSpeedtestConfigBuilderTests
{
    private const string XrayJson = """
    {
      "inbounds": [ { "tag": "socks-in", "port": 10808, "protocol": "socks" } ],
      "outbounds": [
        { "protocol": "vless", "tag": "proxyA" },
        { "protocol": "shadowsocks", "tag": "proxyB" },
        { "protocol": "freedom", "tag": "direct" }
      ],
      "routing": { "domainStrategy": "IPIfNonMatch", "rules": [ { "type": "field", "outboundTag": "direct" } ] }
    }
    """;

    private static List<(OutboundTestTarget, int)> Targets()
        => new()
        {
            (new OutboundTestTarget("proxyA", 0, System.Array.Empty<string>()), 20001),
            (new OutboundTestTarget("proxyB", 1, System.Array.Empty<string>()), 20002),
        };

    [Fact]
    public void Build_Xray_pins_one_inbound_per_outbound()
    {
        var json = CustomSpeedtestConfigBuilder.Build(XrayJson, ECoreType.Xray, Targets());
        var root = JsonUtils.ParseJson(json)!;

        var inbounds = root["inbounds"]!.AsArray();
        inbounds.Should().HaveCount(2);
        inbounds[0]!["port"]!.GetValue<int>().Should().Be(20001);
        inbounds[0]!["tag"]!.GetValue<string>().Should().Be("in-proxyA");

        var rules = root["routing"]!["rules"]!.AsArray();
        rules.Should().HaveCount(2);
        rules[0]!["inboundTag"]!.AsArray()[0]!.GetValue<string>().Should().Be("in-proxyA");
        rules[0]!["outboundTag"]!.GetValue<string>().Should().Be("proxyA");

        // outbounds preserved so chains stay intact
        root["outbounds"]!.AsArray().Should().HaveCount(3);
    }

    [Fact]
    public void Build_null_returns_empty()
    {
        CustomSpeedtestConfigBuilder.Build(null, ECoreType.Xray, Targets()).Should().BeEmpty();
    }
}
