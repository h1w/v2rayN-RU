using AwesomeAssertions;
using ServiceLib.Handler;
using ServiceLib.Models.Dto;
using ServiceLib.Models.Entities;
using Xunit;

namespace ServiceLib.Tests.Handler;

public class CustomRuleStateHelperTests
{
    [Fact]
    public void OrderedOrdinals_NullState_ReturnsNaturalOrder()
    {
        var result = CustomRuleStateHelper.OrderedOrdinals(3, null);
        result.Should().Equal(0, 1, 2);
    }

    [Fact]
    public void OrderedOrdinals_ReordersThenAppendsNew()
    {
        var state = new List<CustomRuleStateItem>
        {
            new() { Index = 2, Enabled = true },
            new() { Index = 0, Enabled = false },
        };
        // файл теперь имеет 4 правила (0..3); 1 и 3 отсутствуют в state -> в конец по возрастанию
        var result = CustomRuleStateHelper.OrderedOrdinals(4, state);
        result.Should().Equal(2, 0, 1, 3);
    }

    [Fact]
    public void OrderedOrdinals_DropsOutOfRangeAndDuplicates()
    {
        var state = new List<CustomRuleStateItem>
        {
            new() { Index = 5, Enabled = true },  // вне диапазона -> отброшен
            new() { Index = 1, Enabled = true },
            new() { Index = 1, Enabled = false }, // дубль -> игнор
        };
        var result = CustomRuleStateHelper.OrderedOrdinals(3, state);
        result.Should().Equal(1, 0, 2);
    }

    [Fact]
    public void IsEnabled_DefaultsTrueWhenAbsent()
    {
        var state = new List<CustomRuleStateItem> { new() { Index = 0, Enabled = false } };
        CustomRuleStateHelper.IsEnabled(0, state).Should().BeFalse();
        CustomRuleStateHelper.IsEnabled(1, state).Should().BeTrue();
        CustomRuleStateHelper.IsEnabled(0, null).Should().BeTrue();
    }

    private static List<RulesItem> ThreeItems() =>
        [new() { Id = "a" }, new() { Id = "b" }, new() { Id = "c" }];

    [Fact]
    public void ReorderPaired_MovesForward_KeepsOrdinalsAligned()
    {
        var items = ThreeItems();
        var ords = new List<int> { 0, 1, 2 };

        var moved = CustomRuleStateHelper.ReorderPaired(items, ords, "a", "c", insertAfter: true);

        moved.Should().BeTrue();
        items.Select(i => i.Id).Should().Equal("b", "c", "a");
        ords.Should().Equal(1, 2, 0);
    }

    [Fact]
    public void ReorderPaired_MovesBackward_KeepsOrdinalsAligned()
    {
        var items = ThreeItems();
        var ords = new List<int> { 0, 1, 2 };

        var moved = CustomRuleStateHelper.ReorderPaired(items, ords, "c", "a", insertAfter: false);

        moved.Should().BeTrue();
        items.Select(i => i.Id).Should().Equal("c", "a", "b");
        ords.Should().Equal(2, 0, 1);
    }

    [Fact]
    public void ReorderPaired_InsertAfterTrue_PlacesItemImmediatelyAfterTarget()
    {
        var items = ThreeItems();
        var ords = new List<int> { 0, 1, 2 };

        var moved = CustomRuleStateHelper.ReorderPaired(items, ords, "b", "c", insertAfter: true);

        moved.Should().BeTrue();
        items.Select(i => i.Id).Should().Equal("a", "c", "b");
        ords.Should().Equal(0, 2, 1);
    }

    [Fact]
    public void ReorderPaired_InsertAfterFalse_PlacesItemImmediatelyBeforeTarget()
    {
        var items = ThreeItems();
        var ords = new List<int> { 0, 1, 2 };

        var moved = CustomRuleStateHelper.ReorderPaired(items, ords, "a", "c", insertAfter: false);

        moved.Should().BeTrue();
        items.Select(i => i.Id).Should().Equal("b", "a", "c");
        ords.Should().Equal(1, 0, 2);
    }

    [Fact]
    public void ReorderPaired_SelfMove_ReturnsFalseAndListsUnchanged()
    {
        var items = ThreeItems();
        var ords = new List<int> { 0, 1, 2 };

        var moved = CustomRuleStateHelper.ReorderPaired(items, ords, "a", "a", insertAfter: true);

        moved.Should().BeFalse();
        items.Select(i => i.Id).Should().Equal("a", "b", "c");
        ords.Should().Equal(0, 1, 2);
    }

    [Fact]
    public void ReorderPaired_MissingId_ReturnsFalseAndListsUnchanged()
    {
        var items = ThreeItems();
        var ords = new List<int> { 0, 1, 2 };

        var moved = CustomRuleStateHelper.ReorderPaired(items, ords, "a", "missing", insertAfter: true);

        moved.Should().BeFalse();
        items.Select(i => i.Id).Should().Equal("a", "b", "c");
        ords.Should().Equal(0, 1, 2);
    }

    [Fact]
    public void ReorderPaired_SizeMismatch_ReturnsFalseAndListsUnchanged()
    {
        var items = ThreeItems();
        var ords = new List<int> { 0, 1 };

        var moved = CustomRuleStateHelper.ReorderPaired(items, ords, "a", "c", insertAfter: true);

        moved.Should().BeFalse();
        items.Select(i => i.Id).Should().Equal("a", "b", "c");
        ords.Should().Equal(0, 1);
    }
}
