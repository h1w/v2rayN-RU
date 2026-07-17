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
}
