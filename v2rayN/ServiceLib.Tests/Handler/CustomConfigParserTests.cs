using AwesomeAssertions;
using ServiceLib.Handler;
using Xunit;

namespace ServiceLib.Tests.Handler;

public class CustomConfigParserTests
{
    private const string XrayJson = """
    {
      "outbounds": [
        { "protocol": "vless", "tag": "proxy" },
        { "protocol": "freedom", "tag": "direct" }
      ],
      "routing": {
        "rules": [
          { "type": "field", "outboundTag": "direct", "domain": ["geosite:cn"], "port": "443" },
          { "type": "field", "outboundTag": "proxy", "ip": ["0.0.0.0/0"], "network": "tcp,udp" }
        ]
      }
    }
    """;

    [Fact]
    public void ParseDisplayRules_Xray_maps_fields_in_order()
    {
        var rules = CustomConfigParser.ParseDisplayRules(XrayJson, ECoreType.Xray);

        rules.Should().HaveCount(2);
        rules[0].OutboundTag.Should().Be("direct");
        rules[0].Domain.Should().ContainSingle().Which.Should().Be("geosite:cn");
        rules[0].Port.Should().Be("443");
        rules[0].Enabled.Should().BeTrue();
        rules[1].OutboundTag.Should().Be("proxy");
        rules[1].Ip.Should().ContainSingle().Which.Should().Be("0.0.0.0/0");
        rules[1].Network.Should().Be("tcp,udp");
    }

    [Fact]
    public void ParseDisplayRules_null_or_garbage_returns_empty()
    {
        CustomConfigParser.ParseDisplayRules(null, ECoreType.Xray).Should().BeEmpty();
        CustomConfigParser.ParseDisplayRules("not json", ECoreType.Xray).Should().BeEmpty();
    }
}
