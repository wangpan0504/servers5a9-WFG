using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Quests;

namespace DfoServer.Game.Quests;

// 地轨每日任务的本服兼容规则；不把此入口推广到其它专用每日任务。
internal sealed class QuestAutoDailyPlan
{
    internal const ushort QuestId = 13056;
    private QuestAutoDailyPlan(Character expected, int day, ImmutableArray<ActiveMainQuest> active)
    { Expected = expected; Day = day; ActiveAfter = active; }

    public Character Expected { get; }
    public int Day { get; }
    public ImmutableArray<ActiveMainQuest> ActiveAfter { get; }

    public static bool Supports(StoryQuest quest) => quest is
        { Id: QuestId, Grade: "[auto daily mission]", Condition: "[hunt monster]", SubType: -1, MainQuest: null,
          IntData: [-4,39,-1,-1,>= 1 and <= 511,-4,39,-1,-3,>= 1 and <= 511] } &&
        quest.Definition is { Diagnostics.Length: 0, AccountQuest: false, AcceptanceRewards.Length: 0,
            Dungeons.Length: > 0, IsEvent: false, HardcoreOnly: false } definition &&
        definition.Dungeons.All(row => row.DungeonId > 0 && row.Parameter is >= -1 and <= 4) &&
        QuestCoverage.HasOrdinaryDeclarations(quest);

    public static bool MatchesDungeon(StoryQuest quest, uint dungeon, byte difficulty) => Supports(quest) &&
        quest.Definition!.Dungeons.Any(row => row.DungeonId == dungeon && (row.Parameter == -1 || row.Parameter <= difficulty));

    internal static bool CountableMonster(DungeonPlannedMonster monster) => monster.AiCharacter is null &&
        monster.CanReportDeath && !monster.IsFriendly && !monster.IsDummyMapBoss && !monster.IsScriptedBoss &&
        !monster.IsSpecialChild && !monster.IsScriptedRetirement && monster.MonsterId > 0;

    public static QuestAutoDailyPlan? Prepare(Character expected, QuestCatalog catalog, int day, bool allowAssignment)
    {
        if (day < expected.QuestCycleDay || !catalog.TryGet(QuestId,out var quest) || !Supports(quest)) return null;
        bool reset = day > expected.QuestCycleDay;
        var active = expected.ActiveMainQuests.Where(row => !reset || row.QuestId != QuestId).ToImmutableArray();
        var eligible = expected with
        {
            ActiveMainQuests = active.ToArray(),
            CompletedQuests = expected.CompletedQuests.Where(id => !reset ||
                !expected.QuestDailyClaims.TryGetValue(id,out int period) || period >= day).ToArray()
        };
        if (allowAssignment && active.Length < 30 && QuestEligibility.Evaluate(eligible,quest,catalog).Eligible &&
            (!expected.QuestDailyClaims.TryGetValue(QuestId,out int claimDay) || claimDay < day))
        {
            byte slot = Enumerable.Range(0,30).Select(value => (byte)value).First(value => active.All(row => row.Slot != value));
            active = active.Add(new(slot,QuestId,QuestHuntProgress.Initial(quest)) { AcceptanceId = Guid.NewGuid() }).OrderBy(row => row.Slot).ToImmutableArray();
        }
        return active.SequenceEqual(expected.ActiveMainQuests) ? null : new(expected,day,active);
    }
}
