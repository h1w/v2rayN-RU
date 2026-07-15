using AwesomeAssertions;
using ServiceLib.Handler;
using ServiceLib.Models.Entities;
using Xunit;

namespace ServiceLib.Tests.Handler;

public class MoveRoutingRuleToTests
{
    private static List<RulesItem> Make(params string[] ids)
        => ids.Select(id => new RulesItem { Id = id }).ToList();

    [Fact]
    public void Move_down_places_item_at_target_index()
    {
        var list = Make("a", "b", "c", "d");
        var ret = ConfigHandler.MoveRoutingRuleTo(list, 0, 2);
        ret.Should().Be(0);
        list.Select(x => x.Id).Should().Equal("b", "c", "a", "d");
    }

    [Fact]
    public void Move_up_places_item_at_target_index()
    {
        var list = Make("a", "b", "c", "d");
        var ret = ConfigHandler.MoveRoutingRuleTo(list, 3, 1);
        ret.Should().Be(0);
        list.Select(x => x.Id).Should().Equal("a", "d", "b", "c");
    }

    [Fact]
    public void Invalid_indices_return_minus_one_and_do_not_mutate()
    {
        var list = Make("a", "b");
        ConfigHandler.MoveRoutingRuleTo(list, 0, 5).Should().Be(-1);
        ConfigHandler.MoveRoutingRuleTo(list, -1, 0).Should().Be(-1);
        list.Select(x => x.Id).Should().Equal("a", "b");
    }

    [Fact]
    public void Null_list_returns_minus_one()
    {
        ConfigHandler.MoveRoutingRuleTo(null, 0, 1).Should().Be(-1);
    }

    [Fact]
    public void Same_index_no_op_returns_zero_and_preserves_order()
    {
        var list = Make("a", "b", "c");
        var ret = ConfigHandler.MoveRoutingRuleTo(list, 1, 1);
        ret.Should().Be(0);
        list.Select(x => x.Id).Should().Equal("a", "b", "c");
    }
}
