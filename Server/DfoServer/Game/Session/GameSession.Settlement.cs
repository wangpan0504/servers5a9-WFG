using DfoServer.Game.Dungeons;
using DfoServer.Game.Characters;
using DfoServer.Game.Town;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    sealed class FormalSettlement(PreparedDungeonSettlement pending, DungeonSettlementPresentation presentation, Character expected)
    {
        public PreparedDungeonSettlement Pending { get; } = pending;
        public DungeonSettlementPresentation Presentation { get; } = presentation;
        public Character Expected { get; } = expected;
        public bool Committed { get; set; }
        public bool ScoreScrolled { get; set; }
        public bool CardsShown { get; set; }
        public byte? SelectedCard { get; set; }
        public bool OtherSelectionRequested { get; set; }
    }

    public (FormalDungeonReceipt Receipt, ushort BossKey)? EnableFormalDungeonSettlement(ushort? reportedBossKey = null) => owner.Execute(() =>
    {
        if (!Ready || Phase != SessionPhase.Settlement || formalDungeon is not { } dungeon ||
            dungeon.SettlementEnabled || dungeon.Run.ClearFact is null)
            return ((FormalDungeonReceipt, ushort)?)null;
        var bosses = dungeon.Route.Rooms.Single(room => room.Room.Map == dungeon.Entry.BossMap).Monsters
            .Where(monster => monster.IsBlocking && monster.Source.RankKind == "[boss]" &&
                !dungeon.Run.Snapshot.LivingMonsters.Contains(monster.ObjectKey));
        var boss = bosses.FirstOrDefault(monster => monster.ObjectKey == reportedBossKey) ?? bosses.FirstOrDefault();
        if (boss is null) return ((FormalDungeonReceipt, ushort)?)null;
        return (FormalReceipt(), boss.ObjectKey);
    });

    public void ConfirmFormalDungeonSettlementEnabled(FormalDungeonReceipt receipt) => owner.Execute(() =>
    {
        if (Ready && receipt.Generation == owner.Generation && receipt.SelectionEpoch == SelectionEpoch &&
            formalDungeon?.Run.Snapshot.Identity == receipt.Snapshot.Identity)
            formalDungeon.SettlementEnabled = true;
        return true;
    });

    public bool CanPublishDungeonSettlement(FormalDungeonReceipt receipt) => owner.Execute(() =>
    {
        if (!Ready || receipt.Generation != owner.Generation || receipt.SelectionEpoch != SelectionEpoch ||
            formalDungeon?.Run.Snapshot.Identity != receipt.Snapshot.Identity)
            return false;
        if (!ReferenceEquals(Selected, receipt.Character))
            throw new InvalidOperationException("结算通知发布前角色快照已变化");
        return true;
    });

    // 117只是已清图实例的协调请求，不能用它创建死亡事实或发放经验。
    public FormalDungeonReceipt? ConfirmFormalDungeonBoss(ushort actorId, ushort bossKey, uint spawnLevel, ulong spawnHp) => owner.Execute(() =>
    {
        if (!Ready || Selected?.Id != actorId || Phase != SessionPhase.Settlement || formalDungeon is not { } dungeon ||
            dungeon.Run.ClearFact is null || dungeon.Run.HasPendingSettlement || dungeon.Run.Snapshot.CurrentMap != dungeon.Entry.BossMap)
            return null;
        var boss = dungeon.Route.Rooms.Single(room => room.Room.Map == dungeon.Entry.BossMap).Monsters
            .SingleOrDefault(monster => monster.IsBlocking && monster.ObjectKey == bossKey && monster.Source.RankKind == "[boss]");
        return boss is not null && spawnLevel == boss.CandidateLevel &&
            dungeon.Authority.MatchesIssuedSpawn(dungeon.Run, bossKey, spawnLevel, spawnHp) &&
            !dungeon.Run.Snapshot.LivingMonsters.Contains(bossKey) ? FormalReceipt() : null;
    });

    public FormalDungeonSettlementResult SettleFormalDungeon(ushort actorId, byte rankPoint, byte maximumRankPoint,
        byte[] reportBody, DungeonCatalog dungeons, DungeonClearExperienceCatalog rewards, CharacterGrowthCatalog growth) => owner.Execute(() =>
    {
        if (!Ready || Selected?.Id != actorId || Phase != SessionPhase.Settlement || formalDungeon is not { } dungeon ||
            dungeon.Run.ClearFact is not { } fact || dungeon.Ground.HasPendingPickup || pendingQuestCompletion is not null || maximumRankPoint != rankPoint)
            return new FormalDungeonSettlementResult(DungeonRunOutcome.Rejected);
        var previous = Selected!;
        var reward = dungeon.Settlement?.Pending.Reward ?? rewards.Calculate(dungeons.Get(checked((int)dungeon.Entry.DungeonId)),
            dungeon.Entry.Difficulty, rankPoint);
        if (dungeon.Settlement is null && !CharacterGrowthPlan.AddExperience(Selected!, reward.TotalExperience, growth).Success)
            return new FormalDungeonSettlementResult(DungeonRunOutcome.Rejected);
        var prepared = dungeon.Run.PrepareSettlement(fact, reportBody, reward);
        if (prepared.Pending is null || prepared.Outcome is not (DungeonRunOutcome.Accepted or DungeonRunOutcome.Duplicate))
            return new FormalDungeonSettlementResult(DungeonRunOutcome.Rejected);
        var settlement = dungeon.Settlement ??= new(prepared.Pending,
            new(dungeon.ClearTimeMilliseconds, rankPoint, rewards.GradeForPoint(rankPoint), dungeon.KillExperienceTotal), Selected!);
        if (!ReferenceEquals(settlement.Pending, prepared.Pending))
            throw new InvalidOperationException("结算准备与本实例冻结报告不同");
        if (!settlement.Committed)
        {
            // 原owner内校验、提交并安装实际结果；异常时保留同一准备供持久receipt查询。
            var expected = settlement.Expected;
            if (!ReferenceEquals(Selected, expected) || !ReferenceEquals(formalDungeon, dungeon) || fact.Identity.CharacterId != expected.Id ||
                !dungeon.Run.IsPendingSettlement(prepared.Pending))
                throw new InvalidOperationException("通关结算owner或待提交实例已失效");
            var commit = repository.CommitDungeonSettlement(owner, expected, dungeon.Run, prepared.Pending, growth);
            if (commit.Character.Id != expected.Id || commit.Character.Revision < expected.Revision ||
                dungeon.Run.ConfirmCommittedSettlement(prepared.Pending) != DungeonRunOutcome.Accepted)
                throw new InvalidOperationException("已提交通关结算不能确认原准备");
            Selected = commit.Character;
            settlement.Committed = true;
        }
        return new FormalDungeonSettlementResult(prepared.Outcome, FormalReceipt(), settlement.Pending.Reward,
            settlement.Presentation, previous);
    });

    public FormalDungeonReceipt? AdvanceFormalDungeonCards(bool showCards) => owner.Execute(() =>
    {
        if (!Ready || Phase != SessionPhase.Settlement || formalDungeon?.Settlement is not { Committed: true } settlement)
            return null;
        if (showCards)
        {
            if (!settlement.ScoreScrolled) return null;
            settlement.CardsShown = true;
        }
        else settlement.ScoreScrolled = true;
        return FormalReceipt();
    });

    public FormalDungeonReceipt? SelectFormalDungeonEmptyCard(byte row, byte card) => owner.Execute(() =>
    {
        if (!Ready || Phase != SessionPhase.Settlement || formalDungeon?.Settlement is not { Committed: true, CardsShown: true } settlement ||
            row != 0 || card != 0 || settlement.SelectedCard is { } old && old != card)
            return null;
        // 当前翻牌没有道具、金币或费用；确认空结果不再写角色资产。
        settlement.SelectedCard = card;
        return FormalReceipt();
    });

    public FormalDungeonReceipt? FormalDungeonResultFocus() => owner.Execute(() =>
        Ready && Phase == SessionPhase.Settlement && formalDungeon?.Settlement is { Committed: true } ? FormalReceipt() : null);

    public FormalDungeonReceipt? BeginFormalDungeonOtherSelection() => owner.Execute(() =>
    {
        if (!Ready || Phase != SessionPhase.Settlement || formalDungeon?.Settlement is not { Committed: true } settlement)
            return null;
        settlement.OtherSelectionRequested = true;
        return FormalReceipt();
    });

    public TownTravelReceipt? ConfirmFormalDungeonOtherSelection() => owner.Execute(() =>
        formalDungeon?.Settlement is { Committed: true, OtherSelectionRequested: true } ? ReturnFromFormalDungeon() : null);

    public FormalDungeonReceipt? RetryFormalDungeon(FormalDungeonReceipt expected, DungeonCatalog dungeons,
        DungeonRoomCatalog rooms, DungeonMonsterDropCatalog drops, ItemCatalog items) => owner.Execute(() =>
    {
        if (!CanPublishFormalDungeon(expected) || formalDungeon?.Settlement is not { Committed: true } ||
            ReturnFromFormalDungeon() is null)
            return null;
        return EnterFormalDungeon(expected.Entry.DungeonId, expected.Entry.QuestId, expected.Entry.Difficulty,
            dungeons, rooms, drops, items) ?? throw new InvalidOperationException("结算再挑战无法建立新实例");
    });
}
