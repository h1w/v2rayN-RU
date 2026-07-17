using AwesomeAssertions;
using ServiceLib.Handler;
using ServiceLib.Tests.CoreConfig;
using Xunit;

namespace ServiceLib.Tests.Handler;

public class CustomConfigComposerTests
{
    private const string XrayJson = """
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

    private static CoreConfigContext EmptyContext(ECoreType coreType)
    {
        var config = CoreConfigTestFactory.CreateConfig(coreType);
        CoreConfigTestFactory.BindAppManagerConfig(config);
        var node = CoreConfigTestFactory.CreateVmessNode(coreType);
        return CoreConfigTestFactory.CreateContext(config, node, coreType);
    }

    [Fact]
    public void Compose_signals_fallback_for_unusable_json()
    {
        var ctx = EmptyContext(ECoreType.Xray);

        CustomConfigComposer.Compose(null, ECoreType.Xray, ctx).Json.Should().BeNull();
        CustomConfigComposer.Compose("not json", ECoreType.Xray, ctx).Json.Should().BeNull();
        CustomConfigComposer.Compose("""{ "routing": { "rules": [] } }""", ECoreType.Xray, ctx).Json.Should().BeNull();
    }

    [Fact]
    public void Compose_preserves_untouched_sections()
    {
        var ctx = EmptyContext(ECoreType.Xray);

        var merged = CustomConfigComposer.Compose(XrayJson, ECoreType.Xray, ctx).Json;

        merged.Should().NotBeNull();
        var json = JsonUtils.ParseJson(merged);
        // Инбаунды пользователя не трогаем.
        json?["inbounds"]?.AsArray().Should().HaveCount(1);
        json?["inbounds"]?[0]?["port"]?.GetValue<int>().Should().Be(10808);
        // Правила пользователя остаются на месте и первыми.
        json?["routing"]?["rules"]?[0]?["outboundTag"]?.GetValue<string>().Should().Be("direct");
    }
}
