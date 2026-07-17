namespace ServiceLib.Models.CoreConfigs;

/// <summary>
/// Одно цепочечное ядро: .json-профиль, на который ссылается правило роутинга,
/// поднимается отдельным процессом и отдаёт socks на локальном порту.
/// Описание готовится билдером контекста, процесс поднимает CoreManager.
/// </summary>
public record ChainCoreDescriptor
{
    /// <summary>Профиль типа Custom, чей .json исполняет это ядро.</summary>
    public required ProfileItem Node { get; init; }

    /// <summary>Ядро, которым его запускать.</summary>
    public required ECoreType CoreType { get; init; }

    /// <summary>Локальный порт, на котором это ядро отдаёт socks.</summary>
    public required int Port { get; init; }

    /// <summary>Имя файла конфига в binConfigs (без пути) — например "configChain0.json".</summary>
    public required string ConfigFileName { get; init; }
}
