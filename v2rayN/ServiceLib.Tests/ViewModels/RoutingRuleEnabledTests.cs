using AwesomeAssertions;
using ReactiveUI.Builder;
using ServiceLib.Tests.CoreConfig;
using ServiceLib.ViewModels;
using Xunit;

namespace ServiceLib.Tests.ViewModels;

[CollectionDefinition("Routing rule dialogs", DisableParallelization = true)]
public class RoutingRuleDialogCollection;

[Collection("Routing rule dialogs")]
public class RoutingRuleEnabledTests
{
    static RoutingRuleEnabledTests()
    {
        ReactiveUI.Builder.RxAppBuilder.CreateReactiveUIBuilder().WithCoreServices().BuildApp();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InlineToggle_IsVisibleWhenOpeningDetails(bool enabled)
    {
        await WithDialogAsync(enabled, async (list, dialog) =>
        {
            list.SelectedSource.Enabled = !enabled;
            dialog.Edit = details =>
            {
                details.SelectedSource.Enabled.Should().Be(!enabled);
                return false;
            };
            await list.RuleEditAsync(false);
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConfirmedDetailsToggle_SurvivesRefreshAndReopening(bool enabled)
    {
        await WithDialogAsync(enabled, async (list, dialog) =>
        {
            dialog.Edit = details =>
            {
                details.SelectedSource.Enabled = !enabled;
                return true;
            };
            await list.RuleEditAsync(false);
            list.RefreshRulesItems();
            list.SelectedSource = list.RulesItems.Single();
            list.SelectedSource.Enabled.Should().Be(!enabled);
            dialog.Edit = details =>
            {
                details.SelectedSource.Enabled.Should().Be(!enabled);
                return false;
            };
            await list.RuleEditAsync(false);
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancelledDetailsToggle_DoesNotChangeRule(bool enabled)
    {
        await WithDialogAsync(enabled, async (list, dialog) =>
        {
            dialog.Edit = details =>
            {
                details.SelectedSource.Enabled = !enabled;
                return false;
            };
            await list.RuleEditAsync(false);
            list.SelectedSource.Enabled.Should().Be(enabled);
            dialog.Edit = details =>
            {
                details.SelectedSource.Enabled.Should().Be(enabled);
                return false;
            };
            await list.RuleEditAsync(false);
        });
    }

    private static async Task WithDialogAsync(bool enabled, Func<RoutingRuleSettingViewModel, RuleDialog, Task> scenario)
    {
        var previousConfig = AppManager.Instance.Config;
        var previousDialog = AppManager.Instance.WindowDialog;
        try
        {
            CoreConfigTestFactory.BindAppManagerConfig(CoreConfigTestFactory.CreateConfig());
            var dialog = new RuleDialog();
            AppManager.Instance.WindowDialog = dialog;
            var list = new RoutingRuleSettingViewModel(new RoutingItem
            {
                Id = "routing",
                Remarks = "Routing",
                RuleSet = JsonUtils.Serialize(new List<RulesItem>
                {
                    new() { Id = "rule", Enabled = enabled, OutboundTag = "proxy", Domain = ["example.com"] }
                })
            });
            list.SelectedSource = list.RulesItems.Single();
            await scenario(list, dialog);
        }
        finally
        {
            AppManager.Instance.WindowDialog = previousDialog;
            CoreConfigTestFactory.BindAppManagerConfig(previousConfig);
        }
    }

    private sealed class RuleDialog : IWindowDialog
    {
        public Func<RoutingRuleDetailsViewModel, bool> Edit { get; set; } = null!;

        public Task<bool> ShowDialogAsync<TViewModel>(TViewModel vm) where TViewModel : class
            => Task.FromResult(Edit((RoutingRuleDetailsViewModel)(object)vm));
    }
}
