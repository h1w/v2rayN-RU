namespace ServiceLib.ViewModels;

public class ClashConnectionsViewModel : MyReactiveObject
{
    public IObservableCollection<ClashConnectionModel> ConnectionItems { get; } = new ObservableCollectionExtended<ClashConnectionModel>();

    [Reactive]
    public ClashConnectionModel SelectedSource { get; set; }

    public ReactiveCommand<Unit, Unit> ConnectionCloseCmd { get; }
    public ReactiveCommand<Unit, Unit> ConnectionCloseAllCmd { get; }

    [Reactive]
    public string HostFilter { get; set; }

    [Reactive]
    public bool AutoRefresh { get; set; }

    public ClashConnectionsViewModel()
    {
        _config = AppManager.Instance.Config;
        AutoRefresh = _config.ClashUIItem.ConnectionsAutoRefresh;

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
        var numOfExecuted = 1;
        while (!token.IsCancellationRequested)
        {
            await Task.Delay(1000 * 5, token);
            numOfExecuted++;
            if (!(AutoRefresh && AppManager.Instance.ShowInTaskbar && AppManager.Instance.IsRunningCore(ECoreType.sing_box)))
            {
                continue;
            }

            if (_config.ClashUIItem.ConnectionsRefreshInterval <= 0)
            {
                continue;
            }

            if (numOfExecuted % _config.ClashUIItem.ConnectionsRefreshInterval != 0)
            {
                continue;
            }
            await GetClashConnections();
        }
    }

    private async Task GetClashConnections()
    {
        var ret = await ClashApiManager.Instance.GetClashConnectionsAsync();
        if (ret == null)
        {
            return;
        }

        RxSchedulers.MainThreadScheduler.Schedule(ret?.connections, (scheduler, model) =>
        {
            _ = RefreshConnections(model);
            return Disposable.Empty;
        });
    }

    public async Task RefreshConnections(List<ConnectionItem>? connections)
    {
        ConnectionItems.Clear();

        var dtNow = DateTime.Now;
        var lstModel = new List<ClashConnectionModel>();
        foreach (var item in connections ?? [])
        {
            if (item.metadata == null)
            {
                continue;
            }
            var dest = item.metadata.host.IsNullOrEmpty() ? item.metadata.destinationIP : item.metadata.host;
            var hostSb = new StringBuilder();
            hostSb.Append(dest);
            hostSb.Append($":{item.metadata.destinationPort}");
            if (!string.IsNullOrEmpty(item.metadata.sniffHost) &&
                dest?.Equals(item.metadata.sniffHost, StringComparison.OrdinalIgnoreCase) == false)
            {
                hostSb.Append($" ({item.metadata.sniffHost})");
            }
            var host = hostSb.ToString();
            if (HostFilter.IsNotEmpty() && !host.Contains(HostFilter))
            {
                continue;
            }

            var model = new ClashConnectionModel
            {
                Id = item.id,
                Network = item.metadata.network,
                Type = item.metadata.type,
                Host = host,
                Time = (dtNow - item.start).TotalSeconds < 0 ? 1 : (dtNow - item.start).TotalSeconds,
                Elapsed = (dtNow - item.start).ToString(@"hh\:mm\:ss"),
                Chain = $"{item.rule} , {string.Join("->", item.chains ?? [])}",
                ProcessPath = item.metadata.processPath,
            };

            lstModel.Add(model);
        }
        if (lstModel.Count <= 0)
        {
            return;
        }

        ConnectionItems.AddRange(lstModel);
        await Task.CompletedTask;
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
        else
        {
            ConnectionItems.Clear();
        }
        await ClashApiManager.Instance.ClashConnectionClose(id);
        await GetClashConnections();
    }
}
