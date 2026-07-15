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

    private const string SingboxJson = """
    {
      "outbounds": [
        { "type": "vless", "tag": "proxy" },
        { "type": "direct", "tag": "direct" },
        { "type": "selector", "tag": "auto", "outbounds": ["proxy"] }
      ],
      "route": {
        "rules": [
          { "outbound": "direct", "domain_suffix": ["cn"], "geosite": ["cn"], "port": [443] },
          { "action": "reject", "domain_keyword": ["ads"] },
          { "outbound": "proxy", "ip_cidr": ["8.8.8.8/32"], "network": ["tcp"], "protocol": ["tls"] }
        ]
      }
    }
    """;

    [Fact]
    public void ParseDisplayRules_Singbox_maps_fields_and_actions()
    {
        var rules = CustomConfigParser.ParseDisplayRules(SingboxJson, ECoreType.sing_box);

        rules.Should().HaveCount(3);
        rules[0].OutboundTag.Should().Be("direct");
        rules[0].Domain.Should().Contain("domain:cn");
        rules[0].Domain.Should().Contain("geosite:cn");
        rules[0].Port.Should().Be("443");
        rules[1].OutboundTag.Should().Be("block");     // action=reject maps to block tag
        rules[1].Domain.Should().Contain("keyword:ads");
        rules[2].OutboundTag.Should().Be("proxy");
        rules[2].Ip.Should().Contain("8.8.8.8/32");
        rules[2].Network.Should().Be("tcp");
        rules[2].Protocol.Should().Contain("tls");
    }

    [Fact]
    public void ParseTestableOutbounds_Xray_excludes_utility_keeps_order()
    {
        var targets = CustomConfigParser.ParseTestableOutbounds(XrayJson, ECoreType.Xray);
        targets.Should().ContainSingle();
        targets[0].Tag.Should().Be("proxy");
        targets[0].Order.Should().Be(0);
    }

    [Fact]
    public void ParseTestableOutbounds_Singbox_excludes_utility_and_group()
    {
        var targets = CustomConfigParser.ParseTestableOutbounds(SingboxJson, ECoreType.sing_box);
        targets.Should().ContainSingle();
        targets[0].Tag.Should().Be("proxy");   // direct + selector excluded
    }

    private const string XrayChainJson = """
    {
      "outbounds": [
        { "protocol": "vless", "tag": "front", "streamSettings": { "sockopt": { "dialerProxy": "bridge" } } },
        { "protocol": "shadowsocks", "tag": "bridge" },
        { "protocol": "freedom", "tag": "direct" }
      ]
    }
    """;

    private const string SingboxChainJson = """
    {
      "outbounds": [
        { "type": "vless", "tag": "front", "detour": "bridge" },
        { "type": "shadowsocks", "tag": "bridge" },
        { "type": "direct", "tag": "direct" }
      ]
    }
    """;

    [Fact]
    public void ParseTestableOutbounds_Xray_resolves_dialerProxy_chain()
    {
        var targets = CustomConfigParser.ParseTestableOutbounds(XrayChainJson, ECoreType.Xray);
        var front = targets.Single(t => t.Tag == "front");
        front.ChainTags.Should().Contain("bridge");
        front.ChainTags.Should().NotContain("front");
    }

    [Fact]
    public void ParseTestableOutbounds_Singbox_resolves_detour_chain()
    {
        var targets = CustomConfigParser.ParseTestableOutbounds(SingboxChainJson, ECoreType.sing_box);
        var front = targets.Single(t => t.Tag == "front");
        front.ChainTags.Should().Contain("bridge");
    }

    [Fact]
    public void GetOutboundNodesByTag_returns_cloned_nodes_keyed_by_tag()
    {
        var map = CustomConfigParser.GetOutboundNodesByTag(XrayChainJson, ECoreType.Xray);
        map.Keys.Should().BeEquivalentTo(new[] { "front", "bridge", "direct" });
        map["front"]!["protocol"]!.GetValue<string>().Should().Be("vless");
        // returned nodes are detached clones (no parent) so callers can re-parent them
        map["front"].Parent.Should().BeNull();
    }
}
