using ServiceLib.IntegrationTests.Infrastructure;
using ServiceLib.Tests.CoreConfig;
using Xunit;

namespace ServiceLib.IntegrationTests;

public sealed class SharedRoutingPipelineTests
{
    [Theory]
    [InlineData(ECoreType.Xray, "proxy", true)]
    [InlineData(ECoreType.Xray, "active", true)]
    [InlineData(ECoreType.Xray, "B", false)]
    [InlineData(ECoreType.Xray, "B", true)]
    [InlineData(ECoreType.sing_box, "proxy", true)]
    [InlineData(ECoreType.sing_box, "active", true)]
    [InlineData(ECoreType.sing_box, "B", false)]
    [InlineData(ECoreType.sing_box, "B", true)]
    public async Task Shared_native_rules_and_local_targets_preserve_interleave(ECoreType type, string target, bool localFirst)
    {
        var executable = CoreProcessFixture.RequireExecutable(type);
        await using var destination = new LoopbackTargetServer(IPAddress.Loopback);
        await using var a = new LoopbackProxyFixture([destination.Endpoint]);
        await using var b = new LoopbackProxyFixture([destination.Endpoint]);
        using var front = Reserve();
        using var own = Reserve();
        var field = typeof(AppManager).GetField("_config", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var previous = field.GetValue(AppManager.Instance);
        try
        {
            var config = CoreConfigTestFactory.CreateConfig(type);
            config.UiItem.EnableCustomRuleEditing = true;
            CoreConfigTestFactory.BindAppManagerConfig(config);
            var node = new ProfileItem { IndexId = "active", Remarks = "active", ConfigType = EConfigType.Custom,
                CoreType = type, PreSocksPort = Port(front), CustomRuleState = localFirst
                    ? """[{"LocalId":"local","Enabled":true},{"Index":0,"Enabled":true}]"""
                    : """[{"Index":0,"Enabled":true},{"LocalId":"local","Enabled":true}]""" };
            var ctx = CoreConfigTestFactory.CreateContext(config, node, type) with
            {
                SharedRoutingPort = Port(own),
                RoutingItem = new RoutingItem { DomainStrategy = Global.AsIs, DomainStrategy4Singbox = "",
                    RuleSet = JsonUtils.Serialize(new[] { new RulesItem { Id = "local", Enabled = true,
                        OutboundTag = target, Ip = ["127.0.0.1/32"] } }) }
            };
            var bNode = CoreConfigTestFactory.CreateSocksNode(type, "b", "B");
            bNode.Port = b.Port;
            bNode.Username = bNode.Password = "";
            ctx.AllProxiesMap["remark:B"] = bNode;
            ctx.AllProxiesMap[bNode.IndexId] = bNode;
            var source = Native(type, a.Port, b.Port);
            var result = CustomConfigComposer.Compose(source, type, ctx);
            Assert.Null(result.Error);
            Assert.NotNull(result.Json);
            var frontPort = Port(front);
            front.Stop(); own.Stop();
            await using var process = await CoreProcessFixture.StartAsync(executable, type, result.Json, frontPort);
            var expectB = target == "B" && localFirst;
            await RoutingPipelineTests.AssertRouteAsync(frontPort, destination, expectB ? b : a, expectB ? a : b);
            process.AssertRunning();
        }
        finally { field.SetValue(AppManager.Instance, previous); }
    }

    [Theory]
    [InlineData(ECoreType.Xray, ECoreType.sing_box)]
    [InlineData(ECoreType.sing_box, ECoreType.Xray)]
    public async Task Multiple_json_targets_one_failure_does_not_fallback_or_stop_other(ECoreType frontType, ECoreType childType)
    {
        var frontExe = CoreProcessFixture.RequireExecutable(frontType);
        var childExe = CoreProcessFixture.RequireExecutable(childType);
        await using var targetB = new LoopbackTargetServer(IPAddress.Loopback);
        await using var targetC = new LoopbackTargetServer(IPAddress.Loopback);
        await using var a = new LoopbackProxyFixture([targetB.Endpoint, targetC.Endpoint]);
        await using var b = new LoopbackProxyFixture([targetB.Endpoint, targetC.Endpoint]);
        await using var c = new LoopbackProxyFixture([targetB.Endpoint, targetC.Endpoint]);
        using var front = Reserve(); using var own = Reserve(); using var childB = Reserve(); using var childC = Reserve();
        var field = typeof(AppManager).GetField("_config", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var previous = field.GetValue(AppManager.Instance);
        CoreProcessFixture? processB = null;
        try
        {
            var config = CoreConfigTestFactory.CreateConfig(frontType);
            config.UiItem.EnableCustomRuleEditing = true;
            CoreConfigTestFactory.BindAppManagerConfig(config);
            var node = new ProfileItem { IndexId = "active", Remarks = "active", ConfigType = EConfigType.Custom,
                CoreType = frontType, PreSocksPort = Port(front),
                CustomRuleState = """[{"LocalId":"B","Enabled":true},{"LocalId":"C","Enabled":true},{"Index":0,"Enabled":true}]""" };
            var ctx = CoreConfigTestFactory.CreateContext(config, node, frontType) with
            {
                SharedRoutingPort = Port(own),
                RoutingItem = new RoutingItem { DomainStrategy = Global.AsIs, DomainStrategy4Singbox = "",
                    RuleSet = JsonUtils.Serialize(new[] {
                        new RulesItem { Id = "B", Enabled = true, OutboundTag = "B", Port = targetB.Port.ToString() },
                        new RulesItem { Id = "C", Enabled = true, OutboundTag = "C", Port = targetC.Port.ToString() } }) }
            };
            foreach (var (name, port) in new[] { ("B", Port(childB)), ("C", Port(childC)) })
            {
                var socks = CoreConfigTestFactory.CreateSocksNode(frontType, name, name);
                socks.Port = port; socks.Username = socks.Password = "";
                ctx.AllProxiesMap["remark:" + name] = socks;
                ctx.AllProxiesMap[socks.IndexId] = socks;
            }
            var composed = CustomConfigComposer.Compose(Native(frontType, a.Port, a.Port), frontType, ctx);
            Assert.Null(composed.Error);
            var jsonB = ChainConfigBuilder.Build(Native(childType, b.Port, a.Port), childType, Port(childB));
            var jsonC = ChainConfigBuilder.Build(Native(childType, c.Port, a.Port), childType, Port(childC));
            var frontPort = Port(front); var bPort = Port(childB); var cPort = Port(childC);
            front.Stop(); own.Stop(); childB.Stop(); childC.Stop();
            processB = await CoreProcessFixture.StartAsync(childExe, childType, jsonB!, bPort);
            await using var processC = await CoreProcessFixture.StartAsync(childExe, childType, jsonC!, cPort);
            await using var process = await CoreProcessFixture.StartAsync(frontExe, frontType, composed.Json!, frontPort);
            await RoutingPipelineTests.AssertRouteAsync(frontPort, targetB, b, a);
            await RoutingPipelineTests.AssertRouteAsync(frontPort, targetC, c, a);
            await processB.DisposeAsync(); processB = null;
            var beforeA = a.RequestIds.Count;
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {
                var error = await Record.ExceptionAsync(async () =>
                {
                    using var connection = await SocksWire.ConnectAsync(frontPort, targetB.Endpoint, timeout.Token);
                    await connection.GetStream().WriteAsync(Encoding.ASCII.GetBytes($"GET /{Guid.NewGuid():N} HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n"), timeout.Token);
                    await SocksWire.ReadHeadersAsync(connection.GetStream(), timeout.Token);
                });
                Assert.NotNull(error);
                Assert.True(error is IOException or SocketException or OperationCanceledException, error.ToString());
            }
            await Task.Delay(200, TestContext.Current.CancellationToken);
            Assert.Equal(beforeA, a.RequestIds.Count);
            await RoutingPipelineTests.AssertRouteAsync(frontPort, targetC, c, a);
            process.AssertRunning(); processC.AssertRunning();
        }
        finally
        {
            if (processB != null) await processB.DisposeAsync();
            field.SetValue(AppManager.Instance, previous);
        }
    }

    [Fact]
    public async Task Selector_API_change_is_shared_by_native_and_proxy_paths()
    {
        var type = ECoreType.sing_box;
        var executable = CoreProcessFixture.RequireExecutable(type);
        await using var nativeTarget = new LoopbackTargetServer(IPAddress.Loopback);
        await using var proxyTarget = new LoopbackTargetServer(IPAddress.Loopback);
        await using var a = new LoopbackProxyFixture([nativeTarget.Endpoint, proxyTarget.Endpoint]);
        await using var b = new LoopbackProxyFixture([nativeTarget.Endpoint, proxyTarget.Endpoint]);
        using var front = Reserve(); using var own = Reserve(); using var api = Reserve();
        var field = typeof(AppManager).GetField("_config", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var previous = field.GetValue(AppManager.Instance);
        try
        {
            var config = CoreConfigTestFactory.CreateConfig(type);
            config.UiItem.EnableCustomRuleEditing = true;
            CoreConfigTestFactory.BindAppManagerConfig(config);
            var node = new ProfileItem { IndexId = "active", Remarks = "active", CoreType = type,
                ConfigType = EConfigType.Custom, PreSocksPort = Port(front),
                CustomRuleState = """[{"LocalId":"proxy","Enabled":true},{"Index":0,"Enabled":true}]""" };
            var ctx = CoreConfigTestFactory.CreateContext(config, node, type) with
            {
                SharedRoutingPort = Port(own),
                RoutingItem = new RoutingItem { DomainStrategy = Global.AsIs, DomainStrategy4Singbox = "",
                    RuleSet = JsonUtils.Serialize(new[] { new RulesItem { Id = "proxy", Enabled = true,
                        OutboundTag = "proxy", Port = proxyTarget.Port.ToString() } }) }
            };
            var raw = JsonUtils.ParseJson(Native(type, a.Port, b.Port))!;
            raw["experimental"] = System.Text.Json.Nodes.JsonNode.Parse(
                $$$"""{"clash_api":{"external_controller":"127.0.0.1:{{{Port(api)}}}"}}""");
            var composed = CustomConfigComposer.Compose(raw.ToJsonString(), type, ctx);
            Assert.Null(composed.Error);
            var frontPort = Port(front); var apiPort = Port(api);
            front.Stop(); own.Stop(); api.Stop();
            await using var process = await CoreProcessFixture.StartAsync(executable, type, composed.Json!, frontPort);
            await RoutingPipelineTests.AssertRouteAsync(frontPort, nativeTarget, a, b);
            await RoutingPipelineTests.AssertRouteAsync(frontPort, proxyTarget, a, b);
            using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
            using var update = await http.PutAsync($"http://127.0.0.1:{apiPort}/proxies/pick",
                new StringContent("""{"name":"b"}""", Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);
            update.EnsureSuccessStatusCode();
            await RoutingPipelineTests.AssertRouteAsync(frontPort, nativeTarget, b, a);
            await RoutingPipelineTests.AssertRouteAsync(frontPort, proxyTarget, b, a);
            process.AssertRunning();
        }
        finally { field.SetValue(AppManager.Instance, previous); }
    }

    /// <summary>
    /// `protocol` вычисляется сниффингом полезной нагрузки, поэтому обязан совпадать и
    /// после loopback-SOCKS. Локальное правило уводит трафик на own-вход по IP, так что
    /// сработать может только собственное protocol-правило JSON уже за хопом: если
    /// управляемый вход не сниффит, трафик уйдёт в дефолт на a вместо b.
    /// </summary>
    [Theory]
    [InlineData(ECoreType.Xray)]
    [InlineData(ECoreType.sing_box)]
    public async Task Own_side_protocol_rule_matches_behind_the_socks_hop(ECoreType type)
    {
        var executable = CoreProcessFixture.RequireExecutable(type);
        await using var destination = new LoopbackTargetServer(IPAddress.Loopback);
        await using var a = new LoopbackProxyFixture([destination.Endpoint]);
        await using var b = new LoopbackProxyFixture([destination.Endpoint]);
        using var front = Reserve();
        using var own = Reserve();
        var field = typeof(AppManager).GetField("_config", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var previous = field.GetValue(AppManager.Instance);
        try
        {
            var config = CoreConfigTestFactory.CreateConfig(type);
            config.UiItem.EnableCustomRuleEditing = true;
            CoreConfigTestFactory.BindAppManagerConfig(config);
            var node = new ProfileItem { IndexId = "active", Remarks = "active", ConfigType = EConfigType.Custom,
                CoreType = type, PreSocksPort = Port(front),
                CustomRuleState = type == ECoreType.Xray
                    ? """[{"LocalId":"local","Enabled":true},{"Index":0,"Enabled":true}]"""
                    : """[{"LocalId":"local","Enabled":true},{"Index":0,"Enabled":true},{"Index":1,"Enabled":true}]""" };
            var ctx = CoreConfigTestFactory.CreateContext(config, node, type) with
            {
                SharedRoutingPort = Port(own),
                RoutingItem = new RoutingItem { DomainStrategy = Global.AsIs, DomainStrategy4Singbox = "",
                    RuleSet = JsonUtils.Serialize(new[] { new RulesItem { Id = "local", Enabled = true,
                        OutboundTag = "proxy", Ip = ["127.0.0.1/32"] } }) }
            };
            // Default/final is a; only a matching protocol predicate can reach b.
            var source = type == ECoreType.Xray
                ? $$$"""{"outbounds":[{"tag":"a","protocol":"socks","settings":{"servers":[{"address":"127.0.0.1","port":{{{a.Port}}}}]}},{"tag":"b","protocol":"socks","settings":{"servers":[{"address":"127.0.0.1","port":{{{b.Port}}}}]}}],"routing":{"rules":[{"type":"field","protocol":["http"],"outboundTag":"b"}]}}"""
                : $$$"""{"outbounds":[{"tag":"a","type":"socks","server":"127.0.0.1","server_port":{{{a.Port}}}},{"tag":"b","type":"socks","server":"127.0.0.1","server_port":{{{b.Port}}}}],"route":{"final":"a","rules":[{"action":"sniff"},{"protocol":["http"],"action":"route","outbound":"b"}]}}""";

            var result = CustomConfigComposer.Compose(source, type, ctx);
            Assert.Null(result.Error);
            Assert.NotNull(result.Json);
            var frontPort = Port(front);
            front.Stop(); own.Stop();
            await using var process = await CoreProcessFixture.StartAsync(executable, type, result.Json, frontPort);
            await RoutingPipelineTests.AssertRouteAsync(frontPort, destination, b, a);
            process.AssertRunning();
        }
        finally { field.SetValue(AppManager.Instance, previous); }
    }

    private static TcpListener Reserve() { var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); return listener; }
    private static int Port(TcpListener listener) => ((IPEndPoint)listener.LocalEndpoint).Port;
    private static string Native(ECoreType type, int a, int b)
    {
        var json = type == ECoreType.Xray
        ? $$$"""{"outbounds":[{"tag":"a","protocol":"socks","settings":{"servers":[{"address":"127.0.0.1","port":{{{a}}}}]}},{"tag":"b","protocol":"socks","settings":{"servers":[{"address":"127.0.0.1","port":{{{b}}}}]}}],"routing":{"rules":[{"type":"field","ip":["127.0.0.1/32"],"outboundTag":"a"}]}}"""
        : $$$"""{"outbounds":[{"tag":"a","type":"socks","server":"127.0.0.1","server_port":{{{a}}}},{"tag":"b","type":"socks","server":"127.0.0.1","server_port":{{{b}}}},{"tag":"pick","type":"selector","outbounds":["a","b"],"default":"a"}],"route":{"final":"b","rules":[{"ip_cidr":["127.0.0.1/32"],"action":"route","outbound":"pick"}]}}""";
        // The native rule deliberately chooses a different outbound from the default/first.
        var root = JsonUtils.ParseJson(json)!;
        var outbounds = root["outbounds"]!.AsArray();
        var first = outbounds[0]!;
        outbounds.RemoveAt(0);
        outbounds.Insert(1, first);
        return root.ToJsonString();
    }
}
