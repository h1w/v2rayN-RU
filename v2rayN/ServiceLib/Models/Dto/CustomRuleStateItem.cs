namespace ServiceLib.Models.Dto;

/// <summary>
/// Одна запись состояния правил custom-профиля. Порядок записей в списке
/// задаёт желаемый порядок отображения/применения. Два вида токенов,
/// различаемых по LocalId:
///   • LocalId == null — JSON-правило: Index — исходный ordinal правила в
///     файле (среди не-null правил), Enabled — включено ли.
///   • LocalId != null — локальное правило: LocalId — Id локального RulesItem,
///     на которое ссылается токен; Index и Enabled в этом случае не используются.
/// Обратная совместимость: старое сериализованное состояние не содержит
/// LocalId, при десериализации он получает null — то есть трактуется как
/// JSON-правило, как и раньше.
/// </summary>
[Serializable]
public class CustomRuleStateItem
{
    public int Index { get; set; }
    public bool Enabled { get; set; }
    public string? LocalId { get; set; }
}
