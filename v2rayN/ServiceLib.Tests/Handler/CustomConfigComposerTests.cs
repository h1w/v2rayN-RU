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

    private const string XrayJsonWithBlock = """
    {
      "outbounds": [
        { "protocol": "vless", "tag": "main-out" },
        { "protocol": "blackhole", "tag": "block" }
      ]
    }
    """;

    [Fact]
    public void Compose_xray_reuses_existing_block_tag()
    {
        var ctx = EmptyContext(ECoreType.Xray) with
        {
            RoutingItem = new RoutingItem
            {
                Id = "r1", Remarks = "d", DomainStrategy = Global.AsIs, DomainStrategy4Singbox = string.Empty,
                RuleSet = """[{ "Id": "r1", "OutboundTag": "block", "Domain": ["a.example.com"], "Enabled": true }]""",
            },
        };

        // XrayJsonWithBlock уже содержит blackhole/block — дубля быть не должно.
        var merged = CustomConfigComposer.Compose(XrayJsonWithBlock, ECoreType.Xray, ctx).Json;

        var outbounds = JsonUtils.ParseJson(merged)?["outbounds"]?.AsArray();
        outbounds.Should().NotBeNull();
        outbounds!.Count(o => o?["tag"]?.GetValue<string>() == "block").Should().Be(1);
    }

    private const string SingboxJsonNoDirect = """
    {
      "outbounds": [ { "type": "vless", "tag": "main-out" } ]
    }
    """;

    [Fact]
    public void Compose_singbox_synthesizes_direct_but_not_block()
    {
        var ctx = EmptyContext(ECoreType.sing_box) with
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

        var merged = CustomConfigComposer.Compose(SingboxJsonNoDirect, ECoreType.sing_box, ctx).Json;

        var outbounds = JsonUtils.ParseJson(merged)?["outbounds"]?.AsArray();
        outbounds.Should().NotBeNull();

        // sing-box direct — { "type": "direct", "tag": "direct" }, НЕ xray-форма
        // с "protocol"/"freedom".
        var direct = outbounds!.SingleOrDefault(o => o?["tag"]?.GetValue<string>() == "direct");
        direct.Should().NotBeNull();
        var directType = direct!["type"];
        directType.Should().NotBeNull();
        directType!.GetValue<string>().Should().Be("direct");
        direct!["protocol"].Should().BeNull();

        // В sing-box выхода block не существует как класса — блокировка задаётся
        // через action: "reject" в правиле. Синтезировать outbound с тегом block
        // нельзя: это сделает конфиг невалидным.
        outbounds.Any(o => o?["tag"]?.GetValue<string>() == "block").Should().BeFalse();
    }

    [Fact]
    public void Compose_disabled_rule_does_not_synthesize_outbound()
    {
        var ctx = EmptyContext(ECoreType.Xray) with
        {
            RoutingItem = new RoutingItem
            {
                Id = "r1", Remarks = "d", DomainStrategy = Global.AsIs, DomainStrategy4Singbox = string.Empty,
                RuleSet = """[{ "Id": "r1", "OutboundTag": "direct", "Domain": ["a.example.com"], "Enabled": false }]""",
            },
        };

        // Правило выключено — CollectUsedOutboundTags обязан его игнорировать,
        // synth outbound "direct" появиться не должен.
        var merged = CustomConfigComposer.Compose(XrayJsonNoDirect, ECoreType.Xray, ctx).Json;

        var outbounds = JsonUtils.ParseJson(merged)?["outbounds"]?.AsArray();
        outbounds.Should().NotBeNull();
        outbounds!.Any(o => o?["tag"]?.GetValue<string>() == "direct").Should().BeFalse();
    }

    [Fact]
    public void Compose_xray_appends_local_rules_after_json_rules_and_maps_proxy()
    {
        var ctx = EmptyContext(ECoreType.Xray) with
        {
            RoutingItem = new RoutingItem
            {
                Id = "r1", Remarks = "d", DomainStrategy = Global.AsIs, DomainStrategy4Singbox = string.Empty,
                RuleSet = """[{ "Id": "r1", "OutboundTag": "proxy", "Domain": ["youtube.com"], "Enabled": true }]""",
            },
        };

        var merged = CustomConfigComposer.Compose(XrayJson, ECoreType.Xray, ctx).Json;

        var rules = JsonUtils.ParseJson(merged)?["routing"]?["rules"]?.AsArray();
        rules.Should().HaveCount(2);
        // Правило из JSON осталось первым.
        rules![0]!["domain"]![0]!.GetValue<string>().Should().Be("geosite:cn");
        // Наше — следом, с подменённым на главный выход JSON тегом.
        rules[1]!["domain"]![0]!.GetValue<string>().Should().Be("youtube.com");
        rules[1]!["outboundTag"]!.GetValue<string>().Should().Be("main-out");
    }

    // Симметричный тест для sing-box: та же гарантия порядка и подмены тега,
    // но в разделе route.rules и с полем outbound (а не outboundTag). Домен
    // задан как "full:youtube.com" — в отличие от простого "youtube.com" это
    // попадает в Rule4Sbox.domain, а не в domain_keyword (см. ParseV2Domain).
    [Fact]
    public void Compose_singbox_appends_local_rules_after_json_rules_and_maps_proxy()
    {
        const string singboxJson = """
        {
          "outbounds": [
            { "type": "vless", "tag": "main-out" },
            { "type": "direct", "tag": "direct" }
          ],
          "route": {
            "rules": [
              { "outbound": "direct", "domain": ["geosite:cn"] }
            ]
          }
        }
        """;

        var ctx = EmptyContext(ECoreType.sing_box) with
        {
            RoutingItem = new RoutingItem
            {
                Id = "r1", Remarks = "d", DomainStrategy = Global.AsIs, DomainStrategy4Singbox = string.Empty,
                RuleSet = """[{ "Id": "r1", "OutboundTag": "proxy", "Domain": ["full:youtube.com"], "Enabled": true }]""",
            },
        };

        var merged = CustomConfigComposer.Compose(singboxJson, ECoreType.sing_box, ctx).Json;

        var rules = JsonUtils.ParseJson(merged)?["route"]?["rules"]?.AsArray();
        rules.Should().HaveCount(2);
        // Правило из JSON осталось первым.
        rules![0]!["domain"]![0]!.GetValue<string>().Should().Be("geosite:cn");
        // Наше — следом, с подменённым на главный выход JSON тегом.
        rules[1]!["domain"]![0]!.GetValue<string>().Should().Be("youtube.com");
        rules[1]!["outbound"]!.GetValue<string>().Should().Be("main-out");
    }

    // Тег сгенерированного extra outbound сталкивается с тегом, который уже
    // существует в пользовательском JSON: MakeUniqueTag обязан развести их,
    // а правило — сослаться именно на новый (разведённый) тег, не на старый.
    [Fact]
    public void Compose_xray_renames_extra_outbound_on_tag_collision_and_remaps_rule()
    {
        var targetNode = CoreConfigTestFactory.CreateVmessNode(ECoreType.Xray, "n-target", "target-node");
        var generatedTag = $"{targetNode.IndexId}-{Global.ProxyTag}-{targetNode.Remarks}";

        var ctx = EmptyContext(ECoreType.Xray) with
        {
            RoutingItem = new RoutingItem
            {
                Id = "r1", Remarks = "d", DomainStrategy = Global.AsIs, DomainStrategy4Singbox = string.Empty,
                RuleSet = JsonUtils.Serialize(new List<RulesItem>
                {
                    new() { Id = "r1", Enabled = true, OutboundTag = targetNode.Remarks, Domain = ["target.example.com"] },
                }),
            },
        };
        ctx.AllProxiesMap[$"remark:{targetNode.Remarks}"] = targetNode;

        // Выход в пользовательском JSON уже носит ровно тот тег, который
        // BuildUserRoutingForCustom сгенерирует для targetNode.
        var collidingJson = $$"""
        {
          "outbounds": [
            { "protocol": "vless", "tag": "main-out" },
            { "protocol": "vmess", "tag": "{{generatedTag}}" }
          ]
        }
        """;

        var merged = CustomConfigComposer.Compose(collidingJson, ECoreType.Xray, ctx).Json;
        merged.Should().NotBeNull();

        var parsed = JsonUtils.ParseJson(merged);
        parsed.Should().NotBeNull();

        var outboundsNode = parsed!["outbounds"];
        outboundsNode.Should().NotBeNull();
        var outbounds = outboundsNode!.AsArray();

        // Исходный (пользовательский) выход с этим тегом остаётся ровно один...
        outbounds.Count(o => o?["tag"]?.GetValue<string>() == generatedTag).Should().Be(1);
        // ...а новый (сгенерированный) выход получает разведённый тег с суффиксом.
        var renamedTag = $"{generatedTag}-2";
        outbounds.Count(o => o?["tag"]?.GetValue<string>() == renamedTag).Should().Be(1);

        var rulesNode = parsed["routing"]?["rules"];
        rulesNode.Should().NotBeNull();
        var rules = rulesNode!.AsArray();
        var appended = rules.SingleOrDefault(r => r?["domain"]?[0]?.GetValue<string>() == "target.example.com");
        appended.Should().NotBeNull();

        // Правило должно указывать на разведённый тег, а не на исходный,
        // с которым столкнулись.
        var outboundTag = appended!["outboundTag"];
        outboundTag.Should().NotBeNull();
        outboundTag!.GetValue<string>().Should().Be(renamedTag);
    }

    [Fact]
    public void HasCatchAllLastRule_detects_unconditional_trailing_rule()
    {
        var withCatchAll = """
        {
          "outbounds": [ { "protocol": "vless", "tag": "main-out" } ],
          "routing": { "rules": [
            { "type": "field", "outboundTag": "direct", "domain": ["geosite:cn"] },
            { "type": "field", "outboundTag": "main-out", "network": "tcp,udp" }
          ] }
        }
        """;

        CustomConfigComposer.HasCatchAllLastRule(withCatchAll, ECoreType.Xray).Should().BeTrue();
        // У последнего правила есть сужающее условие — недостижимости нет.
        CustomConfigComposer.HasCatchAllLastRule(XrayJson, ECoreType.Xray).Should().BeFalse();
    }

    // Сверх брифа: тот же детект, но для sing-box (route.rules/outbound, свой
    // список сужающих ключей) — брифовский тест покрывает только xray-ветку.
    [Fact]
    public void HasCatchAllLastRule_detects_singbox_unconditional_trailing_rule()
    {
        var withCatchAll = """
        {
          "outbounds": [ { "type": "vless", "tag": "main-out" } ],
          "route": { "rules": [
            { "outbound": "direct", "domain": ["geosite:cn"] },
            { "outbound": "main-out" }
          ] }
        }
        """;

        CustomConfigComposer.HasCatchAllLastRule(withCatchAll, ECoreType.sing_box).Should().BeTrue();

        var withNarrowingLastRule = """
        {
          "outbounds": [ { "type": "vless", "tag": "main-out" } ],
          "route": { "rules": [
            { "outbound": "direct", "domain": ["geosite:cn"] },
            { "outbound": "main-out", "port": [443] }
          ] }
        }
        """;
        // У последнего правила есть сужающее условие (port) — недостижимости нет.
        CustomConfigComposer.HasCatchAllLastRule(withNarrowingLastRule, ECoreType.sing_box).Should().BeFalse();
    }

    // Сверх брифа: подтверждаем сквозь Compose, что CatchAllDetected читается из
    // исходного rawJson, а не из смерженного результата. Наше собственное правило,
    // дописанное последним, содержит "domain" — если бы флаг считался по дереву
    // после вливания, он бы ложно обнулился (порядок в Compose load-bearing).
    [Fact]
    public void Compose_sets_CatchAllDetected_from_raw_json_not_from_merged_result()
    {
        const string withCatchAll = """
        {
          "outbounds": [ { "protocol": "vless", "tag": "main-out" } ],
          "routing": { "rules": [
            { "type": "field", "outboundTag": "direct", "domain": ["geosite:cn"] },
            { "type": "field", "outboundTag": "main-out", "network": "tcp,udp" }
          ] }
        }
        """;

        var ctx = EmptyContext(ECoreType.Xray) with
        {
            RoutingItem = new RoutingItem
            {
                Id = "r1", Remarks = "d", DomainStrategy = Global.AsIs, DomainStrategy4Singbox = string.Empty,
                RuleSet = """[{ "Id": "r1", "OutboundTag": "proxy", "Domain": ["youtube.com"], "Enabled": true }]""",
            },
        };

        var result = CustomConfigComposer.Compose(withCatchAll, ECoreType.Xray, ctx);

        result.Json.Should().NotBeNull();
        result.CatchAllDetected.Should().BeTrue();

        var rulesNode = JsonUtils.ParseJson(result.Json)?["routing"]?["rules"];
        rulesNode.Should().NotBeNull();
        var rules = rulesNode!.AsArray();
        rules.Should().HaveCount(3);

        // Последнее правило смерженного дерева — уже наше (с полем domain),
        // а не пользовательский catch-all: если бы флаг вычислялся отсюда
        // (после вливания), эта проверка доказывала бы, что он был бы false.
        var lastRule = rules[^1];
        lastRule.Should().NotBeNull();
        lastRule!["domain"].Should().NotBeNull();
    }
}
