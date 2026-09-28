using AwesomeAssertions;
using ServiceLib.Tests.CoreConfig;
using Xunit;

namespace ServiceLib.Tests.Handler;

public class SharedProcessRoutingHandoffTests
{
    private static (CoreConfigContext Main, CoreConfigContext Helper, string Json) Compose(string target = "target", bool processFirst = false,
        List<RulesItem>? additionalRules = null)
    {
        var config = CoreConfigTestFactory.CreateConfig();
        config.UiItem.EnableCustomRuleEditing = true;
        CoreConfigTestFactory.BindAppManagerConfig(config);
        var node = new ProfileItem
        {
            IndexId = "main-custom", Remarks = "main-custom", ConfigType = EConfigType.Custom,
            CoreType = ECoreType.Xray, PreSocksPort = 31010,
            CustomRuleState = processFirst
                ? """[{"LocalId":"process","Enabled":true},{"Index":0,"Enabled":true},{"LocalId":"domain","Enabled":true}]"""
                : """[{"Index":0,"Enabled":true},{"LocalId":"process","Enabled":true},{"LocalId":"domain","Enabled":true}]""",
        };
        var main = CoreConfigTestFactory.CreateContext(config, node, ECoreType.Xray) with
        {
            SharedRoutingPort = 31011,
            ManagedIngressPort = 31010,
            RoutingItem = new RoutingItem
            {
                RuleSet = JsonUtils.Serialize(new[]
                {
                    new RulesItem { Id = "process", Enabled = true, OutboundTag = target, Process = ["Discord.exe"], Port = "443", Network = "tcp" },
                    new RulesItem { Id = "domain", Enabled = true, OutboundTag = "target", Domain = ["full:later.test"] },
                }.Concat(additionalRules ?? [])),
            },
        };
        main.AllProxiesMap["remark:target"] = CoreConfigTestFactory.CreateSocksNode(ECoreType.Xray, "resolved-child", "target");
        main.AllProxiesMap["remark:target"].Port = 32100;
        main.RoutingProfileIds["remark:target"] = "original-custom-profile";
        var helper = CoreConfigTestFactory.CreateContext(config,
            CoreConfigTestFactory.CreateSocksNode(ECoreType.sing_box), ECoreType.sing_box) with { RoutingItem = null };
        var composed = CustomConfigComposer.Compose("""
            {"inbounds":[],"outbounds":[
              {"tag":"native","protocol":"freedom"},
              {"tag":"original-custom-profile-proxy-target","protocol":"blackhole"}],
             "routing":{"domainStrategy":"IPIfNonMatch","rules":[
              {"type":"field","domain":["full:earlier.test"],"outboundTag":"native"}]}}
            """, ECoreType.Xray, main);
        composed.Error.Should().BeNull();
        return (main, helper, composed.Json!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SharedCustom_ProcessBranchPreservesNativeAndLocalPriorityAndExactRenamedTarget(bool processFirst)
    {
        var (main, helper, json) = Compose(processFirst: processFirst);
        var result = HelperRoutingHandoff.Prepare(main, helper, json, () => 32200);
        var root = JsonNode.Parse(result.MainJson)!;
        var selectedTag = main.StatisticsOutboundProfiles.Single().Key;
        selectedTag.Should().NotBe("original-custom-profile-proxy-target", "the native JSON already owns that tag");
        main.StatisticsOutboundProfiles[selectedTag].Should().Be("original-custom-profile");
        var generated = new CoreConfigSingboxService(result.HelperContext).GenerateClientConfigContent();
        generated.Success.Should().BeTrue(generated.Msg);
        var helperRoot = JsonNode.Parse(generated.Data!.ToString()!)!;
        var process = helperRoot["route"]!["rules"]!.AsArray().Single(r => r?["process_name"]?.AsArray().Any(p => p?.GetValue<string>() == "Discord.exe") == true)!;
        var forward = helperRoot["outbounds"]!.AsArray().Single(o => o?["tag"]?.GetValue<string>() == process["outbound"]!.GetValue<string>())!;
        forward["server_port"]!.GetValue<int>().Should().Be(32200);
        var ingress = root["inbounds"]!.AsArray().Single(i => i?["port"]?.GetValue<int>() == 32200)!;
        ingress["settings"]!["udp"]!.GetValue<bool>().Should().BeTrue();
        var branch = RulesFor(root, ingress["tag"]!.GetValue<string>());
        Route(branch, "earlier.test", "443").Should().Be(processFirst ? selectedTag : "native");
        Route(branch, "later.test", "443").Should().Be(selectedTag);
        Route(branch, "earlier.test", "80").Should().Be("native", "a process match must retain its port constraint");
        Route(branch, "unmatched.test", "80").Should().BeNull("no synthetic catch-all may suppress IPIfNonMatch");
        Route(branch, "unmatched.test", "443", "udp").Should().BeNull("process selection retains its network constraint");
        root["routing"]!["domainStrategy"]!.GetValue<string>().Should().Be("IPIfNonMatch");
        root["routing"]!["rules"]!.AsArray().Should().NotContain(r => r!["process"] != null);
        Route(RulesFor(root, "v2rayn-front-in"), "unmatched.test", "443").Should().BeNull("unknown processes must not hit a widened process rule");
        Route(RulesFor(root, "v2rayn-own-in"), "earlier.test", "443").Should().Be("native");
        StatisticsXrayService.GetIngressTags(result.MainJson).Should().Contain(ingress["tag"]!.GetValue<string>());
        StatisticsXrayService.GetIngressTags(result.MainJson).Should().NotContain("v2rayn-own-in");
    }

    [Theory]
    [InlineData("direct", "direct")]
    [InlineData("block", "block")]
    [InlineData("proxy", "v2rayn-own-out")]
    public void SharedCustom_ProcessUtilityTargetsRemainInMain(string target, string expected)
    {
        var (main, helper, json) = Compose(target, true);
        var result = HelperRoutingHandoff.Prepare(main, helper, json, () => 32200);
        var root = JsonNode.Parse(result.MainJson)!;
        Route(RulesFor(root, "v2rayn-handoff-32200"), "earlier.test", "443").Should().Be(expected);
        main.StatisticsOutboundProfiles.Values.Should().OnlyContain(id => id == "original-custom-profile");
    }

    [Fact]
    public void SharedCustom_RejectsConflictingHandoffPort()
    {
        var (main, helper, json) = Compose();
        var prepare = () => HelperRoutingHandoff.Prepare(main, helper, json, () => 31011);
        prepare.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void SharedCustom_OverlappingNameAndPathRulesRetainBothMatchesAndTheirConstraints()
    {
        var (main, helper, json) = Compose(additionalRules:
        [
            new RulesItem { Id = "path", Enabled = true, OutboundTag = "block", Process = ["C:/apps/Discord.exe"], Port = "80" },
            new RulesItem { Id = "alias", Enabled = true, OutboundTag = "target", Process = ["Discord.exe", "Other.exe"], Port = "8080" },
        ]);
        var port = 32200;
        var result = HelperRoutingHandoff.Prepare(main, helper, json, () => port++);
        var root = JsonNode.Parse(result.MainJson)!;
        var selected = main.StatisticsOutboundProfiles.Single().Key;
        var path = Utils.IsWindows() ? "C:\\apps\\Discord.exe" : "C:/apps/Discord.exe";
        var overlap = SelectProcessBranch(result.HelperContext, "Discord.exe", path);
        Route(RulesFor(root, overlap!), "unknown.test", "443").Should().Be(selected);
        Route(RulesFor(root, overlap!), "unknown.test", "80").Should().Be("block");
        Route(RulesFor(root, overlap!), "unknown.test", "8080").Should().Be(selected);
        var renamedProcess = SelectProcessBranch(result.HelperContext, "renamed", path);
        Route(RulesFor(root, renamedProcess!), "unknown.test", "443").Should().BeNull();
        Route(RulesFor(root, renamedProcess!), "unknown.test", "80").Should().Be("block");
        var differentPath = SelectProcessBranch(result.HelperContext, "Discord.exe", "unrelated");
        Route(RulesFor(root, differentPath!), "unknown.test", "443").Should().Be(selected);
        Route(RulesFor(root, differentPath!), "unknown.test", "80").Should().BeNull();
        SelectProcessBranch(result.HelperContext, "unknown", "unrelated").Should().BeNull();
    }

    [Fact]
    public void SharedCustom_IpRuleAfterProcessStillMatchesWhenProcessConstraintsDoNot()
    {
        var (main, helper, json) = Compose(additionalRules:
        [new RulesItem { Id = "ip", Enabled = true, OutboundTag = "direct", Ip = ["192.0.2.1/32"] }]);
        var result = HelperRoutingHandoff.Prepare(main, helper, json, () => 32200);
        var root = JsonNode.Parse(result.MainJson)!;
        var selected = main.StatisticsOutboundProfiles.Single().Key;
        var branch = RulesFor(root, "v2rayn-handoff-32200");
        Route(branch, "unknown.test", "443", ip: "192.0.2.1").Should().Be(selected);
        Route(branch, "unknown.test", "80", ip: "192.0.2.1").Should().Be("direct");
        Route(branch, "unknown.test", "80", ip: "192.0.2.2").Should().BeNull();
        Route(RulesFor(root, "v2rayn-front-in"), "unknown.test", "443", ip: "192.0.2.1").Should().Be("direct");
    }

    [Fact]
    public void SharedCustom_ProcessAndDomainBytesUseOriginalProfileAndCountIngressOnlyOnce()
    {
        var (main, helper, json) = Compose();
        var result = HelperRoutingHandoff.Prepare(main, helper, json, () => 32200);
        var tag = main.StatisticsOutboundProfiles.Single().Key;
        var counters = JsonSerializer.Serialize(new
        {
            stats = new
            {
                inbound = new Dictionary<string, object>
                {
                    ["v2rayn-front-in"] = new { uplink = 11, downlink = 13 },
                    ["v2rayn-handoff-32200"] = new { uplink = 17, downlink = 19 },
                    ["v2rayn-own-in"] = new { uplink = 17, downlink = 19 },
                },
                outbound = new Dictionary<string, object> { [tag] = new { uplink = 28, downlink = 32 } },
            },
        });
        var aggregate = new StatisticsCounterTracker(0).Sample(
            StatisticsXrayService.ParseCounters(counters, StatisticsXrayService.GetIngressTags(result.MainJson))!, 1);
        aggregate.UpBytes.Should().Be(28);
        aggregate.DownBytes.Should().Be(32);
        var auxiliary = new StatisticsCounterTracker(0).Sample(
            StatisticsXrayService.ParseCounters(counters, main.StatisticsOutboundProfiles.Keys.ToHashSet(), true)!, 1);
        auxiliary.UpBytes.Should().Be(28);
        main.StatisticsOutboundProfiles[tag].Should().Be("original-custom-profile");
    }

    [Fact]
    public void SharedCustom_LocalProcessInboundRestrictionIsRejectedRatherThanWidened()
    {
        var (main, _, _) = Compose();
        var rules = JsonUtils.Deserialize<List<RulesItem>>(main.RoutingItem!.RuleSet)!;
        rules[0].InboundTag = ["unrelated-inbound"];
        main.RoutingItem.RuleSet = JsonUtils.Serialize(rules);
        var composed = CustomConfigComposer.Compose("""
            {"inbounds":[],"outbounds":[{"tag":"native","protocol":"freedom"}],"routing":{"rules":[]}}
            """, ECoreType.Xray, main);
        composed.Json.Should().BeNull();
        composed.Error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void SharedCustom_EquivalentNamesShareIngressAndReloadDoesNotReusePort()
    {
        var (main, helper, json) = Compose(additionalRules:
        [new RulesItem { Id = "many", Enabled = true, OutboundTag = "block", Process = ["One.exe", "Two.exe", "Three.exe"] }]);
        var firstPort = 32200;
        var first = HelperRoutingHandoff.Prepare(main, helper, json, () => firstPort++);
        var one = SelectProcessBranch(first.HelperContext, "One.exe", "unrelated");
        one.Should().Be(SelectProcessBranch(first.HelperContext, "Two.exe", "unrelated"));
        one.Should().Be(SelectProcessBranch(first.HelperContext, "Three.exe", "unrelated"));
        Route(RulesFor(JsonNode.Parse(first.MainJson)!, one!), "unmatched.test", "443").Should().Be("block");
        var nextPort = 32300;
        var next = HelperRoutingHandoff.Prepare(main, helper, json, () => nextPort++);
        SelectProcessBranch(next.HelperContext, "One.exe", "unrelated").Should().NotBe(one);
        helper.SharedProcessRules.Should().BeEmpty();
        helper.SharedProcessOutbounds.Should().BeEmpty();
    }

    private static string? SelectProcessBranch(CoreConfigContext helper, string name, string path) =>
        helper.SharedProcessRules.FirstOrDefault(r => MatchesProcess(r, name, path))?.outbound;

    private static bool MatchesProcess(Rule4Sbox rule, string name, string path) => rule.type == "logical"
        ? rule.rules!.All(r => MatchesProcess(r, name, path))
        : (rule.process_name == null || rule.process_name.Contains(name))
            && (rule.process_path == null || rule.process_path.Contains(path));

    private static List<JsonNode> RulesFor(JsonNode root, string inbound) => root["routing"]!["rules"]!.AsArray()
        .Where(r => r?["inboundTag"]?.AsArray().Any(t => t?.GetValue<string>() == inbound) == true).Select(r => r!).ToList();

    private static string? Route(IEnumerable<JsonNode> rules, string domain, string port, string network = "tcp", string? ip = null) => rules.FirstOrDefault(r =>
        (r["domain"] == null || r["domain"]!.AsArray().Any(d => d!.GetValue<string>() == $"full:{domain}"))
        && (r["ip"] == null || r["ip"]!.AsArray().Any(i => i!.GetValue<string>() == $"{ip}/32"))
        && (r["port"] == null || r["port"]!.GetValue<string>() == port)
        && (r["network"] == null || r["network"]!.GetValue<string>().Split(',').Contains(network)))?["outboundTag"]?.GetValue<string>();
}
