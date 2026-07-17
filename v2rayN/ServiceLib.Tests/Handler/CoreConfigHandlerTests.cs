using AwesomeAssertions;
using ServiceLib.Handler;
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
}
