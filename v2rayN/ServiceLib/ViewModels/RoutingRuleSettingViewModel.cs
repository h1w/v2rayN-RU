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
    private Dictionary<int, RulesItem> _jsonByOrdinal = [];
    private ProfileItem? _activeCustomProfile;

    /// <summary>
    /// Единый порядок отображения/сохранения: смесь JSON-токенов (LocalId == null,
    /// Index — ordinal в _jsonByOrdinal) и локальных токенов (LocalId — Id записи в
    /// _rules). Порядок элементов в списке — это порядок строк в RulesItems и то,
    /// что персистится как ProfileItem.CustomRuleState при включённом
    /// EnableCustomRuleEditing. Локальный порядок в `_rules` самостоятельного
    /// значения не имеет — он лишь хранилище содержимого, финальный порядок для
    /// RuleSet выводится из _displayOrder при сохранении.
    /// </summary>
    private List<CustomRuleStateItem> _displayOrder = [];

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

        // Default display order (used as-is for non-custom/inactive routing items,
        // and as the base that InitReadonlyJsonRulesAsync rebuilds/reconciles for an
        // active custom-JSON profile): local rules only, in RuleSet order.
        _displayOrder = _rules.Select(r => new CustomRuleStateItem { LocalId = r.Id }).ToList();

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

            // ordinal каждого JSON-правила = его позиция в parsed (порядок не-null
            // правил файла, как ParseDisplayRules) — тот же ordinal, что использует
            // композер конфига (CustomConfigComposer.BuildUnifiedRules).
            var jsonByOrdinal = new Dictionary<int, RulesItem>();
            for (var i = 0; i < parsed.Count; i++)
            {
                var r = parsed[i];
                r.Id = Utils.GetGuid(false); // stable per-load id for selection/self-drop guard only
                jsonByOrdinal[i] = r;
            }
            _jsonByOrdinal = jsonByOrdinal;

            var savedState = _config.UiItem.EnableCustomRuleEditing
                ? JsonUtils.Deserialize<List<CustomRuleStateItem>>(node.CustomRuleState)
                : null;

            var jsonOrdinals = Enumerable.Range(0, parsed.Count).ToList();
            _displayOrder = CustomRuleStateHelper.BuildDisplayOrder(savedState, jsonOrdinals, _rules.Select(r => r.Id).ToList());

            RefreshRulesItems();
        }
        catch (Exception ex)
        {
            Logging.SaveLog(ex.Message, ex);
        }
    }

    public void RefreshRulesItems()
    {
        // Persist inline JSON-rule toggles back into _displayOrder so this rebuild
        // does not revert an un-saved Enabled change.
        foreach (var model in RulesItems.Where(m => m.IsReadonly))
        {
            var token = _displayOrder.FirstOrDefault(t => t.LocalId == null && t.Index == model.RawOrdinal);
            if (token != null)
            {
                token.Enabled = model.Enabled;
            }
        }

        RulesItems.Clear();

        var models = new List<RulesItemModel>();
        foreach (var token in _displayOrder)
        {
            if (token.LocalId == null)
            {
                if (!_jsonByOrdinal.TryGetValue(token.Index, out var jsonRule))
                {
                    continue; // stale token (shouldn't happen post-reconcile); skip defensively
                }
                var model = ToRuleModel(jsonRule, isReadonly: true);
                model.RawOrdinal = token.Index;
                model.Enabled = token.Enabled;
                model.CanEditCustom = _config.UiItem.EnableCustomRuleEditing;
                models.Add(model);
            }
            else
            {
                var localRule = _rules.FirstOrDefault(r => r.Id == token.LocalId);
                if (localRule is null)
                {
                    continue; // stale token (shouldn't happen post-reconcile); skip defensively
                }
                models.Add(ToRuleModel(localRule, isReadonly: false));
            }
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
            if (!_jsonByOrdinal.TryGetValue(SelectedSource.RawOrdinal, out var ro))
            {
                return;
            }
            var canToggle = _config.UiItem.EnableCustomRuleEditing;
            var copy = JsonUtils.DeepCopy(ro);
            copy.Enabled = SelectedSource.Enabled;   // reflect the current list checkbox state (item may be toggled but not yet saved)
            var readonlyVm = new RoutingRuleDetailsViewModel(copy, isReadonly: true, canToggleEnabled: canToggle);
            if (await AppManager.Instance.WindowDialog.ShowDialogAsync(readonlyVm) == true && canToggle)
            {
                // Sync the toggle back onto the matching _displayOrder JSON token and the list checkbox.
                var newEnabled = readonlyVm.SelectedSource.Enabled;
                var token = _displayOrder.FirstOrDefault(t => t.LocalId == null && t.Index == SelectedSource.RawOrdinal);
                if (token != null)
                {
                    token.Enabled = newEnabled;
                }
                SelectedSource.Enabled = newEnabled;
                RefreshRulesItems();
            }
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
                InsertNewLocalToken(edited.Id);
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
        var removedIds = new List<string>();
        foreach (var it in SelectedSources ?? [SelectedSource])
        {
            var item = _rules.FirstOrDefault(t => t.Id == it?.Id);
            if (item != null)
            {
                _rules.Remove(item);
                removedIds.Add(item.Id);
            }
        }
        _displayOrder.RemoveAll(t => t.LocalId != null && removedIds.Contains(t.LocalId));

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

        // Operates on the local-token slots of _displayOrder directly (not on `_rules`'
        // own list order, which is content-only now) so this stays consistent with any
        // prior cross-group drag and with the unified list the user actually sees.
        if (CustomRuleStateHelper.MoveLocalToken(_displayOrder, SelectedSource.Id, eMove))
        {
            RefreshRulesItems();
        }
        await Task.CompletedTask;
    }

    public void MoveRuleByDrag(RulesItemModel? dragged, RulesItemModel? target, bool insertAfter)
    {
        if (dragged is null || target is null || dragged.Id == target.Id)
        {
            return;
        }

        var from = FindDisplayOrderIndex(dragged);
        var to = FindDisplayOrderIndex(target);
        if (from < 0 || to < 0)
        {
            return;
        }

        if (CustomRuleStateHelper.MoveTokenRelative(_displayOrder, from, to, insertAfter))
        {
            RefreshRulesItems();
        }
    }

    /// <summary>
    /// Finds the _displayOrder token index matching a row model: a JSON row by its
    /// RawOrdinal (LocalId == null), a local row by its RulesItem.Id.
    /// </summary>
    private int FindDisplayOrderIndex(RulesItemModel model)
    {
        return model.IsReadonly
            ? _displayOrder.FindIndex(t => t.LocalId == null && t.Index == model.RawOrdinal)
            : _displayOrder.FindIndex(t => t.LocalId == model.Id);
    }

    /// <summary>
    /// Inserts a token for a freshly-added local rule right before the first
    /// existing local token (mirrors the previous "new rule at top of the local
    /// block" placement), or at the end of _displayOrder if there is none yet.
    /// </summary>
    private void InsertNewLocalToken(string id)
    {
        var firstLocalIdx = _displayOrder.FindIndex(t => t.LocalId != null);
        var insertAt = firstLocalIdx >= 0 ? firstLocalIdx : _displayOrder.Count;
        _displayOrder.Insert(insertAt, new CustomRuleStateItem { LocalId = id });
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

        // Stable ids: only assign a fresh Id to a local rule that doesn't have one
        // yet, so _displayOrder's LocalId tokens (and any persisted CustomRuleState)
        // keep referencing the right rule across saves.
        foreach (var it in _rules)
        {
            if (it.Id.IsNullOrEmpty())
            {
                it.Id = Utils.GetGuid(false);
            }
        }

        // Reorder `_rules` to match the local-token relative order in _displayOrder,
        // so RuleSet reflects the mixed list's local order. Any rule not referenced
        // by a token (should not normally happen — every mutator keeps them paired)
        // is appended at the end defensively, so nothing is silently dropped.
        var byId = new Dictionary<string, RulesItem>(StringComparer.Ordinal);
        foreach (var rule in _rules)
        {
            byId.TryAdd(rule.Id, rule);
        }
        var reordered = new List<RulesItem>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in _displayOrder)
        {
            if (token.LocalId != null && byId.TryGetValue(token.LocalId, out var rule) && used.Add(token.LocalId))
            {
                reordered.Add(rule);
            }
        }
        foreach (var rule in _rules)
        {
            if (used.Add(rule.Id))
            {
                reordered.Add(rule);
            }
        }
        _rules = reordered;

        item.RuleNum = _rules.Count;
        item.RuleSet = JsonUtils.Serialize(_rules, false);

        if (_config.UiItem.EnableCustomRuleEditing && _activeCustomProfile is not null)
        {
            // Sync current inline JSON toggles back into _displayOrder before
            // persisting (same sync-back RefreshRulesItems does).
            foreach (var model in RulesItems.Where(m => m.IsReadonly))
            {
                var token = _displayOrder.FirstOrDefault(t => t.LocalId == null && t.Index == model.RawOrdinal);
                if (token != null)
                {
                    token.Enabled = model.Enabled;
                }
            }

            _activeCustomProfile.CustomRuleState = _displayOrder.Count > 0 ? JsonUtils.Serialize(_displayOrder, false) : null;
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
            _displayOrder.RemoveAll(t => t.LocalId != null);
            _rules = lstRules;
        }
        else
        {
            _rules.AddRange(lstRules);
        }
        _displayOrder.AddRange(lstRules.Select(r => new CustomRuleStateItem { LocalId = r.Id }));
        return 0;
    }

    #endregion Import rules
}
