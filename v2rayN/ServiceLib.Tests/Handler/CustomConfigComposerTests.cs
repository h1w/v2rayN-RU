using AwesomeAssertions;
using ServiceLib.Handler;
using ServiceLib.Tests.CoreConfig;
using Xunit;

namespace ServiceLib.Tests.Handler;

public class CustomConfigComposerTests
{
    private const string XrayJson = """
    {
      "inbounds": [{ "port": 10808, "protocol": "socks" }],
      "outbounds": [
        { "protocol": "vless", "tag": "main-out" },
        { "protocol": "freedom", "tag": "direct" }
      ],
      "routing": {
        "rules": [
          { "type": "field", "outboundTag": "direct", "domain": ["geosite:cn"] }
        ]
      }
    }
    """;

    private static CoreConfigContext EmptyContext(ECoreType coreType)
    {
        var config = CoreConfigTestFactory.CreateConfig(coreType);
        CoreConfigTestFactory.BindAppManagerConfig(config);
        var node = CoreConfigTestFactory.CreateVmessNode(coreType);
        return CoreConfigTestFactory.CreateContext(config, node, coreType);
    }

    [Fact]
    public void Compose_signals_fallback_for_unusable_json()
    {
        var ctx = EmptyContext(ECoreType.Xray);

        CustomConfigComposer.Compose(null, ECoreType.Xray, ctx).Json.Should().BeNull();
        CustomConfigComposer.Compose("not json", ECoreType.Xray, ctx).Json.Should().BeNull();
        CustomConfigComposer.Compose("""{ "routing": { "rules": [] } }""", ECoreType.Xray, ctx).Json.Should().BeNull();
    }

    [Fact]
    public void Compose_preserves_untouched_sections()
    {
        var ctx = EmptyContext(ECoreType.Xray);

        var merged = CustomConfigComposer.Compose(XrayJson, ECoreType.Xray, ctx).Json;
        merged.Should().NotBeNull();

        var json = JsonUtils.ParseJson(merged);
        json.Should().NotBeNull();

        // Каждый узел сначала проверяется на null отдельным утверждением: если
        // индексировать через `?.` и повесить `.Should()` на хвост той же цепочки,
        // то при null где-то в середине вся цепочка (включая .Should()) молча
        // не выполнится и тест ложно позеленеет.

        // Инбаунды пользователя не трогаем.
        var inboundsNode = json!["inbounds"];
        inboundsNode.Should().NotBeNull();
        var inbounds = inboundsNode!.AsArray();
        inbounds.Should().HaveCount(1);

        var inbound0 = inbounds[0];
        inbound0.Should().NotBeNull();
        var port = inbound0!["port"];
        port.Should().NotBeNull();
        port!.GetValue<int>().Should().Be(10808);

        // Правила пользователя остаются на месте и первыми.
        var rulesNode = json["routing"]?["rules"];
        rulesNode.Should().NotBeNull();
        var rules = rulesNode!.AsArray();
        rules.Should().NotBeEmpty();

        var rule0 = rules[0];
        rule0.Should().NotBeNull();
        var outboundTag = rule0!["outboundTag"];
        outboundTag.Should().NotBeNull();
        outboundTag!.GetValue<string>().Should().Be("direct");
    }

    [Fact]
    public void Compose_signals_fallback_for_empty_outbounds_array()
    {
        var ctx = EmptyContext(ECoreType.Xray);

        var result = CustomConfigComposer.Compose("""{ "outbounds": [] }""", ECoreType.Xray, ctx);

        result.Json.Should().BeNull();
    }

    [Fact]
    public void Compose_signals_fallback_for_non_array_outbounds()
    {
        var ctx = EmptyContext(ECoreType.Xray);

        // "outbounds": {} — правдоподобная форма руками правленного JSON.
        // JsonNode.AsArray() бросает InvalidOperationException, которую обязан
        // погасить внешний catch в Compose.
        var result = CustomConfigComposer.Compose("""{ "outbounds": {} }""", ECoreType.Xray, ctx);

        result.Json.Should().BeNull();
    }

    [Fact]
    public void Compose_signals_fallback_when_outbounds_have_no_proxy()
    {
        var ctx = EmptyContext(ECoreType.Xray);

        // freedom/blackhole — служебные xray-протоколы (CustomConfigParser._xrayUtility),
        // ParseTestableOutbounds не считает их proxy-выходами, поэтому mainProxyTag
        // не находится и Compose обязан откатиться на дословное копирование.
        const string utilityOnlyJson = """
        {
          "outbounds": [
            { "protocol": "freedom", "tag": "direct" },
            { "protocol": "blackhole", "tag": "block" }
          ]
        }
        """;

        var result = CustomConfigComposer.Compose(utilityOnlyJson, ECoreType.Xray, ctx);

        result.Json.Should().BeNull();
    }

    private const string XrayJsonNoDirect = """
    {
      "outbounds": [ { "protocol": "vless", "tag": "main-out" } ],
      "routing": { "rules": [] }
    }
    """;

    [Fact]
    public void Compose_xray_synthesizes_missing_direct_and_block()
    {
        var ctx = EmptyContext(ECoreType.Xray) with
        {
            RoutingItem = new RoutingItem
            {
                Id = "r1", Remarks = "d", DomainStrategy = Global.AsIs, DomainStrategy4Singbox = string.Empty,
                RuleSet = """
                [
                  { "Id": "r1", "OutboundTag": "direct", "Domain": ["a.example.com"], "Enabled": true },
                  { "Id": "r2", "OutboundTag": "block", "Domain": ["b.example.com"], "Enabled": true }
                ]
                """,
            },
        };

        var merged = CustomConfigComposer.Compose(XrayJsonNoDirect, ECoreType.Xray, ctx).Json;

        var outbounds = JsonUtils.ParseJson(merged)?["outbounds"]?.AsArray();
        outbounds.Should().NotBeNull();

        var direct = outbounds!.SingleOrDefault(o => o?["tag"]?.GetValue<string>() == "direct");
        direct.Should().NotBeNull();
        direct!["protocol"]!.GetValue<string>().Should().Be("freedom");

        var block = outbounds.SingleOrDefault(o => o?["tag"]?.GetValue<string>() == "block");
        block.Should().NotBeNull();
        block!["protocol"]!.GetValue<string>().Should().Be("blackhole");
    }

    [Fact]
    public void Compose_xray_reuses_existing_direct_tag()
    {
        var ctx = EmptyContext(ECoreType.Xray) with
        {
            RoutingItem = new RoutingItem
            {
                Id = "r1", Remarks = "d", DomainStrategy = Global.AsIs, DomainStrategy4Singbox = string.Empty,
                RuleSet = """[{ "Id": "r1", "OutboundTag": "direct", "Domain": ["a.example.com"], "Enabled": true }]""",
            },
        };

        // XrayJson уже содержит freedom/direct — дубля быть не должно.
        var merged = CustomConfigComposer.Compose(XrayJson, ECoreType.Xray, ctx).Json;

        var outbounds = JsonUtils.ParseJson(merged)?["outbounds"]?.AsArray();
        outbounds.Should().NotBeNull();
        outbounds!.Count(o => o?["tag"]?.GetValue<string>() == "direct").Should().Be(1);
    }
}
