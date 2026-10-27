using System.Collections.Immutable;
using DfoServer.GameWorld.Raids;

namespace DfoServer.Game.Raids;

// 开战人数与显式进图能力；PVF 的类型、容量、阶段、费用和资源门禁保持权威。
internal sealed record RaidStartSettings
{
    public int? StartMinimumOverride { get; init; }
    public bool AntonFormalEntryEnabled { get; init; }
    // 只打开已解析的地下城状态迁移。计时原值单位未闭合，不因此发送 596 或调度超时。
    public bool AntonPhaseProgressEnabled { get; init; }

    public RaidCatalog Apply(RaidCatalog catalog)
    {
        if (StartMinimumOverride is not { } minimum) return catalog;
        if (minimum < 1 || catalog.Definitions.Values.Any(raid => minimum > raid.MemberMax))
            throw new InvalidDataException("StartMinimumOverride 必须在 1 至各攻坚队人数上限之间");
        return new(catalog.Definitions.ToImmutableDictionary(row => row.Key,
            row => row.Value with { StartMinimum = minimum }));
    }
}
