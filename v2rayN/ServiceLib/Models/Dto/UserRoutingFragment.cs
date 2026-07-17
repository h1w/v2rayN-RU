namespace ServiceLib.Models.Dto;

/// <summary>
/// Пользовательские правила и всё, что они за собой притащили, сгенерированные
/// в отрыве от оболочки приложения — для вливания в пользовательский JSON.
/// </summary>
public class V2rayUserRouting
{
    public List<RulesItem4Ray> Rules { get; set; } = [];

    /// <summary>Выходы, добавленные правилами на конкретные профили (без шаблонных direct/block).</summary>
    public List<Outbounds4Ray> ExtraOutbounds { get; set; } = [];

    public List<BalancersItem4Ray>? Balancers { get; set; }
    public Observatory4Ray? Observatory { get; set; }
    public BurstObservatory4Ray? BurstObservatory { get; set; }

    /// <summary>Remarks профилей типа Custom, для которых не удалось поднять цепочку — правила на них пропущены.</summary>
    public List<string> UnsupportedCustomTargets { get; set; } = [];
}

public class SingboxUserRouting
{
    public List<Rule4Sbox> Rules { get; set; } = [];

    /// <summary>Outbounds и endpoints, добавленные правилами на конкретные профили (без шаблонного direct).</summary>
    public List<BaseServer4Sbox> ExtraServers { get; set; } = [];

    /// <summary>Remarks профилей типа Custom, для которых не удалось поднять цепочку — правила на них пропущены.</summary>
    public List<string> UnsupportedCustomTargets { get; set; } = [];
}

/// <summary>
/// Результат слияния локальных правил в пользовательский custom JSON.
/// Предупреждения возвращаются вместе с конфигом, чтобы генерация правил
/// отрабатывала ровно один раз за перегенерацию.
/// </summary>
public class CustomComposeResult
{
    /// <summary>Смерженный конфиг, либо null — признак «фолбэк на дословное копирование».</summary>
    public string? Json { get; set; }

    /// <summary>Последнее правило JSON ловит весь трафик — локальные правила недостижимы.</summary>
    public bool CatchAllDetected { get; set; }

    /// <summary>Remarks .json-профилей, для которых не удалось поднять цепочку — правила на них пропущены.</summary>
    public List<string> UnsupportedCustomTargets { get; set; } = [];
}
