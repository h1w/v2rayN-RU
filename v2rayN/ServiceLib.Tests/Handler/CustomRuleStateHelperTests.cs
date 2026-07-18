using AwesomeAssertions;
using ServiceLib.Enums;
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

    #region BuildDisplayOrder

    private static string Describe(CustomRuleStateItem t) => t.LocalId ?? $"json{t.Index}:{(t.Enabled ? "on" : "off")}";

    [Fact]
    public void BuildDisplayOrder_NullState_DefaultsToJsonBlockThenLocalBlock()
    {
        var result = CustomRuleStateHelper.BuildDisplayOrder(null, [0, 1], ["a", "b"]);

        result.Select(Describe).Should().Equal("json0:on", "json1:on", "a", "b");
    }

    [Fact]
    public void BuildDisplayOrder_EmptyState_SameAsNull()
    {
        var result = CustomRuleStateHelper.BuildDisplayOrder([], [0, 1], ["a", "b"]);

        result.Select(Describe).Should().Equal("json0:on", "json1:on", "a", "b");
    }

    [Fact]
    public void BuildDisplayOrder_DropsTokensWithMissingRefs()
    {
        var saved = new List<CustomRuleStateItem>
        {
            new() { Index = 5, Enabled = true },     // ordinal вне диапазона -> отброшен
            new() { LocalId = "missing" },           // локальный id отсутствует в _rules -> отброшен
        };

        var result = CustomRuleStateHelper.BuildDisplayOrder(saved, [0, 1], ["a"]);

        // оба токена отброшены -> остаются только leftover'ы: json 0, json 1, затем local a
        result.Select(Describe).Should().Equal("json0:on", "json1:on", "a");
    }

    [Fact]
    public void BuildDisplayOrder_PreservesSavedOrder_ThenAppendsLeftoversJsonThenLocal()
    {
        var saved = new List<CustomRuleStateItem>
        {
            new() { LocalId = "b" },
            new() { Index = 1, Enabled = false },
        };

        var result = CustomRuleStateHelper.BuildDisplayOrder(saved, [0, 1], ["a", "b"]);

        // порядок из saved сохранён, затем непомянутый json0 (enabled=true), затем непомянутый local a
        result.Select(Describe).Should().Equal("b", "json1:off", "json0:on", "a");
    }

    [Fact]
    public void BuildDisplayOrder_DuplicateTokens_SecondOccurrenceDropped()
    {
        var saved = new List<CustomRuleStateItem>
        {
            new() { Index = 0, Enabled = false },
            new() { Index = 0, Enabled = true },   // дубль ordinal 0 -> игнор
            new() { LocalId = "a" },
            new() { LocalId = "a" },               // дубль local a -> игнор
        };

        var result = CustomRuleStateHelper.BuildDisplayOrder(saved, [0, 1], ["a"]);

        result.Select(Describe).Should().Equal("json0:off", "a", "json1:on");
    }

    #endregion BuildDisplayOrder

    #region MoveTokenRelative

    private static List<CustomRuleStateItem> ThreeLocalTokens() =>
        [new() { LocalId = "a" }, new() { LocalId = "b" }, new() { LocalId = "c" }];

    [Fact]
    public void MoveTokenRelative_MovesForward_InsertAfterTarget()
    {
        var order = ThreeLocalTokens();

        var moved = CustomRuleStateHelper.MoveTokenRelative(order, 0, 2, insertAfter: true);

        moved.Should().BeTrue();
        order.Select(t => t.LocalId).Should().Equal("b", "c", "a");
    }

    [Fact]
    public void MoveTokenRelative_MovesBackward_InsertBeforeTarget()
    {
        var order = ThreeLocalTokens();

        var moved = CustomRuleStateHelper.MoveTokenRelative(order, 2, 0, insertAfter: false);

        moved.Should().BeTrue();
        order.Select(t => t.LocalId).Should().Equal("c", "a", "b");
    }

    [Fact]
    public void MoveTokenRelative_DropOnAdjacentEdge_IsNoOp()
    {
        var order = ThreeLocalTokens();

        // insertAfter=false, targetIndex=1 -> insertPos=1 == fromIndex(0)+1: dropping
        // right after its own current position, so nothing actually moves.
        var moved = CustomRuleStateHelper.MoveTokenRelative(order, 0, 1, insertAfter: false);

        moved.Should().BeTrue();
        order.Select(t => t.LocalId).Should().Equal("a", "b", "c");
    }

    [Fact]
    public void MoveTokenRelative_SelfIndex_ReturnsFalse()
    {
        var order = ThreeLocalTokens();

        var moved = CustomRuleStateHelper.MoveTokenRelative(order, 1, 1, insertAfter: true);

        moved.Should().BeFalse();
        order.Select(t => t.LocalId).Should().Equal("a", "b", "c");
    }

    [Fact]
    public void MoveTokenRelative_OutOfRangeIndex_ReturnsFalse()
    {
        var order = ThreeLocalTokens();

        var moved = CustomRuleStateHelper.MoveTokenRelative(order, -1, 1, insertAfter: true);

        moved.Should().BeFalse();
        order.Select(t => t.LocalId).Should().Equal("a", "b", "c");
    }

    #endregion MoveTokenRelative

    #region MoveLocalToken

    private static List<CustomRuleStateItem> InterleavedOrder() =>
    [
        new() { Index = 0, Enabled = true },  // json slot
        new() { LocalId = "a" },
        new() { LocalId = "b" },
        new() { Index = 1, Enabled = true },  // json slot
        new() { LocalId = "c" },
    ];

    [Fact]
    public void MoveLocalToken_Top_MovesToFirstLocalSlot_JsonSlotsUntouched()
    {
        var order = InterleavedOrder();

        var moved = CustomRuleStateHelper.MoveLocalToken(order, "c", EMove.Top);

        moved.Should().BeTrue();
        // json-слоты (позиции 0 и 3) остаются json0/json1; локальные слоты теперь c, a, b
        order[0].LocalId.Should().BeNull();
        order[0].Index.Should().Be(0);
        order[3].LocalId.Should().BeNull();
        order[3].Index.Should().Be(1);
        new[] { order[1].LocalId, order[2].LocalId, order[4].LocalId }.Should().Equal("c", "a", "b");
    }

    [Fact]
    public void MoveLocalToken_Down_SwapsWithNextLocalSlot()
    {
        var order = InterleavedOrder();

        var moved = CustomRuleStateHelper.MoveLocalToken(order, "a", EMove.Down);

        moved.Should().BeTrue();
        new[] { order[1].LocalId, order[2].LocalId, order[4].LocalId }.Should().Equal("b", "a", "c");
    }

    [Fact]
    public void MoveLocalToken_Bottom_MovesToLastLocalSlot()
    {
        var order = InterleavedOrder();

        var moved = CustomRuleStateHelper.MoveLocalToken(order, "a", EMove.Bottom);

        moved.Should().BeTrue();
        new[] { order[1].LocalId, order[2].LocalId, order[4].LocalId }.Should().Equal("b", "c", "a");
    }

    [Fact]
    public void MoveLocalToken_AlreadyAtEdge_ReturnsTrueNoChange()
    {
        var order = InterleavedOrder();

        var moved = CustomRuleStateHelper.MoveLocalToken(order, "a", EMove.Top);

        moved.Should().BeTrue();
        new[] { order[1].LocalId, order[2].LocalId, order[4].LocalId }.Should().Equal("a", "b", "c");
    }

    [Fact]
    public void MoveLocalToken_MissingId_ReturnsFalse()
    {
        var order = InterleavedOrder();

        var moved = CustomRuleStateHelper.MoveLocalToken(order, "missing", EMove.Top);

        moved.Should().BeFalse();
    }

    #endregion MoveLocalToken
}
