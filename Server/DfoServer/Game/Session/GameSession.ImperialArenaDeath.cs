using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.ImperialArena;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using System.Collections.Immutable;

namespace DfoServer.Game.Session;

internal sealed record ImperialArenaDeathPreparation(Character Character, long Generation, long Epoch,
    SinglePlayerDungeonRun Run, PreparedDungeonDeath Death, long Created, long ElapsedMilliseconds)
{
    // 先冻结抽样结果，再在锁外生成附件；生成失败后的重试不能重新抽奖。
    internal (ImmutableArray<ImperialArenaRewardItem> Reward, int PreviousHardRound)? Selection { get; set; }
}

internal sealed partial class GameSession
{
    ImperialArenaDeathPreparation PrepareImperialArenaDeath(FormalDungeon dungeon, PreparedDungeonDeath pending)
    {
        if (dungeon.PendingImperialArenaDeath is { } previous)
        {
            if (!ReferenceEquals(previous.Death, pending)) throw new InvalidOperationException("帝国竞技场不能替换待提交击杀");
            return previous;
        }
        if (dungeon.TagEvildomStartedTimestamp is not { } started) throw new InvalidOperationException("帝国竞技场缺少真实加载完成计时点");
        var clock = timeProvider ?? TimeProvider.System;
        return dungeon.PendingImperialArenaDeath = new(Selected!, owner.Generation, SelectionEpoch, dungeon.Run, pending,
            clock.GetUtcNow().ToUnixTimeSeconds(), checked((long)clock.GetElapsedTime(started, clock.GetTimestamp()).TotalMilliseconds));
    }

    public FormalDungeonDeathResult? CommitFormalImperialArenaDeath(ImperialArenaDeathPreparation expected,
        ImperialArenaCatalog catalog, ItemCatalog items, IImperialArenaRunStore store, Func<int, int>? draw = null)
    {
        var capture = owner.Execute(() =>
        {
            if (!ImperialDeathCurrent(expected)) return default;
            var current = formalDungeon!;
            if (expected.Selection is null)
            {
                var admission = current.ImperialArenaEntry!.Admission;
                int round = expected.Death.ImperialArenaRound!.Value;
                var opponents = admission.Route.Opponents(admission.Mode);
                var state = current.ImperialArenaEntry.Store.ReadImperialArenaState(owner, admission.Route.Period);
                var reward = state.RewardedMonsters.Contains(opponents[round - 1].MonsterId) ? [] :
                    admission.Mode == ImperialArenaMode.Hard ? ImperialArenaRewardPolicy.HardDelta(catalog, state.HardRewardRound, round) :
                    ImperialArenaRewardPolicy.Normal(catalog, round == opponents.Length, draw ?? System.Security.Cryptography.RandomNumberGenerator.GetInt32);
                expected.Selection = (reward, state.HardRewardRound);
            }
            return (Dungeon: current, Plan: current.PendingImperialArenaReward);
        });
        if (capture.Dungeon is not { } dungeon) return null;
        var plan = capture.Plan;
        if (plan is null)
        {
            var admission = dungeon.ImperialArenaEntry!.Admission;
            int round = expected.Death.ImperialArenaRound!.Value;
            var selection = expected.Selection!.Value;
            var reward = selection.Reward;
            var at = DateTimeOffset.FromUnixTimeSeconds(expected.Created);
            plan = new(expected.Run, expected.Death, admission, round, reward, ImperialArenaDeathPlan.Materialize(reward, items, at),
                expected.Created, expected.ElapsedMilliseconds, selection.PreviousHardRound);
        }
        return owner.Execute(() =>
        {
            if (!ImperialDeathCurrent(expected) || !ReferenceEquals(formalDungeon, dungeon)) return null;
            dungeon.PendingImperialArenaReward ??= plan;
            var commit = store.CommitImperialArenaDeath(owner, dungeon.PendingImperialArenaReward);
            var result = dungeon.Run.ConfirmCommittedImperialArenaDeath(commit);
            if (result.Outcome != DungeonRunOutcome.Accepted) throw new InvalidOperationException("已提交帝国竞技场击杀不能安装原事件");
            dungeon.PendingImperialArenaDeath = null;
            dungeon.PendingImperialArenaReward = null;
            SetState(dungeon.Run.Phase == SinglePlayerDungeonPhase.Settlement ? SessionPhase.Settlement : SessionPhase.Dungeon);
            return new FormalDungeonDeathResult(DungeonRunOutcome.Accepted, FormalReceipt(), expected.Death.Death.MonsterKey)
            {
                ImperialArenaDeath = commit, CulledMonsterKeys = result.CulledMonsterKeys,
                BossPositionUpdate = result.BossPositionUpdate, RoomClearFact = result.RoomClearFact
            };
        });
    }

    bool ImperialDeathCurrent(ImperialArenaDeathPreparation expected) => Ready && owner.Generation == expected.Generation &&
        SelectionEpoch == expected.Epoch && ReferenceEquals(Selected, expected.Character) && Phase == SessionPhase.Dungeon &&
        formalDungeon is { } dungeon && ReferenceEquals(dungeon.Run, expected.Run) &&
        ReferenceEquals(dungeon.PendingImperialArenaDeath, expected) && dungeon.Run.IsPendingDeath(expected.Death);
}
