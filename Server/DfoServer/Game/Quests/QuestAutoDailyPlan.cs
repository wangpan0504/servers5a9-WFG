using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Quests;

namespace DfoServer.Game.Quests;

// 已支持自动每日沿同一分配与周期事务；其它专用每日仍不生成活动槽。
internal sealed class QuestAutoDailyPlan
{
    private QuestAutoDailyPlan(Character expected, int day, ImmutableArray<ActiveMainQuest> active)
    { Expected = expected; Day = day; ActiveAfter = active; }

    public Character Expected { get; }
    public int Day { get; }
    public ImmutableArray<ActiveMainQuest> ActiveAfter { get; }

    public static bool Supports(StoryQuest quest) => Hunt(quest) ||
        quest is { Grade: "[auto daily mission]", MainQuest: null, RewardType: "[item]", CompleteNpc: -1,
            Definition: { Diagnostics.Length: 0, AccountQuest: false, AcceptanceRewards.Length: 0,
                IsEvent: false, HardcoreOnly: false } } &&
        QuestDungeonClearProgress.Recommended(quest) && QuestCoverage.HasOrdinaryDeclarations(quest);

    // 地区双目标共享已发布的本服兼容编码；区域必须一致，实际范围仍由完整dungeon info约束。
    internal static bool Hunt(StoryQuest quest) => quest is
        { Grade: "[auto daily mission]", Condition: "[hunt monster]", SubType: -1, MainQuest: null,
          RewardType: "[item]", CompleteNpc: -1,
          IntData: [-4,> 0,-1,-1,>= 1 and <= 511,-4,> 0,-1,-3,>= 1 and <= 511] } &&
        quest.IntData[1] == quest.IntData[6] &&
        quest.Definition is { Diagnostics.Length: 0, AccountQuest: false, AcceptanceRewards.Length: 0,
            Dungeons.Length: > 0, IsEvent: false, HardcoreOnly: false } definition &&
        definition.Dungeons.All(row => row.DungeonId > 0 && row.Parameter is >= -1 and <= 4) &&
        QuestCoverage.HasOrdinaryDeclarations(quest);

    public static bool MatchesDungeon(StoryQuest quest, uint dungeon, byte difficulty) => Hunt(quest) &&
        quest.Definition!.Dungeons.Any(row => row.DungeonId == dungeon && (row.Parameter == -1 || row.Parameter <= difficulty));

    internal static bool CountableMonster(DungeonPlannedMonster monster) => monster.AiCharacter is null &&
        monster.CanReportDeath && !monster.IsFriendly && !monster.IsDummyMapBoss && !monster.IsScriptedBoss &&
        !monster.IsSpecialChild && !monster.IsScriptedRetirement && monster.MonsterId > 0;

    public static QuestAutoDailyPlan? Prepare(Character expected, QuestCatalog catalog, int day, bool allowAssignment)
    {
        if (day < expected.QuestCycleDay) return null;
        var definitions = catalog.All.Where(Supports).OrderBy(row => row.Id).ToArray();
        if (definitions.Length == 0) return null;
        var dailyIds = definitions.Select(row => row.Id).ToHashSet();
        bool reset = day > expected.QuestCycleDay;
        var active = expected.ActiveMainQuests.Where(row => !reset || !dailyIds.Contains(row.QuestId)).ToImmutableArray();
        var eligible = expected with
        {
            ActiveMainQuests = active.ToArray(),
            CompletedQuests = expected.CompletedQuests.Where(id => !reset ||
                !expected.QuestDailyClaims.TryGetValue(id,out int period) || period >= day).ToArray()
        };
        foreach (var quest in definitions)
        {
            if (!allowAssignment || active.Length >= 30) break;
            if (!QuestEligibility.Evaluate(eligible with { ActiveMainQuests = active.ToArray() },quest,catalog).Eligible ||
                expected.QuestDailyClaims.TryGetValue(quest.Id,out int claimDay) && claimDay >= day) continue;
            byte slot = Enumerable.Range(0,30).Select(value => (byte)value).First(value => active.All(row => row.Slot != value));
            uint initial = Hunt(quest) ? QuestHuntProgress.Initial(quest) : QuestDungeonClearProgress.Initial(quest);
            active = active.Add(new(slot,checked((ushort)quest.Id),initial) { AcceptanceId = Guid.NewGuid() })
                .OrderBy(row => row.Slot).ToImmutableArray();
        }
        return active.SequenceEqual(expected.ActiveMainQuests) ? null : new(expected,day,active);
    }
}
