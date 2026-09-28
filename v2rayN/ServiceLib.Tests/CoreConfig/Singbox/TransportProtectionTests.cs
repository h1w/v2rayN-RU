using AwesomeAssertions;
using ServiceLib.Common;
using ServiceLib.Enums;
using ServiceLib.Handler;
using ServiceLib.Handler.Builder;
using ServiceLib.Models;
using ServiceLib.Models.CoreConfigs;
using ServiceLib.Services.CoreConfig;
using Xunit;

namespace ServiceLib.Tests.CoreConfig.Singbox;

public class TransportProtectionTests
{
    [Fact]
    public void MissingProcess_ProtectsOnlyExactIpAndPortPairs()
    {
        var generated = Generate("""
            {"outbounds":[
              {"protocol":"vless","settings":{"vnext":[{"address":"192.0.2.10","port":5012},{"address":"2001:db8::10","port":443}]}},
              {"protocol":"trojan","settings":{"servers":[{"address":"192.0.2.20","port":8443}]}}
            ]}
            """);
        var rules = EndpointRules(generated);
        rules.Should().Contain(r => r.ip_cidr!.Contains("192.0.2.10/32") && r.port!.SequenceEqual(new[] { 5012 }));
        rules.Should().Contain(r => r.ip_cidr!.Contains("2001:db8::10/128") && r.port!.SequenceEqual(new[] { 443 }));
        rules.Should().Contain(r => r.ip_cidr!.Contains("192.0.2.20/32") && r.port!.SequenceEqual(new[] { 8443 }));
        rules.Should().OnlyContain(r => r.process_path == null && r.process_name == null);
        rules.Should().NotContain(r => r.ip_cidr!.Contains("192.0.2.10/32") && r.port!.Contains(8443));
        generated.route.final.Should().Be(Global.ProxyTag);
        var processIndex = generated.route.rules.FindIndex(r => r.process_path?.Count > 0);
        if (processIndex >= 0)
            generated.route.rules.IndexOf(rules[0]).Should().BeLessThan(processIndex);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void DomainTransport_UsesDirectDnsReverseMappingAndPostSniffMatch(bool customDns, bool sniffing)
    {
        var generated = Generate("""
            {"outbounds":[{"protocol":"vless","settings":{"vnext":[{"address":"edge.example","port":5012}]},
             "streamSettings":{"security":"reality","realitySettings":{"serverName":"cover.example"},"wsSettings":{"headers":{"Host":"web.example"}}}}]}
            """, customDns: customDns, sniffing: sniffing);
        generated.dns.reverse_mapping.Should().BeTrue();
        var dnsRule = generated.dns.rules.First(r => r.domain?.Contains("edge.example") == true);
        dnsRule.server.Should().Be(customDns ? Global.SingboxLocalDNSTag : Global.SingboxDirectDNSTag);
        generated.dns.rules.TakeWhile(r => r != dnsRule).Should().NotContain(r => r.clash_mode == "Global");
        var rules = generated.route.rules.Where(r => r.domain?.Contains("edge.example") == true).ToList();
        rules.Should().NotBeEmpty();
        rules.Should().OnlyContain(r => r.port!.SequenceEqual(new[] { 5012 }) && r.outbound == Global.DirectTag
            && r.process_path == null && r.process_name == null);
        if (sniffing)
        {
            var sniffIndex = generated.route.rules.FindIndex(r => r.action == "sniff");
            rules.Should().Contain(r => generated.route.rules.IndexOf(r) > sniffIndex);
        }
        generated.route.rules.Should().NotContain(r => r.domain != null && (r.domain.Contains("cover.example") || r.domain.Contains("web.example")));
    }

    [Fact]
    public void ExplicitChains_ProtectTerminalNotProxiedDestinations()
    {
        var generated = Generate("""
            {"outbounds":[
              {"protocol":"vless","settings":{"vnext":[{"address":"192.0.2.1","port":443}]},"streamSettings":{"sockopt":{"dialerProxy":"terminal"}}},
              {"protocol":"trojan","settings":{"servers":[{"address":"192.0.2.2","port":443}]},"proxySettings":{"tag":"terminal"}},
              {"protocol":"socks","tag":"terminal","settings":{"servers":[{"address":"192.0.2.3","port":1080}]}},
              {"protocol":"freedom","settings":{"redirect":"192.0.2.4:443"}}
            ]}
            """);
        EndpointRules(generated).Should().ContainSingle().Which.ip_cidr.Should().Equal("192.0.2.3/32");
        var singbox = Generate("""
            {"outbounds":[
              {"type":"vless","server":"192.0.2.1","server_port":443,"detour":"terminal"},
              {"type":"socks","tag":"terminal","server":"192.0.2.3","server_port":1080},
              {"type":"direct","server":"192.0.2.4","server_port":443}
            ]}
            """, ECoreType.sing_box);
        EndpointRules(singbox).Should().ContainSingle().Which.ip_cidr.Should().Equal("192.0.2.3/32");
    }

    [Fact]
    public void Xhttp_ProtectsSeparateDirectDownloadButNotChainedDownload()
    {
        var endpoints = CustomConfigComposer.ExtractDirectTransportEndpoints("""
            {"outbounds":[
              {"protocol":"vless","settings":{"vnext":[{"address":"192.0.2.1","port":443}]},
               "streamSettings":{"network":"xhttp","xhttpSettings":{"extra":{"downloadSettings":{"address":"download.example","port":8443}}}}},
              {"protocol":"vless","settings":{"vnext":[{"address":"192.0.2.2","port":443}]},
               "streamSettings":{"network":"xhttp","xhttpSettings":{"extra":{"downloadSettings":{"address":"chained.example","port":8443,"sockopt":{"dialerProxy":"next"}}}}}}
            ]}
            """, ECoreType.Xray);
        endpoints.Should().Contain(new TransportEndpoint("download.example", 8443));
        endpoints.Should().NotContain(e => e.Address == "chained.example");
    }

    [Fact]
    public void NonSocketValues_DoNotCreateBroadProtection()
    {
        var endpoints = CustomConfigComposer.ExtractDirectTransportEndpoints("""
            {"dns":{"servers":["resolver.example"]},"burstObservatory":{"pingConfig":{"destination":"https://probe.example/"}},
             "outbounds":[
              {"protocol":"vless","settings":{"vnext":[{"address":"edge.example","port":0},{"address":"192.0.2.0/24","port":443},{"address":"valid.example","port":65536}]}},
              {"protocol":"vless","settings":{"vnext":[{"address":"_service._tcp.example","port":443}]},"streamSettings":{"sockopt":{"addressPortStrategy":"srvportandaddress"}}},
              {"protocol":"vless","settings":{"vnext":[{"address":"upload.example","port":443}]},"streamSettings":{"network":"xhttp","sockopt":{"dialerProxy":"next","penetrate":true},"xhttpSettings":{"extra":{"downloadSettings":{"address":"download.example","port":8443}}}}}
             ]}
            """, ECoreType.Xray);
        endpoints.Should().BeEmpty();
    }

    [Fact]
    public async Task ActiveAndChainedJson_ContributeOnlyTheirTransportEndpoints()
    {
        var active = Path.GetTempFileName();
        var child = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(active, """{"outbounds":[{"protocol":"vless","settings":{"vnext":[{"address":"192.0.2.10","port":5012}]}}]}""", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(child, """{"outbounds":[{"type":"trojan","server":"child.example","server_port":8443}]}""", TestContext.Current.CancellationToken);
            var config = CoreConfigTestFactory.CreateConfig();
            var context = CoreConfigTestFactory.CreateContext(config,
                new ProfileItem { ConfigType = EConfigType.Custom, Address = active }, ECoreType.Xray) with
            {
                IsTunEnabled = true,
                ChainCores = [new ChainCoreDescriptor
                {
                    Node = new ProfileItem { ConfigType = EConfigType.Custom, Address = child },
                    CoreType = ECoreType.sing_box, Port = 32100, ConfigFileName = "configChain0.json",
                }],
            };
            await CoreConfigContextBuilder.PopulateTransportProtectionAsync(context);
            context.ProtectTransportEndpoints.Should().BeEquivalentTo(new[]
            {
                new TransportEndpoint("192.0.2.10", 5012), new TransportEndpoint("child.example", 8443),
            });
            context.ProtectDomainList.Should().Contain("child.example");
        }
        finally
        {
            File.Delete(active);
            File.Delete(child);
        }
    }

    private static List<Rule4Sbox> EndpointRules(SingboxConfig config) => config.route.rules
        .Where(r => r.outbound == Global.DirectTag && r.port != null && r.ip_cidr != null).ToList();

    private static SingboxConfig Generate(string json, ECoreType sourceCore = ECoreType.Xray,
        bool customDns = false, bool sniffing = true)
    {
        var config = CoreConfigTestFactory.CreateConfig(ECoreType.sing_box);
        config.TunModeItem.EnableTun = true;
        config.Inbound[0].SniffingEnabled = sniffing;
        CoreConfigTestFactory.BindAppManagerConfig(config);
        var endpoints = CustomConfigComposer.ExtractDirectTransportEndpoints(json, sourceCore);
        var context = CoreConfigTestFactory.CreateContext(config,
            CoreConfigTestFactory.CreateSocksNode(ECoreType.sing_box), ECoreType.sing_box) with
        {
            IsTunEnabled = true, RoutingItem = null, ProtectCoreTypeList = [ECoreType.Xray],
            ProtectTransportEndpoints = endpoints,
            ProtectDomainList = endpoints.Where(e => Utils.IsDomain(e.Address)).Select(e => e.Address).ToHashSet(),
            RawDnsItem = customDns ? new DNSItem { Enabled = true } : null,
        };
        var result = new CoreConfigSingboxService(context).GenerateClientConfigContent();
        result.Success.Should().BeTrue($"ret msg: {result.Msg}");
        return JsonUtils.Deserialize<SingboxConfig>(result.Data!.ToString())!;
    }
}
