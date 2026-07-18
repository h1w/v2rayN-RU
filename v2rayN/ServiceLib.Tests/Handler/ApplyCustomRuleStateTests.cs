using System.Text.Json.Nodes;
using AwesomeAssertions;
using ServiceLib.Handler;
using ServiceLib.Models.Dto;
using Xunit;

namespace ServiceLib.Tests.Handler;

public class ApplyCustomRuleStateTests
{
    private const string XrayJson = """
    {
      "outbounds": [{ "protocol": "vless", "tag": "main" }],
      "routing": { "rules": [
        { "outboundTag": "direct", "domain": ["r0"] },
        { "outboundTag": "proxy",  "domain": ["r1"] },
        { "outboundTag": "block",  "domain": ["r2"] }
      ] }
    }
    """;

    private const string SingboxJson = """
    {
      "outbounds": [{ "type": "vless", "tag": "main" }],
      "route": { "rules": [
        { "outbound": "main", "domain": ["s0"] },
        { "outbound": "direct", "domain": ["s1"] }
      ] }
    }
    """;

    private static List<string> XrayDomains(string json)
    {
        var rules = JsonNode.Parse(json)!["routing"]!["rules"]!.AsArray();
        return rules.Select(r => r!["domain"]!.AsArray()[0]!.GetValue<string>()).ToList();
    }

    private static List<string> SingboxDomains(string json)
    {
        var rules = JsonNode.Parse(json)!["route"]!["rules"]!.AsArray();
        return rules.Select(r => r!["domain"]!.AsArray()[0]!.GetValue<string>()).ToList();
    }

    [Fact]
    public void NullState_ReturnsUnchanged()
    {
        var result = CustomConfigComposer.ApplyCustomRuleState(XrayJson, ECoreType.Xray, null);
        XrayDomains(result).Should().Equal("r0", "r1", "r2");
    }

    [Fact]
    public void ReorderAndDisable_Xray()
    {
        var state = new List<CustomRuleStateItem>
        {
            new() { Index = 2, Enabled = true },
            new() { Index = 0, Enabled = false }, // отключено -> пропущено
            new() { Index = 1, Enabled = true },
        };
        var result = CustomConfigComposer.ApplyCustomRuleState(XrayJson, ECoreType.Xray, state);
        XrayDomains(result).Should().Equal("r2", "r1"); // r0 отключён, порядок 2,1
    }

    [Fact]
    public void NewFileRules_AppendedEnabled_Xray()
    {
        // state знает только про ordinal 0; 1 и 2 — "новые" -> в конец включёнными
        var state = new List<CustomRuleStateItem> { new() { Index = 0, Enabled = true } };
        var result = CustomConfigComposer.ApplyCustomRuleState(XrayJson, ECoreType.Xray, state);
        XrayDomains(result).Should().Equal("r0", "r1", "r2");
    }

    [Fact]
    public void AllDisabled_ProducesEmptyRules_Xray()
    {
        var state = new List<CustomRuleStateItem>
        {
            new() { Index = 0, Enabled = false },
            new() { Index = 1, Enabled = false },
            new() { Index = 2, Enabled = false },
        };
        var result = CustomConfigComposer.ApplyCustomRuleState(XrayJson, ECoreType.Xray, state);
        JsonNode.Parse(result)!["routing"]!["rules"]!.AsArray().Count.Should().Be(0);
    }

    [Fact]
    public void Reorder_Singbox()
    {
        var state = new List<CustomRuleStateItem>
        {
            new() { Index = 1, Enabled = true },
            new() { Index = 0, Enabled = true },
        };
        var result = CustomConfigComposer.ApplyCustomRuleState(SingboxJson, ECoreType.sing_box, state);
        SingboxDomains(result).Should().Equal("s1", "s0");
    }
}
