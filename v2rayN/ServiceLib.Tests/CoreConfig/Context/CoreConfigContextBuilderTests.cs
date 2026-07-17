using AwesomeAssertions;
using ServiceLib.Enums;
using ServiceLib.Handler.Builder;
using ServiceLib.Helper;
using ServiceLib.Models;
using Xunit;

namespace ServiceLib.Tests.CoreConfig.Context;

public class CoreConfigContextBuilderTests
{
    [Fact]
    public async Task ResolveNodeAsync_DirectCycleDependency_ShouldFailWithCycleError()
    {
        var config = CoreConfigTestFactory.CreateConfig();
        CoreConfigTestFactory.BindAppManagerConfig(config);

        var groupAId = NewId("group-a");
        var groupBId = NewId("group-b");
        var groupA = CoreConfigTestFactory.CreatePolicyGroupNode(ECoreType.Xray, groupAId, "group-a", [groupBId]);
        var groupB = CoreConfigTestFactory.CreatePolicyGroupNode(ECoreType.Xray, groupBId, "group-b", [groupAId]);

        await UpsertProfilesAsync(groupA, groupB);

        var context = CoreConfigTestFactory.CreateContext(config, groupA, ECoreType.Xray);
        context.AllProxiesMap.Clear();

        var (_, validatorResult) = await CoreConfigContextBuilder.ResolveNodeAsync(context, groupA, false);

        validatorResult.Success.Should().BeFalse();
        validatorResult.Errors.Should().Contain(msg => ContainsCycleDependencyMessage(msg));
        context.AllProxiesMap.Should().NotContainKey(groupA.IndexId);
        context.AllProxiesMap.Should().NotContainKey(groupB.IndexId);
    }

    [Fact]
    public async Task ResolveNodeAsync_IndirectCycleDependency_ShouldFailWithCycleError()
    {
        var config = CoreConfigTestFactory.CreateConfig();
        CoreConfigTestFactory.BindAppManagerConfig(config);

        var groupAId = NewId("group-a");
        var groupBId = NewId("group-b");
        var groupCId = NewId("group-c");
        var groupA = CoreConfigTestFactory.CreatePolicyGroupNode(ECoreType.Xray, groupAId, "group-a", [groupBId]);
        var groupB = CoreConfigTestFactory.CreatePolicyGroupNode(ECoreType.Xray, groupBId, "group-b", [groupCId]);
        var groupC = CoreConfigTestFactory.CreatePolicyGroupNode(ECoreType.Xray, groupCId, "group-c", [groupAId]);

        await UpsertProfilesAsync(groupA, groupB, groupC);

        var context = CoreConfigTestFactory.CreateContext(config, groupA, ECoreType.Xray);
        context.AllProxiesMap.Clear();

        var (_, validatorResult) = await CoreConfigContextBuilder.ResolveNodeAsync(context, groupA, false);

        validatorResult.Success.Should().BeFalse();
        validatorResult.Errors.Should().Contain(msg => ContainsCycleDependencyMessage(msg));
        context.AllProxiesMap.Should().NotContainKey(groupA.IndexId);
        context.AllProxiesMap.Should().NotContainKey(groupB.IndexId);
        context.AllProxiesMap.Should().NotContainKey(groupC.IndexId);
    }

    [Fact]
    public async Task ResolveNodeAsync_CycleWithValidBranch_ShouldSkipCycleAndKeepValidChild()
    {
        var config = CoreConfigTestFactory.CreateConfig();
        CoreConfigTestFactory.BindAppManagerConfig(config);

        var groupAId = NewId("group-a");
        var groupBId = NewId("group-b");
        var leafId = NewId("leaf");
        var groupA = CoreConfigTestFactory.CreatePolicyGroupNode(ECoreType.Xray, groupAId, "group-a", [groupBId, leafId]);
        var groupB = CoreConfigTestFactory.CreatePolicyGroupNode(ECoreType.Xray, groupBId, "group-b", [groupAId]);
        var leaf = CoreConfigTestFactory.CreateSocksNode(ECoreType.Xray, leafId, "leaf");

        await UpsertProfilesAsync(groupA, groupB, leaf);

        var context = CoreConfigTestFactory.CreateContext(config, groupA, ECoreType.Xray);
        context.AllProxiesMap.Clear();

        var (_, validatorResult) = await CoreConfigContextBuilder.ResolveNodeAsync(context, groupA, false);

        validatorResult.Success.Should().BeTrue();
        validatorResult.Errors.Should().BeEmpty();
        validatorResult.Warnings.Should().Contain(msg => ContainsCycleDependencyMessage(msg));

        context.AllProxiesMap.Should().ContainKey(leaf.IndexId);
        context.AllProxiesMap.Should().ContainKey(groupA.IndexId);
        context.AllProxiesMap.Should().NotContainKey(groupB.IndexId);
        groupA.GetProtocolExtra().ChildItems.Should().Be(leaf.IndexId);
    }

    [Fact]
    public async Task ResolveRuleTargets_RuleTargetingCustomProfile_ResolvesToSocksChainNode()
    {
        var config = CoreConfigTestFactory.CreateConfig();
        CoreConfigTestFactory.BindAppManagerConfig(config);

        var mainId = NewId("main");
        var customId = NewId("custom");
        var mainNode = CoreConfigTestFactory.CreateVmessNode(ECoreType.Xray, mainId, "main-vmess");
        var customRemark = NewId("poland-json");
        var customNode = new ProfileItem
        {
            IndexId = customId,
            ConfigType = EConfigType.Custom,
            CoreType = ECoreType.Xray,
            Remarks = customRemark,
            Address = "poland.json",
        };
        await UpsertProfilesAsync(mainNode, customNode);

        var context = CoreConfigTestFactory.CreateContext(config, mainNode, ECoreType.Xray) with
        {
            RoutingItem = new RoutingItem
            {
                Id = "r1",
                Remarks = "d",
                RuleSet = JsonUtils.Serialize(new List<RulesItem>
                {
                    new() { Id = "rule-1", Enabled = true, OutboundTag = customRemark, Domain = ["ifconfig.me"] },
                }),
                DomainStrategy = Global.AsIs,
                DomainStrategy4Singbox = string.Empty,
            },
        };
        context.AllProxiesMap.Clear();

        var validatorResult = NodeValidatorResult.Empty();
        await CoreConfigContextBuilder.ResolveRuleTargetsAsync(context, validatorResult);

        // Ровно одно цепочечное ядро на этот профиль.
        context.ChainCores.Should().ContainSingle();
        var descriptor = context.ChainCores[0];
        descriptor.Node.IndexId.Should().Be(customId);
        descriptor.Port.Should().BeInRange(1, 65535);
        descriptor.ConfigFileName.Should().Be("configChain0.json");

        // В карту лёг socks-узел к цепочке, а НЕ сырой Custom-профиль. Это и есть вся суть:
        // SOCKS поддерживается генераторами, Custom — нет.
        context.AllProxiesMap.Should().ContainKey($"remark:{customRemark}");
        var mapped = context.AllProxiesMap[$"remark:{customRemark}"];
        mapped.ConfigType.Should().Be(EConfigType.SOCKS);
        mapped.Address.Should().Be(Global.Loopback);
        mapped.Port.Should().Be(descriptor.Port);
        // Непустой IndexId обязателен: GenRoutingUserRuleOutbound строит тег как
        // "{IndexId}-proxy-{Remarks}", на пустом теги цепочек столкнулись бы.
        mapped.IndexId.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ResolveRuleTargets_TwoRulesOnSameCustomProfile_ShareOneChainCore()
    {
        var config = CoreConfigTestFactory.CreateConfig();
        CoreConfigTestFactory.BindAppManagerConfig(config);

        var mainNode = CoreConfigTestFactory.CreateVmessNode(ECoreType.Xray, NewId("main"), "main-vmess");
        var customRemark = NewId("poland-json");
        var customNode = new ProfileItem
        {
            IndexId = NewId("custom"),
            ConfigType = EConfigType.Custom,
            CoreType = ECoreType.Xray,
            Remarks = customRemark,
            Address = "poland.json",
        };
        await UpsertProfilesAsync(mainNode, customNode);

        var context = CoreConfigTestFactory.CreateContext(config, mainNode, ECoreType.Xray) with
        {
            RoutingItem = new RoutingItem
            {
                Id = "r1",
                Remarks = "d",
                RuleSet = JsonUtils.Serialize(new List<RulesItem>
                {
                    new() { Id = "rule-1", Enabled = true, OutboundTag = customRemark, Domain = ["ifconfig.me"] },
                    new() { Id = "rule-2", Enabled = true, OutboundTag = customRemark, Domain = ["whatismyip.com"] },
                }),
                DomainStrategy = Global.AsIs,
                DomainStrategy4Singbox = string.Empty,
            },
        };
        context.AllProxiesMap.Clear();

        await CoreConfigContextBuilder.ResolveRuleTargetsAsync(context, NodeValidatorResult.Empty());

        // Один .json = одно ядро, сколько бы правил на него ни ссылалось.
        context.ChainCores.Should().ContainSingle();
    }

    private static string NewId(string prefix)
    {
        return $"{prefix}-{Guid.NewGuid():N}";
    }

    private static bool ContainsCycleDependencyMessage(string message)
    {
        return message.Contains("cycle dependency", StringComparison.OrdinalIgnoreCase)
               || message.Contains("循环依赖", StringComparison.Ordinal)
               || message.Contains("循環依賴", StringComparison.Ordinal)
               || message.Contains("циклическую зависимость", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task UpsertProfilesAsync(params ProfileItem[] profiles)
    {
        SQLiteHelper.Instance.CreateTable<ProfileItem>();
        foreach (var profile in profiles)
        {
            await SQLiteHelper.Instance.ReplaceAsync(profile);
        }
    }
}
