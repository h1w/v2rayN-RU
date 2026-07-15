namespace v2rayN.Views;

public partial class RoutingSettingWindow
{
    public RoutingSettingWindow()
    {
        InitializeComponent();

        Closing += RoutingSettingWindow_Closing;
        PreviewKeyDown += RoutingSettingWindow_PreviewKeyDown;
        lstRoutings.SelectionChanged += lstRoutings_SelectionChanged;
        lstRoutings.MouseDoubleClick += LstRoutings_MouseDoubleClick;
        menuRoutingAdvancedSelectAll.Click += menuRoutingAdvancedSelectAll_Click;

        cmbdomainStrategy.ItemsSource = Global.DomainStrategies;
        cmbdomainStrategy4Singbox.ItemsSource = Global.DomainStrategies4Sbox;

        this.WhenActivated(disposables =>
        {
            this.OneWayBind(ViewModel, vm => vm.RoutingItems, v => v.lstRoutings.ItemsSource).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.SelectedSource, v => v.lstRoutings.SelectedItem).DisposeWith(disposables);

            this.Bind(ViewModel, vm => vm.DomainStrategy, v => v.cmbdomainStrategy.Text).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.DomainStrategy4Singbox, v => v.cmbdomainStrategy4Singbox.Text).DisposeWith(disposables);

            this.BindCommand(ViewModel, vm => vm.RoutingAdvancedAddCmd, v => v.menuRoutingAdvancedAdd).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.RoutingAdvancedAddCmd, v => v.menuRoutingAdvancedAdd2).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.RoutingAdvancedRemoveCmd, v => v.menuRoutingAdvancedRemove).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.RoutingAdvancedSetDefaultCmd, v => v.menuRoutingAdvancedSetDefault).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.RoutingAdvancedImportRulesCmd, v => v.menuRoutingAdvancedImportRules).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.RoutingAdvancedImportRulesCmd, v => v.menuRoutingAdvancedImportRules2).DisposeWith(disposables);

            this.BindCommand(ViewModel, vm => vm.ExportRoutingToClipboardCmd, v => v.menuRoutingAdvancedExportToClipboard).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.ExportRoutingToClipboardCmd, v => v.menuRoutingAdvancedExportToClipboard2).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.ExportRoutingToFileCmd, v => v.menuRoutingAdvancedExportToFile).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.ExportRoutingToFileCmd, v => v.menuRoutingAdvancedExportToFile2).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.ImportRoutingFromFileCmd, v => v.menuRoutingAdvancedImportFromFile).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.ImportRoutingFromFileCmd, v => v.menuRoutingAdvancedImportFromFile2).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.ImportRoutingFromClipboardCmd, v => v.menuRoutingAdvancedImportFromClipboard).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.ImportRoutingFromClipboardCmd, v => v.menuRoutingAdvancedImportFromClipboard2).DisposeWith(disposables);

            ViewModel.ShowYesNoInteraction.RegisterHandler(interaction =>
            {
                var message = interaction.Input;
                var result = UI.ShowYesNo(message) != MessageBoxResult.No;
                interaction.SetOutput(result);
            }).DisposeWith(disposables);

            ViewModel?.SetClipboardDataInteraction.RegisterHandler(interaction =>
            {
                WindowsUtils.SetClipboardData(interaction.Input);
                interaction.SetOutput(Unit.Default);
            }).DisposeWith(disposables);

            ViewModel?.ReadTextFromClipboardInteraction.RegisterHandler(interaction =>
            {
                interaction.SetOutput(WindowsUtils.GetClipboardData());
            }).DisposeWith(disposables);

            ViewModel?.BrowseRoutingFileInteraction.RegisterHandler(interaction =>
            {
                if (UI.OpenFileDialog(out var fileName, "Rules|*.json|All|*.*") != true)
                {
                    interaction.SetOutput(null);
                    return;
                }
                interaction.SetOutput(fileName);
            }).DisposeWith(disposables);

            ViewModel?.SaveRoutingFileInteraction.RegisterHandler(interaction =>
            {
                if (UI.SaveFileDialog(out var fileName, "Rules|*.json|All|*.*", interaction.Input) != true)
                {
                    interaction.SetOutput(null);
                    return;
                }
                interaction.SetOutput(fileName);
            }).DisposeWith(disposables);
        });
        WindowsUtils.SetDarkBorder(this, AppManager.Instance.Config.UiItem.CurrentTheme);
    }

    private void RoutingSettingWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // DomainStrategy is auto-saved reactively; just ensure the caller knows changes were made
        if (ViewModel?.IsModified == true)
        {
            DialogResult = true;
        }
    }

    private void RoutingSettingWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl))
        {
            switch (e.Key)
            {
                case Key.A:
                    lstRoutings.SelectAll();
                    break;
            }
        }
        else
        {
            switch (e.Key)
            {
                case Key.Enter:
                    //case Key.Return:
                    ViewModel?.RoutingAdvancedSetDefault();
                    break;

                case Key.Delete:
                case Key.Back:
                    ViewModel?.RoutingAdvancedRemoveAsync();
                    break;
            }
        }
    }

    private void menuRoutingAdvancedSelectAll_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        lstRoutings.SelectAll();
    }

    private void lstRoutings_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ViewModel != null)
        {
            ViewModel.SelectedSources = lstRoutings.SelectedItems.Cast<RoutingItemModel>().ToList();
        }
    }

    private void LstRoutings_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        ViewModel?.RoutingAdvancedEditAsync(false);
    }

    private void linkdomainStrategy_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        ProcUtils.ProcessStart("https://xtls.github.io/config/routing.html");
    }

    private void linkdomainStrategy4Singbox_Click(object sender, RoutedEventArgs e)
    {
        ProcUtils.ProcessStart("https://sing-box.sagernet.org/zh/configuration/route/rule_action/#strategy");
    }
}
