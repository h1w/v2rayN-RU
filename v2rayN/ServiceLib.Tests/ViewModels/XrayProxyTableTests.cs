using AwesomeAssertions;
using ReactiveUI.Builder;
using ServiceLib.Models.Dto;
using ServiceLib.Tests.CoreConfig;
using ServiceLib.ViewModels;
using Xunit;

namespace ServiceLib.Tests.ViewModels;

[Collection("Xray proxy panels")]
public class XrayProxyTableTests
{
    static XrayProxyTableTests() => RxAppBuilder.CreateReactiveUIBuilder().WithCoreServices().BuildApp();

    [Fact]
    public void Refresh_PreservesSelectedObjectWhileUpdatingTrafficAndRemovingExpiredRows()
    {
        WithConfig(vm =>
        {
            vm.RefreshXrayProxies([Row("a", 1), Row("b", 0)]);
            var selected = vm.XrayProxies[0];
            vm.SelectedXrayProxy = selected;
            vm.RefreshXrayProxies([Row("a", 3), Row("c", 1)]);
            vm.XrayProxies[0].Should().BeSameAs(selected);
            vm.SelectedXrayProxy.Should().BeSameAs(selected);
            selected.ConnectionCount.Should().Be(3);
            vm.XrayProxies.Select(row => row.Key).Should().Equal("a", "c");
            vm.RefreshXrayProxies([Row("c", 2), Row("a", 4)]);
            vm.XrayProxies[1].Should().BeSameAs(selected);
            vm.SelectedXrayProxy.Should().BeSameAs(selected);
            selected.ConnectionCount.Should().Be(4);
        });
    }

    [Fact]
    public void Filters_SearchHiddenEndpointsAndRestoreOriginalObjectsAcrossServiceSections()
    {
        WithConfig(vm =>
        {
            var active = Row("North", 2);
            var idle = Row("South", 0);
            var service = new XrayProxyRow { Key = "direct", Tag = "direct", IsService = true };
            vm.RefreshXrayProxies([active, idle, service]);
            var original = vm.XrayProxies[0];
            vm.XrayConnectionFilter = 1;
            vm.XrayProxies.Select(row => row.Tag).Should().Equal("North");
            vm.XrayServiceProxies.Should().BeEmpty();
            vm.XraySearch = "PROXY.EXAMPLE";
            vm.XrayProxies.Single().Should().BeSameAs(original);
            vm.XraySearch = "missing";
            vm.XrayProxies.Should().BeEmpty();
            vm.XraySearch = "";
            vm.XrayConnectionFilter = 0;
            vm.XrayProxies[0].Should().BeSameAs(original);
            vm.XrayServiceProxies.Single().Tag.Should().Be("direct");
        });
    }

    [Fact]
    public void Inventory_DistinguishesBalancerFromSameNamedServerAndLoopbackServices()
    {
        var manager = new XrayProxyPanelManager();
        manager.Activate("""
            {"outbounds":[
              {"tag":"pool","protocol":"socks","settings":{"servers":[{"address":"proxy.example","port":443}]}},
              {"tag":"internal","protocol":"socks","settings":{"servers":[{"address":"::1","port":1080}]}},
              {"tag":"direct","protocol":"freedom"},
              {"tag":"block","protocol":"blackhole"}],
             "routing":{"balancers":[{"tag":"pool","selector":["pool"]}]}}
            """);
        var rows = manager.GetRows([], DateTimeOffset.UtcNow);
        rows.Select(row => row.Key).Distinct().Count().Should().Be(rows.Count);
        rows.Where(row => row.IsService).Select(row => row.Tag).Should().Equal("internal", "direct", "block");
        rows.Single(row => row.IsBalancer).IsService.Should().BeFalse();
        rows.Single(row => row.Protocol == "socks" && !row.IsService).Groups.Should().Be("pool");
    }

    private static XrayProxyRow Row(string key, int count) => new()
    {
        Key = key, Tag = key, Endpoint = "proxy.example:443", ConnectionCount = count
    };

    private static void WithConfig(Action<ClashProxiesViewModel> scenario)
    {
        var previous = AppManager.Instance.Config;
        try
        {
            CoreConfigTestFactory.BindAppManagerConfig(CoreConfigTestFactory.CreateConfig());
            XrayProxyPanelManager.Instance.Reset();
            scenario(new ClashProxiesViewModel());
        }
        finally
        {
            XrayProxyPanelManager.Instance.Reset();
            CoreConfigTestFactory.BindAppManagerConfig(previous);
        }
    }
}
