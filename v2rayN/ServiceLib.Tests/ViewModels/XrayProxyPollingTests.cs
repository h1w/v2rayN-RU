using ReactiveUI.Builder;
using ServiceLib.Tests.CoreConfig;
using ServiceLib.ViewModels;
using Xunit;

namespace ServiceLib.Tests.ViewModels;

[Collection("Xray proxy panels")]
public class XrayProxyPollingTests
{
    static XrayProxyPollingTests() => RxAppBuilder.CreateReactiveUIBuilder().WithCoreServices().BuildApp();

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(60)]
    public async Task ActivatedInventory_RefreshesStableRowsWithoutNativeListInterval(int nativeInterval)
    {
        var app = AppManager.Instance;
        var previousConfig = app.Config;
        var previousVisibility = app.ShowInTaskbar;
        var portField = typeof(AppManager).GetField("_statePort2", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var previousPort = portField.GetValue(app);
        using var listener = new HttpListener();
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        IDisposable? activation = null;
        try
        {
            var config = CoreConfigTestFactory.CreateConfig();
            config.ClashUIItem.ProxiesRefreshInterval = nativeInterval;
            CoreConfigTestFactory.BindAppManagerConfig(config);
            portField.SetValue(app, port);
            app.ShowInTaskbar = true;
            XrayProxyPanelManager.Instance.Activate("""
                {"outbounds":[{"tag":"North","protocol":"socks","settings":{"servers":[{"address":"proxy.example","port":443}]}}]}
                """);
            var vm = new ClashProxiesViewModel { AutoRefresh = true };
            activation = vm.Activator.Activate();
            await Respond(listener, 100, 200);
            await WaitUntil(() => vm.XrayProxies.Count == 1);
            var row = vm.XrayProxies.Single();
            Assert.Equal("200 B", row.Download);
            vm.SelectedXrayProxy = row;
            await Respond(listener, 200, 400);
            await WaitUntil(() => row.Download == "400 B");
            Assert.Same(row, vm.XrayProxies.Single());
            Assert.Same(row, vm.SelectedXrayProxy);
            Assert.NotEqual("—", row.DownloadRate);
            var pending = await listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(4), TestContext.Current.CancellationToken);
            // A slow in-flight request must not accumulate manual or timer refreshes.
            await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => vm.ProxiesReload()))
                .WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            activation.Dispose();
            await Respond(pending, 300, 600);
            var extraRequest = listener.GetContextAsync();
            Assert.NotSame(extraRequest, await Task.WhenAny(extraRequest, Task.Delay(1300, TestContext.Current.CancellationToken)));
            Assert.Equal("400 B", row.Download);
        }
        finally
        {
            activation?.Dispose();
            XrayProxyPanelManager.Instance.Reset();
            app.ShowInTaskbar = previousVisibility;
            portField.SetValue(app, previousPort);
            CoreConfigTestFactory.BindAppManagerConfig(previousConfig);
        }
    }

    private static async Task Respond(HttpListener listener, long upload, long download)
    {
        var context = await listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(4), TestContext.Current.CancellationToken);
        await Respond(context, upload, download);
    }

    private static async Task Respond(HttpListenerContext context, long upload, long download)
    {
        Assert.Equal("/connections", context.Request.Url!.AbsolutePath);
        var bytes = Encoding.UTF8.GetBytes($$"""
            {"connections":[{"id":"live","upload":{{upload}},"download":{{download}},"start":"2026-09-28T00:00:00Z","metadata":{"host":"proxy.example","destinationPort":"443","network":"tcp","type":"Mixed"},"chains":["proxy"],"rule":"MATCH"}]}
            """);
        context.Response.ContentType = "application/json";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes, TestContext.Current.CancellationToken);
        context.Response.Close();
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }
}
