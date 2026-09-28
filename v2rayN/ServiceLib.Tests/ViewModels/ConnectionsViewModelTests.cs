using System.Collections.Specialized;
using AwesomeAssertions;
using ReactiveUI.Builder;
using ServiceLib.Models.Dto;
using ServiceLib.Tests.CoreConfig;
using ServiceLib.ViewModels;
using Xunit;

namespace ServiceLib.Tests.ViewModels;

[Collection("Xray proxy panels")]
public class ConnectionsViewModelTests
{
    static ConnectionsViewModelTests()
    {
        RxAppBuilder.CreateReactiveUIBuilder().WithCoreServices().BuildApp();
    }

    [Theory]
    [InlineData(@"C:\Program Files\Browser\browser.exe", "browser.exe")]
    [InlineData("/Applications/Browser.app/Contents/MacOS/Browser", "Browser")]
    [InlineData("/usr/bin/firefox", "firefox")]
    [InlineData(@"\\server\apps\client.exe", "client.exe")]
    [InlineData("client", "client")]
    [InlineData(null, "—")]
    [InlineData("   ", "—")]
    public void ProcessName_RecognizesBothPlatformSeparators(string? path, string expected)
    {
        ClashConnectionModel.GetProcessName(path).Should().Be(expected);
    }

    [Fact]
    public async Task Search_MatchesProcessNameAndFullPathWithoutCaseSensitivity()
    {
        await WithConfig(async () =>
        {
            var vm = new ClashConnectionsViewModel();
            var connection = Connection("process", "example.com", ["direct"]);
            connection.metadata!.processPath = @"C:\Program Files\Steam\steam.exe";
            await vm.RefreshConnections([connection, Connection("unknown", "other.example", ["direct"])]);
            vm.HostFilter = "STEAM.EXE";
            vm.ConnectionItems.Select(row => row.Id).Should().Equal("process");
            vm.HostFilter = @"PROGRAM FILES\STEAM";
            vm.ConnectionItems.Select(row => row.Id).Should().Equal("process");
            vm.HostFilter = "firefox";
            vm.ConnectionItems.Should().BeEmpty();
        });
    }

    [Fact]
    public async Task XrayRoutes_DistinguishTransportForwardingDirectAndUnknown()
    {
        await WithConfig(async () =>
        {
            XrayProxyPanelManager.Instance.Activate("""
                {"outbounds":[
                  {"tag":"North","protocol":"socks","settings":{"servers":[{"address":"proxy.example","port":443}]}},
                  {"tag":"East","protocol":"socks","settings":{"servers":[{"address":"shared.example","port":443}]}},
                  {"tag":"West","protocol":"socks","settings":{"servers":[{"address":"shared.example","port":443}]}}
                ]}
                """);
            var vm = new ClashConnectionsViewModel();
            await vm.RefreshConnections([
                Connection("transport", "proxy.example", ["direct"]),
                Connection("ambiguous", "shared.example", ["direct"]),
                Connection("forwarded", "user.example", ["proxy"]),
                Connection("direct", "direct.example", ["direct"]),
                Connection("unknown", "unknown.example", ["other"])
            ]);
            vm.ConnectionItems.Select(row => row.RouteKind).Should().Equal(
                ConnectionRouteKind.ProxyTransport, ConnectionRouteKind.AmbiguousTransport,
                ConnectionRouteKind.ForwardedToXray, ConnectionRouteKind.Direct, ConnectionRouteKind.Unknown);
            vm.TrafficFilter = (int)ConnectionTrafficFilter.User;
            vm.ConnectionItems.Select(row => row.Id).Should().Equal("forwarded", "direct");
            vm.TrafficFilter = (int)ConnectionTrafficFilter.ToProxies;
            vm.ConnectionItems.Select(row => row.Id).Should().Equal("transport", "ambiguous");
            vm.TrafficFilter = (int)ConnectionTrafficFilter.All;
            vm.ConnectionItems.Select(row => row.Id).Should().BeEquivalentTo("transport", "ambiguous", "forwarded", "direct", "unknown");
        });
    }

    [Fact]
    public async Task NativeFilter_MatchesDestinationWithoutInventingXrayTransport()
    {
        await WithConfig(async () =>
        {
            var vm = new ClashConnectionsViewModel { HostFilter = "EXAMPLE" };
            await vm.RefreshConnections([Connection("native", "example.com", ["proxy", "GLOBAL"])]);
            vm.ConnectionItems.Single().RouteKind.Should().Be(ConnectionRouteKind.Native);
            vm.TrafficFilter = (int)ConnectionTrafficFilter.User;
            vm.ConnectionItems.Select(row => row.Id).Should().Equal("native");
            vm.TrafficFilter = (int)ConnectionTrafficFilter.ToProxies;
            vm.ConnectionItems.Should().BeEmpty();
            vm.TrafficFilter = (int)ConnectionTrafficFilter.All;
            vm.HostFilter = "GLOBAL";
            vm.ConnectionItems.Should().BeEmpty();
        });
    }

    [Fact]
    public async Task Refresh_PreservesSelectedIdentityAndUpdatesDetailsWithoutCollectionReset()
    {
        await WithConfig(async () =>
        {
            var vm = new ClashConnectionsViewModel();
            var connection = Connection("selected", "example.com", ["direct"]);
            connection.metadata!.processPath = @"C:\Apps\browser.exe";
            await vm.RefreshConnections([connection, Connection("removed", "old.example", [])]);
            var selected = vm.ConnectionItems[0];
            vm.SelectedSource = selected;
            var changes = new List<NotifyCollectionChangedAction>();
            ((INotifyCollectionChanged)vm.ConnectionItems).CollectionChanged += (_, args) => changes.Add(args.Action);
            var properties = new List<string?>();
            selected.PropertyChanged += (_, args) => properties.Add(args.PropertyName);
            connection.metadata.processPath = "/usr/bin/new-browser";
            connection.metadata.network = "udp";
            await vm.RefreshConnections([connection, Connection("added", "new.example", [])]);
            vm.SelectedSource.Should().BeSameAs(selected);
            vm.ConnectionItems[0].Should().BeSameAs(selected);
            selected.ProcessPath.Should().Be("/usr/bin/new-browser");
            selected.ProcessName.Should().Be("new-browser");
            selected.Network.Should().Be("udp");
            properties.Should().Contain(nameof(ClashConnectionModel.ProcessName));
            changes.Should().NotContain(NotifyCollectionChangedAction.Reset);
            vm.ConnectionItems.Select(row => row.Id).Should().Equal("selected", "added");
            await vm.RefreshConnections([]);
            vm.SelectedSource.Should().BeNull();
        });
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task MissingProcessPath_DisablesCopyWithoutUsingProcessFallback(string? path)
    {
        await WithConfig(async () =>
        {
            var vm = new ClashConnectionsViewModel();
            var connection = Connection("missing-process", "example.com", []);
            connection.metadata!.process = "browser.exe";
            connection.metadata.processPath = path;
            await vm.RefreshConnections([connection]);
            var row = vm.ConnectionItems.Single();
            row.HasProcessPath.Should().BeFalse();
            row.ProcessPath.Should().BeNull();
            row.ProcessName.Should().Be("—");
        });
    }

    [Fact]
    public async Task NewSession_DoesNotPreserveSelectionForReusedConnectionId()
    {
        await WithConfig(async () =>
        {
            var vm = new ClashConnectionsViewModel();
            await vm.RefreshConnections([Connection("reused", "old.example", [])]);
            var oldRow = vm.ConnectionItems.Single();
            vm.SelectedSource = oldRow;
            XrayProxyPanelManager.Instance.Reset();
            await vm.RefreshConnections([Connection("reused", "new.example", [])]);
            vm.SelectedSource.Should().BeNull();
            vm.ConnectionItems.Single().Should().NotBeSameAs(oldRow);
        });
    }

    private static ConnectionItem Connection(string id, string host, List<string> chains) => new()
    {
        id = id,
        metadata = new MetadataItem { host = host, destinationPort = "443", network = "tcp", type = "Mixed" },
        start = DateTime.Now.AddMinutes(-2),
        rule = "MATCH",
        chains = chains
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
