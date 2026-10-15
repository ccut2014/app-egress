using System.IO;
namespace TrafikRuler.Core;

/// <summary>Правило, которое хранится в настройках.</summary>
public sealed class Rule
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Полный путь к exe или только имя файла (например, "chrome.exe").
    /// Правило с одним именем срабатывает для exe с таким именем из любой папки.
    /// </summary>
    public string ProgramPath { get; set; } = "";

    /// <summary>NetworkInterface.Id (GUID адаптера). Не меняется при переименовании адаптера.</summary>
    public string AdapterId { get; set; } = "";

    /// <summary>Имя адаптера на момент сохранения, показывается, если адаптера сейчас нет.</summary>
    public string AdapterName { get; set; } = "";

    /// <summary>Блокировать трафик программы, если целевой адаптер недоступен.</summary>
    public bool KillSwitch { get; set; }

    public bool Enabled { get; set; } = true;

    public string DisplayName => Path.GetFileName(ProgramPath) is { Length: > 0 } n ? n : ProgramPath;

    public bool MatchesByNameOnly => ProgramPath.IndexOfAny(['\\', '/']) < 0;

    public bool Matches(string? imagePath)
    {
        if (string.IsNullOrEmpty(imagePath) || string.IsNullOrWhiteSpace(ProgramPath))
            return false;
        if (MatchesByNameOnly)
            return string.Equals(Path.GetFileName(imagePath), ProgramPath.Trim(), StringComparison.OrdinalIgnoreCase);
        return string.Equals(imagePath, ProgramPath.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    public Rule Clone() => (Rule)MemberwiseClone();
}

internal enum RuleState
{
    /// <summary>Правило выключено или стоит глобальная пауза.</summary>
    Disabled,
    /// <summary>Трафик идёт через целевой адаптер.</summary>
    Active,
    /// <summary>Адаптера нет, включён kill-switch: трафик блокируется.</summary>
    Blocking,
    /// <summary>Адаптера нет, kill-switch выключен: трафик идёт обычным маршрутом.</summary>
    Waiting,
}

/// <summary>Счётчики правила, которые не обнуляются при пересборке снимка.</summary>
internal sealed class RuleStats
{
    public long BytesOut;
    public long BytesIn;
    public long PacketsBlocked;
    public long ConnectionsReset;
    public int ActiveFlows;
}

/// <summary>Правило в текущем снимке маршрутизации, вместе с найденным адаптером.</summary>
internal sealed class ActiveRule
{
    public required Rule Rule { get; init; }
    public required RuleState State { get; init; }
    public AdapterInfo? Target { get; init; }
    public required RuleStats Stats { get; init; }

    public ushort MssV4 => (ushort)Math.Max(536, (Target?.Mtu ?? 1500) - 40);
    public ushort MssV6 => (ushort)Math.Max(1220, (Target?.Mtu ?? 1500) - 60);
}

/// <summary>Неизменяемый снимок правил и адаптеров для потоков обработки пакетов.</summary>
internal sealed class RoutingSnapshot
{
    public static readonly RoutingSnapshot Empty = new([], new Dictionary<uint, AdapterInfo>(), true, 0);

    public IReadOnlyList<ActiveRule> Rules { get; }
    public IReadOnlyDictionary<uint, AdapterInfo> AdaptersByIfIndex { get; }
    public bool ExcludeLocalNetworks { get; }
    public int Generation { get; }

    public RoutingSnapshot(IReadOnlyList<ActiveRule> rules, IReadOnlyDictionary<uint, AdapterInfo> adapters,
        bool excludeLocalNetworks, int generation)
    {
        Rules = rules;
        AdaptersByIfIndex = adapters;
        ExcludeLocalNetworks = excludeLocalNetworks;
        Generation = generation;
    }

    /// <summary>Перехват нужен, если хотя бы одно правило что-то делает с трафиком.</summary>
    public bool NeedsInterception => Rules.Any(r => r.State is RuleState.Active or RuleState.Blocking);

    public ActiveRule? Match(string? imagePath)
    {
        if (imagePath == null)
            return null;
        // Правило с полным путём точнее правила с одним именем.
        ActiveRule? byName = null;
        foreach (var r in Rules)
        {
            if (!r.Rule.Matches(imagePath))
                continue;
            if (!r.Rule.MatchesByNameOnly)
                return r;
            byName ??= r;
        }
        return byName;
    }
}
