using AwesomeAssertions;
using ServiceLib.Tests.CoreConfig;
using Xunit;

namespace ServiceLib.Tests.Handler;

[Collection("Routing rule dialogs")]
public class SubscriptionRuleStateTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SubscriptionRefresh_PreservesMixedRuleOrderAndDisabledState(bool editingEnabled)
    {
        var previousConfig = AppManager.Instance.Config;
        var config = CoreConfigTestFactory.CreateConfig();
        config.UiItem.EnableCustomRuleEditing = editingEnabled;
        CoreConfigTestFactory.BindAppManagerConfig(config);
        SQLiteHelper.Instance.CreateTable<ProfileItem>();
        SQLiteHelper.Instance.CreateTable<SubItem>();
        var sub = new SubItem { Id = Guid.NewGuid().ToString("N"), Remarks = "Rule state subscription" };
        const string json = """
            {"remarks":"Rule state server","inbounds":[],"outbounds":[{"protocol":"freedom","tag":"direct","settings":{}}],
             "routing":{"rules":[{"type":"field","domain":["example.com"],"outboundTag":"direct"},
                                 {"type":"field","network":"tcp,udp","outboundTag":"direct"}]}}
            """;
        const string state = """[{"Index":0,"Enabled":false},{"LocalId":"local-rule"},{"Index":1,"Enabled":true}]""";
        try
        {
            await SQLiteHelper.Instance.InsertAsync(sub);
            (await ConfigHandler.AddBatchServers(config, json, sub.Id, true)).Should().Be(1);
            var original = (await AppManager.Instance.ProfileItems(sub.Id)).Single();
            original.CustomRuleState = state;
            await SQLiteHelper.Instance.UpdateAsync(original);

            (await ConfigHandler.AddBatchServers(config, json, sub.Id, true)).Should().Be(1);
            var refreshed = (await AppManager.Instance.ProfileItems(sub.Id)).Single();
            var tokens = JsonUtils.Deserialize<List<CustomRuleStateItem>>(refreshed.CustomRuleState);
            var displayOrder = CustomRuleStateHelper.BuildDisplayOrder(tokens, [0, 1], ["local-rule"]);
            displayOrder.Select(t => t.LocalId ?? $"json-{t.Index}").Should().Equal("json-0", "local-rule", "json-1");
            displayOrder[0].Enabled.Should().BeFalse();
            var composed = JsonNode.Parse(CustomConfigComposer.ApplyCustomRuleState(json, ECoreType.Xray, tokens))!;
            composed["routing"]!["rules"]!.AsArray().Select(r => r!["network"]?.GetValue<string>()).Should().Equal("tcp,udp");
        }
        finally
        {
            await ConfigHandler.RemoveServersViaSubid(config, sub.Id, true);
            await SQLiteHelper.Instance.DeleteAsync(sub);
            CoreConfigTestFactory.BindAppManagerConfig(previousConfig);
        }
    }
}
