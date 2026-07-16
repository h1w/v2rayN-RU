namespace ServiceLib.ViewModels;

public class SubEditViewModel : MyReactiveObject, ICloseable
{
    public event EventHandler? RequestClose;

    private readonly string _originalRemarks;

    [Reactive]
    public SubItem SelectedSource { get; set; }

    public ReactiveCommand<Unit, Unit> SelectPrevProfileCmd { get; }
    public ReactiveCommand<Unit, Unit> SelectNextProfileCmd { get; }
    public ReactiveCommand<Unit, Unit> SaveCmd { get; }

    public SubEditViewModel(SubItem subItem)
    {
        _config = AppManager.Instance.Config;

        SelectPrevProfileCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            var profileItem = await SelectProfileAsync();
            if (profileItem != null)
            {
                SelectedSource?.PrevProfile = profileItem.Remarks;
                SelectedSource = JsonUtils.DeepCopy(SelectedSource);
            }
        });
        SelectNextProfileCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            var profileItem = await SelectProfileAsync();
            if (profileItem != null)
            {
                SelectedSource?.NextProfile = profileItem.Remarks;
                SelectedSource = JsonUtils.DeepCopy(SelectedSource);
            }
        });
        SaveCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await SaveSubAsync();
        });

        _originalRemarks = subItem.Remarks ?? string.Empty;
        SelectedSource = subItem.Id.IsNullOrEmpty() ? subItem : JsonUtils.DeepCopy(subItem);
    }

    private async Task SaveSubAsync()
    {
        var url = SelectedSource.Url.TrimEx();
        if (url.IsNullOrEmpty())
        {
            if (SelectedSource.Remarks.IsNullOrEmpty())
            {
                NoticeManager.Instance.Enqueue(ResUI.PleaseFillRemarks);
                return;
            }
        }
        else
        {
            var urlHost = Utils.TryUri(url)?.IdnHost ?? string.Empty;
            var (resolvedRemarks, autoRemark) = SubscriptionInfoHelper.ResolveRemarkOnSave(
                SelectedSource.Remarks, _originalRemarks, SelectedSource.AutoRemark, urlHost);
            SelectedSource.Remarks = resolvedRemarks;
            SelectedSource.AutoRemark = autoRemark;
        }

        if (url.IsNotEmpty())
        {
            var uri = Utils.TryUri(url);
            if (uri == null)
            {
                NoticeManager.Instance.Enqueue(ResUI.InvalidUrlTip);
                return;
            }
            //Do not allow http protocol
            if (url.StartsWith(Global.HttpProtocol) && !Utils.IsPrivateNetwork(uri.IdnHost))
            {
                NoticeManager.Instance.Enqueue(ResUI.InsecureUrlProtocol);
                //return;
            }
        }

        if (await ConfigHandler.AddSubItem(_config, SelectedSource) == 0)
        {
            NoticeManager.Instance.Enqueue(ResUI.OperationSuccess);
            RequestClose?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            NoticeManager.Instance.Enqueue(ResUI.OperationFailed);
        }
    }

    private async Task<ProfileItem?> SelectProfileAsync()
    {
        var profileSelectViewModel = new ProfilesSelectViewModel();
        profileSelectViewModel.SetConfigTypeFilter([EConfigType.Custom], exclude: true);
        var result = await AppManager.Instance.WindowDialog.ShowDialogAsync(profileSelectViewModel);
        if (result != true)
        {
            return null;
        }
        var profileItem = await profileSelectViewModel.GetProfileItem();
        return profileItem;
    }
}
