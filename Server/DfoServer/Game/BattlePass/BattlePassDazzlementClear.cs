using System.Security.Cryptography;
using System.Text;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.BattlePass;
using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Game.BattlePass;

// 仅原眩惑入场和真正终末清图组成整次来源；入场日消耗不是通关。
internal sealed class BattlePassDazzlementClear
{
    readonly DungeonDefinition definition;
    readonly DungeonEntryPlan entry;
    readonly DungeonClearFact clear;
    public DazzlementTowerAdmission Admission { get; }
    public DungeonRunIdentity Identity => clear.Identity;
    public TimeProvider Clock { get; }
    public DateTimeOffset PlannedAt { get; }
    public string SourceId { get; }

    BattlePassDazzlementClear(DungeonDefinition definition, DungeonEntryPlan entry, DungeonClearFact clear,
        DazzlementTowerAdmission admission, TimeProvider clock)
    {
        this.definition=definition;this.entry=entry;this.clear=clear;Admission=admission;Clock=clock;
        PlannedAt=clock.GetUtcNow();
        var identity=FormattableString.Invariant($"dazzlement-clear:{Identity.InstanceId:N}:{Identity.CharacterId}:{Identity.QuestId}:{Identity.TaskAttempt}:{Identity.Epoch}:{entry.DungeonId}:{admission.Week}:{admission.Day}");
        SourceId=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)).AsSpan(0,16)).ToLowerInvariant();
    }

    public static BattlePassDazzlementClear? Prepare(DungeonDefinition definition, DungeonEntryPlan entry,
        DungeonClearFact fact, DazzlementTowerAdmission? admission, TimeProvider clock) =>
        admission is { Week: > 0, Day: >= 0 } && Eligible(definition,entry) &&
        fact.DungeonId==entry.DungeonId && fact.Identity.CharacterId==entry.CharacterId &&
        fact.Identity.QuestId==0 && fact.Identity.InstanceId!=Guid.Empty && fact.ConfirmedDeathEvents.Count>0 &&
        fact.PassiveSource is null && fact.ClearMap==entry.BossMap
            ? new(definition,entry,fact,admission,clock) : null;

    static bool Eligible(DungeonDefinition definition, DungeonEntryPlan entry) =>
        definition.Id==entry.DungeonId && definition.IsDazzlementTower &&
        definition.Path.StartsWith("dungeon/towers/towerofdazzlement",StringComparison.Ordinal) &&
        entry.IsOrdinary && entry.QuestId==0 && entry.Difficulty==0 && !entry.IsPractice && !entry.IsTutorial &&
        !entry.IsClassExperience && !entry.IsQuestReplay && !entry.IsTrainingRoom && !entry.IsTournament &&
        !entry.DisableClearReward && entry.RaidEntry is null;

    public bool Matches(SinglePlayerDungeonRun run, PreparedDungeonSettlement prepared) =>
        ReferenceEquals(entry,run.Entry) && ReferenceEquals(clear,prepared.Fact) &&
        Identity==run.Snapshot.Identity && Eligible(definition,run.Entry) && clear.ClearMap==entry.BossMap;

    public static bool MatchesMission(byte category, BattlePassMission mission) => category==0 && mission is
        { Id:16,RawTypeCode:45,RawParameter:-1,RequiredCount:1,Experience:100 };
}
