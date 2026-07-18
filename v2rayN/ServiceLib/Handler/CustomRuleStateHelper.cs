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

    /// <summary>
    /// Строит/сверяет единый порядок отображения (список токенов, тот же формат,
    /// что персистится в ProfileItem.CustomRuleState). Начинает с `savedState` (null
    /// или пустой список — обычный случай выключенного редактирования или ещё не
    /// сохранённого состояния): токены с недостижимой ссылкой (JSON-ordinal вне
    /// jsonOrdinalsFileOrder; локальный id не из localIdsRuleSetOrder) и дубликаты
    /// отбрасываются. Затем в конец дописываются непомянутые JSON-ordinal-ы (порядок
    /// файла, Enabled=true) и непомянутые локальные id (порядок RuleSet). При
    /// savedState == null/пустом результат — как раз "блок JSON, затем блок
    /// локальных, все JSON включены" — поведение по умолчанию.
    /// </summary>
    public static List<CustomRuleStateItem> BuildDisplayOrder(
        List<CustomRuleStateItem>? savedState,
        IReadOnlyList<int> jsonOrdinalsFileOrder,
        IReadOnlyList<string> localIdsRuleSetOrder)
    {
        var jsonSet = new HashSet<int>(jsonOrdinalsFileOrder);
        var localSet = new HashSet<string>(localIdsRuleSetOrder, StringComparer.Ordinal);

        var result = new List<CustomRuleStateItem>();
        var seenJson = new HashSet<int>();
        var seenLocal = new HashSet<string>(StringComparer.Ordinal);

        if (savedState != null)
        {
            foreach (var token in savedState)
            {
                if (token.LocalId == null)
                {
                    if (!jsonSet.Contains(token.Index) || !seenJson.Add(token.Index))
                    {
                        continue;
                    }
                    result.Add(new CustomRuleStateItem { Index = token.Index, Enabled = token.Enabled });
                }
                else
                {
                    if (!localSet.Contains(token.LocalId) || !seenLocal.Add(token.LocalId))
                    {
                        continue;
                    }
                    result.Add(new CustomRuleStateItem { LocalId = token.LocalId });
                }
            }
        }

        foreach (var ord in jsonOrdinalsFileOrder)
        {
            if (seenJson.Add(ord))
            {
                result.Add(new CustomRuleStateItem { Index = ord, Enabled = true });
            }
        }
        foreach (var id in localIdsRuleSetOrder)
        {
            if (seenLocal.Add(id))
            {
                result.Add(new CustomRuleStateItem { LocalId = id });
            }
        }

        return result;
    }

    /// <summary>
    /// Перемещает элемент `order[fromIndex]` так, чтобы он оказался непосредственно
    /// перед (insertAfter=false) или после (insertAfter=true) `order[targetIndex]`
    /// (индексы — в исходном, ДО удаления, пространстве индексов). Аналог
    /// ConfigHandler.MoveRoutingRuleRelative, только для токенов единого порядка.
    /// Возвращает false при некорректных индексах или самоперемещении; true — в
    /// т.ч. когда перемещение оказалось no-op (drop на соседнюю границу).
    /// </summary>
    public static bool MoveTokenRelative(List<CustomRuleStateItem> order, int fromIndex, int targetIndex, bool insertAfter)
    {
        if (order is null
            || fromIndex < 0 || fromIndex >= order.Count
            || targetIndex < 0 || targetIndex >= order.Count
            || fromIndex == targetIndex)
        {
            return false;
        }

        var insertPos = insertAfter ? targetIndex + 1 : targetIndex;
        if (insertPos == fromIndex || insertPos == fromIndex + 1)
        {
            return true; // drop на границу, смежную с текущей позицией — без изменений
        }

        var token = order[fromIndex];
        order.RemoveAt(fromIndex);
        if (insertPos > fromIndex)
        {
            insertPos--;
        }
        insertPos = Math.Clamp(insertPos, 0, order.Count);
        order.Insert(insertPos, token);
        return true;
    }

    /// <summary>
    /// Двигает локальный токен (LocalId == localId) кнопками Top/Up/Down/Bottom
    /// СРЕДИ ТОЛЬКО локальных токенов `order` — JSON-токены и сам паттерн
    /// чередования (в каких "слотах" стоят локальные записи) не трогаются, меняется
    /// только то, какая локальная запись занимает какой слот. Так кнопки остаются
    /// согласованы с единым порядком независимо от порядка элементов в `_rules` и
    /// от предыдущих cross-group drag-перемещений. Возвращает false, если
    /// localId не найден среди локальных токенов.
    /// </summary>
    public static bool MoveLocalToken(List<CustomRuleStateItem> order, string localId, EMove eMove)
    {
        var slotIndices = new List<int>();
        for (var i = 0; i < order.Count; i++)
        {
            if (order[i].LocalId != null)
            {
                slotIndices.Add(i);
            }
        }

        var localTokens = slotIndices.Select(i => order[i]).ToList();
        var pos = localTokens.FindIndex(t => t.LocalId == localId);
        if (pos < 0)
        {
            return false;
        }

        var newPos = eMove switch
        {
            EMove.Top => 0,
            EMove.Up => Math.Max(0, pos - 1),
            EMove.Down => Math.Min(localTokens.Count - 1, pos + 1),
            EMove.Bottom => localTokens.Count - 1,
            _ => pos,
        };
        if (newPos == pos)
        {
            return true; // уже на нужном краю — не ошибка
        }

        var token = localTokens[pos];
        localTokens.RemoveAt(pos);
        localTokens.Insert(newPos, token);

        for (var k = 0; k < slotIndices.Count; k++)
        {
            order[slotIndices[k]] = localTokens[k];
        }
        return true;
    }
}
