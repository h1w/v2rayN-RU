using AwesomeAssertions;
using ServiceLib.Handler;
using ServiceLib.Models.Dto;
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
}
