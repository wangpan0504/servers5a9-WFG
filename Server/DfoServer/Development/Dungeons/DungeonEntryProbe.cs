using DfoServer.Game.Dungeons;
using DfoServer.Network.Builders;

namespace DfoServer.Development.Dungeons;

internal enum DungeonEntryProbeMode
{
    Disabled,
    Information,
    StartMap,
    FinishLoading,
    FinishLoadingMonsters
}

internal static class DungeonEntryProbe
{
    // 诊断只委托共用纯Builder；发送候选不建立正式事实，默认seed1仅保留可重复诊断行为。
    internal static byte[] BuildDeath(ushort key) => ExclusiveDropPackets.MonsterDeath(key, [], null);
    internal static byte[] BuildFinishLoading() => DungeonPackets.LoadingNotice();
    internal static byte[] BuildPlayerState(ushort characterId) => DungeonPackets.DungeonPlayerState(characterId);
    internal static byte[] BuildInformation(DungeonEntryPlan plan) => DungeonPackets.Information(plan);
    internal static byte[] BuildStartMap(DungeonEntryPlan plan, uint seed = 1) => DungeonPackets.StartMap(plan, seed);
    internal static byte[] BuildStartMap(DungeonEntryPlan plan, DungeonMonsterPlan monsters, uint seed = 1)
        => DungeonPackets.StartMap(plan, monsters, seed);
}
