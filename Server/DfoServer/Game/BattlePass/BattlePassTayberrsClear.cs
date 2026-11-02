using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.BattlePass;
using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Game.BattlePass;

// 正常目录与原整场事实共同生成；客户端map_id、评分或选牌不能创建通关来源。
internal sealed class BattlePassTayberrsClear
{
    readonly DungeonDefinition definition;
    public DungeonRunIdentity Identity { get; }
    public TimeProvider Clock { get; }
    public DateTimeOffset PlannedAt { get; }

    private BattlePassTayberrsClear(DungeonDefinition definition, DungeonClearFact fact, TimeProvider clock)
    {
        this.definition = definition; Identity = fact.Identity; Clock = clock; PlannedAt = clock.GetUtcNow();
    }

    public static BattlePassTayberrsClear? Prepare(DungeonDefinition definition, DungeonEntryPlan entry,
        DungeonClearFact fact, TimeProvider clock) => Eligible(definition, entry) && fact.DungeonId == entry.DungeonId &&
        fact.Identity.CharacterId == entry.CharacterId && fact.Identity.QuestId == entry.QuestId &&
        fact.Identity.InstanceId != Guid.Empty && fact.ConfirmedDeathEvents.Count > 0
            ? new(definition, fact, clock) : null;

    public bool Matches(SinglePlayerDungeonRun run, PreparedDungeonSettlement prepared) =>
        Identity == prepared.Fact.Identity && Identity == run.Snapshot.Identity &&
        prepared.Fact.DungeonId == run.DungeonId && Eligible(definition, run.Entry);

    static bool Eligible(DungeonDefinition definition, DungeonEntryPlan entry) => definition.IsTayberrs &&
        definition.Id == entry.DungeonId && entry.DungeonId is 291100320 or 291100322 or 5349 &&
        entry.IsOrdinary && !entry.IsPractice && !entry.IsTutorial && !entry.IsClassExperience &&
        !entry.IsQuestReplay && entry.RaidEntry is null;

    public static bool Matches(BattlePassMission definition) => definition is
        { Id: 50, RawTypeCode: 44, RawParameter: -1, RequiredCount: 4, Experience: 1000 };
}
