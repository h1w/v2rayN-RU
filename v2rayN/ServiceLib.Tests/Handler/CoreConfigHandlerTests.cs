using AwesomeAssertions;
using ServiceLib.Handler;
using ServiceLib.Models.Dto;
using ServiceLib.Tests.CoreConfig;
using Xunit;

namespace ServiceLib.Tests.Handler;

/// <summary>
/// Регрессионные тесты для custom-профиля на уровне CoreConfigHandler
/// (публичная точка входа GenerateClientConfig). До этих тестов ничего в
/// ServiceLib.Tests не гоняло этот путь целиком: раньше GenerateClientCustomConfig
/// не заполнял ret.Data и пользовательский JSON копировался дословно через
/// File.Copy, из-за чего локальные правила маршрутизации, настроенные в UI,
/// молча отбрасывались и никогда не попадали в core. Task 9 подключил
/// CustomConfigComposer.Compose, чтобы вливать локальные правила; эти тесты
/// закрепляют то, что реально пишется в файл, который получает core —
/// а не только то, что возвращает Compose в отрыве от вызывающего кода.
/// </summary>
public class CoreConfigHandlerTests
{
    // Тот же xray-джейсон, что и в CustomConfigComposerTests (main-out/direct,
    // одно пользовательское правило на direct) — заведомо пригоден для слияния.
    private const string MergeableXrayJson = """
    {
      "inbounds": [{ "port": 10808, "protocol": "socks" }],
      "outbounds": [
        { "protocol": "vless", "tag": "main-out" },
        { "protocol": "freedom", "tag": "direct" }
      ],
      "routing": {
        "rules": [
          { "type": "field", "outboundTag": "direct", "domain": ["geosite:cn"] }
        ]
      }
    }
    """;

    // Нет секции outbounds вовсе — CustomConfigComposer.Compose обязан
    // вернуть Json == null (см. CustomConfigComposer.cs: root is null ||
    // outbounds is null || outbounds.Count == 0), и GenerateClientCustomConfig
    // обязан откатиться на дословный File.Copy.
    private const string NonMergeableJsonWithoutOutbounds = """
    {
      "inbounds": [{ "port": 10808, "protocol": "socks" }]
    }
    """;

    private const string EnabledRuleSet =
        """[{ "Id": "r1", "OutboundTag": "proxy", "Domain": ["youtube.com"], "Enabled": true }]""";

    // Тот же формат, что MergeableXrayJson, но с тремя правилами в routing.rules —
    // нужно как минимум три, чтобы за один тест проверить и переупорядочивание,
    // и отключение (Finding 2, кейс 1: EnableCustomRuleEditing=true + CustomRuleState).
    private const string MergeableXrayJsonThreeRules = """
    {
      "inbounds": [{ "port": 10808, "protocol": "socks" }],
      "outbounds": [
        { "protocol": "vless", "tag": "main-out" },
        { "protocol": "freedom", "tag": "direct" }
      ],
      "routing": {
        "rules": [
          { "type": "field", "outboundTag": "direct", "domain": ["r0"] },
          { "type": "field", "outboundTag": "main-out", "domain": ["r1"] },
          { "type": "field", "outboundTag": "direct", "domain": ["r2"] }
        ]
      }
    }
    """;

    // Как NonMergeableJsonWithoutOutbounds (нет outbounds -> Compose обязан
    // откатиться на фолбэк), но с routing.rules — иначе ApplyCustomRuleState
    // не сможет ничего перестраивать (Finding 2, кейс 2: доказывает Finding 1 —
    // фолбэк обязан писать rawJson, а не дословную копию с диска).
    private const string NonMergeableJsonWithRoutingRules = """
    {
      "inbounds": [{ "port": 10808, "protocol": "socks" }],
      "routing": {
        "rules": [
          { "type": "field", "outboundTag": "proxy", "domain": ["r0"] },
          { "type": "field", "outboundTag": "direct", "domain": ["r1"] }
        ]
      }
    }
    """;

    private static ProfileItem CreateCustomNode(string sourcePath)
    {
        return new ProfileItem
        {
            IndexId = "custom-1",
            ConfigType = EConfigType.Custom,
            CoreType = ECoreType.Xray,
            Remarks = "custom-profile",
            Address = sourcePath,
        };
    }

    private static CoreConfigContext CreateCustomContext(Config config, ProfileItem node, string ruleSetJson)
    {
        var ctx = CoreConfigTestFactory.CreateContext(config, node, ECoreType.Xray);
        return ctx with
        {
            RoutingItem = new RoutingItem
            {
                Id = "r1",
                Remarks = "d",
                DomainStrategy = Global.AsIs,
                DomainStrategy4Singbox = string.Empty,
                RuleSet = ruleSetJson,
            },
        };
    }

    [Fact]
    public async Task GenerateClientConfig_MergeableJsonWithEnabledRule_MergesLocalRule_NotVerbatimCopy()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), $"v2rayn-ru-test-source-{Guid.NewGuid():N}.json");
        var destPath = Path.Combine(Path.GetTempPath(), $"v2rayn-ru-test-dest-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(sourcePath, MergeableXrayJson, TestContext.Current.CancellationToken);

            var config = CoreConfigTestFactory.CreateConfig(ECoreType.Xray);
            CoreConfigTestFactory.BindAppManagerConfig(config);
            var node = CreateCustomNode(sourcePath);
            var ctx = CreateCustomContext(config, node, EnabledRuleSet);

            var ret = await CoreConfigHandler.GenerateClientConfig(ctx, destPath);

            ret.Success.Should().BeTrue();
            File.Exists(destPath).Should().BeTrue();

            var written = await File.ReadAllTextAsync(destPath, TestContext.Current.CancellationToken);
            // Главная гарантия этой задачи: раз локальное правило включено и JSON
            // пригоден для слияния, итоговый файл — НЕ побайтовая копия исходника.
            written.Should().NotBe(MergeableXrayJson);

            var parsed = JsonUtils.ParseJson(written);
            parsed.Should().NotBeNull();
            var rulesNode = parsed!["routing"]?["rules"];
            rulesNode.Should().NotBeNull();
            var rules = rulesNode!.AsArray();
            // Правило из JSON (direct/geosite:cn) плюс наше локальное — итого два.
            rules.Should().HaveCount(2);

            var appended = rules[1];
            appended.Should().NotBeNull();
            var domainNode = appended!["domain"];
            domainNode.Should().NotBeNull();
            domainNode![0]!.GetValue<string>().Should().Be("youtube.com");
            var outboundTagNode = appended["outboundTag"];
            outboundTagNode.Should().NotBeNull();
            // CustomConfigComposer подменяет тег на главный proxy-выход из JSON.
            outboundTagNode!.GetValue<string>().Should().Be("main-out");
        }
        finally
        {
            File.Delete(sourcePath);
            File.Delete(destPath);
        }
    }

    [Fact]
    public async Task GenerateClientConfig_NonMergeableJson_FallsBackToVerbatimCopy()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), $"v2rayn-ru-test-source-{Guid.NewGuid():N}.json");
        var destPath = Path.Combine(Path.GetTempPath(), $"v2rayn-ru-test-dest-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(sourcePath, NonMergeableJsonWithoutOutbounds, TestContext.Current.CancellationToken);

            var config = CoreConfigTestFactory.CreateConfig(ECoreType.Xray);
            CoreConfigTestFactory.BindAppManagerConfig(config);
            var node = CreateCustomNode(sourcePath);
            // Правило включено и валидно — но без outbounds в исходном JSON
            // слияние невозможно в принципе, и это не должно зависеть от того,
            // сколько локальных правил настроено.
            var ctx = CreateCustomContext(config, node, EnabledRuleSet);

            var ret = await CoreConfigHandler.GenerateClientConfig(ctx, destPath);

            ret.Success.Should().BeTrue();
            File.Exists(destPath).Should().BeTrue();

            var written = await File.ReadAllTextAsync(destPath, TestContext.Current.CancellationToken);
            // Обещание, которое эта задача не должна нарушать: конфиг, для
            // которого слияние невозможно, доходит до core байт-в-байт, как и
            // до Task 9.
            written.Should().Be(NonMergeableJsonWithoutOutbounds);
        }
        finally
        {
            File.Delete(sourcePath);
            File.Delete(destPath);
        }
    }

    /// <summary>
    /// Регрессия для Finding 2 (кейс 1): EnableCustomRuleEditing=true и непустой
    /// CustomRuleState на узле ранее вообще не были покрыты тестами — ветка
    /// ApplyCustomRuleState в GenerateClientCustomConfig не гонялась ни разу.
    /// Здесь JSON пригоден для слияния, так что итог идёт по пути
    /// composed.Json != null; проверяем, что ret.Data отражает и переупорядочивание,
    /// и отключение правила, заданные в CustomRuleState.
    /// </summary>
    [Fact]
    public async Task GenerateClientConfig_RuleEditingEnabled_MergeableJson_ReflectsReorderedAndDisabledRules()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), $"v2rayn-ru-test-source-{Guid.NewGuid():N}.json");
        var destPath = Path.Combine(Path.GetTempPath(), $"v2rayn-ru-test-dest-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(sourcePath, MergeableXrayJsonThreeRules, TestContext.Current.CancellationToken);

            var config = CoreConfigTestFactory.CreateConfig(ECoreType.Xray);
            config.UiItem.EnableCustomRuleEditing = true;
            CoreConfigTestFactory.BindAppManagerConfig(config);

            var node = CreateCustomNode(sourcePath);
            // Ordinal 2 (r2) первым и включён, ordinal 0 (r0) отключён,
            // ordinal 1 (r1) вторым и включён -> ожидаемый порядок: r2, r1.
            node.CustomRuleState = JsonUtils.Serialize(new List<CustomRuleStateItem>
            {
                new() { Index = 2, Enabled = true },
                new() { Index = 0, Enabled = false },
                new() { Index = 1, Enabled = true },
            }, false);
            // Пустой набор локальных UI-правил — изолируем именно rule-state
            // rebuild от слияния локальных правил из окна роутинга.
            var ctx = CreateCustomContext(config, node, "[]");

            var ret = await CoreConfigHandler.GenerateClientConfig(ctx, destPath);

            ret.Success.Should().BeTrue();
            var data = ret.Data;
            data.Should().NotBeNull();

            var parsed = JsonUtils.ParseJson(data!.ToString());
            parsed.Should().NotBeNull();
            var rulesNode = parsed!["routing"]?["rules"];
            rulesNode.Should().NotBeNull();
            var rules = rulesNode!.AsArray();
            // r0 отключён -> в итоге только два правила, в порядке r2, r1.
            rules.Should().HaveCount(2);

            var first = rules[0];
            first.Should().NotBeNull();
            var firstDomain = first!["domain"];
            firstDomain.Should().NotBeNull();
            firstDomain![0]!.GetValue<string>().Should().Be("r2");

            var second = rules[1];
            second.Should().NotBeNull();
            var secondDomain = second!["domain"];
            secondDomain.Should().NotBeNull();
            secondDomain![0]!.GetValue<string>().Should().Be("r1");
        }
        finally
        {
            File.Delete(sourcePath);
            File.Delete(destPath);
        }
    }

    /// <summary>
    /// Регрессия для Finding 2 (кейс 2) и одновременно доказательство фикса
    /// Finding 1: JSON без outbounds непригоден для слияния, поэтому
    /// GenerateClientCustomConfig идёт в фолбэк-ветку. До фикса Finding 1 эта
    /// ветка писала File.Copy(addressFileName, fileName) — дословную копию
    /// исходника с диска, — и правки CustomRuleState (тут: отключение правила
    /// r0) молча терялись. После фикса фолбэк обязан писать перестроенный
    /// rawJson через File.WriteAllTextAsync, когда ApplyCustomRuleState
    /// реально сработал.
    /// </summary>
    [Fact]
    public async Task GenerateClientConfig_RuleEditingEnabled_NonMergeableJson_FallbackWritesRebuiltJson_NotOriginal()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), $"v2rayn-ru-test-source-{Guid.NewGuid():N}.json");
        var destPath = Path.Combine(Path.GetTempPath(), $"v2rayn-ru-test-dest-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(sourcePath, NonMergeableJsonWithRoutingRules, TestContext.Current.CancellationToken);

            var config = CoreConfigTestFactory.CreateConfig(ECoreType.Xray);
            config.UiItem.EnableCustomRuleEditing = true;
            CoreConfigTestFactory.BindAppManagerConfig(config);

            var node = CreateCustomNode(sourcePath);
            node.CustomRuleState = JsonUtils.Serialize(new List<CustomRuleStateItem>
            {
                new() { Index = 0, Enabled = false }, // r0 отключено
            }, false);
            var ctx = CreateCustomContext(config, node, "[]");

            var ret = await CoreConfigHandler.GenerateClientConfig(ctx, destPath);

            ret.Success.Should().BeTrue();
            File.Exists(destPath).Should().BeTrue();

            var written = await File.ReadAllTextAsync(destPath, TestContext.Current.CancellationToken);
            // Без фикса Finding 1 здесь оказался бы NonMergeableJsonWithRoutingRules
            // дословно (с r0 внутри). С фиксом — перестроенный JSON без r0.
            written.Should().NotBe(NonMergeableJsonWithRoutingRules);

            var parsed = JsonUtils.ParseJson(written);
            parsed.Should().NotBeNull();
            var rulesNode = parsed!["routing"]?["rules"];
            rulesNode.Should().NotBeNull();
            var rules = rulesNode!.AsArray();
            rules.Should().HaveCount(1);

            var remaining = rules[0];
            remaining.Should().NotBeNull();
            var domainNode = remaining!["domain"];
            domainNode.Should().NotBeNull();
            domainNode![0]!.GetValue<string>().Should().Be("r1");
        }
        finally
        {
            File.Delete(sourcePath);
            File.Delete(destPath);
        }
    }
}
