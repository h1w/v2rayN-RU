using AwesomeAssertions;
using Xunit;

namespace ServiceLib.Tests.Handler;

public class ChainConfigBuilderTests
{
    private const string XrayJson = """
    {
      "log": { "loglevel": "warning" },
      "inbounds": [
        { "tag": "socks-in", "port": 10808, "protocol": "socks" },
        { "tag": "http-in", "port": 10809, "protocol": "http" }
      ],
      "outbounds": [
        { "protocol": "vless", "tag": "main-out" },
        { "protocol": "freedom", "tag": "direct" }
      ],
      "routing": {
        "rules": [ { "type": "field", "outboundTag": "direct", "domain": ["geosite:cn"] } ]
      }
    }
    """;

    private const string SingboxJson = """
    {
      "log": { "level": "debug" },
      "inbounds": [ { "type": "socks", "tag": "socks-in", "listen_port": 10808 } ],
      "outbounds": [
        { "type": "vless", "tag": "main-out" },
        { "type": "direct", "tag": "direct" }
      ],
      "route": { "rules": [], "final": "main-out" }
    }
    """;

    [Fact]
    public void Build_xray_replaces_inbounds_with_single_socks_on_given_port()
    {
        var result = ChainConfigBuilder.Build(XrayJson, ECoreType.Xray, 34567);

        result.Should().NotBeNull();
        var json = JsonUtils.ParseJson(result);
        json.Should().NotBeNull();

        var inbounds = json!["inbounds"]?.AsArray();
        inbounds.Should().NotBeNull();
        inbounds!.Count.Should().Be(1);
        inbounds[0]!["port"]!.GetValue<int>().Should().Be(34567);
        inbounds[0]!["protocol"]!.GetValue<string>().Should().Be("socks");
        inbounds[0]!["listen"]!.GetValue<string>().Should().Be(Global.Loopback);
        // xray: protocol/port, НЕ sing-box-форма type/listen_port.
        inbounds[0]?["type"].Should().BeNull();
        inbounds[0]?["listen_port"].Should().BeNull();
    }

    [Fact]
    public void Build_xray_preserves_outbounds_routing_and_log()
    {
        var result = ChainConfigBuilder.Build(XrayJson, ECoreType.Xray, 34567);

        var json = JsonUtils.ParseJson(result);
        json.Should().NotBeNull();

        // Всё, ради чего цепочка и существует: выходы, правила и балансеры цели целы.
        var outbounds = json!["outbounds"]?.AsArray();
        outbounds.Should().NotBeNull();
        outbounds!.Count.Should().Be(2);
        outbounds[0]!["tag"]!.GetValue<string>().Should().Be("main-out");

        var rules = json["routing"]?["rules"]?.AsArray();
        rules.Should().NotBeNull();
        rules!.Count.Should().Be(1);
        rules[0]!["domain"]![0]!.GetValue<string>().Should().Be("geosite:cn");

        var log = json["log"];
        log.Should().NotBeNull();
        log!["loglevel"]!.GetValue<string>().Should().Be("warning");
    }

    [Fact]
    public void Build_xray_preserves_balancers_observatory_and_dns()
    {
        // Балансеры, observatory и dns — то, ради чего auto-конфиги (и вся
        // цепочечная фича) вообще существуют; фикстуры выше их не содержат.
        const string json = """
        {
          "outbounds": [ { "protocol": "vless", "tag": "main-out" } ],
          "routing": {
            "rules": [],
            "balancers": [ { "tag": "balancer-1", "selector": ["main-out"] } ]
          },
          "observatory": { "subjectSelector": ["main-out"] },
          "dns": { "servers": ["8.8.8.8"] }
        }
        """;

        var result = ChainConfigBuilder.Build(json, ECoreType.Xray, 34569);

        var parsed = JsonUtils.ParseJson(result);
        parsed.Should().NotBeNull();

        var balancers = parsed!["routing"]?["balancers"]?.AsArray();
        balancers.Should().NotBeNull();
        balancers!.Count.Should().Be(1);
        balancers[0]!["tag"]!.GetValue<string>().Should().Be("balancer-1");

        var observatory = parsed["observatory"];
        observatory.Should().NotBeNull();
        observatory!["subjectSelector"]![0]!.GetValue<string>().Should().Be("main-out");

        var dns = parsed["dns"];
        dns.Should().NotBeNull();
        dns!["servers"]![0]!.GetValue<string>().Should().Be("8.8.8.8");
    }

    [Fact]
    public void Build_singbox_replaces_inbounds_with_single_socks_and_preserves_route()
    {
        var result = ChainConfigBuilder.Build(SingboxJson, ECoreType.sing_box, 34568);

        var json = JsonUtils.ParseJson(result);
        json.Should().NotBeNull();

        var inbounds = json!["inbounds"]?.AsArray();
        inbounds.Should().NotBeNull();
        inbounds!.Count.Should().Be(1);
        // sing-box: type/listen_port, НЕ xray-форма protocol/port.
        inbounds[0]!["type"]!.GetValue<string>().Should().Be("socks");
        inbounds[0]!["listen_port"]!.GetValue<int>().Should().Be(34568);
        inbounds[0]!["listen"]!.GetValue<string>().Should().Be(Global.Loopback);
        inbounds[0]?["protocol"].Should().BeNull();
        inbounds[0]?["port"].Should().BeNull();

        var route = json["route"];
        route.Should().NotBeNull();
        route!["final"]!.GetValue<string>().Should().Be("main-out");
    }

    [Fact]
    public void Build_returns_null_for_unusable_json()
    {
        ChainConfigBuilder.Build(null, ECoreType.Xray, 1080).Should().BeNull();
        ChainConfigBuilder.Build("not json", ECoreType.Xray, 1080).Should().BeNull();
        // Без outbounds цепочке нечем ходить наружу.
        ChainConfigBuilder.Build("""{ "inbounds": [] }""", ECoreType.Xray, 1080).Should().BeNull();
        // Ключ outbounds есть, но пуст — нечем ходить наружу точно так же.
        ChainConfigBuilder.Build("""{ "outbounds": [] }""", ECoreType.Xray, 1080).Should().BeNull();
    }
}
