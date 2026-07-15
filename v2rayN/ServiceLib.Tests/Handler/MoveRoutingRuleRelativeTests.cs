using AwesomeAssertions;
using ServiceLib.Handler;
using ServiceLib.Models.Entities;
using Xunit;

namespace ServiceLib.Tests.Handler;

public class MoveRoutingRuleRelativeTests
{
    private static List<RulesItem> Make(params string[] ids)
        => ids.Select(id => new RulesItem { Id = id }).ToList();

    [Fact]
    public void Insert_after_target_dragging_down()
    {
        var list = Make("a", "b", "c", "d");
        var ret = ConfigHandler.MoveRoutingRuleRelative(list, 0, 2, insertAfter: true);
        ret.Should().Be(0);
        list.Select(x => x.Id).Should().Equal("b", "c", "a", "d");
    }

    [Fact]
    public void Insert_before_target_dragging_down()
    {
        var list = Make("a", "b", "c", "d");
        var ret = ConfigHandler.MoveRoutingRuleRelative(list, 0, 2, insertAfter: false);
        ret.Should().Be(0);
        list.Select(x => x.Id).Should().Equal("b", "a", "c", "d");
    }

    [Fact]
    public void Insert_before_target_dragging_up()
    {
        var list = Make("a", "b", "c", "d");
        var ret = ConfigHandler.MoveRoutingRuleRelative(list, 3, 1, insertAfter: false);
        ret.Should().Be(0);
        list.Select(x => x.Id).Should().Equal("a", "d", "b", "c");
    }

    [Fact]
    public void Insert_after_target_dragging_up()
    {
        var list = Make("a", "b", "c", "d");
        var ret = ConfigHandler.MoveRoutingRuleRelative(list, 3, 1, insertAfter: true);
        ret.Should().Be(0);
        list.Select(x => x.Id).Should().Equal("a", "b", "d", "c");
    }

    [Fact]
    public void Dropping_on_adjacent_edge_is_a_no_op()
    {
        // b is already right after a -> insert-after-a does nothing
        var afterPrev = Make("a", "b", "c");
        ConfigHandler.MoveRoutingRuleRelative(afterPrev, 1, 0, insertAfter: true).Should().Be(0);
        afterPrev.Select(x => x.Id).Should().Equal("a", "b", "c");

        // b is already right before c -> insert-before-c does nothing
        var beforeNext = Make("a", "b", "c");
        ConfigHandler.MoveRoutingRuleRelative(beforeNext, 1, 2, insertAfter: false).Should().Be(0);
        beforeNext.Select(x => x.Id).Should().Equal("a", "b", "c");
    }

    [Fact]
    public void Dropping_on_itself_is_a_no_op()
    {
        var list = Make("a", "b", "c");
        ConfigHandler.MoveRoutingRuleRelative(list, 1, 1, insertAfter: false).Should().Be(0);
        ConfigHandler.MoveRoutingRuleRelative(list, 1, 1, insertAfter: true).Should().Be(0);
        list.Select(x => x.Id).Should().Equal("a", "b", "c");
    }

    [Fact]
    public void Invalid_indices_or_null_return_minus_one_without_mutating()
    {
        var list = Make("a", "b");
        ConfigHandler.MoveRoutingRuleRelative(list, 0, 5, insertAfter: true).Should().Be(-1);
        ConfigHandler.MoveRoutingRuleRelative(list, -1, 0, insertAfter: false).Should().Be(-1);
        ConfigHandler.MoveRoutingRuleRelative(null, 0, 1, insertAfter: true).Should().Be(-1);
        list.Select(x => x.Id).Should().Equal("a", "b");
    }
}
