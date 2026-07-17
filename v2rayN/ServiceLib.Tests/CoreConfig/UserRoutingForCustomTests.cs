using AwesomeAssertions;
using ServiceLib.Services.CoreConfig;
using Xunit;

namespace ServiceLib.Tests.CoreConfig;

public class UserRoutingForCustomTests
{
    private static CoreConfigContext BuildContext(string ruleSetJson)
    {
        var config = CoreConfigTestFactory.CreateConfig(ECoreType.Xray);
        CoreConfigTestFactory.BindAppManagerConfig(config);
        var node = CoreConfigTestFactory.CreateVmessNode(ECoreType.Xray);
        var context = CoreConfigTestFactory.CreateContext(config, node, ECoreType.Xray);

        return context with
        {
            RoutingItem = new RoutingItem
            {
                Id = "r1",
                Remarks = "default",
                RuleSet = ruleSetJson,
                DomainStrategy = Global.AsIs,
                DomainStrategy4Singbox = string.Empty,
            },
        };
    }

    [Fact]
    public void Xray_rule_survives_trailing_commented_domain()
    {
        // Последняя строка блока «Домены» закомментирована — правило не должно исчезать.
        var ruleSet = """
        [
          {
            "Id": "rule-1",
            "OutboundTag": "direct",
            "Domain": ["example.com", "#commented.example.com"],
            "Enabled": true
          }
        ]
        """;

        var result = new CoreConfigV2rayService(BuildContext(ruleSet)).GenerateClientConfigContent();

        result.Success.Should().BeTrue();
        var json = JsonUtils.ParseJson(result.Data?.ToString());
        var rules = json?["routing"]?["rules"]?.AsArray();
        rules.Should().NotBeNull();
        rules!.Count.Should().BeGreaterThan(0);

        var domains = rules
            .Select(r => r?["domain"]?.AsArray())
            .Where(d => d is not null)
            .SelectMany(d => d!.Select(x => x?.GetValue<string>()))
            .ToList();

        domains.Should().Contain("example.com");
        domains.Should().NotContain("#commented.example.com");
    }

    [Fact]
    public void Xray_BuildUserRoutingForCustom_returns_only_user_rules()
    {
        var ruleSet = """
        [
          { "Id": "r1", "OutboundTag": "direct", "Domain": ["example.com"], "Enabled": true },
          { "Id": "r2", "OutboundTag": "proxy", "Ip": ["8.8.8.8/32"], "Enabled": true },
          { "Id": "r3", "OutboundTag": "block", "Domain": ["ads.example.com"], "Enabled": false }
        ]
        """;

        var fragment = new CoreConfigV2rayService(BuildContext(ruleSet)).BuildUserRoutingForCustom();

        // Выключенное правило пропущено, шаблонные выходы не считаются «лишними».
        fragment.Rules.Should().HaveCount(2);
        fragment.Rules[0].outboundTag.Should().Be("direct");
        fragment.Rules[0].domain.Should().ContainSingle().Which.Should().Be("example.com");
        fragment.Rules[1].outboundTag.Should().Be("proxy");
        fragment.ExtraOutbounds.Should().BeEmpty();
        fragment.UnsupportedCustomTargets.Should().BeEmpty();
    }

    [Fact]
    public void Xray_BuildUserRoutingForCustom_excludes_dns_rules()
    {
        // RuleType = DNS (2) не должен попадать в пользовательский фрагмент,
        // иначе DNS-правило просочится в custom JSON пользователя.
        var ruleSet = """
        [
          { "Id": "r1", "OutboundTag": "direct", "Domain": ["dns.example.com"], "Enabled": true, "RuleType": 2 },
          { "Id": "r2", "OutboundTag": "proxy", "Domain": ["routing.example.com"], "Enabled": true, "RuleType": 1 }
        ]
        """;

        var fragment = new CoreConfigV2rayService(BuildContext(ruleSet)).BuildUserRoutingForCustom();

        fragment.Rules.Should().ContainSingle();
        fragment.Rules[0].outboundTag.Should().Be("proxy");
        fragment.Rules[0].domain.Should().ContainSingle().Which.Should().Be("routing.example.com");
    }

    [Fact]
    public void Xray_BuildUserRoutingForCustom_rule_to_profile_produces_extra_outbound()
    {
        // Правило нацелено на реальный профиль (не direct/proxy/block) — должен
        // появиться отдельный outbound, а правило должно указывать на его tag.
        var targetNode = CoreConfigTestFactory.CreateVmessNode(ECoreType.Xray, "n-target", "target-node");
        var context = BuildContext(JsonUtils.Serialize(new List<RulesItem>
        {
            new()
            {
                Id = "r1",
                Enabled = true,
                OutboundTag = targetNode.Remarks,
                Domain = ["target.example.com"],
            },
        }));
        context.AllProxiesMap[$"remark:{targetNode.Remarks}"] = targetNode;

        var fragment = new CoreConfigV2rayService(context).BuildUserRoutingForCustom();

        var expectedTag = $"{targetNode.IndexId}-{Global.ProxyTag}-{targetNode.Remarks}";

        fragment.Rules.Should().ContainSingle();
        fragment.Rules[0].outboundTag.Should().Be(expectedTag);

        // Реальный «лишний» выход присутствует, а шаблонные direct/block — нет.
        fragment.ExtraOutbounds.Should().ContainSingle(o => o.tag == expectedTag);
        fragment.ExtraOutbounds.Should().NotContain(o => o.tag == Global.DirectTag || o.tag == Global.BlockTag);
    }

    [Fact]
    public void Xray_BuildUserRoutingForCustom_rule_to_custom_profile_is_unsupported()
    {
        // Правило нацелено на профиль типа Custom — Часть 1 такие цели не поддерживает:
        // ремарка уходит в UnsupportedCustomTargets, а правило в fragment.Rules не появляется.
        var customNode = new ProfileItem
        {
            IndexId = "n-custom",
            ConfigType = EConfigType.Custom,
            Remarks = "custom-node",
        };
        var context = BuildContext(JsonUtils.Serialize(new List<RulesItem>
        {
            new()
            {
                Id = "r1",
                Enabled = true,
                OutboundTag = customNode.Remarks,
                Domain = ["custom.example.com"],
            },
        }));
        context.AllProxiesMap[$"remark:{customNode.Remarks}"] = customNode;

        var fragment = new CoreConfigV2rayService(context).BuildUserRoutingForCustom();

        fragment.UnsupportedCustomTargets.Should().ContainSingle().Which.Should().Be(customNode.Remarks);
        fragment.Rules.Should().BeEmpty();
    }
}
