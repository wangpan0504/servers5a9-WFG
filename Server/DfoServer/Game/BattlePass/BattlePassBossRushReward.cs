using System.Security.Cryptography;
using System.Text;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.BattlePass;
using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Game.BattlePass;

// 原灵魂之冢终末结算的真实段位奖励来源；入场、计分和无奖通关不等同领奖。
internal sealed class BattlePassBossRushReward
{
    readonly DungeonDefinition definition;
    readonly DungeonEntryPlan entry;
    readonly DungeonClearFact clear;
    readonly BossRushRunState rush;
    public BossRushClearReward Reward { get; }
    public DungeonRunIdentity Identity => clear.Identity;
    public TimeProvider Clock { get; }
    public DateTimeOffset PlannedAt { get; }
    public string SourceId { get; }

    BattlePassBossRushReward(DungeonDefinition definition, DungeonEntryPlan entry, DungeonClearFact clear,
        BossRushRunState rush, BossRushClearReward reward, TimeProvider clock)
    {
        this.definition=definition;this.entry=entry;this.clear=clear;this.rush=rush;Reward=reward;Clock=clock;
        PlannedAt=clock.GetUtcNow();
        var identity=FormattableString.Invariant($"boss-rush-reward:{Identity.InstanceId:N}:{Identity.CharacterId}:{Identity.QuestId}:{Identity.TaskAttempt}:{Identity.Epoch}:{entry.DungeonId}");
        SourceId=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)).AsSpan(0,16)).ToLowerInvariant();
    }

    public static BattlePassBossRushReward? Prepare(DungeonDefinition definition, DungeonEntryPlan entry,
        DungeonClearFact fact, BossRushRunState? rush, BossRushClearReward? reward, TimeProvider clock) =>
        rush is not null && reward is not null && Eligible(definition,entry) && HasReward(rush,reward) &&
        fact.DungeonId==entry.DungeonId && fact.Identity.CharacterId==entry.CharacterId &&
        fact.Identity.QuestId==0 && fact.Identity.InstanceId!=Guid.Empty && fact.ConfirmedDeathEvents.Count>0 &&
        fact.PassiveSource is null && fact.ClearMap==entry.BossMap
            ? new(definition,entry,fact,rush,reward,clock) : null;

    static bool Eligible(DungeonDefinition definition, DungeonEntryPlan entry) =>
        definition.Id==entry.DungeonId && definition.IsBossRushDungeon && entry.IsBossRush &&
        entry.IsOrdinary && entry.QuestId==0 && !entry.IsPractice && !entry.IsTutorial &&
        !entry.IsClassExperience && !entry.IsQuestReplay && !entry.IsTrainingRoom && !entry.IsTournament &&
        entry.RaidEntry is null;

    static bool HasReward(BossRushRunState rush, BossRushClearReward reward) =>
        reward is { Rank: > 0, Score: > 0, Item: { Count: > 0 } item } &&
        reward.Rank==rush.Rank && reward.Score==rush.Score && rush.Catalog.RankOf(reward.Score)==reward.Rank &&
        rush.Catalog.RankRewards.Any(row=>row.Rank==reward.Rank && row.ItemId==item.ItemId && row.Count==item.Count && row.Weight==item.Weight) &&
        reward.Delivery.Rewards is [{ RuleId: BossRushClearReward.Rule, ResourceSource: BossRushCatalog.ResourcePath, Item: { } core }] &&
        core.ItemId==item.ItemId && core.Count==item.Count &&
        (reward.Delivery.Inventory.Changed || !reward.Delivery.MailItems.IsEmpty);

    public bool Matches(SinglePlayerDungeonRun run, PreparedDungeonSettlement prepared) =>
        ReferenceEquals(entry,run.Entry) && ReferenceEquals(clear,prepared.Fact) && ReferenceEquals(Reward,prepared.BossRush) &&
        Identity==run.Snapshot.Identity && Eligible(definition,run.Entry) && HasReward(rush,Reward) && clear.ClearMap==entry.BossMap;

    public static bool MatchesMission(byte category, BattlePassMission mission) => category==0 && mission is
        { Id:8,RawTypeCode:7,RawParameter:-1,RequiredCount:1,Experience:100 };
}
