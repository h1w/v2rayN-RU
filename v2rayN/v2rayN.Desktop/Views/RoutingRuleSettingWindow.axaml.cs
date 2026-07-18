using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.VisualTree;
using v2rayN.Desktop.Base;
using v2rayN.Desktop.Common;

namespace v2rayN.Desktop.Views;

public partial class RoutingRuleSettingWindow : WindowBase<RoutingRuleSettingViewModel>
{
    private static readonly DataFormat<object> LstRulesRowFormat =
        DataFormat.CreateInProcessFormat<object>("LstRulesRow");

    private static readonly SolidColorBrush InsertionLineBrush = new(Color.FromRgb(0, 122, 204));
    private static readonly SolidColorBrush DragGhostBackgroundBrush = new(Color.FromArgb(210, 45, 45, 48));
    private static readonly SolidColorBrush DragGhostBorderBrush = new(Color.FromArgb(220, 0, 122, 204));

    private AdornerLayer? _adornerLayer;
    private Border? _insertionAdorner;
    private DataGridRow? _insertionAdornerRow;
    private bool _insertionAdornerIsTopEdge;
    private Border? _dragGhostAdorner;
    private TextBlock? _dragGhostText;

    public RoutingRuleSettingWindow()
    {
        InitializeComponent();

        Loaded += Window_Loaded;
        btnCancel.Click += (s, e) => Close();
        KeyDown += RoutingRuleSettingWindow_KeyDown;
        lstRules.SelectionChanged += lstRules_SelectionChanged;
        lstRules.DoubleTapped += LstRules_DoubleTapped;
        menuRuleSelectAll.Click += menuRuleSelectAll_Click;
        menuAutofitColumnWidth.Click += MenuAutofitColumnWidth_Click;
        //btnBrowseCustomIcon.Click += btnBrowseCustomIcon_Click;
        btnBrowseCustomRulesetPath4Singbox.Click += btnBrowseCustomRulesetPath4Singbox_ClickAsync;

        lstRules.AddHandler(PointerPressedEvent, LstRules_PointerPressed, RoutingStrategies.Bubble, true);
        lstRules.AddHandler(DragDrop.DragOverEvent, LstRules_DragOver, RoutingStrategies.Bubble);
        lstRules.AddHandler(DragDrop.DragLeaveEvent, LstRules_DragLeave, RoutingStrategies.Bubble);
        lstRules.AddHandler(DragDrop.DropEvent, LstRules_Drop, RoutingStrategies.Bubble);

        cmbdomainStrategy.ItemsSource = Global.DomainStrategies.AppendEmpty();
        cmbdomainStrategy4Singbox.ItemsSource = Global.DomainStrategies4Sbox;

        this.WhenActivated(disposables =>
        {
            this.OneWayBind(ViewModel, vm => vm.RulesItems, v => v.lstRules.ItemsSource).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.SelectedSource, v => v.lstRules.SelectedItem).DisposeWith(disposables);

            this.Bind(ViewModel, vm => vm.SelectedRouting.Remarks, v => v.txtRemarks.Text).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.SelectedRouting.DomainStrategy, v => v.cmbdomainStrategy.SelectedValue).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.SelectedRouting.DomainStrategy4Singbox, v => v.cmbdomainStrategy4Singbox.SelectedValue).DisposeWith(disposables);

            this.Bind(ViewModel, vm => vm.SelectedRouting.Url, v => v.txtUrl.Text).DisposeWith(disposables);
            //this.Bind(ViewModel, vm => vm.SelectedRouting.CustomIcon, v => v.txtCustomIcon.Text).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.SelectedRouting.CustomRulesetPath4Singbox, v => v.txtCustomRulesetPath4Singbox.Text).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.SelectedRouting.Sort, v => v.txtSort.Text).DisposeWith(disposables);

            this.BindCommand(ViewModel, vm => vm.RuleAddCmd, v => v.menuRuleAdd).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.ImportRulesFromFileCmd, v => v.menuImportRulesFromFile).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.ImportRulesFromClipboardCmd, v => v.menuImportRulesFromClipboard).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.ImportRulesFromUrlCmd, v => v.menuImportRulesFromUrl).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.RuleExportToClipboardCmd, v => v.menuRuleExportToClipboard).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.RuleExportToFileCmd, v => v.menuRuleExportToFile).DisposeWith(disposables);

            this.BindCommand(ViewModel, vm => vm.RuleAddCmd, v => v.menuRuleAdd2).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.RuleRemoveCmd, v => v.menuRuleRemove).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.RuleExportSelectedCmd, v => v.menuRuleExportSelected).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.MoveTopCmd, v => v.menuMoveTop).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.MoveUpCmd, v => v.menuMoveUp).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.MoveDownCmd, v => v.menuMoveDown).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.MoveBottomCmd, v => v.menuMoveBottom).DisposeWith(disposables);

            this.BindCommand(ViewModel, vm => vm.SaveCmd, v => v.btnSave).DisposeWith(disposables);

            ViewModel.ShowYesNoInteraction.RegisterHandler(async interaction =>
            {
                var message = interaction.Input;
                var result = await UI.ShowYesNo(message);
                interaction.SetOutput(result == ButtonResult.Yes);
            }).DisposeWith(disposables);

            ViewModel.SetClipboardDataInteraction.RegisterHandler(async interaction =>
            {
                var strData = interaction.Input;
                await AvaUtils.SetClipboardData(this, strData);
                interaction.SetOutput(Unit.Default);
            }).DisposeWith(disposables);

            ViewModel.ReadTextFromClipboardInteraction.RegisterHandler(async interaction =>
            {
                var result = await AvaUtils.GetClipboardData(this);
                interaction.SetOutput(result);
            }).DisposeWith(disposables);

            ViewModel.BrowseRulesFileInteraction.RegisterHandler(async interaction =>
            {
                var fileName = await UI.OpenFileDialog(null);
                interaction.SetOutput(fileName);
            }).DisposeWith(disposables);

            ViewModel.SaveRulesFileInteraction.RegisterHandler(async interaction =>
            {
                var fileName = await UI.SaveFileDialog("json", interaction.Input);
                interaction.SetOutput(fileName);
            }).DisposeWith(disposables);
        });
    }

    private void Window_Loaded(object? sender, RoutedEventArgs e)
    {
        txtRemarks.Focus();
    }

    private void RoutingRuleSettingWindow_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers is KeyModifiers.Control or KeyModifiers.Meta)
        {
            if (e.Key == Key.A)
            {
                lstRules.SelectAll();
            }
            else if (e.Key == Key.C)
            {
                ViewModel?.RuleExportSelectedAsync();
            }
        }
        else
        {
            switch (e.Key)
            {
                case Key.T:
                    ViewModel?.MoveRule(EMove.Top);
                    break;

                case Key.U:
                    ViewModel?.MoveRule(EMove.Up);
                    break;

                case Key.D:
                    ViewModel?.MoveRule(EMove.Down);
                    break;

                case Key.B:
                    ViewModel?.MoveRule(EMove.Bottom);
                    break;

                case Key.Delete:
                case Key.Back:
                    ViewModel?.RuleRemoveAsync();
                    break;
            }
        }
    }

    private void lstRules_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ViewModel != null)
        {
            ViewModel.SelectedSources = lstRules.SelectedItems.Cast<RulesItemModel>().ToList();
        }
    }

    private void LstRules_DoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        ViewModel?.RuleEditAsync(false);
    }

    private void menuRuleSelectAll_Click(object? sender, RoutedEventArgs e)
    {
        lstRules.SelectAll();
    }

    private void MenuAutofitColumnWidth_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            foreach (var it in lstRules.Columns)
            {
                it.Width = new DataGridLength(1, DataGridLengthUnitType.Auto);
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog(ex.Message, ex);
        }
    }

    //private async void btnBrowseCustomIcon_Click(object? sender, RoutedEventArgs e)
    //{
    //    var fileName = await UI.OpenFileDialog(this, FilePickerFileTypes.ImagePng);
    //    if (fileName.IsNullOrEmpty())
    //    {
    //        return;
    //    }

    //    txtCustomIcon.Text = fileName;
    //}

    private async void btnBrowseCustomRulesetPath4Singbox_ClickAsync(object? sender, RoutedEventArgs e)
    {
        var fileName = await UI.OpenFileDialog(null);
        if (fileName.IsNullOrEmpty())
        {
            return;
        }

        txtCustomRulesetPath4Singbox.Text = fileName;
    }

    private void linkCustomRulesetPath4Singbox(object? sender, RoutedEventArgs e)
    {
        ProcUtils.ProcessStart("https://github.com/2dust/v2rayCustomRoutingList/blob/master/singbox_custom_ruleset_example.json");
    }

    #region Drag and Drop

    private async void LstRules_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        try
        {
            if (e.Source is not Visual visualSource)
            {
                return;
            }

            // Don't hijack presses that target an interactive cell control (the enable
            // checkbox): starting DragDrop here captures the pointer and swallows the
            // click meant for the checkbox, so it never toggles. Let those through;
            // dragging still works from any other part of the row.
            if (visualSource.FindAncestorOfType<CheckBox>(true) is not null)
            {
                return;
            }

            var row = visualSource.FindAncestorOfType<DataGridRow>(true);
            if (row?.DataContext is not RulesItemModel item || (item.IsReadonly && !item.CanEditCustom))
            {
                return;
            }

            if (e.GetCurrentPoint(row).Properties.IsLeftButtonPressed)
            {
                Logging.SaveLog($"[DragDbg] PointerPressed start drag row={(item.Remarks.IsNullOrEmpty() == false ? item.Remarks : item.OutboundTag)} ro={item.IsReadonly} edit={item.CanEditCustom}");
                var dragData = new DataTransfer();
                var dataItem = DataTransferItem.Create(LstRulesRowFormat, item);
                dragData.Add(dataItem);
                try
                {
                    var effect = await DragDrop.DoDragDropAsync(e, dragData, DragDropEffects.Move);
                    Logging.SaveLog($"[DragDbg] DoDragDropAsync completed effect={effect}");
                }
                finally
                {
                    RemoveDragAdorners();
                }
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog("LstRules_PointerPressed drag", ex);
        }
    }

    private void LstRules_DragOver(object? sender, DragEventArgs e)
    {
        if (!e.DataTransfer.Contains(LstRulesRowFormat))
        {
            e.DragEffects = DragDropEffects.None;
            RemoveDragAdorners();
            return;
        }

        var sourceItem = GetDraggedItem(e);
        UpdateDragGhost(e, sourceItem);

        if (e.Source is not Visual visualTarget)
        {
            e.DragEffects = DragDropEffects.None;
            RemoveInsertionAdorner();
            return;
        }

        var row = visualTarget.FindAncestorOfType<DataGridRow>(true);
        if (row is not { DataContext: RulesItemModel targetItem } || sourceItem is null
            || targetItem.IsReadonly != sourceItem.IsReadonly
            || (sourceItem.IsReadonly && !sourceItem.CanEditCustom)
            || ReferenceEquals(sourceItem, targetItem))
        {
            e.DragEffects = DragDropEffects.None;
            RemoveInsertionAdorner();
            return;
        }

        e.DragEffects = DragDropEffects.Move;
        var isTopEdge = e.GetPosition(row).Y < row.Bounds.Height / 2;
        ShowInsertionAdorner(row, isTopEdge);
    }

    private void LstRules_DragLeave(object? sender, DragEventArgs e)
    {
        RemoveDragAdorners();
    }

    private void LstRules_Drop(object? sender, DragEventArgs e)
    {
        RemoveDragAdorners();

        if (!e.DataTransfer.Contains(LstRulesRowFormat))
        {
            return;
        }
        var sourceItem = GetDraggedItem(e);
        if (sourceItem == null)
        {
            return;
        }
        if (e.Source is not Visual visualTarget)
        {
            return;
        }

        var targetRow = visualTarget.FindAncestorOfType<DataGridRow>(true);
        if (targetRow is not { DataContext: RulesItemModel targetItem })
        {
            return;
        }
        if (ReferenceEquals(sourceItem, targetItem))
        {
            return;
        }
        // Match the insertion line shown in DragOver: top half -> before target, bottom half -> after.
        var isTopEdge = e.GetPosition(targetRow).Y < targetRow.Bounds.Height / 2;
        Logging.SaveLog($"[DragDbg] Drop source ro={sourceItem.IsReadonly} target ro={targetItem.IsReadonly} after={!isTopEdge}");
        ViewModel?.MoveRuleByDrag(sourceItem, targetItem, insertAfter: !isTopEdge);
    }

    private static RulesItemModel? GetDraggedItem(DragEventArgs e)
    {
        foreach (var dataItem in e.DataTransfer.Items)
        {
            if (!dataItem.Formats.Contains(LstRulesRowFormat))
            {
                continue;
            }
            if (dataItem.TryGetRaw(LstRulesRowFormat) is RulesItemModel model)
            {
                return model;
            }
        }
        return null;
    }

    private AdornerLayer? EnsureAdornerLayer()
    {
        return _adornerLayer ??= AdornerLayer.GetAdornerLayer(lstRules);
    }

    private void ShowInsertionAdorner(DataGridRow row, bool isTopEdge)
    {
        var layer = EnsureAdornerLayer();
        if (layer is null)
        {
            return;
        }

        if (_insertionAdorner is not null && ReferenceEquals(_insertionAdornerRow, row) && _insertionAdornerIsTopEdge == isTopEdge)
        {
            return;
        }

        if (_insertionAdorner is null)
        {
            _insertionAdorner = new Border
            {
                Height = 3,
                Background = InsertionLineBrush,
                CornerRadius = new CornerRadius(1.5),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                IsHitTestVisible = false,
            };
            layer.Children.Add(_insertionAdorner);
        }

        AdornerLayer.SetAdornedElement(_insertionAdorner, row);
        _insertionAdorner.VerticalAlignment = isTopEdge ? VerticalAlignment.Top : VerticalAlignment.Bottom;
        _insertionAdornerRow = row;
        _insertionAdornerIsTopEdge = isTopEdge;
    }

    private void RemoveInsertionAdorner()
    {
        if (_insertionAdorner is not null)
        {
            _adornerLayer?.Children.Remove(_insertionAdorner);
            _insertionAdorner = null;
            _insertionAdornerRow = null;
        }
    }

    private void UpdateDragGhost(DragEventArgs e, RulesItemModel? draggedItem)
    {
        if (draggedItem is null)
        {
            RemoveDragGhostAdorner();
            return;
        }

        var layer = EnsureAdornerLayer();
        if (layer is null)
        {
            return;
        }

        if (_dragGhostAdorner is null || _dragGhostText is null)
        {
            _dragGhostText = new TextBlock
            {
                Foreground = Brushes.White,
                FontSize = 12,
            };
            _dragGhostAdorner = new Border
            {
                Background = DragGhostBackgroundBrush,
                BorderBrush = DragGhostBorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 3),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                IsHitTestVisible = false,
                Opacity = 0.9,
                Child = _dragGhostText,
            };
            AdornerLayer.SetAdornedElement(_dragGhostAdorner, lstRules);
            layer.Children.Add(_dragGhostAdorner);
        }

        var text = draggedItem.Remarks.IsNullOrEmpty() ? draggedItem.OutboundTag : draggedItem.Remarks;
        _dragGhostText.Text = text ?? string.Empty;

        var position = e.GetPosition(lstRules);
        _dragGhostAdorner.Margin = new Thickness(position.X + 16, position.Y + 10, 0, 0);
    }

    private void RemoveDragGhostAdorner()
    {
        if (_dragGhostAdorner is not null)
        {
            _adornerLayer?.Children.Remove(_dragGhostAdorner);
            _dragGhostAdorner = null;
            _dragGhostText = null;
        }
    }

    private void RemoveDragAdorners()
    {
        RemoveInsertionAdorner();
        RemoveDragGhostAdorner();
        _adornerLayer = null;
    }

    #endregion Drag and Drop
}
