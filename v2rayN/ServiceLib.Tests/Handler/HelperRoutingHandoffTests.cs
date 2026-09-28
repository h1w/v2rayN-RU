using AwesomeAssertions;
using ServiceLib.Tests.CoreConfig;
using Xunit;

namespace ServiceLib.Tests.Handler;

public class HelperRoutingHandoffTests
{
    private static (CoreConfigContext Main, CoreConfigContext Helper) Contexts(bool custom)
    {
        var config = CoreConfigTestFactory.CreateConfig();
        CoreConfigTestFactory.BindAppManagerConfig(config);
        var routing = new RoutingItem
        {
            RuleSet = """
                [{"Id":"domain","OutboundTag":"target","Domain":["domain:example.test"],"Enabled":true},
                 {"Id":"process","OutboundTag":"target","Process":["curl","C:/tools/app.exe"],"Enabled":true},
                 {"Id":"duplicate","OutboundTag":"target","Process":["other"],"Enabled":true}]
                """
        };
        var main = CoreConfigTestFactory.CreateContext(config,
            CoreConfigTestFactory.CreateVmessNode(ECoreType.Xray), ECoreType.Xray) with
        {
            RoutingItem = routing,
            IsProcessRoutingDelegated = true,
        };
        var target = custom
            ? CoreConfigTestFactory.CreateSocksNode(ECoreType.Xray, "custom-chain", "target")
            : CoreConfigTestFactory.CreateVmessNode(ECoreType.Xray, "resolved", "target");
        target.Port = 32100;
        main.AllProxiesMap["remark:target"] = target;
        main.RoutingProfileIds["remark:target"] = "persisted-profile";
        var helper = CoreConfigTestFactory.CreateContext(config,
            CoreConfigTestFactory.CreateSocksNode(ECoreType.sing_box), ECoreType.sing_box) with
        {
            RoutingItem = routing,
        };
        // A helper never owns/resolves the main launch's Custom children.
        helper.AllProxiesMap["remark:target"] = custom
            ? new ProfileItem { ConfigType = EConfigType.Custom, IndexId = "persisted-profile", Remarks = "target" }
            : target;
        return (main, helper);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProcessAndDomainTargets_UseOneCountableSelectedBoundary(bool custom)
    {
        var (main, helper) = Contexts(custom);
        var generated = new CoreConfigV2rayService(main).GenerateClientConfigContent();
        generated.Success.Should().BeTrue();
        var bridged = HelperRoutingHandoff.Prepare(main, helper, generated.Data!.ToString()!, () => 32200);
        var root = JsonNode.Parse(bridged.MainJson)!;
        var ingress = root["inbounds"]!.AsArray().Single(i => i?["port"]?.GetValue<int>() == 32200)!;
        ingress["settings"]!["udp"]!.GetValue<bool>().Should().BeTrue();
        ingress["listen"]!.GetValue<string>().Should().Be(Global.Loopback);
        var selection = root["routing"]!["rules"]![0]!;
        selection["inboundTag"]![0]!.GetValue<string>().Should().Be(ingress["tag"]!.GetValue<string>());
        var outbound = selection["outboundTag"]!.GetValue<string>();
        main.StatisticsOutboundProfiles.Should().ContainSingle();
        main.StatisticsOutboundProfiles[outbound].Should().Be("persisted-profile");
        root["routing"]!["rules"]!.AsArray().Should().NotContain(r => r!["process"] != null);
        var helperRouting = new CoreConfigSingboxService(bridged.HelperContext).BuildUserRoutingForCustom();
        helperRouting.Rules.Should().Contain(r => r.process_name != null);
        helperRouting.Rules.Should().Contain(r => r.process_path != null);
        helperRouting.Rules.Should().Contain(r => r.domain_suffix != null);
        helperRouting.ExtraServers.Should().ContainSingle();
        var forward = (Outbound4Sbox)helperRouting.ExtraServers.Single();
        forward.type.Should().Be("socks");
        forward.server.Should().Be(Global.Loopback);
        forward.server_port.Should().Be(32200);
        forward.password.Should().BeNull();
        helperRouting.Rules.Should().OnlyContain(r => r.outbound == forward.tag);
        StatisticsXrayService.GetIngressTags(bridged.MainJson).Should().Contain(ingress["tag"]!.GetValue<string>());
        var aggregate = new StatisticsCounterTracker(0);
        var auxiliary = new StatisticsCounterTracker(0);
        var counters = JsonSerializer.Serialize(new
        {
            stats = new
            {
                inbound = new Dictionary<string, object>
                {
                    [ingress["tag"]!.GetValue<string>()] = new { uplink = 10, downlink = 20 },
                },
                outbound = new Dictionary<string, object>
                {
                    [outbound] = new { uplink = 10, downlink = 20 },
                },
            },
        });
        aggregate.Sample(StatisticsXrayService.ParseCounters(counters, StatisticsXrayService.GetIngressTags(bridged.MainJson))!, 1)
            .UpBytes.Should().Be(10);
        auxiliary.Sample(StatisticsXrayService.ParseCounters(counters, main.StatisticsOutboundProfiles.Keys.ToHashSet(), true)!, 1)
            .UpBytes.Should().Be(10);
    }

    [Fact]
    public void Reload_DoesNotReusePreviousLaunchPortsOrMutateHelperContext()
    {
        var (main, helper) = Contexts(true);
        var generated = new CoreConfigV2rayService(main).GenerateClientConfigContent().Data!.ToString()!;
        var first = HelperRoutingHandoff.Prepare(main, helper, generated, () => 32200);
        var next = HelperRoutingHandoff.Prepare(main, helper, generated, () => 32201);
        first.HelperContext.RoutingOutboundPorts.Values.Should().BeEquivalentTo([32200]);
        next.HelperContext.RoutingOutboundPorts.Values.Should().BeEquivalentTo([32201]);
        helper.RoutingOutboundPorts.Should().BeEmpty();
        JsonNode.Parse(next.MainJson)!["inbounds"]!.AsArray().Should()
            .NotContain(i => i!["port"]!.GetValue<int>() == 32200);
    }

    [Fact]
    public void Handoff_RejectsPortAlreadyOwnedByMainOrChild()
    {
        var (main, helper) = Contexts(true);
        main.ChainCores.Add(new ChainCoreDescriptor
        {
            Node = new ProfileItem(), CoreType = ECoreType.Xray, Port = 32200, ConfigFileName = "child.json"
        });
        var generated = new CoreConfigV2rayService(main).GenerateClientConfigContent().Data!.ToString()!;
        var prepare = () => HelperRoutingHandoff.Prepare(main, helper, generated, () => 32200);
        prepare.Should().Throw<InvalidOperationException>();
        prepare = () => HelperRoutingHandoff.Prepare(main, helper, generated, () => 10808);
        prepare.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Handoff_UsesSelectedBalancerBeforeOrdinaryRules()
    {
        var (main, helper) = Contexts(false);
        var first = CoreConfigTestFactory.CreateVmessNode(ECoreType.Xray, "one", "one");
        var second = CoreConfigTestFactory.CreateVmessNode(ECoreType.Xray, "two", "two");
        main.AllProxiesMap[first.IndexId] = first;
        main.AllProxiesMap[second.IndexId] = second;
        main.AllProxiesMap["remark:target"] = CoreConfigTestFactory.CreatePolicyGroupNode(
            ECoreType.Xray, "group", "target", [first.IndexId, second.IndexId]);
        var generated = new CoreConfigV2rayService(main).GenerateClientConfigContent();
        generated.Success.Should().BeTrue();
        var bridge = HelperRoutingHandoff.Prepare(main, helper, generated.Data!.ToString()!, () => 32200);
        var root = JsonNode.Parse(bridge.MainJson)!;
        var selected = root["routing"]!["rules"]![0]!;
        selected["outboundTag"].Should().BeNull();
        var balancer = selected["balancerTag"]!.GetValue<string>();
        root["routing"]!["balancers"]!.AsArray().Should().Contain(b => b!["tag"]!.GetValue<string>() == balancer);
        main.StatisticsOutboundProfiles.Values.Should().OnlyContain(id => id == "persisted-profile");
    }

    [Fact]
    public void Handoff_RejectsUnresolvedTargetInsteadOfChangingSelection()
    {
        var (main, helper) = Contexts(false);
        main.AllProxiesMap.Clear();
        var generated = new CoreConfigV2rayService(main).GenerateClientConfigContent().Data!.ToString()!;
        var prepare = () => HelperRoutingHandoff.Prepare(main, helper, generated, () => 32200);
        prepare.Should().Throw<InvalidOperationException>();
    }
}
