namespace ServiceLib.Models.Dto;

/// <summary>
/// Одна запись состояния JSON-правил custom-профиля: Index — исходный ordinal
/// правила в файле (среди не-null правил), Enabled — включено ли. Порядок
/// записей в списке задаёт желаемый порядок отображения/применения.
/// </summary>
[Serializable]
public class CustomRuleStateItem
{
    public int Index { get; set; }
    public bool Enabled { get; set; }
}
