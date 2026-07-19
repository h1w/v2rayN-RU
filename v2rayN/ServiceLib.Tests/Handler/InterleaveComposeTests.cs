using AwesomeAssertions;
using ServiceLib.Handler;
using ServiceLib.Models.Dto;
using ServiceLib.Tests.CoreConfig;
using Xunit;

namespace ServiceLib.Tests.Handler;

/// <summary>
/// Task 2 (full interleave): проверяет unified-сборку routing.rules/route.rules
/// в CustomConfigComposer, когда EnableCustomRuleEditing==true — JSON-правила
/// и локальные правила должны идти в ОДНОМ порядке, заданном CustomRuleState
/// (List&lt;CustomRuleStateItem&gt; с LocalId), а не двумя блоками подряд.
/// Порядок правил — это порядок применения маршрутизации, поэтому здесь же
/// закрепляется back-compat с фазой 1 (JSON-only state) и то, что editing-off
/// путь (append) не меняется.
/// </summary>
public class InterleaveComposeTests
{
    // Два JSON-правила: ordinal 0 -> j0.example.com, ordinal 1 -> j1.example.com.
    private const string XrayTwoRuleJson = """
    {
      "outbounds": [
        { "protocol": "vless", "tag": "main-out" },
        { "protocol": "freedom", "tag": "direct" }
      ],
      "routing": {
        "rules": [
          { "type": "field", "outboundTag": "direct", "domain": ["j0.example.com"] },
          { "type": "field", "outboundTag": "direct", "domain": ["j1.example.com"] }
        ]
      }
    }
    """;

    private const string SingboxTwoRuleJson = """
    {
      "outbounds": [
        { "type": "vless", "tag": "main-out" },
        { "type": "direct", "tag": "direct" }
      ],
      "route": {
        "rules": [
          { "outbound": "direct", "domain": ["j0.example.com"] },
          { "outbound": "direct", "domain": ["j1.example.com"] }
        ]
      }
    }
    """;

    // Как TwoRuleJson, но ordinal 1 — catch-all (без единого сужающего ключа):
    // используется для проверки детекта недосягаемости по ФИНАЛЬНОМУ порядку.
    private const string XrayCatchAllJson = """
    {
      "outbounds": [
        { "protocol": "vless", "tag": "main-out" },
        { "protocol": "freedom", "tag": "direct" }
      ],
      "routing": {
        "rules": [
          { "type": "field", "outboundTag": "direct", "domain": ["j0.example.com"] },
          { "type": "field", "outboundTag": "direct" }
        ]
      }
    }
    """;

    private const string SingboxCatchAllJson = """
    {
      "outbounds": [
        { "type": "vless", "tag": "main-out" },
        { "type": "direct", "tag": "direct" }
      ],
      "route": {
        "rules": [
          { "outbound": "direct", "domain": ["j0.example.com"] },
          { "outbound": "direct" }
        ]
      }
    }
    """;

    // Два локальных правила, оба на "proxy" (ремапится на главный JSON-выход
    // main-out) — id "A" и "B", с разными доменами для идентификации в тесте.
    // Домены для xray без префикса (RulesItem.Domain копируется как есть),
    // для sing-box с "full:" — иначе ParseV2Domain положит их не в "domain",
    // а в "domain_keyword" (см. CustomConfigComposerTests).
    private static string XrayLocalRuleSet(bool bEnabled = true, bool aEnabled = true) => JsonUtils.Serialize(new List<RulesItem>
    {
        new() { Id = "A", Enabled = aEnabled, OutboundTag = Global.ProxyTag, Domain = ["local-a.example.com"] },
        new() { Id = "B", Enabled = bEnabled, OutboundTag = Global.ProxyTag, Domain = ["local-b.example.com"] },
    });

    private static string SingboxLocalRuleSet(bool bEnabled = true, bool aEnabled = true) => JsonUtils.Serialize(new List<RulesItem>
    {
        new() { Id = "A", Enabled = aEnabled, OutboundTag = Global.ProxyTag, Domain = ["full:local-a.example.com"] },
        new() { Id = "B", Enabled = bEnabled, OutboundTag = Global.ProxyTag, Domain = ["full:local-b.example.com"] },
    });

    private static CoreConfigContext BuildContext(ECoreType coreType, string ruleSetJson, bool editingEnabled, List<CustomRuleStateItem>? tokens)
    {
        var config = CoreConfigTestFactory.CreateConfig(coreType);
        config.UiItem.EnableCustomRuleEditing = editingEnabled;
        CoreConfigTestFactory.BindAppManagerConfig(config);

        var node = CoreConfigTestFactory.CreateVmessNode(coreType);
        if (tokens != null)
        {
            node.CustomRuleState = JsonUtils.Serialize(tokens, false);
        }

        var ctx = CoreConfigTestFactory.CreateContext(config, node, coreType);
        return ctx with
        {
            RoutingItem = new RoutingItem
            {
                Id = "r1",
                Remarks = "d",
                DomainStrategy = Global.AsIs,
                DomainStrategy4Singbox = string.Empty,
                RuleSet = ruleSetJson,
            },
        };
    }

    private static JsonArray GetRulesArray(string? json, ECoreType coreType)
    {
        var parsed = JsonUtils.ParseJson(json);
        parsed.Should().NotBeNull();
        var rulesNode = coreType == ECoreType.sing_box ? parsed!["route"]?["rules"] : parsed!["routing"]?["rules"];
        rulesNode.Should().NotBeNull();
        return rulesNode!.AsArray();
    }

    // Домен-идентификатор одного правила (первый элемент "domain") — работает
    // и для xray (RulesItem4Ray.domain), и для sing-box (Rule4Sbox.domain,
    // куда ParseV2Domain кладёт "full:"-домены как есть).
    private static string? DomainOf(JsonNode? rule) => rule?["domain"]?[0]?.GetValue<string>();

    private static string? OutboundOf(JsonNode? rule, ECoreType coreType) =>
        coreType == ECoreType.sing_box ? rule?["outbound"]?.GetValue<string>() : rule?["outboundTag"]?.GetValue<string>();

    [Theory]
    [InlineData(ECoreType.Xray)]
    [InlineData(ECoreType.sing_box)]
    public void Unified_mixed_order_tokens_interleave_json_and_local_rules_exactly(ECoreType coreType)
    {
        var json = coreType == ECoreType.sing_box ? SingboxTwoRuleJson : XrayTwoRuleJson;
        var ruleSet = coreType == ECoreType.sing_box ? SingboxLocalRuleSet() : XrayLocalRuleSet();

        // [local B, json 1, local A, json 0] -> ожидаемый порядок доменов:
        // local-b, j1, local-a, j0.
        var tokens = new List<CustomRuleStateItem>
        {
            new() { LocalId = "B" },
            new() { Index = 1, Enabled = true },
            new() { LocalId = "A" },
            new() { Index = 0, Enabled = true },
        };

        var ctx = BuildContext(coreType, ruleSet, editingEnabled: true, tokens);
        var merged = CustomConfigComposer.Compose(json, coreType, ctx).Json;

        var rules = GetRulesArray(merged, coreType);
        rules.Should().HaveCount(4);

        var d0 = DomainOf(rules[0]);
        d0.Should().Be("local-b.example.com");
        var d1 = DomainOf(rules[1]);
        d1.Should().Be("j1.example.com");
        var d2 = DomainOf(rules[2]);
        d2.Should().Be("local-a.example.com");
        var d3 = DomainOf(rules[3]);
        d3.Should().Be("j0.example.com");

        // Локальные правила получают ремап outbound-тега на главный JSON-выход.
        var o0 = OutboundOf(rules[0], coreType);
        o0.Should().Be("main-out");
        var o2 = OutboundOf(rules[2], coreType);
        o2.Should().Be("main-out");
    }

    [Theory]
    [InlineData(ECoreType.Xray)]
    [InlineData(ECoreType.sing_box)]
    public void Unified_disabled_json_token_and_disabled_local_rule_contribute_nothing(ECoreType coreType)
    {
        var json = coreType == ECoreType.sing_box ? SingboxTwoRuleJson : XrayTwoRuleJson;
        // B выключено в самом RuleSet -> BuildUserRoutingForCustom его вообще
        // не породит, фрагмент для "B" будет пуст.
        var ruleSet = coreType == ECoreType.sing_box ? SingboxLocalRuleSet(bEnabled: false) : XrayLocalRuleSet(bEnabled: false);

        var tokens = new List<CustomRuleStateItem>
        {
            new() { LocalId = "A" },
            new() { Index = 0, Enabled = false }, // json-ordinal 0 выключен токеном -> дропается
            new() { LocalId = "B" }, // ссылается на выключенное в RuleSet локальное правило -> ничего не даёт
            new() { Index = 1, Enabled = true },
        };

        var ctx = BuildContext(coreType, ruleSet, editingEnabled: true, tokens);
        var merged = CustomConfigComposer.Compose(json, coreType, ctx).Json;

        var rules = GetRulesArray(merged, coreType);
        // Только local-a и j1 остались; j0 (disabled token) и B (disabled rule) исчезли.
        rules.Should().HaveCount(2);

        var d0 = DomainOf(rules[0]);
        d0.Should().Be("local-a.example.com");
        var d1 = DomainOf(rules[1]);
        d1.Should().Be("j1.example.com");
    }

    [Theory]
    [InlineData(ECoreType.Xray)]
    [InlineData(ECoreType.sing_box)]
    public void Unified_leftovers_not_referenced_by_any_token_are_appended_at_the_end(ECoreType coreType)
    {
        var json = coreType == ECoreType.sing_box ? SingboxTwoRuleJson : XrayTwoRuleJson;
        var ruleSet = coreType == ECoreType.sing_box ? SingboxLocalRuleSet() : XrayLocalRuleSet();

        // Токены упоминают только local B и json-ordinal 1. Json-ordinal 0 и
        // local A нигде не упомянуты -> должны быть дописаны в конец: сначала
        // непомянутый JSON-ordinal (в порядке файла: только 0), затем
        // непомянутый локальный id (в порядке фрагмента: только A).
        var tokens = new List<CustomRuleStateItem>
        {
            new() { LocalId = "B" },
            new() { Index = 1, Enabled = true },
        };

        var ctx = BuildContext(coreType, ruleSet, editingEnabled: true, tokens);
        var merged = CustomConfigComposer.Compose(json, coreType, ctx).Json;

        var rules = GetRulesArray(merged, coreType);
        rules.Should().HaveCount(4);

        var d0 = DomainOf(rules[0]);
        d0.Should().Be("local-b.example.com");
        var d1 = DomainOf(rules[1]);
        d1.Should().Be("j1.example.com");
        // Leftover: json ordinal 0 первым (порядок файла), потом local A.
        var d2 = DomainOf(rules[2]);
        d2.Should().Be("j0.example.com");
        var d3 = DomainOf(rules[3]);
        d3.Should().Be("local-a.example.com");
    }

    [Theory]
    [InlineData(ECoreType.Xray)]
    [InlineData(ECoreType.sing_box)]
    public void Unified_json_only_tokens_reproduce_phase1_reorder_then_append_all_locals(ECoreType coreType)
    {
        var json = coreType == ECoreType.sing_box ? SingboxTwoRuleJson : XrayTwoRuleJson;
        var ruleSet = coreType == ECoreType.sing_box ? SingboxLocalRuleSet() : XrayLocalRuleSet();

        // Только JSON-токены (LocalId==null), как в фазе 1: переставляем
        // ordinal 1 перед ordinal 0. Локальных токенов нет вовсе -> оба
        // локальных правила должны быть дописаны в конец (в порядке фрагмента: A, B).
        var tokens = new List<CustomRuleStateItem>
        {
            new() { Index = 1, Enabled = true },
            new() { Index = 0, Enabled = true },
        };

        var ctx = BuildContext(coreType, ruleSet, editingEnabled: true, tokens);
        var merged = CustomConfigComposer.Compose(json, coreType, ctx).Json;

        var rules = GetRulesArray(merged, coreType);
        rules.Should().HaveCount(4);

        var d0 = DomainOf(rules[0]);
        d0.Should().Be("j1.example.com");
        var d1 = DomainOf(rules[1]);
        d1.Should().Be("j0.example.com");
        var d2 = DomainOf(rules[2]);
        d2.Should().Be("local-a.example.com");
        var d3 = DomainOf(rules[3]);
        d3.Should().Be("local-b.example.com");
    }

    [Theory]
    [InlineData(ECoreType.Xray)]
    [InlineData(ECoreType.sing_box)]
    public void Unified_null_state_with_editing_on_matches_editing_off_append_behavior(ECoreType coreType)
    {
        var json = coreType == ECoreType.sing_box ? SingboxTwoRuleJson : XrayTwoRuleJson;
        var ruleSet = coreType == ECoreType.sing_box ? SingboxLocalRuleSet() : XrayLocalRuleSet();

        var ctxOn = BuildContext(coreType, ruleSet, editingEnabled: true, tokens: null);
        var ctxOff = BuildContext(coreType, ruleSet, editingEnabled: false, tokens: null);

        var mergedOn = CustomConfigComposer.Compose(json, coreType, ctxOn).Json;
        var mergedOff = CustomConfigComposer.Compose(json, coreType, ctxOff).Json;

        var rulesOn = GetRulesArray(mergedOn, coreType);
        var rulesOff = GetRulesArray(mergedOff, coreType);
        rulesOn.Should().HaveCount(4);
        rulesOff.Should().HaveCount(4);

        for (var i = 0; i < 4; i++)
        {
            var dOn = DomainOf(rulesOn[i]);
            var dOff = DomainOf(rulesOff[i]);
            dOn.Should().Be(dOff);
        }

        // Явно: J0, J1, local A, local B (файл, потом фрагмент) в обоих случаях.
        var d0 = DomainOf(rulesOn[0]);
        d0.Should().Be("j0.example.com");
        var d1 = DomainOf(rulesOn[1]);
        d1.Should().Be("j1.example.com");
        var d2 = DomainOf(rulesOn[2]);
        d2.Should().Be("local-a.example.com");
        var d3 = DomainOf(rulesOn[3]);
        d3.Should().Be("local-b.example.com");
    }

    [Theory]
    [InlineData(ECoreType.Xray)]
    [InlineData(ECoreType.sing_box)]
    public void Editing_off_keeps_append_behavior_unaffected_by_CustomRuleState(ECoreType coreType)
    {
        var json = coreType == ECoreType.sing_box ? SingboxTwoRuleJson : XrayTwoRuleJson;
        var ruleSet = coreType == ECoreType.sing_box ? SingboxLocalRuleSet() : XrayLocalRuleSet();

        // Даже если на узле болтается CustomRuleState с "мешающим" порядком,
        // editing-off обязан его полностью игнорировать: JSON вербатим +
        // локальные дописаны следом, как до Task 2.
        var tokens = new List<CustomRuleStateItem>
        {
            new() { LocalId = "B" },
            new() { Index = 0, Enabled = false },
        };

        var ctx = BuildContext(coreType, ruleSet, editingEnabled: false, tokens);
        var merged = CustomConfigComposer.Compose(json, coreType, ctx).Json;

        var rules = GetRulesArray(merged, coreType);
        rules.Should().HaveCount(4);

        var d0 = DomainOf(rules[0]);
        d0.Should().Be("j0.example.com");
        var d1 = DomainOf(rules[1]);
        d1.Should().Be("j1.example.com");
        var d2 = DomainOf(rules[2]);
        d2.Should().Be("local-a.example.com");
        var d3 = DomainOf(rules[3]);
        d3.Should().Be("local-b.example.com");
    }

    [Fact]
    public void Unified_xray_still_merges_extra_outbound_from_chained_target_rule()
    {
        // Доказывает пункт брифа "extras (ExtraOutbounds/Balancers/...) сливаются
        // так же, как раньше" — не только правила. Локальное правило нацелено на
        // другой профиль (не "proxy"), поэтому BuildUserRoutingForCustom добавит
        // в fragment.ExtraOutbounds отдельный outbound на этот профиль.
        var targetNode = CoreConfigTestFactory.CreateVmessNode(ECoreType.Xray, "n-target", "target-node");
        var generatedTag = $"{targetNode.IndexId}-{Global.ProxyTag}-{targetNode.Remarks}";

        var config = CoreConfigTestFactory.CreateConfig(ECoreType.Xray);
        config.UiItem.EnableCustomRuleEditing = true;
        CoreConfigTestFactory.BindAppManagerConfig(config);

        var node = CoreConfigTestFactory.CreateVmessNode(ECoreType.Xray);
        node.CustomRuleState = JsonUtils.Serialize(new List<CustomRuleStateItem>
        {
            new() { LocalId = "r1" },
            new() { Index = 0, Enabled = true },
        }, false);

        var ctx = CoreConfigTestFactory.CreateContext(config, node, ECoreType.Xray) with
        {
            RoutingItem = new RoutingItem
            {
                Id = "r1",
                Remarks = "d",
                DomainStrategy = Global.AsIs,
                DomainStrategy4Singbox = string.Empty,
                RuleSet = JsonUtils.Serialize(new List<RulesItem>
                {
                    new() { Id = "r1", Enabled = true, OutboundTag = targetNode.Remarks, Domain = ["target.example.com"] },
                }),
            },
        };
        ctx.AllProxiesMap[$"remark:{targetNode.Remarks}"] = targetNode;

        var merged = CustomConfigComposer.Compose(XrayTwoRuleJson, ECoreType.Xray, ctx).Json;
        merged.Should().NotBeNull();

        var parsed = JsonUtils.ParseJson(merged);
        parsed.Should().NotBeNull();
        var outboundsNode = parsed!["outbounds"];
        outboundsNode.Should().NotBeNull();
        var outbounds = outboundsNode!.AsArray();
        outbounds.Any(o => o?["tag"]?.GetValue<string>() == generatedTag).Should().BeTrue();

        var rules = GetRulesArray(merged, ECoreType.Xray);
        // local r1 (token) + json ordinal 0 (token) + json ordinal 1 (leftover, not referenced by any token).
        rules.Should().HaveCount(3);
        var appended = rules.SingleOrDefault(r => DomainOf(r) == "target.example.com");
        appended.Should().NotBeNull();
        var outboundTag = appended!["outboundTag"];
        outboundTag.Should().NotBeNull();
        outboundTag!.GetValue<string>().Should().Be(generatedTag);
    }

    // Недосягаемость правил считается по ФИНАЛЬНОМУ порядку, а не по последнему
    // правилу исходного файла: catch-all, оказавшийся последним в едином порядке,
    // ничего не делает недосягаемым и НЕ должен давать предупреждение (был ложняк
    // фазы-1 под чередованием).
    [Theory]
    [InlineData(ECoreType.Xray)]
    [InlineData(ECoreType.sing_box)]
    public void CatchAll_detected_from_final_order_not_source_file(ECoreType coreType)
    {
        var json = coreType == ECoreType.sing_box ? SingboxCatchAllJson : XrayCatchAllJson;
        var ruleSet = coreType == ECoreType.sing_box ? SingboxLocalRuleSet() : XrayLocalRuleSet();

        // Catch-all JSON-правило (ordinal 1) — ПОСЛЕДНЕЕ в едином порядке; локальные
        // A,B и json0 перед ним. Ничего после catch-all -> недосягаемых нет.
        var tokensCatchAllLast = new List<CustomRuleStateItem>
        {
            new() { LocalId = "A" },
            new() { LocalId = "B" },
            new() { Index = 0, Enabled = true },
            new() { Index = 1, Enabled = true },
        };
        var resultLast = CustomConfigComposer.Compose(json, coreType, BuildContext(coreType, ruleSet, editingEnabled: true, tokensCatchAllLast));
        resultLast.CatchAllDetected.Should().BeFalse();

        // То же catch-all, но теперь ПЕРВЫМ -> всё после него недосягаемо -> предупреждение.
        var tokensRuleAfter = new List<CustomRuleStateItem>
        {
            new() { Index = 1, Enabled = true },
            new() { LocalId = "A" },
            new() { LocalId = "B" },
            new() { Index = 0, Enabled = true },
        };
        var resultAfter = CustomConfigComposer.Compose(json, coreType, BuildContext(coreType, ruleSet, editingEnabled: true, tokensRuleAfter));
        resultAfter.CatchAllDetected.Should().BeTrue();
    }

    // editing-off (append): локальные правила дописываются после правил файла, поэтому
    // catch-all в конце файла ДЕЛАЕТ их недосягаемыми -> предупреждение сохраняется.
    [Theory]
    [InlineData(ECoreType.Xray)]
    [InlineData(ECoreType.sing_box)]
    public void CatchAll_editing_off_append_still_warns_when_file_ends_catch_all(ECoreType coreType)
    {
        var json = coreType == ECoreType.sing_box ? SingboxCatchAllJson : XrayCatchAllJson;
        var ruleSet = coreType == ECoreType.sing_box ? SingboxLocalRuleSet() : XrayLocalRuleSet();
        var result = CustomConfigComposer.Compose(json, coreType, BuildContext(coreType, ruleSet, editingEnabled: false, tokens: null));
        result.CatchAllDetected.Should().BeTrue();
    }
}
