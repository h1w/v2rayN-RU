using AwesomeAssertions;
using ReactiveUI.Builder;
using ServiceLib.Enums;
using ServiceLib.Models.Dto;
using ServiceLib.Tests.CoreConfig;
using ServiceLib.ViewModels;
using Xunit;

namespace ServiceLib.Tests.ViewModels;

[CollectionDefinition("Xray proxy panels", DisableParallelization = true)]
public class XrayProxyPanelCollection;

[Collection("Xray proxy panels")]
public class XrayProxyPanelViewModelTests
{
    static XrayProxyPanelViewModelTests()
    {
        RxAppBuilder.CreateReactiveUIBuilder().WithCoreServices().BuildApp();
    }

    [Fact]
    public async Task ConnectionFilter_MatchesAllAmbiguousNamesWithoutChangingCloseId()
    {
        await WithConfig(async () =>
        {
            XrayProxyPanelManager.Instance.Activate("""
                {"outbounds":[
                  {"tag":"North","protocol":"socks","settings":{"servers":[{"address":"proxy.example","port":443}]}},
                  {"tag":"South","protocol":"socks","settings":{"servers":[{"address":"proxy.example","port":443}]}}
                ]}
                """);
            var vm = new ClashConnectionsViewModel { HostFilter = "sOuTh" };
            await vm.RefreshConnections([Connection("helper-id", "proxy.example", "443")]);

            vm.ConnectionItems.Should().ContainSingle();
            vm.ConnectionItems.Single().Id.Should().Be("helper-id");
            vm.ConnectionItems.Single().Chain.Should().Contain("North").And.Contain("South");
            vm.HostFilter = "nOrTh";
            vm.ConnectionItems.Should().ContainSingle();
            vm.HostFilter = "unknown";
            vm.ConnectionItems.Should().BeEmpty();
        });
    }

    [Fact]
    public async Task ConnectionFilter_DoesNotResurrectPreviousXraySession()
    {
        await WithConfig(async () =>
        {
            XrayProxyPanelManager.Instance.Activate("""
                {"outbounds":[{"tag":"North","protocol":"socks","settings":{"servers":[{"address":"proxy.example","port":443}]}}]}
                """);
            var vm = new ClashConnectionsViewModel();
            await vm.RefreshConnections([Connection("old-session", "proxy.example", "443")]);
            XrayProxyPanelManager.Instance.Reset();
            vm.HostFilter = "proxy.example";
            vm.ConnectionItems.Should().BeEmpty();
        });
    }


    [Fact]
    public async Task XrayRuleControls_DoNotChangeNativeRuleMode()
    {
        await WithConfig(async () =>
        {
            XrayProxyPanelManager.Instance.Activate("""{"outbounds":[{"tag":"direct","protocol":"freedom"}]}""");
            var config = AppManager.Instance.Config;
            config.ClashUIItem.RuleMode = ERuleMode.Rule;
            var vm = new ClashProxiesViewModel();
            await vm.SetRuleModeCheck(ERuleMode.Global);
            config.ClashUIItem.RuleMode.Should().Be(ERuleMode.Rule);
            vm.RuleModeSelected = (int)ERuleMode.Direct;
            config.ClashUIItem.RuleMode.Should().Be(ERuleMode.Rule);
        });
    }

    [Fact]
    public async Task UnavailableConnections_ClearPreviouslyDisplayedConnections()
    {
        await WithConfig(async () =>
        {
            var vm = new ClashConnectionsViewModel();
            await vm.RefreshConnections([Connection("old-id", "example.com", "443")]);
            await vm.RefreshConnections(null);
            vm.ConnectionItems.Should().BeEmpty();
            vm.HostFilter = "example";
            vm.ConnectionItems.Should().BeEmpty();
        });
    }

    private static ConnectionItem Connection(string id, string host, string port) => new()
    {
        id = id,
        metadata = new MetadataItem { host = host, destinationPort = port, network = "tcp", type = "Mixed" },
        start = DateTime.Now,
        rule = "MATCH",
        chains = ["proxy", "GLOBAL"]
    };

    private static async Task WithConfig(Func<Task> scenario)
    {
        var previousConfig = AppManager.Instance.Config;
        try
        {
            CoreConfigTestFactory.BindAppManagerConfig(CoreConfigTestFactory.CreateConfig());
            XrayProxyPanelManager.Instance.Reset();
            await scenario();
        }
        finally
        {
            XrayProxyPanelManager.Instance.Reset();
            CoreConfigTestFactory.BindAppManagerConfig(previousConfig);
        }
    }
}
