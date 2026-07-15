using AwesomeAssertions;
using ServiceLib.Handler;
using ServiceLib.Models.Dto;
using ServiceLib.Models.Entities;
using Xunit;

namespace ServiceLib.Tests.Handler;

public class RoutingRuleExporterTests
{
    [Fact]
    public void ExportRulesToJson_ThenParse_RoundTripsAndPreservesOrder()
    {
        var rules = new List<RulesItem>
        {
            new() { Id = "a", OutboundTag = "proxy", Domain = ["example.com"], Remarks = "first" },
            new() { Id = "b", OutboundTag = "direct", Ip = ["10.0.0.0/8"], Remarks = "second" },
        };

        var json = RoutingRuleExporter.ExportRulesToJson(rules);
        var parsed = RoutingRuleExporter.ParseRulesJson(json);

        parsed.Should().NotBeNull();
        parsed!.Count.Should().Be(2);
        parsed[0].Remarks.Should().Be("first");
        parsed[1].Remarks.Should().Be("second");
        parsed[0].OutboundTag.Should().Be("proxy");
        parsed[1].Ip.Should().ContainSingle().Which.Should().Be("10.0.0.0/8");
    }

    [Fact]
    public void ExportRulesToJson_StripsId_ParseAssignsFreshIds()
    {
        var rules = new List<RulesItem> { new() { Id = "keep-me", OutboundTag = "proxy" } };

        var json = RoutingRuleExporter.ExportRulesToJson(rules);
        json.Should().NotContain("keep-me");

        var parsed = RoutingRuleExporter.ParseRulesJson(json);
        parsed![0].Id.Should().NotBeNullOrEmpty();
        parsed[0].Id.Should().NotBe("keep-me");
    }

    [Fact]
    public void ParseRulesJson_InvalidJson_ReturnsNull()
    {
        RoutingRuleExporter.ParseRulesJson("not json").Should().BeNull();
        RoutingRuleExporter.ParseRulesJson(null).Should().BeNull();
        RoutingRuleExporter.ParseRulesJson("").Should().BeNull();
    }

    [Fact]
    public void ExportRoutingTemplate_ThenParse_RoundTripsAndStripsInstanceFields()
    {
        var items = new List<RoutingItem>
        {
            new()
            {
                Id = "set-1", Remarks = "My Set", IsActive = true, Sort = 5, Locked = true,
                DomainStrategy = "IPIfNonMatch",
                RuleSet = "[{\"outboundTag\":\"proxy\"}]",
            },
        };

        var json = RoutingRuleExporter.ExportRoutingTemplateToJson(items);
        json.Should().NotContain("set-1");

        var parsed = RoutingRuleExporter.ParseRoutingTemplateJson(json);
        parsed.Should().NotBeNull();
        parsed!.Count.Should().Be(1);
        parsed[0].Remarks.Should().Be("My Set");
        parsed[0].DomainStrategy.Should().Be("IPIfNonMatch");
        parsed[0].RuleSet.Should().Contain("proxy");
        parsed[0].Id.Should().BeNullOrEmpty();
        parsed[0].IsActive.Should().BeFalse();
        parsed[0].Sort.Should().Be(0);
        parsed[0].Locked.Should().BeFalse();
    }

    [Fact]
    public void ExportRoutingTemplate_ProducesRoutingTemplateWithVersion()
    {
        var json = RoutingRuleExporter.ExportRoutingTemplateToJson(new List<RoutingItem> { new() { Remarks = "x" } });
        var template = JsonUtils.Deserialize<RoutingTemplate>(json);
        template.Should().NotBeNull();
        template!.Version.Should().NotBeNullOrEmpty();
        template.RoutingItems.Should().HaveCount(1);
    }

    [Fact]
    public void ParseRoutingTemplateJson_InvalidOrEmpty_ReturnsNull()
    {
        RoutingRuleExporter.ParseRoutingTemplateJson("not json").Should().BeNull();
        RoutingRuleExporter.ParseRoutingTemplateJson(null).Should().BeNull();
    }
}
