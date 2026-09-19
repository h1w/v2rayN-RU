using AwesomeAssertions;
using ServiceLib.Common;
using ServiceLib.Enums;
using ServiceLib.Handler.Fmt;
using ServiceLib.Manager;
using ServiceLib.Models;
using ServiceLib.Models.Dto;
using ServiceLib.Services.CoreConfig;
using Xunit;

namespace ServiceLib.Tests.CoreConfig.Singbox;

public class CoreConfigSingboxServiceTests
{
    [Fact]
    public void IPIfNonMatch_ShouldKeepNamedTargetsAcrossBothPassesAndRepeatedGeneration()
    {
        var config = CoreConfigTestFactory.CreateConfig(ECoreType.sing_box);
        config.RoutingBasicItem.DomainStrategy = Global.IPIfNonMatch;
        CoreConfigTestFactory.BindAppManagerConfig(config);
        var main = CoreConfigTestFactory.CreateSocksNode(ECoreType.sing_box, "a", "A");
        var b = CoreConfigTestFactory.CreateSocksNode(ECoreType.sing_box, "b", "B");
        var c = CoreConfigTestFactory.CreateSocksNode(ECoreType.sing_box, "c", "C");
        var targets = new[] { "B", "C", "B", Global.DirectTag, Global.BlockTag, Global.ProxyTag };
        var items = targets.Select((target, i) => new RulesItem
        {
            Id = $"rule-{i}", Enabled = true, RuleType = ERuleType.Routing,
            OutboundTag = target, Ip = [$"192.0.2.{i + 1}/32"], Port = "443", Network = "tcp",
        }).ToList();
        var snapshot = JsonUtils.Serialize(items);
        var context = CoreConfigTestFactory.CreateContext(config, main, ECoreType.sing_box) with
        {
            RoutingItem = new RoutingItem { RuleSet = snapshot },
        };
        context.AllProxiesMap["remark:B"] = b;
        context.AllProxiesMap["remark:C"] = c;
        var service = new CoreConfigSingboxService(context);
        SingboxConfig Generate()
        {
            var result = service.GenerateClientConfigContent();
            result.Success.Should().BeTrue($"ret msg: {result.Msg}");
            return JsonUtils.Deserialize<SingboxConfig>(result.Data!.ToString())!;
        }
        var first = Generate();
        var second = Generate();
        for (var i = 0; i < items.Count; i++)
        {
            var expected = targets[i] switch { "B" => "b-proxy-B", "C" => "c-proxy-C", var tag => tag };
            foreach (var generated in new[] { first, second })
            {
                var matches = generated.route.rules.Where(r => r.ip_cidr?.Contains(items[i].Ip[0]) == true).ToList();
                matches.Should().HaveCount(2);
                if (expected == Global.BlockTag)
                    matches.Should().OnlyContain(r => r.action == "reject" && r.outbound == null);
                else
                    matches.Should().OnlyContain(r => r.outbound == expected);
            }
        }
        first.outbounds.Should().ContainSingle(o => o.tag == "b-proxy-B");
        first.outbounds.Should().ContainSingle(o => o.tag == "c-proxy-C");
        second.route.Should().BeEquivalentTo(first.route);
        second.outbounds.Should().BeEquivalentTo(first.outbounds);
        JsonUtils.Serialize(items).Should().Be(snapshot);
        context.RoutingItem.RuleSet.Should().Be(snapshot);
    }

    [Theory]
    [InlineData("B")]
    [InlineData("direct")]
    [InlineData("block")]
    [InlineData("proxy")]
    public void GenRoutingUserRule_ShouldNotMutateInput(string target)
    {
        var config = CoreConfigTestFactory.CreateConfig(ECoreType.sing_box);
        CoreConfigTestFactory.BindAppManagerConfig(config);
        var context = CoreConfigTestFactory.CreateContext(config,
            CoreConfigTestFactory.CreateSocksNode(ECoreType.sing_box), ECoreType.sing_box);
        context.AllProxiesMap["remark:B"] = CoreConfigTestFactory.CreateSocksNode(ECoreType.sing_box, "b", "B");
        var service = new CoreConfigSingboxService(context);
        service.BuildUserRoutingForCustom(); // Initializes a fresh config without deserializing our input object.
        var item = new RulesItem
        {
            Id = "unchanged", Enabled = true, RuleType = ERuleType.Routing, OutboundTag = target,
            Ip = ["192.0.2.1/32"], Domain = ["full:unchanged.test"], Port = "443,8000-8001",
            Network = "tcp", Protocol = ["tls"], InboundTag = ["mixed"], Process = ["example"],
        };
        var before = JsonUtils.Serialize(item);
        var generate = typeof(CoreConfigSingboxService).GetMethod("GenRoutingUserRule",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        generate.Invoke(service, [item]);
        JsonUtils.Serialize(item).Should().Be(before);
        generate.Invoke(service, [item]);
        JsonUtils.Serialize(item).Should().Be(before);
    }

    [Theory]
    [InlineData("port")]
    [InlineData("range")]
    [InlineData("network")]
    [InlineData("protocol")]
    [InlineData("inbound")]
    [InlineData("all")]
    public void NegativeIp_ShouldConstrainEveryAddressBranch(string constraint)
    {
        var item = new RulesItem { Enabled = true, OutboundTag = Global.DirectTag, Ip = ["!10.0.0.0/8"] };
        if (constraint is "port" or "all") item.Port = "443";
        if (constraint == "range") item.Port = "8000-8002";
        if (constraint is "network" or "all") item.Network = "tcp";
        if (constraint is "protocol" or "all") item.Protocol = ["tls"];
        if (constraint is "inbound" or "all") item.InboundTag = ["mixed"];
        var rule = GenerateAddressRule(item);
        var port = constraint == "range" ? 8001 : 443;
        MatchesAddressRule(rule, "192.0.2.1", port).Should().BeTrue();
        MatchesAddressRule(rule, "10.1.2.3", port).Should().BeFalse("excluded addresses must not match an empty positive branch");
        if (item.Port != null) MatchesAddressRule(rule, "192.0.2.1", 80).Should().BeFalse();
        if (item.Network != null) MatchesAddressRule(rule, "192.0.2.1", port, network: "udp").Should().BeFalse();
        if (item.Protocol != null) MatchesAddressRule(rule, "192.0.2.1", port, protocol: "http").Should().BeFalse();
        if (item.InboundTag != null) MatchesAddressRule(rule, "192.0.2.1", port, inbound: "other").Should().BeFalse();
        rule.type.Should().Be("logical");
        rule.mode.Should().Be("and");
        rule.rules.Should().HaveCount(2);
        var common = rule.rules![0];
        common.outbound.Should().BeNull();
        common.action.Should().BeNull();
        common.network.Should().BeEquivalentTo(item.Network == null ? null : new[] { "tcp" });
        common.protocol.Should().BeEquivalentTo(item.Protocol);
        common.inbound.Should().BeEquivalentTo(item.InboundTag);
        if (constraint == "range") common.port_range.Should().Equal("8000:8002");
        if (constraint is "port" or "all") common.port.Should().Equal(443);
    }

    [Theory]
    [InlineData("!10.0.0.0/8", "10.1.1.1", false)]
    [InlineData("!10.0.0.0/8", "192.0.2.1", true)]
    [InlineData("!2001:db8::/32", "2001:db8::1", false)]
    [InlineData("!2001:db8::/32", "2001:db9::1", true)]
    [InlineData("!10.0.0.0/8,!192.168.0.0/16", "10.1.1.1", false)]
    [InlineData("!10.0.0.0/8,!192.168.0.0/16", "192.168.1.1", false)]
    [InlineData("!10.0.0.0/8,!192.168.0.0/16", "192.0.2.1", true)]
    [InlineData("10.1.0.0/16,!10.0.0.0/8", "10.1.2.3", true)]
    [InlineData("10.1.0.0/16,!10.0.0.0/8", "10.2.2.3", false)]
    [InlineData("10.1.0.0/16,!10.0.0.0/8", "192.0.2.1", true)]
    [InlineData("2001:db8:1::/48,!2001:db8::/32", "2001:db8:1::1", true)]
    [InlineData("2001:db8:1::/48,!2001:db8::/32", "2001:db8:2::1", false)]
    [InlineData("2001:db8:1::/48,!2001:db8::/32", "2001:db9::1", true)]
    public void NegativeIp_ShouldPreservePositiveUnionComplementOfNegativeUnion(string addresses, string ip, bool expected)
    {
        foreach (var constrained in new[] { false, true })
        {
            var rule = GenerateAddressRule(new RulesItem
            {
                Enabled = true, OutboundTag = Global.DirectTag, Ip = addresses.Split(',').ToList(),
                Port = constrained ? "443" : null,
            });
            MatchesAddressRule(rule, ip, 443).Should().Be(expected);
            if (constrained) MatchesAddressRule(rule, ip, 80).Should().BeFalse();
        }
    }

    private static Rule4Sbox GenerateAddressRule(RulesItem item)
    {
        var config = CoreConfigTestFactory.CreateConfig(ECoreType.sing_box);
        CoreConfigTestFactory.BindAppManagerConfig(config);
        var context = CoreConfigTestFactory.CreateContext(config,
            CoreConfigTestFactory.CreateSocksNode(ECoreType.sing_box), ECoreType.sing_box) with
        {
            RoutingItem = new RoutingItem { RuleSet = JsonUtils.Serialize(new[] { item }) },
        };
        var fragment = new CoreConfigSingboxService(context).BuildUserRoutingForCustom();
        fragment.Rules.Should().ContainSingle();
        return fragment.Rules.Single();
    }

    // Deliberately limited unit-test interpreter, not evidence of real sing-box traffic behavior.
    private static bool MatchesAddressRule(Rule4Sbox rule, string ip, int port,
        string network = "tcp", string protocol = "tls", string inbound = "mixed")
    {
        bool match;
        if (rule.type == "logical")
        {
            var branches = rule.rules!.Select(r => MatchesAddressRule(r, ip, port, network, protocol, inbound));
            match = rule.mode == "and" ? branches.All(x => x) : branches.Any(x => x);
        }
        else
        {
            var ports = rule.port == null && rule.port_range == null
                || rule.port?.Contains(port) == true
                || rule.port_range?.Any(range => { var bounds = range.Split(':'); return port >= int.Parse(bounds[0]) && port <= int.Parse(bounds[1]); }) == true;
            match = ports
                && (rule.network == null || rule.network.Contains(network))
                && (rule.protocol == null || rule.protocol.Contains(protocol))
                && (rule.inbound == null || rule.inbound.Contains(inbound))
                && (rule.ip_cidr == null || rule.ip_cidr.Any(cidr => System.Net.IPNetwork.Parse(cidr).Contains(System.Net.IPAddress.Parse(ip))));
        }
        return rule.invert == true ? !match : match;
    }

    [Fact]
    public void GenerateClientConfigContent_ShouldGenerateBasicProxyConfig()
    {
        var config = CoreConfigTestFactory.CreateConfig(ECoreType.sing_box);
        CoreConfigTestFactory.BindAppManagerConfig(config);
        var node = CoreConfigTestFactory.CreateSocksNode(ECoreType.sing_box);
        var context = CoreConfigTestFactory.CreateContext(config, node, ECoreType.sing_box);

        var result = new CoreConfigSingboxService(context).GenerateClientConfigContent();

        result.Success.Should().BeTrue($"ret msg: {result.Msg}");
        result.Data.Should().NotBeNull();

        var singboxConfig = JsonUtils.Deserialize<SingboxConfig>(result.Data!.ToString());
        singboxConfig.Should().NotBeNull();
        singboxConfig!.outbounds.Should().Contain(o => o.tag == Global.ProxyTag && o.type == "socks");
        singboxConfig.inbounds.Should().Contain(i => i.type == nameof(EInboundProtocol.mixed));
    }

    [Fact]
    public void GenerateClientConfigContent_TunWithLoopbackPreSocks_ShouldKeepMixedInbound()
    {
        var config = CoreConfigTestFactory.CreateConfig(ECoreType.sing_box);
        CoreConfigTestFactory.BindAppManagerConfig(config);
        var node = CoreConfigTestFactory.CreateSocksNode(ECoreType.sing_box);
        node.Address = Global.Loopback;
        node.Port = 1080;
        var context = CoreConfigTestFactory.CreateContext(config, node, ECoreType.sing_box) with
        {
            IsTunEnabled = true,
        };

        var result = new CoreConfigSingboxService(context).GenerateClientConfigContent();

        result.Success.Should().BeTrue($"ret msg: {result.Msg}");
        var cfg = JsonUtils.Deserialize<SingboxConfig>(result.Data!.ToString())!;

        cfg.inbounds.Should().Contain(i =>
            i.type == nameof(EInboundProtocol.mixed)
            && i.listen == Global.Loopback
            && i.listen_port == AppManager.Instance.GetLocalPort(EInboundProtocol.socks));
        cfg.inbounds.Should().Contain(i => i.type == "tun");
    }

    [Fact]
    public void GenerateClientConfigContent_BindInterface_ShouldUseDialBindInterface()
    {
        var config = CoreConfigTestFactory.CreateConfig(ECoreType.sing_box);
        config.CoreBasicItem.BindInterface = "eth0";
        CoreConfigTestFactory.BindAppManagerConfig(config);

        var node = CoreConfigTestFactory.CreateVmessNode(ECoreType.sing_box);
        var context = CoreConfigTestFactory.CreateContext(config, node, ECoreType.sing_box) with
        {
            IsTunEnabled = true,
        };

        var result = new CoreConfigSingboxService(context).GenerateClientConfigContent();

        result.Success.Should().BeTrue($"ret msg: {result.Msg}");
        var cfg = JsonUtils.Deserialize<SingboxConfig>(result.Data!.ToString())!;
        var proxy = cfg.outbounds.First(o => o.tag == Global.ProxyTag);

        proxy.bind_interface.Should().Be("eth0");
        proxy.detour.Should().BeNullOrEmpty();
    }

    [Fact]
    public void GenerateClientConfigContent_PolicyGroup_ShouldExpandChildrenAndBuildSelector()
    {
        var config = CoreConfigTestFactory.CreateConfig(ECoreType.sing_box);
        CoreConfigTestFactory.BindAppManagerConfig(config);

        var n1 = CoreConfigTestFactory.CreateSocksNode(ECoreType.sing_box, "n1", "node-1");
        var n2 = CoreConfigTestFactory.CreateSocksNode(ECoreType.sing_box, "n2", "node-2");
        var group = CoreConfigTestFactory.CreatePolicyGroupNode(ECoreType.sing_box, "g1", "group",
            [n1.IndexId, n2.IndexId]);

        var context = CoreConfigTestFactory.CreateContext(config, group, ECoreType.sing_box);
        context.AllProxiesMap[n1.IndexId] = n1;
        context.AllProxiesMap[n2.IndexId] = n2;
        context.AllProxiesMap[group.IndexId] = group;

        var result = new CoreConfigSingboxService(context).GenerateClientConfigContent();

        result.Success.Should().BeTrue($"ret msg: {result.Msg}");
        var cfg = JsonUtils.Deserialize<SingboxConfig>(result.Data!.ToString())!;

        cfg.outbounds.Should().Contain(o => o.tag == Global.ProxyTag && o.type == "selector");
        cfg.outbounds.Should().Contain(o => o.tag == $"{Global.ProxyTag}-auto" && o.type == "urltest");
        cfg.outbounds.Should().Contain(o => o.tag.StartsWith("proxy-1-", StringComparison.Ordinal));
        cfg.outbounds.Should().Contain(o => o.tag.StartsWith("proxy-2-", StringComparison.Ordinal));
    }

    [Fact]
    public void GenerateClientConfigContent_ProxyChain_ShouldBuildDetourChain()
    {
        var config = CoreConfigTestFactory.CreateConfig(ECoreType.sing_box);
        CoreConfigTestFactory.BindAppManagerConfig(config);

        var n1 = CoreConfigTestFactory.CreateSocksNode(ECoreType.sing_box, "n1", "node-1");
        var n2 = CoreConfigTestFactory.CreateSocksNode(ECoreType.sing_box, "n2", "node-2");
        var chain = CoreConfigTestFactory.CreateProxyChainNode(ECoreType.sing_box, "c1", "chain",
            [n1.IndexId, n2.IndexId]);

        var context = CoreConfigTestFactory.CreateContext(config, chain, ECoreType.sing_box);
        context.AllProxiesMap[n1.IndexId] = n1;
        context.AllProxiesMap[n2.IndexId] = n2;
        context.AllProxiesMap[chain.IndexId] = chain;

        var result = new CoreConfigSingboxService(context).GenerateClientConfigContent();

        result.Success.Should().BeTrue($"ret msg: {result.Msg}");
        var cfg = JsonUtils.Deserialize<SingboxConfig>(result.Data!.ToString())!;

        cfg.outbounds.Should().Contain(o => o.tag == Global.ProxyTag && o.type == "socks");
        cfg.outbounds.Should().Contain(o => o.tag.StartsWith("chain-proxy-1-", StringComparison.Ordinal));
        cfg.outbounds.Should().Contain(o =>
            o.tag == Global.ProxyTag &&
            (o.detour ?? string.Empty).StartsWith("chain-proxy-1-", StringComparison.Ordinal));
    }

    [Fact]
    public void GenerateClientConfigContent_PolicyGroupWithProxyChain_ShouldBuildCombinedOutbounds()
    {
        var config = CoreConfigTestFactory.CreateConfig(ECoreType.sing_box);
        CoreConfigTestFactory.BindAppManagerConfig(config);

        var n1 = CoreConfigTestFactory.CreateSocksNode(ECoreType.sing_box, "n1", "node-1");
        var n2 = CoreConfigTestFactory.CreateSocksNode(ECoreType.sing_box, "n2", "node-2");
        var n3 = CoreConfigTestFactory.CreateSocksNode(ECoreType.sing_box, "n3", "node-3");
        var chain = CoreConfigTestFactory.CreateProxyChainNode(ECoreType.sing_box, "c1", "chain",
            [n1.IndexId, n2.IndexId]);
        var group = CoreConfigTestFactory.CreatePolicyGroupNode(ECoreType.sing_box, "g1", "group",
            [chain.IndexId, n3.IndexId]);

        var context = CoreConfigTestFactory.CreateContext(config, group, ECoreType.sing_box);
        context.AllProxiesMap[n1.IndexId] = n1;
        context.AllProxiesMap[n2.IndexId] = n2;
        context.AllProxiesMap[n3.IndexId] = n3;
        context.AllProxiesMap[chain.IndexId] = chain;
        context.AllProxiesMap[group.IndexId] = group;

        var result = new CoreConfigSingboxService(context).GenerateClientConfigContent();

        result.Success.Should().BeTrue($"ret msg: {result.Msg}");
        var cfg = JsonUtils.Deserialize<SingboxConfig>(result.Data!.ToString())!;

        cfg.outbounds.Should().Contain(o => o.tag == Global.ProxyTag && o.type == "selector");
        cfg.outbounds.Should().Contain(o => o.tag == $"{Global.ProxyTag}-auto" && o.type == "urltest");
        cfg.outbounds.Should().Contain(o => o.tag.StartsWith("proxy-1-", StringComparison.Ordinal));
        cfg.outbounds.Should().Contain(o => o.tag.StartsWith("chain-proxy-1-", StringComparison.Ordinal));
        cfg.outbounds.Should().Contain(o => o.tag.StartsWith("proxy-2-", StringComparison.Ordinal));
    }

    [Fact]
    public void GenerateClientConfigContent_ProxyChainWithPolicyGroup_ShouldBuildClonedChainBranches()
    {
        var config = CoreConfigTestFactory.CreateConfig(ECoreType.sing_box);
        CoreConfigTestFactory.BindAppManagerConfig(config);

        var n1 = CoreConfigTestFactory.CreateSocksNode(ECoreType.sing_box, "n1", "node-1");
        var n2 = CoreConfigTestFactory.CreateSocksNode(ECoreType.sing_box, "n2", "node-2");
        var n3 = CoreConfigTestFactory.CreateSocksNode(ECoreType.sing_box, "n3", "node-3");
        var group = CoreConfigTestFactory.CreatePolicyGroupNode(ECoreType.sing_box, "g1", "group",
            [n1.IndexId, n2.IndexId]);
        var chain = CoreConfigTestFactory.CreateProxyChainNode(ECoreType.sing_box, "c1", "chain",
            [group.IndexId, n3.IndexId]);

        var context = CoreConfigTestFactory.CreateContext(config, chain, ECoreType.sing_box);
        context.AllProxiesMap[n1.IndexId] = n1;
        context.AllProxiesMap[n2.IndexId] = n2;
        context.AllProxiesMap[n3.IndexId] = n3;
        context.AllProxiesMap[group.IndexId] = group;
        context.AllProxiesMap[chain.IndexId] = chain;

        var result = new CoreConfigSingboxService(context).GenerateClientConfigContent();

        result.Success.Should().BeTrue($"ret msg: {result.Msg}");
        var cfg = JsonUtils.Deserialize<SingboxConfig>(result.Data!.ToString())!;

        cfg.outbounds.Should().Contain(o => o.tag == Global.ProxyTag && o.type == "selector");
        cfg.outbounds.Should().Contain(o => o.tag == $"{Global.ProxyTag}-auto" && o.type == "urltest");
        cfg.outbounds.Should().Contain(o => o.tag.StartsWith("chain-proxy-1-group-1-", StringComparison.Ordinal));
        cfg.outbounds.Should().Contain(o => o.tag.StartsWith("chain-proxy-1-group-2-", StringComparison.Ordinal));

        var proxyCloneCount = cfg.outbounds.Count(o => o.tag.StartsWith("proxy-clone-", StringComparison.Ordinal));
        proxyCloneCount.Should().Be(2);

        var allCloneDetoursPointToGroupBranches = cfg.outbounds
            .Where(o => o.tag.StartsWith("proxy-clone-", StringComparison.Ordinal))
            .All(o => (o.detour ?? string.Empty).StartsWith("chain-proxy-1-group-", StringComparison.Ordinal));
        allCloneDetoursPointToGroupBranches.Should().BeTrue();
    }

    [Fact]
    public void GenerateClientConfigContent_RoutingSplit_DirectAndBlock_ShouldApplyRules()
    {
        var config = CoreConfigTestFactory.CreateConfig(ECoreType.sing_box);
        CoreConfigTestFactory.BindAppManagerConfig(config);

        var node = CoreConfigTestFactory.CreateSocksNode(ECoreType.sing_box, "n-main", "main");
        var context = CoreConfigTestFactory.CreateContext(config, node, ECoreType.sing_box) with
        {
            RoutingItem = new RoutingItem
            {
                Id = "r-split-1",
                Remarks = "split-direct-block",
                RuleSet = JsonUtils.Serialize(new List<RulesItem>
                {
                    new()
                    {
                        Enabled = true,
                        RuleType = ERuleType.Routing,
                        OutboundTag = Global.DirectTag,
                        Domain = ["full:direct.example.com"],
                    },
                    new()
                    {
                        Enabled = true,
                        RuleType = ERuleType.Routing,
                        OutboundTag = Global.BlockTag,
                        Domain = ["full:block.example.com"],
                    }
                }),
                DomainStrategy = Global.AsIs,
                DomainStrategy4Singbox = string.Empty,
            }
        };

        var result = new CoreConfigSingboxService(context).GenerateClientConfigContent();

        result.Success.Should().BeTrue($"ret msg: {result.Msg}");
        var cfg = JsonUtils.Deserialize<SingboxConfig>(result.Data!.ToString())!;

        var hasDirectRule = cfg.route.rules.Any(r =>
            r.domain != null
            && r.domain.Contains("direct.example.com")
            && r.outbound == Global.DirectTag);
        hasDirectRule.Should().BeTrue();

        var hasBlockRule = cfg.route.rules.Any(r =>
            r.domain != null
            && r.domain.Contains("block.example.com")
            && r.action == "reject");
        hasBlockRule.Should().BeTrue();
    }

    [Fact]
    public void GenerateClientConfigContent_RoutingSplit_ByRemark_ShouldGenerateTargetOutbound()
    {
        var config = CoreConfigTestFactory.CreateConfig(ECoreType.sing_box);
        CoreConfigTestFactory.BindAppManagerConfig(config);

        var node = CoreConfigTestFactory.CreateSocksNode(ECoreType.sing_box, "n-main", "main");
        var routeNode = CoreConfigTestFactory.CreateSocksNode(ECoreType.sing_box, "n-route", "route-node");

        var context = CoreConfigTestFactory.CreateContext(config, node, ECoreType.sing_box) with
        {
            RoutingItem = new RoutingItem
            {
                Id = "r-split-2",
                Remarks = "split-remark",
                RuleSet = JsonUtils.Serialize(new List<RulesItem>
                {
                    new()
                    {
                        Enabled = true,
                        RuleType = ERuleType.Routing,
                        OutboundTag = routeNode.Remarks,
                        Domain = ["full:route.example.com"],
                    }
                }),
                DomainStrategy = Global.AsIs,
                DomainStrategy4Singbox = string.Empty,
            }
        };
        context.AllProxiesMap[$"remark:{routeNode.Remarks}"] = routeNode;

        var result = new CoreConfigSingboxService(context).GenerateClientConfigContent();

        result.Success.Should().BeTrue($"ret msg: {result.Msg}");
        var cfg = JsonUtils.Deserialize<SingboxConfig>(result.Data!.ToString())!;
        var expectedPrefix = $"{routeNode.IndexId}-{Global.ProxyTag}-{routeNode.Remarks}";

        cfg.outbounds.Should().Contain(o => o.tag.StartsWith(expectedPrefix, StringComparison.Ordinal));

        var hasRouteRule = cfg.route.rules.Any(r =>
            r.domain != null
            && r.domain.Contains("route.example.com")
            && (r.outbound ?? string.Empty).StartsWith(expectedPrefix, StringComparison.Ordinal));
        hasRouteRule.Should().BeTrue();
    }

    [Fact]
    public void GenerateClientConfigContent_DirectExpectedIPs_ShouldApplyGeoipAndCidrToDirectDnsRule()
    {
        var config = CoreConfigTestFactory.CreateConfigWithDirectExpectedIPs(
            ECoreType.sing_box,
            "192.168.0.0/16,geoip:cn");
        CoreConfigTestFactory.BindAppManagerConfig(config);

        var node = CoreConfigTestFactory.CreateSocksNode(ECoreType.sing_box, "n-main", "main");
        var context = CoreConfigTestFactory.CreateContext(config, node, ECoreType.sing_box) with
        {
            RoutingItem = new RoutingItem
            {
                Id = "r-dns-direct-expected",
                Remarks = "dns-direct-expected",
                RuleSet = JsonUtils.Serialize(new List<RulesItem>
                {
                    new()
                    {
                        Enabled = true,
                        RuleType = ERuleType.DNS,
                        OutboundTag = Global.DirectTag,
                        Domain = ["geosite:cn"],
                    }
                }),
                DomainStrategy = Global.AsIs,
                DomainStrategy4Singbox = string.Empty,
            }
        };

        var result = new CoreConfigSingboxService(context).GenerateClientConfigContent();

        result.Success.Should().BeTrue($"ret msg: {result.Msg}");
        var cfg = JsonUtils.Deserialize<SingboxConfig>(result.Data!.ToString())!;

        var hasExpectedRule = cfg.dns.rules?.Any(r =>
            r.server == Global.SingboxDirectDNSTag
            && r.ip_cidr?.Contains("192.168.0.0/16") == true
            && r.rule_set?.Contains("geosite-cn") == true
            && r.rule_set?.Contains("geoip-cn") == true) ?? false;

        hasExpectedRule.Should().BeTrue();
    }

    [Fact]
    public void GenerateClientConfigContent_BootstrapDNS_ShouldConfigurePureIPResolver()
    {
        var bootstrapDns = "8.8.8.8";
        var config = CoreConfigTestFactory.CreateConfigWithBootstrapDNS(ECoreType.sing_box, bootstrapDns);
        CoreConfigTestFactory.BindAppManagerConfig(config);

        var node = CoreConfigTestFactory.CreateSocksNode(ECoreType.sing_box, "n-main", "main");
        var context = CoreConfigTestFactory.CreateContext(config, node, ECoreType.sing_box);

        var result = new CoreConfigSingboxService(context).GenerateClientConfigContent();

        result.Success.Should().BeTrue($"ret msg: {result.Msg}");
        config.SimpleDNSItem.BootstrapDNS.Should().Be(bootstrapDns);

        var cfg = JsonUtils.Deserialize<SingboxConfig>(result.Data!.ToString())!;
        var bootstrapServer = cfg.dns.servers?.FirstOrDefault(s => s.tag == Global.SingboxLocalDNSTag);
        bootstrapServer.Should().NotBeNull();
        (bootstrapServer?.server ?? string.Empty).Should().Contain(bootstrapDns);
    }

    [Fact]
    public void GenerateClientConfigContent_DnsFallback_LastRuleDirect_ShouldUseDirectFinalDns()
    {
        var config = CoreConfigTestFactory.CreateConfig(ECoreType.sing_box);
        config.SimpleDNSItem.DirectDNS = "1.1.1.1";
        config.SimpleDNSItem.RemoteDNS = "9.9.9.9";
        CoreConfigTestFactory.BindAppManagerConfig(config);

        var node = CoreConfigTestFactory.CreateSocksNode(ECoreType.sing_box, "n-main", "main");
        var context = CoreConfigTestFactory.CreateContext(config, node, ECoreType.sing_box) with
        {
            RoutingItem = new RoutingItem
            {
                Id = "r-direct-final",
                Remarks = "direct-final",
                RuleSet = JsonUtils.Serialize(new List<RulesItem>
                {
                    new()
                    {
                        Enabled = true,
                        RuleType = ERuleType.Routing,
                        OutboundTag = Global.DirectTag,
                        Ip = ["0.0.0.0/0"],
                        Port = "0-65535",
                        Network = "tcp,udp",
                    }
                }),
                DomainStrategy = Global.AsIs,
                DomainStrategy4Singbox = string.Empty,
            }
        };

        var result = new CoreConfigSingboxService(context).GenerateClientConfigContent();

        result.Success.Should().BeTrue($"ret msg: {result.Msg}");
        var cfg = JsonUtils.Deserialize<SingboxConfig>(result.Data!.ToString())!;

        cfg.dns.final.Should().Be(Global.SingboxDirectDNSTag);
    }

    [Fact]
    public void GenerateClientConfigContent_DirectExpectedIPs_NonMatchingRegion_ShouldNotApplyExpectedRule()
    {
        var config =
            CoreConfigTestFactory.CreateConfigWithDirectExpectedIPs(ECoreType.sing_box, "192.168.0.0/16,geoip:cn");
        CoreConfigTestFactory.BindAppManagerConfig(config);

        var node = CoreConfigTestFactory.CreateSocksNode(ECoreType.sing_box, "n-main", "main");
        var context = CoreConfigTestFactory.CreateContext(config, node, ECoreType.sing_box) with
        {
            RoutingItem = new RoutingItem
            {
                Id = "r-dns-direct-unmatched",
                Remarks = "dns-direct-unmatched",
                RuleSet = JsonUtils.Serialize(new List<RulesItem>
                {
                    new()
                    {
                        Enabled = true,
                        RuleType = ERuleType.DNS,
                        OutboundTag = Global.DirectTag,
                        Domain = ["geosite:us"],
                    }
                }),
                DomainStrategy = Global.AsIs,
                DomainStrategy4Singbox = string.Empty,
            }
        };

        var result = new CoreConfigSingboxService(context).GenerateClientConfigContent();

        result.Success.Should().BeTrue($"ret msg: {result.Msg}");
        var cfg = JsonUtils.Deserialize<SingboxConfig>(result.Data!.ToString())!;

        var hasExpectedRule = cfg.dns.rules?.Any(r =>
            r.server == Global.SingboxDirectDNSTag
            && r.ip_cidr?.Contains("192.168.0.0/16") == true
            && r.rule_set?.Contains("geoip-cn") == true) ?? false;
        hasExpectedRule.Should().BeFalse();
    }

    [Theory]
    [InlineData("geosite:cn", "geosite-cn")]
    [InlineData("geosite:geolocation-cn", "geosite-geolocation-cn")]
    [InlineData("geosite:tld-cn", "geosite-tld-cn")]
    public void GenerateClientConfigContent_DirectExpectedIPs_RegionVariant_ShouldApplyExpectedRule(string domainTag,
        string expectedRuleSetTag)
    {
        var config =
            CoreConfigTestFactory.CreateConfigWithDirectExpectedIPs(ECoreType.sing_box, "192.168.0.0/16,geoip:cn");
        CoreConfigTestFactory.BindAppManagerConfig(config);

        var node = CoreConfigTestFactory.CreateSocksNode(ECoreType.sing_box, "n-main", "main");
        var context = CoreConfigTestFactory.CreateContext(config, node, ECoreType.sing_box) with
        {
            RoutingItem = new RoutingItem
            {
                Id = "r-dns-direct-variant",
                Remarks = "dns-direct-variant",
                RuleSet = JsonUtils.Serialize(new List<RulesItem>
                {
                    new()
                    {
                        Enabled = true, RuleType = ERuleType.DNS, OutboundTag = Global.DirectTag, Domain = [domainTag],
                    }
                }),
                DomainStrategy = Global.AsIs,
                DomainStrategy4Singbox = string.Empty,
            }
        };

        var result = new CoreConfigSingboxService(context).GenerateClientConfigContent();

        result.Success.Should().BeTrue($"ret msg: {result.Msg}");
        var cfg = JsonUtils.Deserialize<SingboxConfig>(result.Data!.ToString())!;

        var hasExpectedRule = cfg.dns.rules?.Any(r =>
            r.server == Global.SingboxDirectDNSTag
            && r.ip_cidr?.Contains("192.168.0.0/16") == true
            && r.rule_set?.Contains(expectedRuleSetTag) == true
            && r.rule_set?.Contains("geoip-cn") == true) ?? false;
        hasExpectedRule.Should().BeTrue();
    }

    [Fact]
    public void GenerateClientConfigContent_Hosts_ShouldPopulateHostsServerAndDomainResolver()
    {
        var config = CoreConfigTestFactory.CreateConfig(ECoreType.sing_box);
        config.SimpleDNSItem.Hosts = "resolver.example 1.1.1.1";
        config.SimpleDNSItem.DirectDNS = "https://resolver.example/dns-query";
        CoreConfigTestFactory.BindAppManagerConfig(config);

        var node = CoreConfigTestFactory.CreateSocksNode(ECoreType.sing_box, "n-main", "main");
        var context = CoreConfigTestFactory.CreateContext(config, node, ECoreType.sing_box);

        var result = new CoreConfigSingboxService(context).GenerateClientConfigContent();

        result.Success.Should().BeTrue($"ret msg: {result.Msg}");
        var cfg = JsonUtils.Deserialize<SingboxConfig>(result.Data!.ToString())!;

        var hostsServer = cfg.dns.servers.FirstOrDefault(s => s.tag == Global.SingboxHostsDNSTag);
        hostsServer.Should().NotBeNull();
        hostsServer!.predefined.Should().ContainKey("resolver.example");
        hostsServer.predefined!["resolver.example"].Should().Contain("1.1.1.1");

        var directServer = cfg.dns.servers.FirstOrDefault(s => s.tag == Global.SingboxDirectDNSTag);
        directServer.Should().NotBeNull();
        directServer!.domain_resolver.Should().Be(Global.SingboxHostsDNSTag);
    }

    [Fact]
    public void GenerateClientConfigContent_RawDnsEnabled_ShouldUseCustomDnsAndInjectLocalResolver()
    {
        var config = CoreConfigTestFactory.CreateConfig(ECoreType.sing_box);
        CoreConfigTestFactory.BindAppManagerConfig(config);

        var node = CoreConfigTestFactory.CreateSocksNode(ECoreType.sing_box, "n-main", "main");
        var rawDns = new Dns4Sbox
        {
            servers =
            [
                new Server4Sbox { tag = "remote", type = "udp", server = "8.8.8.8", detour = Global.ProxyTag, }
            ],
            rules = [],
        };
        var context = CoreConfigTestFactory.CreateContext(config, node, ECoreType.sing_box) with
        {
            RawDnsItem = new DNSItem
            {
                Id = "dns-raw-1",
                Remarks = "raw",
                Enabled = true,
                CoreType = ECoreType.sing_box,
                NormalDNS = JsonUtils.Serialize(rawDns),
                DomainDNSAddress = "1.1.1.1",
            }
        };

        var result = new CoreConfigSingboxService(context).GenerateClientConfigContent();

        result.Success.Should().BeTrue($"ret msg: {result.Msg}");
        var cfg = JsonUtils.Deserialize<SingboxConfig>(result.Data!.ToString())!;

        cfg.dns.servers.Should().Contain(s => s.tag == "remote" && s.type == "udp" && s.server == "8.8.8.8");
        cfg.dns.servers.Should().Contain(s => s.tag == Global.SingboxLocalDNSTag);
        cfg.dns.rules.Should().Contain(r => r.clash_mode == nameof(ERuleMode.Global));
        cfg.dns.rules.Should().Contain(r => r.clash_mode == nameof(ERuleMode.Direct));
    }

    [Fact]
    public void GenerateClientConfigContent_Hysteria2Realm_ShouldEmitHttpsServerUrl()
    {
        var shareLink =
            "hysteria2+realm://public@realm.hy2.io/my-realm-id?auth=uuid&stun=turn.cloudflare.com%3A3478&sni=cloudflare.com&pinSHA256=xxx#Realm-Test";
        var node = Hysteria2Fmt.ResolveRealm(shareLink, out _);
        node.Should().NotBeNull();
        node!.CoreType = ECoreType.sing_box;

        var config = CoreConfigTestFactory.CreateConfig(ECoreType.sing_box);
        config.CoreTypeItem =
        [
            new CoreTypeItem { ConfigType = EConfigType.Hysteria2, CoreType = ECoreType.sing_box }
        ];
        CoreConfigTestFactory.BindAppManagerConfig(config);
        var context = CoreConfigTestFactory.CreateContext(config, node, ECoreType.sing_box);

        var result = new CoreConfigSingboxService(context).GenerateClientConfigContent();

        result.Success.Should().BeTrue($"ret msg: {result.Msg}");
        var cfg = JsonUtils.Deserialize<SingboxConfig>(result.Data!.ToString())!;
        var proxy = cfg.outbounds.First(o => o.tag == Global.ProxyTag);

        proxy.type.Should().Be("hysteria2");
        proxy.realm.Should().NotBeNull();
        proxy.realm!.server_url.Should().StartWith("https://");
        proxy.realm.server_url.Should().Contain("realm.hy2.io");
        proxy.realm.token.Should().Be("public");
        proxy.realm.realm_id.Should().Be("my-realm-id");
        proxy.realm.stun_servers.Should().Contain("turn.cloudflare.com:3478");
        proxy.server.Should().BeNull();
    }
}
