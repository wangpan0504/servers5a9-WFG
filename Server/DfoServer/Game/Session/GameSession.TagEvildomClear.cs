using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.TagEvildom;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Session;

internal sealed record TagEvildomClearPreparation(Character Character, long Generation, long SelectionEpoch,
    SinglePlayerDungeonRun Run, PreparedDungeonDeath Death, long Created, long ElapsedMilliseconds);
internal sealed record TagEvildomClearReceipt(FormalDungeonReceipt Dungeon, TagEvildomClearCommit Commit);

internal sealed partial class GameSession
{
    TagEvildomClearPreparation PrepareTagEvildomClear(FormalDungeon dungeon, PreparedDungeonDeath pending)
    {
        if (dungeon.PendingTagEvildomClear is { } previous)
        {
            if (!ReferenceEquals(previous.Death, pending)) throw new InvalidOperationException("同一TAG实例不能替换待提交终点");
            return previous;
        }
        if (dungeon.TagEvildomStartedTimestamp is not { } started) throw new InvalidOperationException("TAG终点缺少真实首个加载完成时点");
        var clock = timeProvider ?? TimeProvider.System;
        long elapsed = checked((long)clock.GetElapsedTime(started, clock.GetTimestamp()).TotalMilliseconds);
        return dungeon.PendingTagEvildomClear = new(Selected!, owner.Generation, SelectionEpoch, dungeon.Run, pending,
            clock.GetUtcNow().ToUnixTimeSeconds(), elapsed);
    }

    public FormalDungeonDeathResult? CommitFormalTagEvildomClear(TagEvildomClearPreparation expected,
        TagEvildomRewardCatalog catalog, ItemCatalog items, ITagEvildomClearStore store, Func<int, int>? draw = null)
    {
        var capture = owner.Execute(() => TagClearCurrent(expected) ? (Dungeon: formalDungeon, Plan: formalDungeon!.PendingTagEvildomReward, Reward: formalDungeon.TagEvildomRewardSelection) : default);
        if (capture.Dungeon is not { } dungeon) return null;
        var plan = capture.Plan;
        if (plan is null)
        {
            // PVF解释、随机与完整附件生成在owner锁外；附着后数据库忙重派复用同一个计划。
            var chosen = capture.Reward ?? TagEvildomRewardPolicy.Prepare(catalog, expected.Death.TagEvildomClear!.Admission.DungeonId,
                draw ?? System.Security.Cryptography.RandomNumberGenerator.GetInt32);
            var reward = owner.Execute(() => TagClearCurrent(expected)
                ? dungeon.TagEvildomRewardSelection ??= chosen : null);
            if (reward is null) return null;
            var attachments = TagEvildomRewardPolicy.Materialize(reward, items, DateTimeOffset.FromUnixTimeSeconds(expected.Created));
            plan = new(expected.Run, expected.Death, reward, attachments, expected.Created, expected.ElapsedMilliseconds);
        }
        return owner.Execute(() =>
        {
            if (!TagClearCurrent(expected) || !ReferenceEquals(formalDungeon, dungeon)) return null;
            dungeon.PendingTagEvildomReward ??= plan;
            var committed = store.CommitTagEvildomClear(owner, dungeon.PendingTagEvildomReward);
            var death = dungeon.Run.ConfirmCommittedTagEvildomClear(committed);
            if (death.Outcome != DungeonRunOutcome.Accepted) throw new InvalidOperationException("已提交TAG通关不能安装原终点");
            SetState(SessionPhase.Settlement);
            var receipt = new TagEvildomClearReceipt(FormalReceipt() with
                { BattlePassMissionClock = timeProvider ?? TimeProvider.System }, committed);
            dungeon.CommittedTagEvildomClear = receipt;
            return new FormalDungeonDeathResult(DungeonRunOutcome.Accepted, receipt.Dungeon, expected.Death.Death.MonsterKey)
            {
                TagEvildomClear = receipt, CulledMonsterKeys = death.CulledMonsterKeys,
                BossPositionUpdate = death.BossPositionUpdate, RoomClearFact = death.RoomClearFact
            };
        });
    }

    bool TagClearCurrent(TagEvildomClearPreparation expected) => Ready && owner.Generation == expected.Generation &&
        SelectionEpoch == expected.SelectionEpoch && ReferenceEquals(Selected, expected.Character) && Phase == SessionPhase.Dungeon &&
        formalDungeon is { } dungeon && ReferenceEquals(dungeon.Run, expected.Run) &&
        ReferenceEquals(dungeon.PendingTagEvildomClear, expected) && dungeon.Run.IsPendingDeath(expected.Death);

    public TagEvildomClearReceipt? CurrentTagEvildomClear() => owner.Execute(() =>
        formalDungeon?.CommittedTagEvildomClear is { } receipt && CanPublishTagEvildomClear(receipt) ? receipt : null);

    public bool CanPublishTagEvildomClear(TagEvildomClearReceipt receipt) => owner.Execute(() =>
        Phase == SessionPhase.Settlement && ReferenceEquals(formalDungeon?.CommittedTagEvildomClear, receipt) &&
        CanPublishFormalDungeon(receipt.Dungeon));

    public bool CanPublishTagEvildomResult(TagEvildomClearReceipt receipt) => owner.Execute(() =>
        CanPublishTagEvildomClear(receipt) && !formalDungeon!.TagEvildomResultPublished);

    public void ConfirmTagEvildomResultPublished(TagEvildomClearReceipt receipt) => owner.Execute(() =>
    {
        if (CanPublishTagEvildomClear(receipt)) formalDungeon!.TagEvildomResultPublished = true;
        return true;
    });

    public TagEvildomProjection? TagEvildomClearState(TagEvildomClearReceipt receipt, ITagEvildomConfigurationStore store,
        DateTimeOffset now) => owner.Execute(() => CanPublishTagEvildomClear(receipt) && tagEvildomPair is { } pair
            ? pair.Characters.Projection with { State = store.ReadTagEvildomState(owner, TagEvildomPeriod.At(now)) } : null);
}
