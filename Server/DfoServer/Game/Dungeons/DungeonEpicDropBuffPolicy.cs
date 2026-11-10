using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Dungeons;

internal static class DungeonEpicDropBuffPolicy
{
    public const int BonusScale = 100;
    public static bool ConsumesEntry(DungeonEntryPlan entry) => !entry.HasFreeCombatConsumption && !entry.IsTutorial;

    public static int Bonus(DungeonEpicDropBuffDefinition? buff, DungeonEntryPlan entry)
    {
        if (buff is null || !Applies(buff, entry)) return 0;
        // 特殊副本各取自己的参数；没有声明该模式就不借普通项补齐，也不把多项相加。
        return buff.Rates.GetValueOrDefault(Scope(entry.DungeonId, entry.IsInfernoParty, entry.IsHellLegend));
    }

    // 所有掉落效果统一为百分比相对增量，领主项不改变品质分布。
    public static int BossBonus(DungeonEpicDropBuffDefinition? buff, DungeonEntryPlan entry, DungeonPlannedMonster monster) =>
        buff is not null && Applies(buff, entry) && monster.IsBoss && !monster.IsFriendly && !monster.IsScriptedBoss
            ? buff?.BossDropPercent ?? 0 : 0;

    internal static bool Applies(DungeonEpicDropBuffDefinition buff, DungeonEntryPlan entry)
    {
        if (!ConsumesEntry(entry)) return false;
        if (buff.Resource is not { } resource) return true;
        return resource.DungeonScope switch
        {
            VariousBuffDungeonScope.All => true,
            VariousBuffDungeonScope.HellLegend => entry.IsHellLegend,
            VariousBuffDungeonScope.Level => entry.BasisLevel is { } level &&
                level >= resource.MinimumDungeonLevel && level <= resource.MaximumDungeonLevel,
            _ => false
        };
    }

    internal static DungeonEpicDropScope Scope(uint dungeonId, bool inferno, bool hellLegend) =>
        inferno ? DungeonEpicDropScope.Inferno : hellLegend ? DungeonEpicDropScope.HellLegend : dungeonId switch
            {
                316 => DungeonEpicDropScope.Particular,
                RiftNamedEpicDropPolicy.DaybreakDungeonId => DungeonEpicDropScope.Daybreak,
                _ => DungeonEpicDropScope.Ordinary
            };
}
