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
        await CoreConfigContextBuilder.ResolveRuleTargetsAsync(context, validatorResult, resolveChainCores: true);

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

        await CoreConfigContextBuilder.ResolveRuleTargetsAsync(context, NodeValidatorResult.Empty(),
            resolveChainCores: true);

        // Один .json = одно ядро, сколько бы правил на него ни ссылалось.
        context.ChainCores.Should().ContainSingle();
    }

    [Fact]
    public async Task ResolveRuleTargets_ChainResolutionOff_KeepsRawCustomNodeInMap()
    {
        var config = CoreConfigTestFactory.CreateConfig();
        CoreConfigTestFactory.BindAppManagerConfig(config);

        var mainNode = CoreConfigTestFactory.CreateVmessNode(ECoreType.Xray, NewId("main"), "main-vmess");
        var customId = NewId("custom");
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
        // Не передаём resolveChainCores вовсе — целимся именно в дефолт. Это путь всех
        // вызывающих Build, кроме BuildAll: speed test, экспорт конфига, pre-socks.
        await CoreConfigContextBuilder.ResolveRuleTargetsAsync(context, validatorResult);

        // Без резолва цепочек ни одно ядро заводиться не должно — иначе это порт и
        // ChainCoreDescriptor, которые никто никогда не запустит.
        context.ChainCores.Should().BeEmpty();

        // В карте должен остаться сырой Custom-узел — ровно то поведение, что было до
        // Task 3: генератор ниже по течению сам фолбэкнется на Global.ProxyTag.
        context.AllProxiesMap.Should().ContainKey($"remark:{customRemark}");
        var mapped = context.AllProxiesMap[$"remark:{customRemark}"];
        mapped.ConfigType.Should().Be(EConfigType.Custom);
        mapped.IndexId.Should().Be(customId);

        // Молчаливый фолбэк — предупреждений быть не должно.
        validatorResult.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveRuleTargets_PortCollisionWithExistingChainCore_RejectsNewChainAndWarns()
    {
        var config = CoreConfigTestFactory.CreateConfig();
        CoreConfigTestFactory.BindAppManagerConfig(config);

        var mainNode = CoreConfigTestFactory.CreateVmessNode(ECoreType.Xray, NewId("main"), "main-vmess");

        var existingCustomNode = new ProfileItem
        {
            IndexId = NewId("custom-existing"),
            ConfigType = EConfigType.Custom,
            CoreType = ECoreType.Xray,
            Remarks = NewId("existing-json"),
            Address = "existing.json",
        };
        var newCustomId = NewId("custom-new");
        var newCustomRemark = NewId("new-json");
        var newCustomNode = new ProfileItem
        {
            IndexId = newCustomId,
            ConfigType = EConfigType.Custom,
            CoreType = ECoreType.Xray,
            Remarks = newCustomRemark,
            Address = "new.json",
        };
        await UpsertProfilesAsync(mainNode, existingCustomNode, newCustomNode);

        var context = CoreConfigTestFactory.CreateContext(config, mainNode, ECoreType.Xray) with
        {
            RoutingItem = new RoutingItem
            {
                Id = "r1",
                Remarks = "d",
                RuleSet = JsonUtils.Serialize(new List<RulesItem>
                {
                    new() { Id = "rule-1", Enabled = true, OutboundTag = newCustomRemark, Domain = ["ifconfig.me"] },
                }),
                DomainStrategy = Global.AsIs,
                DomainStrategy4Singbox = string.Empty,
            },
        };
        context.AllProxiesMap.Clear();

        // Другая цепочка уже держит порт 23456. allocatePort ниже симулирует Utils.GetFreePort()
        // выдающий тот же самый порт второй раз (ровно сценарий из Finding 2: сентинел 59090
        // на повторном сбое аллокации).
        const int collidingPort = 23456;
        context.ChainCores.Add(new ChainCoreDescriptor
        {
            Node = existingCustomNode,
            CoreType = ECoreType.Xray,
            Port = collidingPort,
            ConfigFileName = "configChain0.json",
        });

        var validatorResult = NodeValidatorResult.Empty();
        await CoreConfigContextBuilder.ResolveRuleTargetsAsync(context, validatorResult,
            resolveChainCores: true, allocatePort: () => collidingPort);

        // Никакой второй цепочки на том же порту — коллизия отклонена, а не тихо принята.
        context.ChainCores.Should().ContainSingle();

        // Провал аллокации трактуется как провал цепочки: сырой Custom-узел остаётся в
        // карте, и добавляется предупреждение — тот же путь, что и для любого другого
        // "цепочку собрать не удалось".
        context.AllProxiesMap.Should().ContainKey($"remark:{newCustomRemark}");
        var mapped = context.AllProxiesMap[$"remark:{newCustomRemark}"];
        mapped.ConfigType.Should().Be(EConfigType.Custom);
        mapped.IndexId.Should().Be(newCustomId);

        validatorResult.Warnings.Should().Contain(w => w.Contains(newCustomRemark));
    }

    [Fact]
    public async Task ResolveRuleTargets_RuleTargetingActiveCustomProfile_IsNoOpWithoutWarning()
    {
        var config = CoreConfigTestFactory.CreateConfig();
        CoreConfigTestFactory.BindAppManagerConfig(config);

        var activeRemark = NewId("active-json");
        var activeCustomNode = new ProfileItem
        {
            IndexId = NewId("active-custom"),
            ConfigType = EConfigType.Custom,
            CoreType = ECoreType.Xray,
            Remarks = activeRemark,
            Address = "active.json",
        };
        await UpsertProfilesAsync(activeCustomNode);

        // Активный узел (context.Node) — это тот же самый .json-профиль, на который
        // указывает правило. Семантически это no-op: трафик и так уже туда уходит.
        var context = CoreConfigTestFactory.CreateContext(config, activeCustomNode, ECoreType.Xray) with
        {
            RoutingItem = new RoutingItem
            {
                Id = "r1",
                Remarks = "d",
                RuleSet = JsonUtils.Serialize(new List<RulesItem>
                {
                    new() { Id = "rule-1", Enabled = true, OutboundTag = activeRemark, Domain = ["ifconfig.me"] },
                }),
                DomainStrategy = Global.AsIs,
                DomainStrategy4Singbox = string.Empty,
            },
        };
        context.AllProxiesMap.Clear();

        var validatorResult = NodeValidatorResult.Empty();
        await CoreConfigContextBuilder.ResolveRuleTargetsAsync(context, validatorResult, resolveChainCores: true);

        // Никакого цепочечного ядра — ни отдельного процесса, ни порта на пустом месте.
        context.ChainCores.Should().BeEmpty();

        // Никакой записи под "remark:{tag}" — GenRoutingUserRuleOutbound увидит node == null,
        // вернёт Global.ProxyTag, а composer Части 1 перепишет его на mainProxyTag самого JSON.
        context.AllProxiesMap.Should().NotContainKey($"remark:{activeRemark}");

        // И никакого предупреждения — это не сбой, а корректный no-op.
        validatorResult.Warnings.Should().BeEmpty();
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
