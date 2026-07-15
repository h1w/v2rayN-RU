namespace ServiceLib.ViewModels;

public class RoutingSettingViewModel : MyReactiveObject
{
    public Interaction<string, bool> ShowYesNoInteraction { get; } = new();
    public Interaction<string, Unit> SetClipboardDataInteraction { get; } = new();
    public Interaction<Unit, string?> ReadTextFromClipboardInteraction { get; } = new();
    public Interaction<Unit, string?> BrowseRoutingFileInteraction { get; } = new();
    public Interaction<string, string?> SaveRoutingFileInteraction { get; } = new();

    #region Reactive

    public IObservableCollection<RoutingItemModel> RoutingItems { get; } = new ObservableCollectionExtended<RoutingItemModel>();

    [Reactive]
    public RoutingItemModel SelectedSource { get; set; }

    public IList<RoutingItemModel> SelectedSources { get; set; }

    [Reactive]
    public string DomainStrategy { get; set; }

    [Reactive]
    public string DomainStrategy4Singbox { get; set; }

    public ReactiveCommand<Unit, Unit> RoutingAdvancedAddCmd { get; }
    public ReactiveCommand<Unit, Unit> RoutingAdvancedRemoveCmd { get; }
    public ReactiveCommand<Unit, Unit> RoutingAdvancedSetDefaultCmd { get; }
    public ReactiveCommand<Unit, Unit> RoutingAdvancedImportRulesCmd { get; }
    public ReactiveCommand<Unit, Unit> ExportRoutingToClipboardCmd { get; }
    public ReactiveCommand<Unit, Unit> ExportRoutingToFileCmd { get; }
    public ReactiveCommand<Unit, Unit> ImportRoutingFromFileCmd { get; }
    public ReactiveCommand<Unit, Unit> ImportRoutingFromClipboardCmd { get; }

    public bool IsModified { get; set; }

    #endregion Reactive

    public RoutingSettingViewModel()
    {
        _config = AppManager.Instance.Config;

        var canEditRemove = this.WhenAnyValue(
            x => x.SelectedSource,
            selectedSource => selectedSource != null && !selectedSource.Remarks.IsNullOrEmpty());

        RoutingAdvancedAddCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await RoutingAdvancedEditAsync(true);
        });
        RoutingAdvancedRemoveCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await RoutingAdvancedRemoveAsync();
        }, canEditRemove);
        RoutingAdvancedSetDefaultCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await RoutingAdvancedSetDefault();
        }, canEditRemove);
        RoutingAdvancedImportRulesCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await RoutingAdvancedImportRules();
        });
        ExportRoutingToClipboardCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await ExportRoutingToClipboard();
        }, canEditRemove);
        ExportRoutingToFileCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await ExportRoutingToFile();
        }, canEditRemove);
        ImportRoutingFromFileCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            var fileName = await BrowseRoutingFileInteraction.Handle(Unit.Default);
            await ImportRoutingFromData(fileName.IsNullOrEmpty() ? null : await File.ReadAllTextAsync(fileName));
        });
        ImportRoutingFromClipboardCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            var data = await ReadTextFromClipboardInteraction.Handle(Unit.Default);
            await ImportRoutingFromData(data);
        });

        _ = Init();

        // Auto-save DomainStrategy when changed
        this.WhenAnyValue(
            x => x.DomainStrategy,
            x => x.DomainStrategy4Singbox)
            .Skip(1)
            .DistinctUntilChanged()
            .Subscribe(x =>
            {
                IsModified = true;
                _ = SaveSettingsAsync();
            });
    }

    private async Task Init()
    {
        SelectedSource = new();

        DomainStrategy = _config.RoutingBasicItem.DomainStrategy;
        DomainStrategy4Singbox = _config.RoutingBasicItem.DomainStrategy4Singbox;

        await ConfigHandler.InitBuiltinRouting(_config);
        await RefreshRoutingItems();
    }

    #region Refresh Save

    public async Task RefreshRoutingItems()
    {
        RoutingItems.Clear();
        var models = new List<RoutingItemModel>();

        var routings = await AppManager.Instance.RoutingItems();
        foreach (var item in routings)
        {
            var it = new RoutingItemModel()
            {
                IsActive = item.IsActive,
                RuleNum = item.RuleNum,
                Id = item.Id,
                Remarks = item.Remarks,
                Url = item.Url,
                CustomIcon = item.CustomIcon,
                CustomRulesetPath4Singbox = item.CustomRulesetPath4Singbox,
                Sort = item.Sort,
            };
            models.Add(it);
        }
        RoutingItems.AddRange(models);
    }

    /// <summary>
    /// Save DomainStrategy settings
    /// </summary>
    public async Task SaveSettingsAsync()
    {
        _config.RoutingBasicItem.DomainStrategy = DomainStrategy;
        _config.RoutingBasicItem.DomainStrategy4Singbox = DomainStrategy4Singbox;
        await ConfigHandler.SaveConfig(_config);
    }

    #endregion Refresh Save

    public async Task RoutingAdvancedEditAsync(bool blNew)
    {
        RoutingItem item;
        if (blNew)
        {
            item = new();
        }
        else
        {
            item = await AppManager.Instance.GetRoutingItem(SelectedSource?.Id);
            if (item is null)
            {
                return;
            }
        }
        var routingRuleSettingViewModel = new RoutingRuleSettingViewModel(item);
        if (await AppManager.Instance.WindowDialog.ShowDialogAsync(routingRuleSettingViewModel) == true)
        {
            await RefreshRoutingItems();
            IsModified = true;
        }
    }

    public async Task RoutingAdvancedRemoveAsync()
    {
        if (SelectedSource is null || SelectedSource.Remarks.IsNullOrEmpty())
        {
            NoticeManager.Instance.Enqueue(ResUI.PleaseSelectRules);
            return;
        }
        if (await ShowYesNoInteraction.Handle(ResUI.RemoveServer) == false)
        {
            return;
        }
        foreach (var it in SelectedSources ?? [SelectedSource])
        {
            var item = await AppManager.Instance.GetRoutingItem(it?.Id);
            if (item != null)
            {
                await ConfigHandler.RemoveRoutingItem(item);
            }
        }

        await RefreshRoutingItems();
        IsModified = true;
    }

    public async Task RoutingAdvancedSetDefault()
    {
        var item = await AppManager.Instance.GetRoutingItem(SelectedSource?.Id);
        if (item is null)
        {
            NoticeManager.Instance.Enqueue(ResUI.PleaseSelectRules);
            return;
        }

        if (await ConfigHandler.SetDefaultRouting(_config, item) == 0)
        {
            await RefreshRoutingItems();
            IsModified = true;
        }
    }

    private async Task RoutingAdvancedImportRules()
    {
        if (await ConfigHandler.InitRouting(_config, true) == 0)
        {
            await RefreshRoutingItems();
            IsModified = true;
        }
    }

    private async Task<List<RoutingItem>> GetSelectedRoutingItems()
    {
        var result = new List<RoutingItem>();
        var sources = SelectedSources ?? [SelectedSource];
        foreach (var model in sources)
        {
            if (model?.Id.IsNullOrEmpty() != false)
            {
                continue;
            }
            var item = await AppManager.Instance.GetRoutingItem(model.Id);
            if (item != null)
            {
                result.Add(item);
            }
        }
        return result;
    }

    private async Task ExportRoutingToClipboard()
    {
        var items = await GetSelectedRoutingItems();
        if (items.Count == 0)
        {
            NoticeManager.Instance.Enqueue(ResUI.PleaseSelectRules);
            return;
        }
        await SetClipboardDataInteraction.Handle(RoutingRuleExporter.ExportRoutingTemplateToJson(items));
    }

    private async Task ExportRoutingToFile()
    {
        var items = await GetSelectedRoutingItems();
        if (items.Count == 0)
        {
            NoticeManager.Instance.Enqueue(ResUI.PleaseSelectRules);
            return;
        }
        var suggested = (items.Count == 1 && items[0].Remarks.IsNullOrEmpty() == false ? items[0].Remarks : "routing_rulesets") + ".json";
        var fileName = await SaveRoutingFileInteraction.Handle(suggested);
        if (fileName.IsNullOrEmpty())
        {
            return;
        }
        await File.WriteAllTextAsync(fileName, RoutingRuleExporter.ExportRoutingTemplateToJson(items));
        NoticeManager.Instance.SendMessageAndEnqueue(string.Format(ResUI.SaveRoutingRulesResult, fileName));
    }

    private async Task ImportRoutingFromData(string? data)
    {
        var items = RoutingRuleExporter.ParseRoutingTemplateJson(data);
        if (items == null || items.Count == 0)
        {
            NoticeManager.Instance.Enqueue(ResUI.OperationFailed);
            return;
        }
        var existing = await AppManager.Instance.RoutingItems() ?? [];
        var maxSort = existing.Count > 0 ? existing.Max(t => t.Sort) : 0;
        foreach (var item in items)
        {
            item.Id = null;            // SaveRoutingItem assigns a fresh GUID
            item.IsActive = false;
            item.Sort = ++maxSort;
            await ConfigHandler.SaveRoutingItem(_config, item);
        }
        await RefreshRoutingItems();
        IsModified = true;
        NoticeManager.Instance.Enqueue(ResUI.OperationSuccess);
    }
}
