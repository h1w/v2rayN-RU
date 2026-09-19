using ServiceLib.IntegrationTests.Infrastructure;
using ServiceLib.Tests.CoreConfig;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace ServiceLib.IntegrationTests;

// These are real-core tests, intentionally independent of the unit-test project's execution.
// No JSON delegation, DNS resolution, UDP, VLESS, TUN or cross-platform claim is made here.
public sealed class RoutingPipelineTests
{
    [Fact]
    public Task Singbox_NegativeIpAndPort_OnlyMatchingRequestUsesB() => RunAsync(ECoreType.sing_box);

    [Fact]
    public Task Xray_OrdinaryA_RoutesMatchingRequestToOrdinaryB() => RunAsync(ECoreType.Xray);

    [Theory]
    [InlineData(ECoreType.Xray)]
    [InlineData(ECoreType.sing_box)]
    public Task LiteralIp_IPIfNonMatch_keeps_selected_target(ECoreType type) => RunAsync(type, true);

    [Theory]
    [InlineData(ECoreType.Xray)]
    [InlineData(ECoreType.sing_box)]
    public Task Domain_IPIfNonMatch_resolves_locally_and_selects_B(ECoreType type) => RunAsync(type, true, true);

    private static async Task RunAsync(ECoreType coreType, bool ipIfNonMatch = false, bool domain = false)
    {
        // Fail preparation before touching singleton state or starting any listener.
        var executable = CoreProcessFixture.RequireExecutable(coreType);
        await using var dns = new LocalDnsFixture();
        await using var matching = new LoopbackTargetServer(IPAddress.Loopback);
        await using var otherPort = new LoopbackTargetServer(IPAddress.Loopback);
        await using var excluded = new LoopbackTargetServer(IPAddress.Parse("127.0.0.2"), matching.Port);
        var permitted = new[] { matching.Endpoint, otherPort.Endpoint, excluded.Endpoint };
        await using var a = new LoopbackProxyFixture(permitted);
        await using var b = new LoopbackProxyFixture(permitted);

        using var inboundReservation = new TcpListener(IPAddress.Loopback, 0);
        inboundReservation.Start();
        var inboundPort = ((IPEndPoint)inboundReservation.LocalEndpoint).Port;
        using var apiReservation = new TcpListener(IPAddress.Loopback, 0);
        apiReservation.Start();
        var apiPort = ((IPEndPoint)apiReservation.LocalEndpoint).Port;

        // Production still reads AppManager for inbound/API ports. Save/restore only those fields;
        // never initialize the app, database, user config, or CoreConfigContextBuilder.
        var fields = new[] { "_config", "_statePort", "_statePort2" }
            .Select(name => typeof(AppManager).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException($"AppManager field missing: {name}"))
            .ToArray();
        var previous = fields.Select(f => f.GetValue(AppManager.Instance)).ToArray();
        try
        {
            var config = CoreConfigTestFactory.CreateConfig(coreType);
            config.Inbound[0].LocalPort = inboundPort;
            config.Inbound[0].SniffingEnabled = false;
            config.Inbound[0].UdpEnabled = false;
            config.CoreBasicItem.EnableCacheFile4Sbox = false;
            // Literal IP destinations need no DNS. Even unexpected DNS cannot use external servers.
            config.SimpleDNSItem.BootstrapDNS = "127.0.0.1";
            config.SimpleDNSItem.DirectDNS = "127.0.0.1";
            config.SimpleDNSItem.RemoteDNS = "127.0.0.1";
            if (domain)
            {
                config.SimpleDNSItem.DirectDNS = $"udp://127.0.0.1:{dns.Port}";
                config.SimpleDNSItem.RemoteDNS = $"udp://127.0.0.1:{dns.Port}";
            }
            CoreConfigTestFactory.BindAppManagerConfig(config);
            fields[1].SetValue(AppManager.Instance, apiPort);
            fields[2].SetValue(AppManager.Instance, apiPort);

            var main = CoreConfigTestFactory.CreateSocksNode(coreType, "a", "A");
            main.Port = a.Port;
            main.Username = main.Password = string.Empty;
            var selected = CoreConfigTestFactory.CreateSocksNode(coreType, "b", "B");
            selected.Port = b.Port;
            selected.Username = selected.Password = string.Empty;
            var rule = new RulesItem
            {
                Id = "local-route", Enabled = true, RuleType = ERuleType.Routing,
                OutboundTag = "B", Network = "tcp", Port = matching.Port.ToString(),
                Ip = coreType == ECoreType.sing_box && !domain ? ["!127.0.0.2/32"] : ["127.0.0.1/32"],
            };
            var rules = new List<RulesItem> { rule };
            if (domain)
            {
                rules.Add(new RulesItem
                {
                    Id = "dns-route", Enabled = true, RuleType = ERuleType.DNS,
                    OutboundTag = Global.DirectTag, Domain = ["target.test"],
                });
            }
            var context = CoreConfigTestFactory.CreateContext(config, main, coreType) with
            {
                RoutingItem = new RoutingItem
                {
                    Id = "local-routing", DomainStrategy = ipIfNonMatch ? "IPIfNonMatch" : Global.AsIs,
                    DomainStrategy4Singbox = ipIfNonMatch ? "UseIP" : string.Empty,
                    RuleSet = JsonUtils.Serialize(rules),
                },
            };
            context.AllProxiesMap["remark:B"] = selected;
            context.AllProxiesMap[selected.IndexId] = selected;
            var result = coreType == ECoreType.sing_box
                ? new CoreConfigSingboxService(context).GenerateClientConfigContent()
                : new CoreConfigV2rayService(context).GenerateClientConfigContent();
            Assert.True(result.Success, result.Msg);
            Assert.NotNull(result.Data);
            // No patching of generated runtime JSON: the production pipeline is under test.
            var json = result.Data.ToString()!;
            inboundReservation.Stop();
            apiReservation.Stop();
            // OS cannot transfer the reserved socket to a core. A bind race fails startup rather
            // than silently changing ports or accepting a TCP-only readiness check.
            await using var core = await CoreProcessFixture.StartAsync(executable, coreType, json, inboundPort);
            try
            {
                await AssertRouteAsync(inboundPort, matching, b, a, domain ? "target.test" : null);
                if (domain) Assert.Contains("target.test", dns.Queries);
                await AssertRouteAsync(inboundPort, otherPort, a, b);
                await AssertRouteAsync(inboundPort, excluded, a, b);
                a.ThrowIfFaulted();
                b.ThrowIfFaulted();
                core.AssertRunning();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Routing traffic failed. Core output:\n{core.Output}", ex);
            }
        }
        finally
        {
            for (var i = 0; i < fields.Length; i++)
                fields[i].SetValue(AppManager.Instance, previous[i]);
        }
    }

    internal static async Task AssertRouteAsync(int inboundPort, LoopbackTargetServer target,
        LoopbackProxyFixture expected, LoopbackProxyFixture unexpected, string? domain = null)
    {
        var id = Guid.NewGuid().ToString("N");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var client = await SocksWire.ConnectAsync(inboundPort, target.Endpoint, timeout.Token, domain);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            $"GET /{id} HTTP/1.1\r\nHost: {target.Endpoint.Address}\r\nConnection: close\r\n\r\n"), timeout.Token);
        using var response = new MemoryStream();
        await stream.CopyToAsync(response, timeout.Token);
        Assert.Contains(id, Encoding.ASCII.GetString(response.ToArray()));
        Assert.Contains(id, target.RequestIds);
        Assert.Contains(id, expected.RequestIds);
        // Bounded negative observation AFTER the complete response, not before forwarding.
        await Task.Delay(TimeSpan.FromMilliseconds(200), timeout.Token);
        Assert.DoesNotContain(id, unexpected.RequestIds);
        target.ThrowIfFaulted();
        expected.ThrowIfFaulted();
        unexpected.ThrowIfFaulted();
    }
}
