namespace ServiceLib.ViewModels;

public class RoutingRuleSettingViewModel : MyReactiveObject, ICloseable
{
    public event EventHandler? RequestClose;
    public Interaction<string, bool> ShowYesNoInteraction { get; } = new();
    public Interaction<string, Unit> SetClipboardDataInteraction { get; } = new();
    public Interaction<Unit, string?> ReadTextFromClipboardInteraction { get; } = new();
    public Interaction<Unit, string?> BrowseRulesFileInteraction { get; } = new();
    public Interaction<string, string?> SaveRulesFileInteraction { get; } = new();

    private List<RulesItem> _rules;
    private List<RulesItem> _readonlyJsonRules = [];
    private List<int> _readonlyOrdinals = [];
    private ProfileItem? _activeCustomProfile;
    private List<CustomRuleStateItem>? _customRuleState;

    [Reactive]
    public RoutingItem SelectedRouting { get; set; }

    public IObservableCollection<RulesItemModel> RulesItems { get; } = new ObservableCollectionExtended<RulesItemModel>();

    [Reactive]
    public RulesItemModel SelectedSource { get; set; }

    public IList<RulesItemModel> SelectedSources { get; set; }

    public ReactiveCommand<Unit, Unit> RuleAddCmd { get; }
    public ReactiveCommand<Unit, Unit> ImportRulesFromFileCmd { get; }
    public ReactiveCommand<Unit, Unit> ImportRulesFromClipboardCmd { get; }
    public ReactiveCommand<Unit, Unit> ImportRulesFromUrlCmd { get; }
    public ReactiveCommand<Unit, Unit> RuleRemoveCmd { get; }
    public ReactiveCommand<Unit, Unit> RuleExportSelectedCmd { get; }
    public ReactiveCommand<Unit, Unit> RuleExportToClipboardCmd { get; }
    public ReactiveCommand<Unit, Unit> RuleExportToFileCmd { get; }
    public ReactiveCommand<Unit, Unit> MoveTopCmd { get; }
    public ReactiveCommand<Unit, Unit> MoveUpCmd { get; }
    public ReactiveCommand<Unit, Unit> MoveDownCmd { get; }
    public ReactiveCommand<Unit, Unit> MoveBottomCmd { get; }

    public ReactiveCommand<Unit, Unit> SaveCmd { get; }

    public RoutingRuleSettingViewModel(RoutingItem routingItem)
    {
        _config = AppManager.Instance.Config;

        var canEditRemove = this.WhenAnyValue(
            x => x.SelectedSource,
            selectedSource => selectedSource != null && !selectedSource.OutboundTag.IsNullOrEmpty());

        RuleAddCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await RuleEditAsync(true);
        });
        ImportRulesFromFileCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            var fileName = await BrowseRulesFileInteraction.Handle(Unit.Default);
            await ImportRulesFromFileAsync(fileName);
        });
        ImportRulesFromClipboardCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await ImportRulesFromClipboardAsync(null);
        });
        ImportRulesFromUrlCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await ImportRulesFromUrl();
        });

        RuleRemoveCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await RuleRemoveAsync();
        }, canEditRemove);
        RuleExportSelectedCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await RuleExportSelectedAsync();
        }, canEditRemove);
        RuleExportToClipboardCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await RuleExportAllToClipboardAsync();
        });
        RuleExportToFileCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await RuleExportAllToFileAsync();
        });

        MoveTopCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await MoveRule(EMove.Top);
        }, canEditRemove);
        MoveUpCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await MoveRule(EMove.Up);
        }, canEditRemove);
        MoveDownCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await MoveRule(EMove.Down);
        }, canEditRemove);
        MoveBottomCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await MoveRule(EMove.Bottom);
        }, canEditRemove);

        SaveCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await SaveRoutingAsync();
        });

        SelectedSource = new();
        SelectedRouting = routingItem;
        _rules = routingItem.Id.IsNullOrEmpty() ? [] : JsonUtils.Deserialize<List<RulesItem>>(SelectedRouting.RuleSet);

        RefreshRulesItems();
        _ = InitReadonlyJsonRulesAsync();
    }

    private async Task InitReadonlyJsonRulesAsync()
    {
        try
        {
            if (SelectedRouting?.IsActive != true)
            {
                return;
            }
            var node = await AppManager.Instance.GetProfileItem(_config.IndexId);
            if (node is null || node.ConfigType != EConfigType.Custom)
            {
                return;
            }
            var path = node.Address;
            if (!File.Exists(path))
            {
                path = Utils.GetConfigPath(node.Address);
            }
            if (!File.Exists(path))
            {
                return;
            }
            var json = await File.ReadAllTextAsync(path);
            var coreType = AppManager.Instance.GetCoreType(node, EConfigType.Custom);
            var parsed = CustomConfigParser.ParseDisplayRules(json, coreType);
            _activeCustomProfile = node;

            _customRuleState = _config.UiItem.EnableCustomRuleEditing
                ? JsonUtils.Deserialize<List<CustomRuleStateItem>>(node.CustomRuleState)
                : null;

            // ordinal каждого правила = его позиция в parsed (parsed идёт в порядке
            // не-null правил файла, как ParseDisplayRules). Переупорядочиваем по state;
            // ordinal несём параллельным списком _readonlyOrdinals (в Remarks не писать).
            var orderedOrdinals = CustomRuleStateHelper.OrderedOrdinals(parsed.Count, _customRuleState);
            var ordered = new List<RulesItem>();
            foreach (var ord in orderedOrdinals)
            {
                var r = parsed[ord];
                r.Id = Utils.GetGuid(false);            // stable id for selection/lookup
                r.Enabled = CustomRuleStateHelper.IsEnabled(ord, _customRuleState);
                ordered.Add(r);
            }
            _readonlyJsonRules = ordered;
            _readonlyOrdinals = orderedOrdinals;
            RefreshRulesItems();
        }
        catch (Exception ex)
        {
            Logging.SaveLog(ex.Message, ex);
        }
    }

    public void RefreshRulesItems()
    {
        RulesItems.Clear();

        var models = new List<RulesItemModel>();
        for (var k = 0; k < _readonlyJsonRules.Count; k++)
        {
            var model = ToRuleModel(_readonlyJsonRules[k], isReadonly: true);
            model.RawOrdinal = _readonlyOrdinals.ElementAtOrDefault(k);
            model.CanEditCustom = _config.UiItem.EnableCustomRuleEditing;
            models.Add(model);
        }
        foreach (var item in _rules)
        {
            models.Add(ToRuleModel(item, isReadonly: false));
        }
        RulesItems.AddRange(models);
    }

    private static RulesItemModel ToRuleModel(RulesItem item, bool isReadonly)
    {
        return new RulesItemModel()
        {
            Id = item.Id,
            RuleTypeName = item.RuleType?.ToString(),
            OutboundTag = item.OutboundTag,
            Port = item.Port,
            Network = item.Network,
            Protocols = Utils.List2String(item.Protocol),
            InboundTags = Utils.List2String(item.InboundTag),
            Domains = Utils.List2String((item.Domain ?? []).Concat(item.Ip ?? []).ToList().Concat(item.Process ?? []).ToList()),
            Enabled = item.Enabled,
            Remarks = item.Remarks,
            IsReadonly = isReadonly,
            RuleSource = isReadonly ? ResUI.RuleSourceJson : string.Empty,
        };
    }

    public async Task RuleEditAsync(bool blNew)
    {
        if (!blNew && SelectedSource?.IsReadonly == true)
        {
            var ro = _readonlyJsonRules.FirstOrDefault(t => t.Id == SelectedSource.Id);
            if (ro is null)
            {
                return;
            }
            var readonlyVm = new RoutingRuleDetailsViewModel(JsonUtils.DeepCopy(ro), isReadonly: true);
            await AppManager.Instance.WindowDialog.ShowDialogAsync(readonlyVm);
            return;
        }

        RulesItem? item;
        if (blNew)
        {
            item = new();
        }
        else
        {
            item = _rules.FirstOrDefault(t => t.Id == SelectedSource?.Id);
            if (item is null)
            {
                return;
            }
        }
        var routingRuleDetailsViewModel = new RoutingRuleDetailsViewModel(item);
        if (await AppManager.Instance.WindowDialog.ShowDialogAsync(routingRuleDetailsViewModel) == true)
        {
            // Read the edited rule back from the detail VM: selecting a profile replaces
            // SelectedSource with a deep copy, so the original `item` reference can be stale.
            var edited = routingRuleDetailsViewModel.SelectedSource ?? item;
            if (blNew)
            {
                _rules.Insert(0, edited);
            }
            else
            {
                var index = _rules.FindIndex(t => t.Id == edited.Id);
                if (index >= 0)
                {
                    _rules[index] = edited;
                }
            }
            RefreshRulesItems();
        }
    }

    public async Task RuleRemoveAsync()
    {
        if (SelectedSource is null || SelectedSource.OutboundTag.IsNullOrEmpty())
        {
            NoticeManager.Instance.Enqueue(ResUI.PleaseSelectRules);
            return;
        }
        if (SelectedSource?.IsReadonly == true)
        {
            NoticeManager.Instance.Enqueue(ResUI.CustomJsonRuleReadonlyTip);
            return;
        }
        if (await ShowYesNoInteraction.Handle(ResUI.RemoveServer) == false)
        {
            return;
        }
        foreach (var it in SelectedSources ?? [SelectedSource])
        {
            var item = _rules.FirstOrDefault(t => t.Id == it?.Id);
            if (item != null)
            {
                _rules.Remove(item);
            }
        }

        RefreshRulesItems();
    }

    public async Task RuleExportSelectedAsync()
    {
        if (SelectedSource is null || SelectedSource.OutboundTag.IsNullOrEmpty())
        {
            NoticeManager.Instance.Enqueue(ResUI.PleaseSelectRules);
            return;
        }

        var sources = SelectedSources ?? [SelectedSource];
        var lst = _rules.Where(it => sources.Any(t => t.Id == it.Id)).ToList();
        if (lst.Count > 0)
        {
            await SetClipboardDataInteraction.Handle(RoutingRuleExporter.ExportRulesToJson(lst));
        }
    }

    public async Task RuleExportAllToClipboardAsync()
    {
        if (_rules.Count == 0)
        {
            NoticeManager.Instance.Enqueue(ResUI.PleaseSelectRules);
            return;
        }
        await SetClipboardDataInteraction.Handle(RoutingRuleExporter.ExportRulesToJson(_rules));
    }

    public async Task RuleExportAllToFileAsync()
    {
        if (_rules.Count == 0)
        {
            NoticeManager.Instance.Enqueue(ResUI.PleaseSelectRules);
            return;
        }
        var suggested = (SelectedRouting?.Remarks.IsNullOrEmpty() == false ? SelectedRouting.Remarks : "routing_rules") + ".json";
        var fileName = await SaveRulesFileInteraction.Handle(suggested);
        if (fileName.IsNullOrEmpty())
        {
            return;
        }
        await File.WriteAllTextAsync(fileName, RoutingRuleExporter.ExportRulesToJson(_rules));
        NoticeManager.Instance.SendMessageAndEnqueue(string.Format(ResUI.SaveRoutingRulesResult, fileName));
    }

    public async Task MoveRule(EMove eMove)
    {
        if (SelectedSource is null || SelectedSource.OutboundTag.IsNullOrEmpty())
        {
            NoticeManager.Instance.Enqueue(ResUI.PleaseSelectRules);
            return;
        }
        if (SelectedSource?.IsReadonly == true)
        {
            NoticeManager.Instance.Enqueue(ResUI.CustomJsonRuleReadonlyTip);
            return;
        }

        var item = _rules.FirstOrDefault(t => t.Id == SelectedSource?.Id);
        if (item == null)
        {
            return;
        }
        var index = _rules.IndexOf(item);
        if (await ConfigHandler.MoveRoutingRule(_rules, index, eMove) == 0)
        {
            RefreshRulesItems();
        }
    }

    public void MoveRuleByDrag(RulesItemModel? dragged, RulesItemModel? target, bool insertAfter)
    {
        if (dragged is null || target is null)
        {
            return;
        }

        if (dragged.Id == target.Id)
        {
            return;
        }

        // JSON-правила: reorder только среди JSON-строк и только при включённом флаге
        if (dragged.IsReadonly || target.IsReadonly)
        {
            if (!_config.UiItem.EnableCustomRuleEditing || !dragged.IsReadonly || !target.IsReadonly)
            {
                return;
            }
            var fromJ = _readonlyJsonRules.FindIndex(t => t.Id == dragged.Id);
            var toJ = _readonlyJsonRules.FindIndex(t => t.Id == target.Id);
            if (fromJ < 0 || toJ < 0)
            {
                return;
            }
            var itemJ = _readonlyJsonRules[fromJ];
            var ordJ = _readonlyOrdinals[fromJ];
            _readonlyJsonRules.RemoveAt(fromJ);
            _readonlyOrdinals.RemoveAt(fromJ);
            toJ = _readonlyJsonRules.FindIndex(t => t.Id == target.Id);
            var insertAt = insertAfter ? toJ + 1 : toJ;
            _readonlyJsonRules.Insert(insertAt, itemJ);
            _readonlyOrdinals.Insert(insertAt, ordJ);
            RefreshRulesItems();
            return;
        }

        var from = _rules.FindIndex(t => t.Id == dragged.Id);
        var to = _rules.FindIndex(t => t.Id == target.Id);
        if (ConfigHandler.MoveRoutingRuleRelative(_rules, from, to, insertAfter) == 0)
        {
            RefreshRulesItems();
        }
    }

    private async Task SaveRoutingAsync()
    {
        var remarks = SelectedRouting.Remarks;
        if (remarks.IsNullOrEmpty())
        {
            NoticeManager.Instance.Enqueue(ResUI.PleaseFillRemarks);
            return;
        }
        var item = SelectedRouting;
        foreach (var it in _rules)
        {
            it.Id = Utils.GetGuid(false);
        }
        item.RuleNum = _rules.Count;
        item.RuleSet = JsonUtils.Serialize(_rules, false);

        if (_config.UiItem.EnableCustomRuleEditing && _activeCustomProfile is not null)
        {
            var state = RulesItems
                .Where(m => m.IsReadonly)
                .Select(m => new CustomRuleStateItem { Index = m.RawOrdinal, Enabled = m.Enabled })
                .ToList();
            _activeCustomProfile.CustomRuleState = state.Count > 0 ? JsonUtils.Serialize(state, false) : null;
            await SQLiteHelper.Instance.UpdateAsync(_activeCustomProfile);
        }

        if (await ConfigHandler.SaveRoutingItem(_config, item) == 0)
        {
            NoticeManager.Instance.Enqueue(ResUI.OperationSuccess);
            RequestClose?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            NoticeManager.Instance.Enqueue(ResUI.OperationFailed);
        }
    }

    #region Import rules

    public async Task ImportRulesFromFileAsync(string fileName)
    {
        if (fileName.IsNullOrEmpty())
        {
            return;
        }

        var result = EmbedUtils.LoadResource(fileName);
        if (result.IsNullOrEmpty())
        {
            return;
        }
        var ret = await AddBatchRoutingRulesAsync(SelectedRouting, result);
        if (ret == 0)
        {
            RefreshRulesItems();
            NoticeManager.Instance.Enqueue(ResUI.OperationSuccess);
        }
    }

    public async Task ImportRulesFromClipboardAsync(string? clipboardData)
    {
        var stringData = clipboardData;
        if (clipboardData == null)
        {
            var result = await ReadTextFromClipboardInteraction.Handle(Unit.Default);
            if (result.IsNullOrEmpty())
            {
                NoticeManager.Instance.Enqueue(ResUI.OperationFailed);
                return;
            }
            stringData = result;
        }
        var ret = await AddBatchRoutingRulesAsync(SelectedRouting, stringData);
        if (ret == 0)
        {
            RefreshRulesItems();
            NoticeManager.Instance.Enqueue(ResUI.OperationSuccess);
        }
    }

    private async Task ImportRulesFromUrl()
    {
        var url = SelectedRouting.Url;
        if (url.IsNullOrEmpty())
        {
            NoticeManager.Instance.Enqueue(ResUI.MsgNeedUrl);
            return;
        }

        var downloadHandle = new DownloadService();
        var result = await downloadHandle.TryDownloadString(url, true, "");
        var ret = await AddBatchRoutingRulesAsync(SelectedRouting, result);
        if (ret == 0)
        {
            RefreshRulesItems();
            NoticeManager.Instance.Enqueue(ResUI.OperationSuccess);
        }
    }

    private async Task<int> AddBatchRoutingRulesAsync(RoutingItem routingItem, string? clipboardData)
    {
        var blReplace = false;
        if (await ShowYesNoInteraction.Handle(ResUI.AddBatchRoutingRulesYesNo) == false)
        {
            blReplace = true;
        }
        if (clipboardData.IsNullOrEmpty())
        {
            return -1;
        }
        var lstRules = JsonUtils.Deserialize<List<RulesItem>>(clipboardData);
        if (lstRules == null)
        {
            return -1;
        }
        foreach (var rule in lstRules)
        {
            rule.Id = Utils.GetGuid(false);
        }

        if (blReplace)
        {
            _rules = lstRules;
        }
        else
        {
            _rules.AddRange(lstRules);
        }
        return 0;
    }

    #endregion Import rules
}
