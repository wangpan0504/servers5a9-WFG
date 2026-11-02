using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.BattlePass;
using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Game.BattlePass;

// 原普通推荐区间是显式本服口径；扣币取整次真实提交，评分取原整场结算冻结的最终档。
internal sealed class BattlePassFormalClear
{
    readonly DungeonDefinition definition;
    readonly DungeonEntryPlan entry;
    public DungeonRunIdentity Identity { get; }
    public bool Recommended { get; }
    public int ReviveCoinsSpent { get; }
    public bool NoReviveCoin => ReviveCoinsSpent == 0;
    public byte? FinalRankGrade { get; }
    public byte MinimumSRankGrade { get; }
    public bool SRankOrAbove => FinalRankGrade is byte grade && MinimumSRankGrade > 0 && grade >= MinimumSRankGrade;
    public TimeProvider Clock { get; }
    public DateTimeOffset PlannedAt { get; }

    private BattlePassFormalClear(DungeonDefinition definition, DungeonEntryPlan entry, DungeonClearFact fact,
        int reviveCoinsSpent, TimeProvider clock, byte? finalRankGrade, byte minimumSRankGrade)
    {
        this.definition = definition; this.entry = entry; Identity = fact.Identity;
        Recommended = entry.GuildMileageEligible; ReviveCoinsSpent = reviveCoinsSpent;
        Clock = clock; PlannedAt = clock.GetUtcNow();
        FinalRankGrade = finalRankGrade; MinimumSRankGrade = minimumSRankGrade;
    }

    public static BattlePassFormalClear? Prepare(DungeonDefinition definition, DungeonEntryPlan entry,
        DungeonClearFact fact, int reviveCoinsSpent, TimeProvider clock, byte? finalRankGrade = null, byte minimumSRankGrade = 0) => reviveCoinsSpent >= 0 &&
        (finalRankGrade is null ? minimumSRankGrade == 0 : minimumSRankGrade > 0) &&
        Eligible(definition, entry) && (entry.GuildMileageEligible || reviveCoinsSpent == 0 ||
            finalRankGrade is byte grade && minimumSRankGrade > 0 && grade >= minimumSRankGrade) &&
        fact.DungeonId == entry.DungeonId && fact.Identity.CharacterId == entry.CharacterId &&
        fact.Identity.QuestId == entry.QuestId && fact.Identity.InstanceId != Guid.Empty &&
        fact.ConfirmedDeathEvents.Count > 0 ? new(definition, entry, fact, reviveCoinsSpent, clock, finalRankGrade, minimumSRankGrade) : null;

    public bool Matches(SinglePlayerDungeonRun run, PreparedDungeonSettlement prepared) =>
        ReferenceEquals(entry, run.Entry) && Identity == run.Snapshot.Identity && Identity == prepared.Fact.Identity &&
        prepared.Fact.DungeonId == run.DungeonId && Recommended == run.Entry.GuildMileageEligible &&
        Eligible(definition, run.Entry);

    static bool Eligible(DungeonDefinition definition, DungeonEntryPlan entry) => definition.Id == entry.DungeonId &&
        entry.IsOrdinary && !entry.IsPractice && !entry.IsTrainingRoom && !entry.IsTournament &&
        !entry.IsTutorial && !entry.IsClassExperience && !entry.IsQuestReplay && !entry.DisableClearReward &&
        entry.RaidEntry is null;

    public bool MatchesMission(byte category, BattlePassMission mission) => mission.RawParameter == -1 &&
        (Recommended && mission.RawTypeCode == 0 && (mission is
            { Id: 3, RequiredCount: 2, Experience: 100 } && category == 0 || mission is
            { Id: 25, RequiredCount: 6, Experience: 1000 } && category == 1 || mission is
            { Id: 51, RequiredCount: 300, Experience: 1600 } && category == 2) ||
        NoReviveCoin && mission.RawTypeCode == 5 && (mission is
            { Id: 6, RequiredCount: 2, Experience: 100 } && category == 0 || mission is
            { Id: 37, RequiredCount: 6, Experience: 1000 } && category == 1) ||
        SRankOrAbove && mission.RawTypeCode == 6 && (mission is
            { Id: 7, RequiredCount: 1, Experience: 100 } && category == 0 || mission is
            { Id: 26, RequiredCount: 3, Experience: 1000 } && category == 1));
}
