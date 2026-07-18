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
}
