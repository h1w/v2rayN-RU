namespace ServiceLib.ViewModels;

public class ClashConnectionsViewModel : MyReactiveObject
{
    private List<ConnectionItem>? _connections;
    private long _connectionsVersion = -1;
    private readonly Dictionary<string, ClashConnectionModel> _rowsByKey = new(StringComparer.Ordinal);

    public IObservableCollection<ClashConnectionModel> ConnectionItems { get; } = new ObservableCollectionExtended<ClashConnectionModel>();

    [Reactive]
    public ClashConnectionModel? SelectedSource { get; set; }

    public ReactiveCommand<Unit, Unit> ConnectionCloseCmd { get; }
    public ReactiveCommand<Unit, Unit> ConnectionCloseAllCmd { get; }

    [Reactive]
    public string HostFilter { get; set; }

    [Reactive]
    public int TrafficFilter { get; set; }

    [Reactive]
    public bool AutoRefresh { get; set; }

    public ClashConnectionsViewModel()
    {
        _config = AppManager.Instance.Config;
        AutoRefresh = _config.ClashUIItem.ConnectionsAutoRefresh;

        this.WhenAnyValue(x => x.HostFilter, x => x.TrafficFilter)
            .Subscribe(value =>
            {
                _ = RefreshConnections(_connectionsVersion == XrayProxyPanelManager.Instance.Version ? _connections : null);
            });

        var canEditRemove = this.WhenAnyValue(
         x => x.SelectedSource,
         selectedSource => selectedSource != null && selectedSource.Id.IsNotEmpty());

        this.WhenAnyValue(
           x => x.AutoRefresh,
           y => y == true)
               .Subscribe(c => { _config.ClashUIItem.ConnectionsAutoRefresh = AutoRefresh; });
        ConnectionCloseCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await ClashConnectionClose(false);
        }, canEditRemove);

        ConnectionCloseAllCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await ClashConnectionClose(true);
        });

        this.WhenActivated(disposables =>
        {
            var cts = new CancellationTokenSource();
            Disposable.Create(() =>
            {
                cts.Cancel();
                cts.Dispose();
            }).DisposeWith(disposables);

            _ = GetClashConnectionsTask(cts.Token);
        });
    }

    private async Task GetClashConnectionsTask(CancellationToken token = default)
    {
        try
        {
            await GetClashConnections();
            var numOfExecuted = 1;
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(1000 * 5, token);
                numOfExecuted++;
                if (_connectionsVersion != XrayProxyPanelManager.Instance.Version)
                {
                    await Observable.Start(() => RefreshConnections(null), RxSchedulers.MainThreadScheduler);
                    await GetClashConnections();
                    continue;
                }
                if (!(AutoRefresh && AppManager.Instance.ShowInTaskbar &&
                    (XrayProxyPanelManager.Instance.IsActive || AppManager.Instance.IsRunningCore(ECoreType.sing_box))))
                {
                    continue;
                }
                if (_config.ClashUIItem.ConnectionsRefreshInterval <= 0 ||
                    numOfExecuted % _config.ClashUIItem.ConnectionsRefreshInterval != 0)
                {
                    continue;
                }
                await GetClashConnections();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    private async Task GetClashConnections()
    {
        var manager = XrayProxyPanelManager.Instance;
        var version = manager.Version;
        var ret = manager.IsActive || AppManager.Instance.IsRunningCore(ECoreType.sing_box)
            ? await ClashApiManager.Instance.GetClashConnectionsAsync()
            : null;
        await Observable.Start(() =>
        {
            if (version == manager.Version)
            {
                _ = RefreshConnections(ret?.connections);
            }
        }, RxSchedulers.MainThreadScheduler);
    }

    public Task RefreshConnections(List<ConnectionItem>? connections)
    {
        var manager = XrayProxyPanelManager.Instance;
        var version = manager.Version;
        if (_connectionsVersion != version)
        {
            _rowsByKey.Clear();
        }
        _connectionsVersion = version;
        _connections = connections;
        var xrayMode = manager.IsActive;
        var now = DateTime.Now;
        var liveKeys = new HashSet<string>(StringComparer.Ordinal);
        var visibleRows = new List<ClashConnectionModel>();
        foreach (var item in connections ?? [])
        {
            if (item.metadata is not { } metadata) continue;
            var key = item.id.IsNotEmpty() ? item.id! : $"{item.start.Ticks}|{metadata.sourceIP}|{metadata.sourcePort}|{metadata.destinationIP}|{metadata.host}|{metadata.destinationPort}|{metadata.network}";
            if (!liveKeys.Add(key)) continue;
            if (!_rowsByKey.TryGetValue(key, out var model))
            {
                model = new ClashConnectionModel { Id = item.id };
                _rowsByKey.Add(key, model);
            }

            var destination = metadata.host.IsNullOrEmpty() ? metadata.destinationIP : metadata.host;
            var host = $"{destination}:{metadata.destinationPort}";
            if (!string.IsNullOrEmpty(metadata.sniffHost) &&
                !string.Equals(destination, metadata.sniffHost, StringComparison.OrdinalIgnoreCase))
            {
                host += $" ({metadata.sniffHost})";
            }
            var names = xrayMode ? manager.MatchTags(item) : [];
            var rawRoute = $"{item.rule} , {string.Join("->", item.chains ?? [])}";
            model.RouteKind = ClassifyRoute(item, xrayMode, names.Count);
            model.Chain = model.RouteKind switch
            {
                ConnectionRouteKind.ProxyTransport => string.Format(ResUI.ConnectionsRouteToProxy, names[0]),
                ConnectionRouteKind.AmbiguousTransport => string.Format(ResUI.ConnectionsRouteAmbiguous, string.Join(" / ", names)),
                ConnectionRouteKind.ForwardedToXray => ResUI.ConnectionsRouteForwarded,
                ConnectionRouteKind.Direct => ResUI.ConnectionsRouteDirect,
                ConnectionRouteKind.Unknown => ResUI.ConnectionsRouteUnknown,
                _ => rawRoute
            };
            model.RawRoute = item.rulePayload.IsNullOrEmpty() ? rawRoute : $"{rawRoute} ({item.rulePayload})";
            model.Network = metadata.network;
            model.Type = metadata.type;
            model.Host = host;
            var age = now - item.start;
            if (age < TimeSpan.Zero) age = TimeSpan.Zero;
            model.Time = age.TotalSeconds;
            model.Elapsed = age.TotalDays >= 1 ? age.ToString(@"d\.hh\:mm\:ss") : age.ToString(@"hh\:mm\:ss");
            model.ProcessPath = string.IsNullOrWhiteSpace(metadata.processPath) ? null : metadata.processPath;
            model.ProcessName = ClashConnectionModel.GetProcessName(model.ProcessPath);
            model.HasProcessPath = model.ProcessPath != null;

            if (!model.MatchesTrafficFilter((ConnectionTrafficFilter)TrafficFilter)) continue;
            if (HostFilter.IsNotEmpty() &&
                !host.Contains(HostFilter, StringComparison.OrdinalIgnoreCase) &&
                !(model.ProcessPath?.Contains(HostFilter, StringComparison.OrdinalIgnoreCase) ?? false) &&
                !(metadata.process?.Contains(HostFilter, StringComparison.OrdinalIgnoreCase) ?? false) &&
                !names.Any(name => name.Contains(HostFilter, StringComparison.OrdinalIgnoreCase))) continue;
            visibleRows.Add(model);
        }

        if (version != manager.Version)
        {
            return RefreshConnections(null);
        }
        foreach (var key in _rowsByKey.Keys.Where(key => !liveKeys.Contains(key)).ToArray())
        {
            _rowsByKey.Remove(key);
        }
        var visible = visibleRows.ToHashSet();
        if (SelectedSource != null && !visible.Contains(SelectedSource)) SelectedSource = null;
        // Keep surviving rows in place: no reset/replacement, preserving selection and scroll anchor.
        for (var i = ConnectionItems.Count - 1; i >= 0; i--)
        {
            if (!visible.Contains(ConnectionItems[i])) ConnectionItems.RemoveAt(i);
        }
        var existing = ConnectionItems.ToHashSet();
        foreach (var row in visibleRows)
        {
            if (existing.Add(row)) ConnectionItems.Add(row);
        }
        return Task.CompletedTask;
    }

    private static ConnectionRouteKind ClassifyRoute(ConnectionItem item, bool xrayMode, int matches)
    {
        if (!xrayMode) return ConnectionRouteKind.Native;
        if (matches > 1) return ConnectionRouteKind.AmbiguousTransport;
        if (matches == 1) return ConnectionRouteKind.ProxyTransport;
        if (item.chains?.Any(chain => string.Equals(chain, "proxy", StringComparison.OrdinalIgnoreCase)) == true)
            return ConnectionRouteKind.ForwardedToXray;
        if (item.chains?.Any(chain => string.Equals(chain, "direct", StringComparison.OrdinalIgnoreCase)) == true)
            return ConnectionRouteKind.Direct;
        return ConnectionRouteKind.Unknown;
    }

    public async Task ClashConnectionClose(bool all)
    {
        var id = string.Empty;
        if (!all)
        {
            var item = SelectedSource;
            if (item is null)
            {
                return;
            }
            id = item.Id;
        }
        await ClashApiManager.Instance.ClashConnectionClose(id);
        await GetClashConnections();
    }
}
