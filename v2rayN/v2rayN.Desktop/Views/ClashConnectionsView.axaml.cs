using Avalonia.Input.Platform;

namespace v2rayN.Desktop.Views;

public partial class ClashConnectionsView : ReactiveUserControl<ClashConnectionsViewModel>
{
    private static Config _config;
    private static readonly string _tag = "ClashConnectionsView";

    public ClashConnectionsView()
    {
        InitializeComponent();

        _config = AppManager.Instance.Config;

        btnAutofitColumnWidth.Click += BtnAutofitColumnWidth_Click;

        this.WhenActivated(disposables =>
        {
            this.OneWayBind(ViewModel, vm => vm.ConnectionItems, v => v.lstConnections.ItemsSource).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.SelectedSource, v => v.lstConnections.SelectedItem).DisposeWith(disposables);

            this.BindCommand(ViewModel, vm => vm.ConnectionCloseCmd, v => v.menuConnectionClose).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.ConnectionCloseCmd, v => v.btnConnectionClose).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.ConnectionCloseAllCmd, v => v.menuConnectionCloseAll).DisposeWith(disposables);

            this.Bind(ViewModel, vm => vm.HostFilter, v => v.txtHostFilter.Text).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.TrafficFilter, v => v.cmbTrafficFilter.SelectedIndex).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.ConnectionCloseAllCmd, v => v.btnConnectionCloseAll).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.AutoRefresh, v => v.togAutoRefresh.IsChecked).DisposeWith(disposables);

            AppEvents.AppExitRequested
              .AsObservable()
              .ObserveOn(RxSchedulers.MainThreadScheduler)
              .Subscribe(_ => StorageUI())
              .DisposeWith(disposables);
        });

        RestoreUI();
    }

    private void BtnAutofitColumnWidth_Click(object? sender, RoutedEventArgs e)
    {
        AutofitColumnWidth();
    }

    private void AutofitColumnWidth()
    {
        try
        {
            foreach (var it in lstConnections.Columns)
            {
                it.Width = it.Tag switch
                {
                    "Host" or "Chain" => new DataGridLength(3, DataGridLengthUnitType.Star),
                    "ProcessPath" => new DataGridLength(2, DataGridLengthUnitType.Star),
                    _ => new DataGridLength(100, DataGridLengthUnitType.Pixel)
                };
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ClashConnectionsView", ex);
        }
    }

    private async void CopyProcessPath_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ClashConnectionModel row } || string.IsNullOrWhiteSpace(row.ProcessPath)) return;
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard == null) return;
            await clipboard.SetTextAsync(row.ProcessPath);
            NoticeManager.Instance.Enqueue(ResUI.ConnectionsProcessPathCopied);
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
        }
    }

    #region UI

    private void RestoreUI()
    {
        try
        {
            var lvColumnItem = _config.ClashUIItem?.ConnectionsColumnItem?.OrderBy(t => t.Index).ToList();
            if (lvColumnItem == null)
            {
                return;
            }

            var displayIndex = 0;
            foreach (var item in lvColumnItem)
            {
                foreach (var item2 in lstConnections.Columns)
                {
                    if (item2.Tag == null)
                    {
                        continue;
                    }
                    if (item2.Tag.Equals(item.Name))
                    {
                        if (item.Width < 0)
                        {
                            item2.IsVisible = false;
                        }
                        else
                        {
                            // Old fixed widths must not disable the redesigned elastic layout.
                            if (!item2.Width.IsStar)
                                item2.Width = new DataGridLength(item.Width, DataGridLengthUnitType.Pixel);
                            item2.DisplayIndex = displayIndex++;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
        }
    }

    private void StorageUI()
    {
        try
        {
            List<ColumnItem> lvColumnItem = [];
            foreach (var item2 in lstConnections.Columns)
            {
                if (item2.Tag == null)
                {
                    continue;
                }
                lvColumnItem.Add(new()
                {
                    Name = (string)item2.Tag,
                    Width = (int)(item2.IsVisible == true ? item2.ActualWidth : -1),
                    Index = item2.DisplayIndex
                });
            }
            _config.ClashUIItem.ConnectionsColumnItem = lvColumnItem;
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
        }
    }

    #endregion UI
}
