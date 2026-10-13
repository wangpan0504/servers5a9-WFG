using DfoServer.Game.Dungeons;
using DfoServer.Game.Characters;
using DfoServer.Game.Town;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;

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
        public DungeonCardReward? Card { get; set; }
        public uint? MailId { get; set; }
        public bool AutoRevealScheduled { get; set; }
        public DungeonCardReward? BlackDiamond { get; set; }
        public DungeonCardReward? Paid { get; set; }
        public uint GainedExperience { get; set; }
        public bool LuckyStarGained { get; set; }
        public int LuckyStarTotal { get; set; }
        public DungeonCardRewardPolicy? CardPolicy { get; init; }
        public DungeonCardClaimPreparation? Claim { get; set; }
    }

    public (FormalDungeonReceipt Receipt, ushort BossKey)? EnableFormalDungeonSettlement(ushort? reportedBossKey = null) => owner.Execute(() =>
    {
        if (!Ready || Phase != SessionPhase.Settlement || formalDungeon is not { } dungeon ||
            dungeon.SettlementEnabled || dungeon.Run.ClearFact is null)
            return ((FormalDungeonReceipt, ushort)?)null;
        var snapshot = dungeon.Run.Snapshot;
        if (!dungeon.Entry.IsTerminalMap(snapshot.CurrentMap)) return ((FormalDungeonReceipt, ushort)?)null;
        if (dungeon.Entry.IsTutorial && dungeon.TutorialClearTarget is { } tutorialTarget)
            return (FormalReceipt(), tutorialTarget);
        // 剧情终点可以只有普通阻挡敌人；115引用真实死亡key，不将清场移除或友方NPC当成Boss死亡。
        var terminal = dungeon.Route.Rooms.Single(room => room.Room.Map == snapshot.CurrentMap).Monsters
            .Where(monster => (monster.IsBoss || monster.IsBlocking && monster.CanReportDeath) &&
                !snapshot.LivingMonsters.Contains(monster.ObjectKey) && !dungeon.Run.IsCulledMonster(monster.ObjectKey)).ToArray();
        var boss = terminal.FirstOrDefault(monster => monster.ObjectKey == reportedBossKey) ??
            terminal.FirstOrDefault(monster => monster.IsBoss) ?? terminal.FirstOrDefault();
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
            dungeon.Run.ClearFact is null || dungeon.Run.HasPendingSettlement || !dungeon.Entry.IsTerminalMap(dungeon.Run.Snapshot.CurrentMap))
            return null;
        var boss = dungeon.Route.Rooms.Single(room => room.Room.Map == dungeon.Run.Snapshot.CurrentMap).Monsters
            .SingleOrDefault(monster => monster.IsBoss && monster.ObjectKey == bossKey);
        return boss is not null &&
            dungeon.Authority.MatchesIssuedSpawn(dungeon.Run, bossKey, spawnLevel, spawnHp) &&
            !dungeon.Run.Snapshot.LivingMonsters.Contains(bossKey) ? FormalReceipt() : null;
    });

    public FormalDungeonSettlementResult SettleFormalDungeon(ushort actorId, byte rankPoint, byte maximumRankPoint,
        byte[] reportBody, DungeonCatalog dungeons, DungeonClearExperienceCatalog rewards, CharacterGrowthCatalog growth,
        DungeonCardRewardPolicy? cards = null, int luckyStarLimit = 0,
        Events.ServerEventState? goldCardEvent = null) => owner.Execute(() =>
    {
        if (!Ready || Selected?.Id != actorId || Phase != SessionPhase.Settlement || formalDungeon is not { } dungeon ||
            dungeon.Run.ClearFact is not { } fact || dungeon.Ground.HasPendingOperation || pendingQuestCompletion is not null || HasPendingQuestScene || maximumRankPoint != rankPoint)
            return new FormalDungeonSettlementResult(DungeonRunOutcome.Rejected);
        var previous = Selected!;
        var reward = dungeon.Settlement?.Pending.Reward ?? rewards.Calculate(dungeons.Get(checked((int)dungeon.Entry.DungeonId)),
            dungeon.Entry.Difficulty, rankPoint);
        if (dungeon.Settlement is null && !(dungeon.Entry.IsTutorial && reward.TotalExperience == 0) &&
            !CharacterGrowthPlan.AddExperience(Selected!, reward.TotalExperience, growth).Success)
            return new FormalDungeonSettlementResult(DungeonRunOutcome.Rejected);
        var card = dungeon.Settlement?.Pending.Card ?? (dungeon.Settlement is null && !dungeon.Entry.IsTutorial ? cards?.Prepare(
            dungeons.Get(checked((int)dungeon.Entry.DungeonId)), dungeon.Entry.Difficulty, fact.ConfirmedDeathEvents.Count) : null);
        var options = dungeon.Settlement?.Pending.Options;
        if (dungeon.Settlement is null && !dungeon.Entry.IsTutorial && cards is not null)
        {
            var now = DateTimeOffset.UtcNow;
            var membership = repository.LoadCardMembership(owner,Selected!.Id,now);
            options = cards.PrepareOptions(dungeons.Get(checked((int)dungeon.Entry.DungeonId)),dungeon.Entry.Difficulty,
                fact.ConfirmedDeathEvents.Count,membership,now,goldCardEvent);
        }
        var prepared = dungeon.Run.PrepareSettlement(fact, reportBody, reward, card,options);
        if (prepared.Pending is null || prepared.Outcome is not (DungeonRunOutcome.Accepted or DungeonRunOutcome.Duplicate))
            return new FormalDungeonSettlementResult(DungeonRunOutcome.Rejected);
        var settlement = dungeon.Settlement ??= new(prepared.Pending,
            new(dungeon.ClearTimeMilliseconds, rankPoint, rewards.GradeForPoint(rankPoint), dungeon.KillExperienceTotal), Selected!)
            { CardPolicy = cards };
        if (!ReferenceEquals(settlement.Pending, prepared.Pending))
            throw new InvalidOperationException("结算准备与本实例冻结报告不同");
        InventoryResult? inventory = null;
        if (!settlement.Committed)
        {
            // 原owner内校验、提交并安装实际结果；异常时保留同一准备供持久receipt查询。
            var expected = settlement.Expected;
            if (!ReferenceEquals(Selected, expected) || !ReferenceEquals(formalDungeon, dungeon) || fact.Identity.CharacterId != expected.Id ||
                !dungeon.Run.IsPendingSettlement(prepared.Pending))
                throw new InvalidOperationException("通关结算owner或待提交实例已失效");
            DungeonSettlementCommit commit;
            if (dungeon.Entry.IsTutorial)
            {
                var checkpoint = TutorialCheckpoint(expected, "settlement", prepared.Pending.EventId, 0,
                    reward.TotalExperience, fact.Identity, prepared.Pending.MapId, true, growth);
                var saved = repository.CommitTutorialCheckpoint(owner, checkpoint);
                commit = new(saved.Character, saved.Duplicate, reward)
                {
                    GainedExperience = checked((uint)(saved.Character.Learning.Experience - expected.Learning.Experience))
                };
            }
            else
            {
                bool grantLuckyStar = DungeonLuckyStarPolicy.IsRecommendedLevel(
                    dungeons.Get(checked((int)dungeon.Entry.DungeonId)), expected.Level);
                commit = repository.CommitDungeonSettlement(owner, expected, dungeon.Run, prepared.Pending, growth,
                    grantLuckyStar: grantLuckyStar, luckyStarLimit: luckyStarLimit);
            }
            if (commit.Character.Id != expected.Id || commit.Character.Revision < expected.Revision ||
                dungeon.Run.ConfirmCommittedSettlement(prepared.Pending) != DungeonRunOutcome.Accepted)
                throw new InvalidOperationException("已提交通关结算不能确认原准备");
            Selected = commit.Character;
            settlement.Card = commit.Card;
            settlement.MailId = commit.MailId;
            settlement.BlackDiamond = commit.BlackDiamond ?? prepared.Pending.Options?.BlackDiamond;
            settlement.GainedExperience = commit.GainedExperience;
            settlement.LuckyStarGained = commit.LuckyStarGained;
            settlement.LuckyStarTotal = commit.LuckyStarTotal;
            if (commit.Inventory is { } plan)
                inventory = new InventoryResult(true, 0, plan)
                {
                    Receipt = new(Selected, owner.Generation, SelectionEpoch)
                        { Phase = Phase, DungeonIdentity = dungeon.Run.Snapshot.Identity }
                };
            settlement.Committed = true;
        }
        return new FormalDungeonSettlementResult(prepared.Outcome, FormalReceipt(), settlement.Pending.Reward,
            settlement.Presentation, previous) { Card = settlement.Card, Inventory = inventory, MailId = settlement.MailId,
                GainedExperience = settlement.GainedExperience,
                LuckyStarGained = settlement.LuckyStarGained,
                LuckyStarTotal = settlement.LuckyStarTotal,
                Options = settlement.Pending.Options,BlackDiamond = settlement.BlackDiamond };
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

    public FormalFreeCardResult? SelectFormalDungeonFreeCard(byte row, byte card) => owner.Execute<FormalFreeCardResult?>(() =>
    {
        if (!Ready || Phase != SessionPhase.Settlement || formalDungeon?.Settlement is not { Committed: true, CardsShown: true } settlement ||
            row != 0 || card != 0 || settlement.SelectedCard is { } old && old != card)
            return null;
        if (settlement.SelectedCard is not null)
            return new(FormalReceipt(), null, settlement.MailId, true);
        InventoryResult? inventory = null;
        bool duplicate = false;
        if (settlement.Pending.Card is { } frozen)
        {
            if (settlement.Claim is { Side: not 0 }) return null;
            var policy = settlement.CardPolicy ?? throw new InvalidOperationException("免费牌缺少发放规则");
            DungeonCardReward[] rewards = settlement.Pending.Options?.BlackDiamond is { } bonus ? [frozen, bonus] : [frozen];
            var claim = settlement.Claim ??= new(Selected!, 0, policy.PlanDelivery(Selected!, rewards, Selected!.Level), DateTimeOffset.UtcNow);
            if (!ReferenceEquals(Selected, claim.Expected)) throw new InvalidOperationException("免费牌待提交快照已变化");
            var committed = repository.CommitFreeDungeonCard(owner, claim.Expected, settlement.Pending, claim.Delivery);
            Selected = committed.Character;
            settlement.Card = committed.Reward;
            settlement.BlackDiamond = committed.BlackDiamond;
            settlement.MailId = committed.MailId;
            duplicate = committed.Duplicate;
            inventory = new(true, 0, committed.Inventory)
            {
                Receipt = new(Selected, owner.Generation, SelectionEpoch)
                    { Phase = Phase, DungeonIdentity = formalDungeon!.Run.Snapshot.Identity }
            };
            settlement.Claim = null;
        }
        settlement.SelectedCard = card;
        return new(FormalReceipt(), inventory, settlement.MailId, duplicate);
    });

    public bool ScheduleFormalDungeonCard(FormalDungeonReceipt receipt) => owner.Execute(() =>
    {
        if (!MatchesCardInstance(receipt) || formalDungeon?.Settlement is not { Committed: true, CardsShown: true,
            SelectedCard: null, AutoRevealScheduled: false, Card: not null } settlement) return false;
        settlement.AutoRevealScheduled = true;
        return true;
    });

    public FormalFreeCardResult? RevealFormalDungeonCard(FormalDungeonReceipt receipt) => owner.Execute(() =>
        MatchesCardInstance(receipt) && formalDungeon?.Settlement is { Committed: true, CardsShown: true, SelectedCard: null }
            ? SelectFormalDungeonFreeCard(0, 0) : null);

    bool MatchesCardInstance(FormalDungeonReceipt receipt) => Ready && Phase == SessionPhase.Settlement &&
        receipt.Generation == owner.Generation && receipt.SelectionEpoch == SelectionEpoch &&
        formalDungeon?.Run.Snapshot.Identity == receipt.Snapshot.Identity;

    // 牌面属于已提交实例，与之后德利拉物品服务产生的新库存revision无关。
    public bool CanPublishDungeonCard(FormalDungeonReceipt receipt) => owner.Execute(() =>
        MatchesCardInstance(receipt) && formalDungeon?.Settlement is { Committed: true, CardsShown: true, SelectedCard: 0 });

    public DungeonCardView? FormalCardState(FormalDungeonReceipt receipt) => owner.Execute(() =>
        MatchesCardInstance(receipt) && formalDungeon?.Settlement is { Committed:true } settlement
            ? new DungeonCardView(settlement.Pending.Options,settlement.SelectedCard == 0,settlement.Paid) : null);

    public bool CanPublishCardView(FormalDungeonReceipt receipt,DungeonCardView view) => owner.Execute(() =>
        MatchesCardInstance(receipt) && formalDungeon?.Settlement is { Committed:true,CardsShown:true } settlement &&
        (settlement.SelectedCard == 0) == view.FreeSelected && settlement.Paid == view.Paid);

    public FormalPaidCardResult? SelectFormalDungeonPaidCard(byte row,byte member,DungeonCardRewardPolicy cards,
        DateTimeOffset? utcNow = null) => owner.Execute(() =>
    {
        if (row is not (1 or 2) || member != 0 || !Ready || Phase != SessionPhase.Settlement || Selected is null ||
            formalDungeon?.Settlement is not { Committed:true,CardsShown:true } settlement || settlement.Pending.Options is not { } options ||
            settlement.Claim is { Side: not 1 })
            return null;
        var now = utcNow ?? DateTimeOffset.UtcNow;
        if (settlement.Paid is not null) return new FormalPaidCardResult(FormalReceipt(),settlement.Paid,null,null,true);
        if (Selected.Inventory.Gold < options.Price) return null;
        var claim = settlement.Claim ??= new(Selected, 1, cards.PlanDelivery(Selected,[options.Paid],Selected.Level,options.Price,now), now);
        var before = claim.Expected;
        if (!ReferenceEquals(Selected, before)) throw new InvalidOperationException("付费牌待提交快照已变化");
        var committed = repository.CommitPaidDungeonCard(owner,before,settlement.Pending,claim.Delivery,claim.Time);
        if (!committed.Accepted || committed.Character is null || committed.Reward is null)
        {
            settlement.Claim = null;
            return null;
        }
        Selected = committed.Character;
        settlement.Paid = committed.Reward;
        settlement.Claim = null;
        InventoryResult? inventory = committed.Inventory is { } plan ? new(true,0,plan)
        {
            Receipt = new(Selected,owner.Generation,SelectionEpoch)
                { Phase = Phase,DungeonIdentity = formalDungeon.Run.Snapshot.Identity,PreviousQuests = before.ActiveMainQuests }
        } : null;
        return new(FormalReceipt(),committed.Reward,inventory,committed.MailId,committed.Duplicate);
    });

    long cardMembershipPublication;

    public CardMembershipReceipt? CardMembershipForClient() => owner.Execute<CardMembershipReceipt?>(() =>
    {
        // 选角初始化还未Ready，但Selected和选角epoch已建立，不能遗漏这次原生状态同步。
        if (Selected is null) return null;
        var membership = repository.LoadCardMembership(owner, Selected.Id, DateTimeOffset.UtcNow);
        return new(Selected, membership, owner.Generation, SelectionEpoch, ++cardMembershipPublication);
    });

    public bool CanPublishCardMembership(CardMembershipReceipt receipt) => owner.Execute(() =>
        receipt.Generation == owner.Generation && receipt.SelectionEpoch == SelectionEpoch &&
        receipt.Publication == cardMembershipPublication && ReferenceEquals(Selected, receipt.Character));

    public CardMembership SetCardMembership(string name,CardMembershipKind kind,long expires) => owner.Execute(() =>
    {
        if (HasPendingFormalCard ||
            formalDungeon?.HasPendingFatigue == true || formalDungeon?.Run.HasPendingDeath == true ||
            formalDungeon?.Run.HasPendingSettlement == true || formalDungeon?.Ground.HasPendingOperation == true ||
            formalDungeon?.PendingTutorialClear is not null || pendingQuestCompletion is not null ||
            pendingQuestLifecycle is not null || pendingQuestQuickClear is not null || HasPendingQuestScene)
            throw new InvalidOperationException("当前业务结果待确认，不能覆盖账号契约快照");
        var result = repository.SetCardMembership(owner,name,kind,expires);
        if (Selected is { } selected)
            Selected = repository.LoadCharacter(owner, selected.Slot);
        cardMembershipPublication++;
        return result;
    });

    bool HasPendingFormalCard => formalDungeon?.Settlement?.Claim is not null;

    bool CanUseSettlementNpcServices() => !HasPendingFormalCard && Phase == SessionPhase.Settlement && formalDungeon is { } dungeon &&
        dungeon.Settlement is { Committed: true } && dungeon.Run.ClearFact is not null &&
        !dungeon.Run.HasPendingDeath && !dungeon.Run.HasPendingSettlement && !dungeon.Ground.HasPendingOperation &&
        pendingQuestCompletion is null && !HasPendingQuestScene;

    public InventoryResult SellAtShop(SellItemRequest request, InventoryShopService shop) => owner.Execute(() =>
    {
        if (!Ready || Selected is null || HasPendingQuestScene ||
            Phase != SessionPhase.Town && !(request.NpcContext == 1000 && request.WindowState == 1000 && CanUseSettlementNpcServices()))
            return InventoryResult.Reject();
        return CommitInventory(Selected, shop.Sell(Selected, request));
    });

    public FormalDungeonReceipt? FormalDungeonResultFocus() => owner.Execute(() =>
        Ready && !HasPendingFormalCard && Phase == SessionPhase.Settlement && formalDungeon?.Settlement is { Committed: true } ? FormalReceipt() : null);

    public FormalDungeonReceipt? BeginFormalDungeonOtherSelection() => owner.Execute(() =>
    {
        if (!Ready || HasPendingFormalCard || Phase != SessionPhase.Settlement || formalDungeon?.Settlement is not { Committed: true } settlement)
            return null;
        settlement.OtherSelectionRequested = true;
        return FormalReceipt();
    });

    public TownTravelReceipt? ConfirmFormalDungeonOtherSelection() => owner.Execute(() =>
        formalDungeon?.Settlement is { Committed: true, OtherSelectionRequested: true } ? ReturnFromFormalDungeon() : null);

    public FormalDungeonReceipt? RetryFormalDungeon(FormalDungeonReceipt expected, DungeonCatalog dungeons,
        DungeonRoomCatalog rooms, DungeonMonsterDropCatalog drops, ItemCatalog items, EpicPieceCatalog? pieces = null)
    {
        var original = owner.Execute(() => CanEnterFormalDungeon(expected) ? formalDungeon : null);
        if (original is null) return null;
        uint quest = DfoServer.Game.Quests.QuestEligibility.Completed(expected.Character).Contains((int)expected.Entry.QuestId) ? 0 : expected.Entry.QuestId;
        // 资源规划失败保留原结算实例；新尝试持久预留成功后才替换旧实例。
        // 复用不可变生成规则和随机源；新Run独立冻结结果，不继承旧地面对象。
        return EnterFormalDungeon(expected.Entry.DungeonId,quest,expected.Entry.Difficulty,dungeons,rooms,drops,items,
            draw: original.Draw, pieces: pieces, requiredCharacter: expected.Character,retry: expected,
            prepareDrops: original.PrepareDrops);
    }
}
