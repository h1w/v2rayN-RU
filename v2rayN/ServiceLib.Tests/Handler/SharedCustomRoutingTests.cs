using AwesomeAssertions;
using ServiceLib.Handler.Builder;
using ServiceLib.Tests.CoreConfig;
using Xunit;

namespace ServiceLib.Tests.Handler;

public class SharedCustomRoutingTests
{
    private static CoreConfigContext Context(ECoreType core)
    {
        var config = CoreConfigTestFactory.CreateConfig(core);
        config.UiItem.EnableCustomRuleEditing = true;
        CoreConfigTestFactory.BindAppManagerConfig(config);
        var node = new ProfileItem
        {
            IndexId = "active", Remarks = "active-json", ConfigType = EConfigType.Custom,
            CoreType = core, PreSocksPort = 31010,
            CustomRuleState = """[{"LocalId":"local","Enabled":true},{"Index":0,"Enabled":true}]""",
        };
        return CoreConfigTestFactory.CreateContext(config, node, core) with
        {
            RoutingItem = new RoutingItem
            {
                Id = "routing", DomainStrategy = Global.AsIs, DomainStrategy4Singbox = string.Empty,
                RuleSet = """[{"Id":"local","Enabled":true,"OutboundTag":"proxy","Domain":["full:target.test"]}]""",
            },
        };
    }

    [Theory]
    [InlineData(ECoreType.Xray, "inboundTag", "[\"old-in\"]")]
    [InlineData(ECoreType.Xray, "sourceIP", "[\"192.0.2.1\"]")]
    [InlineData(ECoreType.Xray, "process", "[\"app.exe\"]")]
    [InlineData(ECoreType.sing_box, "inbound", "[\"old-in\"]")]
    [InlineData(ECoreType.sing_box, "process_name", "[\"app.exe\"]")]
    [InlineData(ECoreType.sing_box, "action", "\"route-options\"")]
    public async Task Shared_router_rejects_unpreservable_context(ECoreType core, string key, string value)
    {
        var ctx = Context(core);
        await CoreConfigContextBuilder.ResolveRuleTargetsAsync(ctx, NodeValidatorResult.Empty(), true, () => 31011);
        var root = JsonUtils.ParseJson(Source(core))!;
        root[core == ECoreType.Xray ? "routing" : "route"]!["rules"]![0]![key] = System.Text.Json.Nodes.JsonNode.Parse(value);
        var result = CustomConfigComposer.Compose(root.ToJsonString(), core, ctx);
        result.Json.Should().BeNull();
        result.Error.Should().NotBeNullOrEmpty();
    }

    /// <summary>
    /// `protocol` приходит из сниффинга полезной нагрузки, а нагрузка через loopback-SOCKS
    /// проходит без изменений — значит предикат сохраняется. Это самое частое правило
    /// в JSON подписок ({"protocol":["bittorrent"],"outboundTag":"direct"}), и отказ
    /// на нём делал такие профили неработоспособными целиком.
    /// </summary>
    [Fact]
    public async Task Shared_xray_preserves_protocol_predicate_by_sniffing_managed_inbounds()
    {
        var ctx = Context(ECoreType.Xray);
        ctx.AppConfig.Inbound[0].SniffingEnabled = true;
        ctx.AppConfig.Inbound[0].DestOverride = ["http", "tls"];
        await CoreConfigContextBuilder.ResolveRuleTargetsAsync(ctx, NodeValidatorResult.Empty(), true, () => 31011);
        var root = JsonUtils.ParseJson(Source(ECoreType.Xray))!;
        root["routing"]!["rules"]![0]!["protocol"] = System.Text.Json.Nodes.JsonNode.Parse("""["bittorrent"]""");

        var result = CustomConfigComposer.Compose(root.ToJsonString(), ECoreType.Xray, ctx);

        result.Error.Should().BeNull();
        var composed = JsonUtils.ParseJson(result.Json)!;
        var inbounds = composed["inbounds"]!.AsArray();
        inbounds.Should().HaveCount(2);
        foreach (var inbound in inbounds)
        {
            inbound!["sniffing"]!["enabled"]!.GetValue<bool>().Should().BeTrue();
            inbound["sniffing"]!["destOverride"]!.AsArray().Should().NotBeEmpty();
        }
        // Own side must not rewrite the address its own outbounds receive.
        inbounds[1]!["sniffing"]!["routeOnly"]!.GetValue<bool>().Should().BeTrue();
        composed["routing"]!["rules"]!.AsArray()
            .Count(r => r?["protocol"] is not null).Should().Be(2, "front and own copies both keep the predicate");
    }

    /// <summary>
    /// У sing-box сниффинг задаётся правилом, а не входом: без него `protocol` молча
    /// перестал бы совпадать, поэтому отказ сохраняется именно в этом случае.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Shared_singbox_protocol_requires_its_own_sniff_action(bool declaresSniff)
    {
        var ctx = Context(ECoreType.sing_box);
        await CoreConfigContextBuilder.ResolveRuleTargetsAsync(ctx, NodeValidatorResult.Empty(), true, () => 31011);
        var root = JsonUtils.ParseJson(Source(ECoreType.sing_box))!;
        var rules = root["route"]!["rules"]!.AsArray();
        rules[0]!["protocol"] = System.Text.Json.Nodes.JsonNode.Parse("""["bittorrent"]""");
        if (declaresSniff)
        {
            rules.Insert(0, System.Text.Json.Nodes.JsonNode.Parse("""{"action":"sniff"}""")!);
        }

        var result = CustomConfigComposer.Compose(root.ToJsonString(), ECoreType.sing_box, ctx);

        if (declaresSniff)
        {
            result.Error.Should().BeNull();
            result.Json.Should().NotBeNull();
        }
        else
        {
            result.Json.Should().BeNull();
            result.Error.Should().Contain("sniff");
        }
    }

    /// <summary>
    /// Общий роутинг сам заменяет входы JSON, поэтому предикат, целиком указывающий
    /// на них, означал «трафик клиентских входов» — то есть ровно то, что теперь
    /// приходит на управляемый вход. Такое правило должно сохраниться, а не отклонить
    /// профиль. Ссылка на незнакомый тег по-прежнему отклоняется.
    /// </summary>
    [Theory]
    [InlineData(ECoreType.Xray, true)]
    [InlineData(ECoreType.Xray, false)]
    [InlineData(ECoreType.sing_box, true)]
    [InlineData(ECoreType.sing_box, false)]
    public async Task Shared_router_keeps_rules_naming_only_the_inbounds_it_replaces(ECoreType core, bool onlyReplaced)
    {
        var ctx = Context(core);
        await CoreConfigContextBuilder.ResolveRuleTargetsAsync(ctx, NodeValidatorResult.Empty(), true, () => 31011);
        var root = JsonUtils.ParseJson(Source(core))!;
        var key = core == ECoreType.Xray ? "inboundTag" : "inbound";
        var tags = onlyReplaced ? """["discard"]""" : """["discard","somewhere-else"]""";
        root[core == ECoreType.Xray ? "routing" : "route"]!["rules"]![0]![key] =
            System.Text.Json.Nodes.JsonNode.Parse(tags);

        var result = CustomConfigComposer.Compose(root.ToJsonString(), core, ctx);

        if (onlyReplaced)
        {
            result.Error.Should().BeNull();
            result.Json.Should().NotBeNull();
        }
        else
        {
            result.Json.Should().BeNull();
            result.Error.Should().NotBeNullOrEmpty();
        }
    }

    [Theory]
    [InlineData(ECoreType.Xray)]
    [InlineData(ECoreType.sing_box)]
    public async Task Shared_router_self_reference_is_same_loopback_as_proxy(ECoreType core)
    {
        var ctx = Context(core);
        ctx.RoutingItem!.RuleSet = ctx.RoutingItem.RuleSet.Replace("\"proxy\"", "\"active-json\"");
        // Avoid a database lookup: composition must normalize a resolved self target as well.
        ctx.SharedRoutingPort = 31011;
        ctx.AllProxiesMap["remark:active-json"] = ctx.Node;
        var result = CustomConfigComposer.Compose(Source(core), core, ctx);
        result.Error.Should().BeNull();
        result.UnsupportedCustomTargets.Should().BeEmpty();
        var root = JsonUtils.ParseJson(result.Json)!;
        var first = root[core == ECoreType.Xray ? "routing" : "route"]!["rules"]![0]!;
        first[core == ECoreType.Xray ? "outboundTag" : "outbound"]!.GetValue<string>().Should().Be("v2rayn-own-out");
        ctx.ChainCores.Should().BeEmpty();
        await Task.CompletedTask;
    }

    [Fact]
    public void Nonduplicated_child_preserves_api_cache_and_endpoints()
    {
        var root = JsonUtils.ParseJson(Source(ECoreType.sing_box))!;
        root["endpoints"] = System.Text.Json.Nodes.JsonNode.Parse("""[{"type":"wireguard","tag":"wg","system":true,"name":"wg-test"}]""");
        var result = ChainConfigBuilder.Build(root.ToJsonString(), ECoreType.sing_box, 31012);
        result.Should().NotBeNull("a single owned core does not duplicate its resources");
        var child = JsonUtils.ParseJson(result)!;
        child["experimental"]!.ToJsonString().Should().Be(root["experimental"]!.ToJsonString());
        child["endpoints"]!.ToJsonString().Should().Be(root["endpoints"]!.ToJsonString());
    }

    [Fact]
    public async Task Shared_pre_core_is_ingress_only_not_a_second_local_router()
    {
        ServiceLib.Helper.SQLiteHelper.Instance.CreateTable<FullConfigTemplateItem>();
        ServiceLib.Helper.SQLiteHelper.Instance.CreateTable<DNSItem>();
        ServiceLib.Helper.SQLiteHelper.Instance.CreateTable<RoutingItem>();
        var ctx = Context(ECoreType.Xray);
        ctx.SharedRoutingPort = 31011;
        ctx.ChainCores.Add(new ChainCoreDescriptor { CoreType = ECoreType.sing_box, Node = new ProfileItem(), Port = 31012, ConfigFileName = "child.json" });
        ctx.RoutingItem!.IsActive = true;
        await ServiceLib.Helper.SQLiteHelper.Instance.ReplaceAsync(ctx.RoutingItem);
        var method = typeof(CoreConfigContextBuilder).GetMethod("BuildPreSocksIfNeeded",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var task = (Task<CoreConfigContextBuilderResult?>)method.Invoke(null, [ctx])!;
        var pre = await task;
        pre.Should().NotBeNull();
        pre!.Context.RoutingItem.Should().BeNull("all local rules belong at the native interleave front");
        pre.Context.ChainCores.Should().BeEmpty();
        pre.Context.ProtectCoreTypeList.Should().Contain(ECoreType.Xray).And.Contain(ECoreType.sing_box);
    }

    [Fact]
    public void Nonduplicated_child_rejects_actual_managed_listener_conflict()
    {
        var raw = Source(ECoreType.sing_box);
        ChainConfigBuilder.Build(raw, ECoreType.sing_box, 31012,
            ownedConfigs: ["""{"inbounds":[{"listen_port":31012}]}"""]).Should().BeNull();
        ChainConfigBuilder.Build(raw, ECoreType.sing_box, 31012,
            ownedConfigs: ["""{"inbounds":[{"listen_port":31013}]}"""]).Should().NotBeNull();
    }

    [Theory]
    [InlineData("clash_api", "external_controller")]
    [InlineData("v2ray_api", "listen")]
    public void Child_rejects_api_collision_with_its_own_managed_inbound(string api, string key)
    {
        var root = JsonUtils.ParseJson(Source(ECoreType.sing_box))!;
        root["experimental"]![api] = System.Text.Json.Nodes.JsonNode.Parse(
            $"{{\"{key}\":\"127.0.0.1:31012\"}}");
        ChainConfigBuilder.Build(root.ToJsonString(), ECoreType.sing_box, 31012).Should().BeNull();
    }

    [Theory]
    [InlineData(ECoreType.Xray)]
    [InlineData(ECoreType.sing_box)]
    public async Task Shared_router_rejects_api_collision_with_managed_inbound(ECoreType core)
    {
        var ctx = Context(core);
        await CoreConfigContextBuilder.ResolveRuleTargetsAsync(ctx, NodeValidatorResult.Empty(), true, () => 31011);
        var root = JsonUtils.ParseJson(Source(core))!;
        if (core == ECoreType.Xray)
            root["api"] = System.Text.Json.Nodes.JsonNode.Parse("""{"listen":"127.0.0.1:31011"}""");
        else
            root["experimental"]!["clash_api"]!["external_controller"] = "127.0.0.1:31011";
        var result = CustomConfigComposer.Compose(root.ToJsonString(), core, ctx);
        result.Json.Should().BeNull();
        result.Error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Child_allows_distinct_api_cache_and_interface_resources()
    {
        var raw = Source(ECoreType.sing_box);
        var other = JsonUtils.ParseJson(raw)!;
        other["experimental"]!["clash_api"]!["external_controller"] = "127.0.0.1:32000";
        other["experimental"]!["cache_file"]!["path"] = "other-state.db";
        ChainConfigBuilder.Build(raw, ECoreType.sing_box, 31012, ownedConfigs: [other.ToJsonString()])
            .Should().NotBeNull();
    }

    [Fact]
    public void Child_resolves_relative_cache_paths_against_core_working_directory()
    {
        var raw = Source(ECoreType.sing_box);
        var other = JsonUtils.ParseJson(raw)!;
        other["experimental"]!["clash_api"]!["external_controller"] = "127.0.0.1:32000";
        other["experimental"]!["cache_file"]!["path"] = Path.Combine(Utils.GetBinConfigPath(), "state.db");
        ChainConfigBuilder.Build(raw, ECoreType.sing_box, 31012, ownedConfigs: [other.ToJsonString()])
            .Should().BeNull();
    }

    [Fact]
    public async Task Shared_loop_tag_does_not_collide_with_native_endpoint()
    {
        var ctx = Context(ECoreType.sing_box);
        await CoreConfigContextBuilder.ResolveRuleTargetsAsync(ctx, NodeValidatorResult.Empty(), true, () => 31011);
        var root = JsonUtils.ParseJson(Source(ECoreType.sing_box))!;
        root["endpoints"] = System.Text.Json.Nodes.JsonNode.Parse("""[{"type":"wireguard","tag":"v2rayn-own-out"}]""");
        var result = CustomConfigComposer.Compose(root.ToJsonString(), ECoreType.sing_box, ctx);
        var composed = JsonUtils.ParseJson(result.Json)!;
        composed["outbounds"]!.AsArray().Select(o => o!["tag"]!.GetValue<string>())
            .Should().NotContain("v2rayn-own-out");
        composed["route"]!["rules"]![0]!["outbound"]!.GetValue<string>().Should().Be("v2rayn-own-out-2");
    }

    [Theory]
    [InlineData(ECoreType.Xray, true)]
    [InlineData(ECoreType.sing_box, true)]
    [InlineData(ECoreType.Xray, false)]
    [InlineData(ECoreType.sing_box, false)]
    public async Task Shared_router_preserves_native_priority_and_default(ECoreType core, bool editing)
    {
        var ctx = Context(core);
        ctx.AppConfig.UiItem.EnableCustomRuleEditing = editing;
        ctx.Node.CustomRuleState = """[{"Index":0,"Enabled":true},{"LocalId":"local","Enabled":true}]""";
        await CoreConfigContextBuilder.ResolveRuleTargetsAsync(ctx, NodeValidatorResult.Empty(), true, () => 31011);
        var result = CustomConfigComposer.Compose(Source(core), core, ctx);
        result.Error.Should().BeNull();
        var root = JsonUtils.ParseJson(result.Json)!;
        var section = core == ECoreType.Xray ? "routing" : "route";
        var action = core == ECoreType.Xray ? "balancerTag" : "outbound";
        root[section]!["rules"]![0]![action]!.GetValue<string>().Should().Be("pick");
        root[section]!["rules"]!.AsArray().Should().HaveCount(3);
        root["outbounds"]![0]!["tag"]!.GetValue<string>().Should().Be("a");
        if (core == ECoreType.sing_box)
            root["route"]!["final"]!.GetValue<string>().Should().Be("pick");
    }

    [Fact]
    public async Task Shared_xray_rejects_balancer_that_would_select_own_loopback()
    {
        var ctx = Context(ECoreType.Xray);
        await CoreConfigContextBuilder.ResolveRuleTargetsAsync(ctx, NodeValidatorResult.Empty(), true, () => 31011);
        var root = JsonUtils.ParseJson(Source(ECoreType.Xray))!;
        root["routing"]!["balancers"]![0]!["selector"] = System.Text.Json.Nodes.JsonNode.Parse("""["v2rayn-own"]""");
        var result = CustomConfigComposer.Compose(root.ToJsonString(), ECoreType.Xray, ctx);
        result.Json.Should().BeNull("balancer prefix must not select the SOCKS outbound back into its own rules");
        result.Error.Should().Contain("balancer");
    }

    [Fact]
    public async Task Future_pre_core_listener_is_reserved_against_child_api()
    {
        var ctx = Context(ECoreType.Xray);
        ctx.SharedRoutingPort = 31011;
        ServiceLib.Helper.SQLiteHelper.Instance.CreateTable<FullConfigTemplateItem>();
        ServiceLib.Helper.SQLiteHelper.Instance.CreateTable<DNSItem>();
        ServiceLib.Helper.SQLiteHelper.Instance.CreateTable<RoutingItem>();
        var method = typeof(CoreConfigContextBuilder).GetMethod("BuildPreSocksIfNeeded",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var pre = await (Task<CoreConfigContextBuilderResult?>)method.Invoke(null, [ctx])!;
        pre.Should().NotBeNull();
        var result = pre!.Context.RunCoreType == ECoreType.sing_box
            ? new CoreConfigSingboxService(pre.Context).GenerateClientConfigContent()
            : new CoreConfigV2rayService(pre.Context).GenerateClientConfigContent();
        result.Success.Should().BeTrue();
        var raw = JsonUtils.ParseJson(Source(ECoreType.sing_box))!;
        raw["experimental"]!["clash_api"]!["external_controller"] = "127.0.0.1:" + ctx.AppConfig.Inbound[0].LocalPort;
        ChainConfigBuilder.Build(raw.ToJsonString(), ECoreType.sing_box, 31012,
            ownedConfigs: [result.Data!.ToString()!]).Should().BeNull();
    }

    private static string Source(ECoreType core) => core == ECoreType.Xray
        ? """{"inbounds":[{"tag":"discard","port":1234}],"outbounds":[{"tag":"a","protocol":"socks"},{"tag":"b","protocol":"socks"}],"routing":{"rules":[{"type":"field","domain":["full:target.test"],"balancerTag":"pick"}],"balancers":[{"tag":"pick","selector":["a","b"]}]}}"""
        : """{"inbounds":[{"tag":"discard","listen_port":1234}],"outbounds":[{"tag":"a","type":"socks"},{"tag":"b","type":"socks"},{"tag":"pick","type":"selector","outbounds":["a","b"]}],"route":{"final":"pick","rules":[{"domain":["target.test"],"action":"route","outbound":"pick"}]},"experimental":{"clash_api":{"external_controller":"127.0.0.1:31999"},"cache_file":{"enabled":true,"path":"state.db"}}}""";

    [Theory]
    [InlineData(ECoreType.Xray)]
    [InlineData(ECoreType.sing_box)]
    public async Task Shared_router_has_two_inbounds_one_group_table_and_no_child_for_active(ECoreType core)
    {
        var ctx = Context(core);
        await CoreConfigContextBuilder.ResolveRuleTargetsAsync(ctx, NodeValidatorResult.Empty(), true, () => 31011);
        var result = CustomConfigComposer.Compose(Source(core), core, ctx);
        result.Json.Should().NotBeNull();
        var root = JsonUtils.ParseJson(result.Json)!;
        root["inbounds"]!.AsArray().Should().HaveCount(2);
        ctx.ChainCores.Should().BeEmpty("active routing must share the same native process");
        var section = core == ECoreType.Xray ? "routing" : "route";
        var rules = root[section]!["rules"]!.AsArray();
        rules.Should().HaveCount(3, "one local and one native front rule, plus the own-only native clone");
        var actionKey = core == ECoreType.Xray ? "balancerTag" : "outbound";
        rules.Count(r => r?[actionKey]?.GetValue<string>() == "pick").Should().Be(2);
        var outboundKey = core == ECoreType.Xray ? "outboundTag" : "outbound";
        var loopTag = rules[0]![outboundKey]!.GetValue<string>();
        var loop = root["outbounds"]!.AsArray().Single(o => o?["tag"]?.GetValue<string>() == loopTag)!;
        (core == ECoreType.Xray ? loop["settings"]!["servers"]![0]!["port"] : loop["server_port"])!.GetValue<int>().Should().Be(31011);
        var gate = core == ECoreType.Xray ? "inboundTag" : "inbound";
        string Gate(System.Text.Json.Nodes.JsonNode rule) => core == ECoreType.Xray
            ? rule[gate]![0]!.GetValue<string>() : rule["rules"]![0]![gate]![0]!.GetValue<string>();
        Gate(rules[0]!).Should().Be(Gate(rules[1]!));
        Gate(rules[2]!).Should().NotBe(Gate(rules[0]!));
        if (core == ECoreType.sing_box)
        {
            root["outbounds"]!.AsArray().Count(o => o?["tag"]?.GetValue<string>() == "pick").Should().Be(1);
            root["experimental"]!.ToJsonString().Should().Be(JsonUtils.ParseJson(Source(core))!["experimental"]!.ToJsonString());
        }
        else
        {
            root["routing"]!["balancers"]!.AsArray().Should().HaveCount(1);
        }
    }
}
