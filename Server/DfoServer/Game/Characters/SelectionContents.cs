using System.Collections.Immutable;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Quests;
using DfoServer.Game.Raids;
using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Game.Characters;

// 选角只读取次数与楼层，不装载其它角色的背包、技能或任务快照。
internal readonly record struct SelectionIncount(int Dungeon, DungeonIncountPeriod Period, DungeonIncountUsage Usage,
    DungeonIncountPeriod ExtraPeriod);
internal readonly record struct SelectionRaidClear(int Kind, int Week, int Count);
internal sealed record SelectionContentsState(int AdventureLevel, DungeonSpecialEntryUsage DespairUsage,
    DungeonDespairProgress Despair, GriefTowerEntryState Grief, ImmutableArray<SelectionIncount> Incounts,
    ImmutableArray<SelectionRaidClear> RaidClears, DazzlementTowerState Dazzlement);

internal sealed record SelectionTowerCounts(int DespairRemaining, int DespairMaximum, int GriefRemaining, int GriefMaximum);
internal sealed record SelectionContents(int DespairUsed, int DespairFloor, int DimensionUsed,
    int GriefUsed, int GriefFloor, ImmutableArray<int> RaidUsed, int NormalFiendRemaining,
    int TayberrsRemaining, int DazzlementUsed, int HopeRemaining, int WarRemaining, int GuildRemaining);

internal sealed class SelectionContentsRules(TowerOfDespairCatalog despair, GriefTowerCatalog grief,
    DungeonIncountCatalog incounts, EvildomWarCatalog? war, RaidWeeklyCycle? lukeCycle = null)
{
    public SelectionTowerCounts Towers(SelectionContentsState state)
    {
        int despairMaximum = checked(despair.AccountMaximum + despair.AdventureCounts
            .Where(row => state.AdventureLevel >= row.MinimumLevel && state.AdventureLevel <= row.MaximumLevel)
            .Select(row => row.AdditionalCount).DefaultIfEmpty().Single());
        int griefMaximum = checked(grief.DeclaredAccountMaximum + grief.DeclaredAdventureBands
            .Where(row => state.AdventureLevel >= row.MinimumLevel && state.AdventureLevel <= row.MaximumLevel)
            .Sum(row => row.AdditionalCount));
        return new(Math.Max(0, despairMaximum - state.DespairUsage.AccountCharacters), despairMaximum,
            Math.Max(0, griefMaximum - state.Grief.AccountUsed), griefMaximum);
    }

    public SelectionContents Character(SelectionContentsState state, ushort character, DateTimeOffset now)
    {
        // 客户端把这两项从资源日上限中相减；重试不能把已用基础额度累加成负剩余。
        int despairUsed = state.DespairUsage.CharacterEntries > 0 || state.Despair.LastClearDay == ShopPurchaseCount.Today(now) ? 1 : 0;
        int griefUsed = state.Grief.CharacterDay == QuestCycle.Day(now) ? grief.DeclaredEnterableCount : 0;
        return new(despairUsed, state.Despair.NextLayer - 1,
            incounts.Dimension is { } dimension ? Usage(dimension.DailyRow.CountKey).DailyUsed : 0,
            griefUsed, state.Grief.Layer - 1, [.. Enumerable.Range(0, 4).Select(kind =>
                state.RaidClears.Where(row => row.Kind == kind && row.Week ==
                    (kind == 1 && lukeCycle is not null ? lukeCycle.Key(now) : DungeonIncountPolicy.Period(now).Week))
                    .Sum(row => row.Count))],
            Remaining(400000016), Remaining(291100320), state.Dazzlement.UsedToday(character, DazzlementTowerAdmission.At(now)),
            Remaining(8523), war is null ? 0 : Remaining(war.IncountDungeon, war.WeeklyLimit), Remaining(8500));

        DungeonIncountUsage Usage(int key)
        {
            var stored = state.Incounts.FirstOrDefault(row => row.Dungeon == key);
            if (stored == default) return default;
            var period = DungeonIncountPolicy.Period(now, incounts.WeeklyResetDay(key));
            var current = DungeonIncountPolicy.Current(stored.Usage, stored.Period, period);
            return current with { DailyExtra = stored.ExtraPeriod.Day == period.Day ? stored.Usage.DailyExtra : 0,
                WeeklyExtra = stored.ExtraPeriod.Week == period.Week ? stored.Usage.WeeklyExtra : 0 };
        }
        int Remaining(int dungeon, int? weeklyLimit = null)
        {
            if (incounts.Find(dungeon) is not { } row) return 0;
            var usage = Usage(row.CountKey);
            // 03B5CAxx等提示的分母取资源周上限；日额度耗尽不能把仍剩余的周额度画成0。
            if (weeklyLimit is { } maximum) row = row with { Second = maximum };
            return row.WeeklyLimit > 0 ? DungeonIncountPolicy.WeeklyRemaining(row, usage) : DungeonIncountPolicy.DailyRemaining(row, usage);
        }
    }
}
