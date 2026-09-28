using System.Reactive.Concurrency;
using static ServiceLib.Models.Dto.ClashProviders;
using static ServiceLib.Models.Dto.ClashProxies;

namespace ServiceLib.ViewModels;

public class ClashProxiesViewModel : MyReactiveObject
{
    private Dictionary<string, ProxiesItem>? _proxies;
    private Dictionary<string, ProvidersItem>? _providers;
    private readonly int _delayTimeout = 99999999;
    private readonly SemaphoreSlim _reloadLock = new(1, 1);
    private long _displayVersion = -1;
    private readonly Dictionary<string, XrayProxyRow> _xrayRows = new(StringComparer.Ordinal);
    private readonly List<XrayProxyRow> _xrayOrder = [];

    public ObservableCollectionExtended<XrayProxyRow> XrayServiceProxies { get; } = new();

    [Reactive] public string XraySearch { get; set; } = string.Empty;
    [Reactive] public int XrayConnectionFilter { get; set; }
    [Reactive] public XrayProxyRow? SelectedXrayProxy { get; set; }

    public ObservableCollectionExtended<XrayProxyRow> XrayProxies { get; } = new();

    [Reactive]
    public bool IsXrayMode { get; private set; }

    public IObservableCollection<ClashProxyModel> ProxyGroups { get; } = new ObservableCollectionExtended<ClashProxyModel>();
    public IObservableCollection<ClashProxyModel> ProxyDetails { get; } = new ObservableCollectionExtended<ClashProxyModel>();

    [Reactive]
    public ClashProxyModel SelectedGroup { get; set; }

    [Reactive]
    public ClashProxyModel SelectedDetail { get; set; }

    public ReactiveCommand<Unit, Unit> ProxiesReloadCmd { get; }
    public ReactiveCommand<Unit, Unit> ProxiesDelayTestCmd { get; }
    public ReactiveCommand<Unit, Unit> ProxiesDelayTestPartCmd { get; }
    public ReactiveCommand<Unit, Unit> ProxiesSelectActivityCmd { get; }

    [Reactive]
    public int RuleModeSelected { get; set; }

    [Reactive]
    public int SortingSelected { get; set; }

    [Reactive]
    public bool AutoRefresh { get; set; }

    public ClashProxiesViewModel()
    {
        _config = AppManager.Instance.Config;
        IsXrayMode = XrayProxyPanelManager.Instance.IsActive;

        ProxiesReloadCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await ProxiesReload();
        });
        ProxiesDelayTestCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await ProxiesDelayTest(true);
        });

        ProxiesDelayTestPartCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await ProxiesDelayTest(false);
        });
        ProxiesSelectActivityCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await SetActiveProxy();
        });

        SelectedGroup = new();
        SelectedDetail = new();
        AutoRefresh = _config.ClashUIItem.ProxiesAutoRefresh;
        SortingSelected = _config.ClashUIItem.ProxiesSorting;
        RuleModeSelected = (int)_config.ClashUIItem.RuleMode;

        #region WhenAnyValue && ReactiveCommand

        this.WhenAnyValue(
           x => x.SelectedGroup,
           y => y != null && y.Name.IsNotEmpty())
               .Subscribe(RefreshProxyDetails);

        this.WhenAnyValue(
           x => x.RuleModeSelected,
           y => y >= 0)
               .Subscribe(async c => await DoRuleModeSelected(c));

        this.WhenAnyValue(
           x => x.SortingSelected,
           y => y >= 0)
              .Subscribe(DoSortingSelected);

        this.WhenAnyValue(
        x => x.AutoRefresh,
        y => y == true)
            .Subscribe(c => { _config.ClashUIItem.ProxiesAutoRefresh = AutoRefresh; });

        this.WhenAnyValue(x => x.XraySearch, x => x.XrayConnectionFilter)
            .Subscribe(_ => ApplyXrayFilters());

        #endregion WhenAnyValue && ReactiveCommand

        this.WhenActivated(disposables =>
        {
            var cts = new CancellationTokenSource();
            Disposable.Create(() =>
            {
                cts.Cancel();
                cts.Dispose();
            }).DisposeWith(disposables);

            _ = GetClashProxiesTask(cts.Token);
        });
    }

    private async Task GetClashProxiesTask(CancellationToken token = default)
    {
        try
        {
            await ProxiesReload(token);
            var numOfExecuted = 1;
            while (!token.IsCancellationRequested)
            {
                var manager = XrayProxyPanelManager.Instance;
                var inventoryTick = manager.IsActive;
                await Task.Delay(TimeSpan.FromSeconds(inventoryTick ? 1 : 5), token);
                if (!inventoryTick) numOfExecuted++;
                if (_displayVersion != manager.Version || IsXrayMode != manager.IsActive)
                {
                    await ProxiesReload(token);
                    continue;
                }
                if (!(AutoRefresh && AppManager.Instance.ShowInTaskbar &&
                    (manager.IsActive || AppManager.Instance.IsRunningCore(ECoreType.sing_box))))
                {
                    continue;
                }
                // The configured interval counts five-second native list ticks, not traffic samples.
                if (!manager.IsActive && (inventoryTick ||
                    _config.ClashUIItem.ProxiesRefreshInterval <= 0 ||
                    numOfExecuted % _config.ClashUIItem.ProxiesRefreshInterval != 0))
                {
                    continue;
                }
                await ProxiesReload(token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    private async Task DoRuleModeSelected(bool c)
    {
        if (!c || XrayProxyPanelManager.Instance.IsActive)
        {
            return;
        }
        if (_config.ClashUIItem.RuleMode == (ERuleMode)RuleModeSelected)
        {
            return;
        }
        await SetRuleModeCheck((ERuleMode)RuleModeSelected);
    }

    public async Task SetRuleModeCheck(ERuleMode mode)
    {
        if (XrayProxyPanelManager.Instance.IsActive || _config.ClashUIItem.RuleMode == mode)
        {
            return;
        }
        await SetRuleMode(mode);
    }

    private void DoSortingSelected(bool c)
    {
        if (!c || XrayProxyPanelManager.Instance.IsActive)
        {
            return;
        }
        if (SortingSelected != _config.ClashUIItem.ProxiesSorting)
        {
            _config.ClashUIItem.ProxiesSorting = SortingSelected;
        }

        RefreshProxyDetails(c);
    }

    public Task ProxiesReload() => ProxiesReload(CancellationToken.None);

    private async Task ProxiesReload(CancellationToken token)
    {
        var manager = XrayProxyPanelManager.Instance;
        // Coalesce timer/manual reloads rather than letting slow requests build a queue.
        if (!await _reloadLock.WaitAsync(0, token)) return;
        try
        {
            var version = manager.Version;
            await Observable.Start(() =>
            {
                token.ThrowIfCancellationRequested();
                UpdatePanelMode();
            }, RxSchedulers.MainThreadScheduler);
            if (manager.IsActive)
            {
                var connections = await ClashApiManager.Instance.GetClashConnectionsAsync();
                token.ThrowIfCancellationRequested();
                if (version != manager.Version || !manager.IsActive)
                {
                    return;
                }
                var rows = await Task.Run(() => manager.GetRows(connections?.connections, DateTimeOffset.UtcNow, version));
                await Observable.Start(() =>
                {
                    token.ThrowIfCancellationRequested();
                    if (version != manager.Version || !manager.IsActive)
                    {
                        UpdatePanelMode();
                        return;
                    }
                    RefreshXrayProxies(rows);
                }, RxSchedulers.MainThreadScheduler);
                return;
            }
            if (AppManager.Instance.IsRunningCore(ECoreType.sing_box))
            {
                await GetClashProxies(true);
                if (!token.IsCancellationRequested && version == manager.Version && !manager.IsActive)
                {
                    await ProxiesDelayTest();
                }
            }
        }
        finally
        {
            _reloadLock.Release();
        }
    }
    public void RefreshXrayProxies(IReadOnlyList<XrayProxyRow> rows)
    {
        var keys = new HashSet<string>(rows.Select(row => row.Key), StringComparer.Ordinal);
        foreach (var key in _xrayRows.Keys.Where(key => !keys.Contains(key)).ToArray())
            _xrayRows.Remove(key);
        _xrayOrder.Clear();
        foreach (var row in rows)
        {
            if (_xrayRows.TryGetValue(row.Key, out var existing)) existing.UpdateTraffic(row);
            else _xrayRows.Add(row.Key, row);
            _xrayOrder.Add(_xrayRows[row.Key]);
        }
        ApplyXrayFilters();
    }

    private void ApplyXrayFilters()
    {
        var search = XraySearch.Trim();
        var rows = _xrayOrder.Where(row =>
            (XrayConnectionFilter != 1 || row.ConnectionCount > 0) &&
            (search.Length == 0 || row.Tag.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                row.Endpoint.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                row.Protocol.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                row.Groups.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                row.Chain.Contains(search, StringComparison.OrdinalIgnoreCase))).ToArray();
        Reconcile(XrayProxies, rows.Where(row => !row.IsService).ToArray());
        Reconcile(XrayServiceProxies, rows.Where(row => row.IsService).ToArray());
        if (SelectedXrayProxy != null && !rows.Contains(SelectedXrayProxy)) SelectedXrayProxy = null;
    }

    private static void Reconcile(ObservableCollectionExtended<XrayProxyRow> target, IReadOnlyList<XrayProxyRow> rows)
    {
        var visible = new HashSet<XrayProxyRow>(rows);
        for (var index = target.Count - 1; index >= 0; index--)
            if (!visible.Contains(target[index])) target.RemoveAt(index);
        for (var index = 0; index < rows.Count; index++)
        {
            if (index < target.Count && ReferenceEquals(target[index], rows[index])) continue;
            var oldIndex = target.IndexOf(rows[index]);
            if (oldIndex >= 0) target.Move(oldIndex, index);
            else target.Insert(index, rows[index]);
        }
    }


    private void UpdatePanelMode()
    {
        var manager = XrayProxyPanelManager.Instance;
        var version = manager.Version;
        var active = manager.IsActive;
        if (_displayVersion == version && IsXrayMode == active)
        {
            return;
        }
        _displayVersion = version;
        IsXrayMode = active;
        _proxies = null;
        _providers = null;
        ProxyGroups.Clear();
        ProxyDetails.Clear();
        XrayProxies.Clear();
        XrayServiceProxies.Clear();
        _xrayRows.Clear();
        _xrayOrder.Clear();
        SelectedXrayProxy = null;
        SelectedGroup = new();
        SelectedDetail = new();
        RuleModeSelected = (int)_config.ClashUIItem.RuleMode;
        SortingSelected = _config.ClashUIItem.ProxiesSorting;
    }

    #region proxy function

    private async Task SetRuleMode(ERuleMode mode)
    {
        if (XrayProxyPanelManager.Instance.IsActive)
        {
            return;
        }
        _config.ClashUIItem.RuleMode = mode;

        if (mode != ERuleMode.Unchanged)
        {
            Dictionary<string, string> headers = new()
                {
                    { "mode", mode.ToString().ToLower() }
                };
            await ClashApiManager.Instance.ClashConfigUpdate(headers);
        }
    }

    private async Task GetClashProxies(bool refreshUI)
    {
        var manager = XrayProxyPanelManager.Instance;
        var version = manager.Version;
        if (manager.IsActive)
        {
            return;
        }
        var ret = await ClashApiManager.Instance.GetClashProxiesAsync();
        if (version != manager.Version || manager.IsActive || ret?.Item1 == null || ret.Item2 == null)
        {
            return;
        }
        _proxies = ret.Item1.proxies;
        _providers = ret?.Item2.providers;

        if (refreshUI)
        {
            RxSchedulers.MainThreadScheduler.Schedule(() =>
            {
                if (version == manager.Version && !manager.IsActive)
                {
                    _ = RefreshProxyGroups();
                }
            });
        }
    }

    public async Task RefreshProxyGroups()
    {
        if (XrayProxyPanelManager.Instance.IsActive || _proxies == null)
        {
            return;
        }

        var selectedName = SelectedGroup?.Name;
        ProxyGroups.Clear();

        var lstProxyGroups = new List<ClashProxyModel>();
        var proxyGroups = ClashApiManager.Instance.GetClashProxyGroups();
        if (proxyGroups is { Count: > 0 })
        {
            foreach (var it in proxyGroups)
            {
                if (it.name.IsNullOrEmpty() || !_proxies.TryGetValue(it.name, out var item))
                {
                    continue;
                }
                if (!Global.allowSelectType.Contains(item.type.ToLower()))
                {
                    continue;
                }
                lstProxyGroups.Add(new ClashProxyModel()
                {
                    Now = item.now,
                    Name = item.name,
                    Type = item.type
                });
            }
        }

        //from api
        foreach (var kv in _proxies)
        {
            if (!Global.allowSelectType.Contains(kv.Value.type?.ToLower()))
            {
                continue;
            }
            if (kv.Key == "GLOBAL")
            {
                continue;
            }
            var item = lstProxyGroups.FirstOrDefault(t => t.Name == kv.Key);
            if (item != null && item.Name.IsNotEmpty())
            {
                continue;
            }
            lstProxyGroups.Add(new ClashProxyModel()
            {
                Now = kv.Value.now,
                Name = kv.Key,
                Type = kv.Value.type,
            });
        }
        if (_proxies.TryGetValue("GLOBAL", out var globalProxy))
        {
            lstProxyGroups.Add(new ClashProxyModel()
            {
                Now = globalProxy.now,
                Name = "GLOBAL",
                Type = globalProxy.type,
            });
        }

        ProxyGroups.AddRange(lstProxyGroups);

        if (ProxyGroups is { Count: > 0 })
        {
            if (selectedName != null && ProxyGroups.Any(t => t.Name == selectedName))
            {
                SelectedGroup = ProxyGroups.FirstOrDefault(t => t.Name == selectedName);
            }
            else
            {
                SelectedGroup = ProxyGroups.First();
            }
        }
        else
        {
            SelectedGroup = new();
        }
        await Task.CompletedTask;
    }

    private void RefreshProxyDetails(bool c)
    {
        ProxyDetails.Clear();
        if (!c || XrayProxyPanelManager.Instance.IsActive)
        {
            return;
        }
        var name = SelectedGroup?.Name;
        if (name.IsNullOrEmpty())
        {
            return;
        }
        if (_proxies == null)
        {
            return;
        }

        _proxies.TryGetValue(name, out var proxy);
        if (proxy?.all == null)
        {
            return;
        }
        var lstDetails = new List<ClashProxyModel>();
        foreach (var item in proxy.all)
        {
            var proxy2 = TryGetProxy(item);
            if (proxy2 == null)
            {
                continue;
            }
            var delay = proxy2.history?.Count > 0 ? proxy2.history.Last().delay : -1;

            lstDetails.Add(new ClashProxyModel()
            {
                IsActive = item == proxy.now,
                Name = item,
                Type = proxy2.type,
                Delay = delay <= 0 ? _delayTimeout : delay,
                DelayName = delay <= 0 ? string.Empty : $"{delay}ms",
            });
        }
        //sort
        switch (SortingSelected)
        {
            case 0:
                lstDetails = lstDetails.OrderBy(t => t.Delay).ToList();
                break;

            case 1:
                lstDetails = lstDetails.OrderBy(t => t.Name).ToList();
                break;

            default:
                break;
        }
        ProxyDetails.AddRange(lstDetails);
    }

    private ProxiesItem? TryGetProxy(string name)
    {
        if (_proxies == null)
        {
            return null;
        }
        _proxies.TryGetValue(name, out var proxy2);
        if (proxy2 != null)
        {
            return proxy2;
        }
        //from providers
        if (_providers != null)
        {
            foreach (var kv in _providers)
            {
                if (Global.proxyVehicleType.Contains(kv.Value.vehicleType.ToLower()))
                {
                    var proxy3 = kv.Value.proxies.FirstOrDefault(t => t.name == name);
                    if (proxy3 != null)
                    {
                        return proxy3;
                    }
                }
            }
        }
        return null;
    }

    public async Task SetActiveProxy()
    {
        if (XrayProxyPanelManager.Instance.IsActive)
        {
            return;
        }
        if (SelectedGroup == null || SelectedGroup.Name.IsNullOrEmpty())
        {
            return;
        }
        if (SelectedDetail == null || SelectedDetail.Name.IsNullOrEmpty())
        {
            return;
        }
        var name = SelectedGroup.Name;
        if (name.IsNullOrEmpty())
        {
            return;
        }
        var nameNode = SelectedDetail.Name;
        if (nameNode.IsNullOrEmpty())
        {
            return;
        }
        var selectedProxy = TryGetProxy(name);
        if (selectedProxy == null || selectedProxy.type != "Selector")
        {
            NoticeManager.Instance.Enqueue(ResUI.OperationFailed);
            return;
        }

        await ClashApiManager.Instance.ClashSetActiveProxy(name, nameNode);

        selectedProxy.now = nameNode;
        var group = ProxyGroups.FirstOrDefault(it => it.Name == SelectedGroup.Name);
        if (group != null)
        {
            group.Now = nameNode;
            var group2 = JsonUtils.DeepCopy(group);
            ProxyGroups.Replace(group, group2);

            SelectedGroup = group2;
        }
        NoticeManager.Instance.Enqueue(ResUI.OperationSuccess);
    }

    private async Task ProxiesDelayTest(bool blAll = true)
    {
        if (XrayProxyPanelManager.Instance.IsActive)
        {
            return;
        }
        ClashApiManager.Instance.ClashProxiesDelayTest(blAll, ProxyDetails.ToList(), async (item, result) =>
        {
            if (item == null || result.IsNullOrEmpty())
            {
                return;
            }

            var model = new SpeedTestResult() { IndexId = item.Name, Delay = result };
            RxSchedulers.MainThreadScheduler.Schedule(model, (scheduler, model) =>
            {
                _ = ProxiesDelayTestResult(model);
                return Disposable.Empty;
            });
            await Task.CompletedTask;
        });
        await Task.CompletedTask;
    }

    public async Task ProxiesDelayTestResult(SpeedTestResult result)
    {
        if (XrayProxyPanelManager.Instance.IsActive)
        {
            return;
        }
        var detail = ProxyDetails.FirstOrDefault(it => it.Name == result.IndexId);
        if (detail == null)
        {
            return;
        }

        var dicResult = JsonUtils.Deserialize<Dictionary<string, object>>(result.Delay);
        if (dicResult != null && dicResult.TryGetValue("delay", out var value))
        {
            detail.Delay = Convert.ToInt32(value.ToString());
            detail.DelayName = $"{detail.Delay}ms";
        }
        else if (dicResult != null && dicResult.TryGetValue("message", out var value1))
        {
            detail.Delay = _delayTimeout;
            detail.DelayName = $"{value1}";
        }
        else
        {
            detail.Delay = _delayTimeout;
            detail.DelayName = string.Empty;
        }
        await Task.CompletedTask;
    }

    #endregion proxy function
}
