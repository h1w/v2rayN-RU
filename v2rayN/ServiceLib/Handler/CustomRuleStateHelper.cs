using ServiceLib.Models.Dto;

namespace ServiceLib.Handler;

/// <summary>
/// Чистая логика порядка и вкл/выкл JSON-правил custom-профиля. Переиспользуется
/// и в отображении (окно роутинга), и в генерации конфига ядра, чтобы ordinal-
/// нумерация и порядок совпадали.
/// </summary>
public static class CustomRuleStateHelper
{
    public static List<int> OrderedOrdinals(int count, List<CustomRuleStateItem>? state)
    {
        var result = new List<int>();
        var used = new HashSet<int>();
        if (state != null)
        {
            foreach (var s in state)
            {
                if (s.Index < 0 || s.Index >= count || used.Contains(s.Index))
                {
                    continue;
                }
                used.Add(s.Index);
                result.Add(s.Index);
            }
        }
        for (var i = 0; i < count; i++)
        {
            if (!used.Contains(i))
            {
                result.Add(i);
            }
        }
        return result;
    }

    public static bool IsEnabled(int ordinal, List<CustomRuleStateItem>? state)
    {
        if (state == null)
        {
            return true;
        }
        foreach (var s in state)
        {
            if (s.Index == ordinal)
            {
                return s.Enabled;
            }
        }
        return true;
    }

    /// <summary>
    /// Moves the element whose Id == fromId to just before/after the element whose
    /// Id == toId, keeping `items` and its parallel `ordinals` list index-aligned.
    /// No-op returning false on self-move, missing id, or size mismatch.
    /// </summary>
    public static bool ReorderPaired(List<RulesItem> items, List<int> ordinals, string? fromId, string? toId, bool insertAfter)
    {
        if (fromId is null || toId is null || fromId == toId || items.Count != ordinals.Count)
        {
            return false;
        }
        var from = items.FindIndex(t => t.Id == fromId);
        var to = items.FindIndex(t => t.Id == toId);
        if (from < 0 || to < 0)
        {
            return false;
        }
        var item = items[from];
        var ord = ordinals[from];
        items.RemoveAt(from);
        ordinals.RemoveAt(from);
        to = items.FindIndex(t => t.Id == toId);
        var insertAt = insertAfter ? to + 1 : to;
        items.Insert(insertAt, item);
        ordinals.Insert(insertAt, ord);
        return true;
    }
}
