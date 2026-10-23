using DfoServer.Game.Dungeons;
using DfoServer.Game.Characters;
using DfoServer.Game.Adventure;
using DfoServer.GameWorld.Adventure;
using DfoServer.Game.Town;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Quests;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    sealed class FormalSettlement(PreparedDungeonSettlement pending, DungeonSettlementPresentation presentation, Character expected)
    {
        public PreparedDungeonSettlement Pending { get; } = pending;
        public DungeonSettlementPresentation Presentation { get; } = presentation;
        public Character Expected { get; } = expected;
        public bool Committed { get; set; }
        public bool AssaultSettled { get; set; }
        public bool CrackSettled { get; set; }
        public bool ScoreScrolled { get; set; }
        public bool CardsShown { get; set; }
        public byte? SelectedCard { get; set; }
        public DungeonCardReward? Card { get; set; }
        public uint? MailId { get; set; }
        public bool AutoRevealScheduled { get; set; }
        public DungeonCardReward? BlackDiamond { get; set; }
        public DungeonCardReward? Paid { get; set; }
        public uint GainedExperience { get; set; }
        public uint ProficiencyGained { get; set; }
        public bool LuckyStarGained { get; set; }
        public int LuckyStarTotal { get; set; }
        public DungeonCardRewardPolicy? CardPolicy { get; init; }
        public DungeonCardClaimPreparation? Claim { get; set; }
    }

    public (FormalDungeonReceipt Receipt, ushort? BossKey)? EnableFormalDungeonSettlement(ushort? reportedBossKey = null) => owner.Execute(() =>
    {
        if (!Ready || Phase != SessionPhase.Settlement || formalDungeon is not { } dungeon ||
            dungeon.SettlementEnabled || dungeon.Run.ClearFact is null)
            return ((FormalDungeonReceipt, ushort?)?)null;
        var snapshot = dungeon.Run.Snapshot;
        if (!dungeon.Entry.IsTerminalMap(snapshot.CurrentMap)) return ((FormalDungeonReceipt, ushort?)?)null;
        if (dungeon.Entry.IsTutorial && dungeon.TutorialClearTarget is { } tutorialTarget)
            return (FormalReceipt(), tutorialTarget);
        // 剧情终点可以只有普通阻挡敌人；115引用真实死亡key，不将清场移除或友方NPC当成Boss死亡。
        var terminal = dungeon.Route.Rooms.Single(room => room.Room.Map == snapshot.CurrentMap).Monsters
            .Where(monster => monster.CanReportDeath && (monster.IsBoss || monster.IsBlocking) &&
                !snapshot.LivingMonsters.Contains(monster.ObjectKey) && !dungeon.Run.IsCulledMonster(monster.ObjectKey)).ToArray();
        var boss = terminal.FirstOrDefault(monster => monster.ObjectKey == reportedBossKey) ??
            terminal.FirstOrDefault(monster => monster.IsBoss) ?? terminal.FirstOrDefault();
        bool cinematic = NonCombatCinematicTerminal(dungeon) is { } movie &&
            dungeon.Run.ClearFact.ConfirmedDeathEvents.Count > 0 && Selected!.ActiveMainQuests.Any(active =>
                active.QuestId == movie.Id && active.ConditionState == 0 && QuestHuntProgress.Bound(active,dungeon.Entry));
        if (boss is null) return dungeon.Run.HasCommittedPassiveClearCondition || cinematic
            ? (FormalReceipt(), (ushort?)null) : ((FormalDungeonReceipt, ushort?)?)null;
        return (FormalReceipt(), boss.ObjectKey);
    });

    // 原Run先在真实战斗层产生ClearFact，再返回全非战斗演出终点；不从演出或友方退场创建清图。
    StoryQuest? NonCombatCinematicTerminal(FormalDungeon dungeon)
    {
        var snapshot = dungeon.Run.Snapshot;
        if (dungeon.Entry.IsTutorial || dungeon.Entry.IsClassExperience || dungeon.Entry.IsQuestReplay || dungeon.Run.HasCommittedPassiveClearCondition ||
            dungeon.Entry.RouteQuestId is 0 or >= 30000 || questCatalog is null ||
            !questCatalog.TryGet((int)dungeon.Entry.RouteQuestId,out var quest) || !QuestClientSceneProgress.DungeonCinematic(quest) ||
            quest.CinematicMapId != snapshot.CurrentMap.Id || !dungeon.Entry.IsTerminalMap(snapshot.CurrentMap) ||
            !quest.Definition!.Dungeons.Any(row => row.DungeonId == dungeon.Entry.DungeonId) ||
            !dungeon.Route.Rooms.Single(room => room.Room.Map == snapshot.CurrentMap).Monsters.All(monster => !monster.CanReportDeath))
            return null;
        return quest;
    }

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
        Events.ServerEventState? goldCardEvent = null, AdventureCatalog? adventure = null)
    {
        var capture = owner.Execute(() => (Character: Ready && Phase == SessionPhase.Settlement ? Selected : null,
            Dungeon: formalDungeon, Points: formalDungeon?.Run.CreatureExperiencePoints ?? 0,
            Prepared: formalDungeon?.Settlement is not null));
        // 宠物资源与纯成长规划在账号锁外完成；结算事务只保存冻结结果，不重新读CRE或抽取进化目标。
        InventoryPlan? creatureInventory = null;
        if (!capture.Prepared && capture.Character is { } character && capture.Points > 0 && creatures is not null &&
            capture.Dungeon?.Entry.IsTutorial != true && maximumRankPoint == rankPoint)
        {
            var grown = creatures.Experience(character, capture.Points, creatures.Clock.GetUtcNow().ToUnixTimeSeconds());
            if (grown.Plan is { Changed: true } changed) creatureInventory = changed;
        }
        return owner.Execute(() =>
    {
        if (!Ready || Selected?.Id != actorId || Phase != SessionPhase.Settlement || formalDungeon is not { } dungeon || dungeon.Entry.IsPractice ||
            dungeon.Run.ClearFact is not { } fact || dungeon.Ground.HasPendingOperation || pendingQuestCompletion is not null || HasPendingQuestScene || maximumRankPoint != rankPoint)
            return new FormalDungeonSettlementResult(DungeonRunOutcome.Rejected);
        if (!capture.Prepared && (!ReferenceEquals(Selected, capture.Character) || !ReferenceEquals(dungeon, capture.Dungeon)))
            return new FormalDungeonSettlementResult(DungeonRunOutcome.Rejected);
        // 这一演出类别须先成功发布空31；其后34清槽不撤销许可，SQL结果不明仍按原冻结结算恢复。
        if (dungeon.Settlement is null && !dungeon.SettlementEnabled && NonCombatCinematicTerminal(dungeon) is not null)
            return new FormalDungeonSettlementResult(DungeonRunOutcome.Rejected);
        var previous = Selected!;
        var reward = dungeon.Settlement?.Pending.Reward ?? rewards.Calculate(dungeons.Get(checked((int)dungeon.Entry.DungeonId)),
            dungeon.Entry.Difficulty, rankPoint);
        if (dungeon.Settlement is null)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var bonuses = PlanExperience(Selected!, ExperienceEvent.Clear, reward.BaseExperience, reward.RankExperience, now,
                dungeon.Entry.DungeonId, dungeon.Entry.ExperienceEquipmentModule);
            // 冒险团「通关经验值增加」按账号当前等级计入基础经验；新手教程不是正式通关，不参与加成。
            uint adventureBonus = adventure is null || dungeon.Entry.IsTutorial ? 0 :
                AdventureBenefitPolicy.ClearExperienceBonus(reward.BaseExperience, repository.ReadAdventureLevel(owner), adventure);
            // 没有分项时保留 default，避免“空数组”和“未记录分项”在 record 相等比较中被当成两份不同奖励。
            reward = reward with { TotalExperience = checked(bonuses.Total + adventureBonus),
                BonusParts = bonuses.Parts.IsDefaultOrEmpty ? default : bonuses.Parts,
                GrowthContractExperience = bonuses.Amount(ExperienceBonusSource.GrowthContract),
                PotionExperience = bonuses.Amount(ExperienceBonusSource.Potion), AdventureExperience = adventureBonus };
        }
        if (dungeon.Settlement is null && !(dungeon.Entry.IsTutorial && reward.TotalExperience == 0) &&
            !CharacterGrowthPlan.AddExperience(Selected!, reward.TotalExperience, growth).Success)
            return new FormalDungeonSettlementResult(DungeonRunOutcome.Rejected);
        var card = dungeon.Settlement?.Pending.Card ?? (dungeon.Settlement is null && !dungeon.Entry.IsTutorial && !dungeon.Entry.IsCosmo ? cards?.Prepare(
            dungeons.Get(checked((int)dungeon.Entry.DungeonId)), dungeon.Entry.Difficulty, fact.ConfirmedDeathEvents.Count) : null);
        var options = dungeon.Settlement?.Pending.Options;
        if (dungeon.Settlement is null && !dungeon.Entry.IsTutorial && !dungeon.Entry.IsCosmo && cards is not null)
        {
            var now = DateTimeOffset.UtcNow;
            var membership = repository.LoadCardMembership(owner,Selected!.Id,now);
            options = cards.PrepareOptions(dungeons.Get(checked((int)dungeon.Entry.DungeonId)),dungeon.Entry.Difficulty,
                fact.ConfirmedDeathEvents.Count,membership,now,goldCardEvent);
        }
        var prepared = dungeon.Run.PrepareSettlement(fact, reportBody, reward, card,options,creatureInventory);
        if (prepared.Pending is null || prepared.Outcome is not (DungeonRunOutcome.Accepted or DungeonRunOutcome.Duplicate))
            return new FormalDungeonSettlementResult(DungeonRunOutcome.Rejected);
        var settlement = dungeon.Settlement ??= new(prepared.Pending,
            new(dungeon.ClearTimeMilliseconds, rankPoint, rewards.GradeForPoint(rankPoint), dungeon.KillExperienceTotal)
            {
                MonsterGrowthContractExperience = dungeon.KillGrowthContractExperience,
                MonsterEquipmentExperience = dungeon.KillEquipmentExperience,
                MonsterBossExperience = dungeon.KillBossExperience,
                MonsterExperienceEntries = [.. dungeon.KillObjectExperience]
            }, Selected!)
            { CardPolicy = dungeon.Entry.IsCosmo ? null : cards };
        if (!ReferenceEquals(settlement.Pending, prepared.Pending))
            throw new InvalidOperationException("结算准备与本实例冻结报告不同");
        InventoryResult? inventory = null;
        AdventureProgressionCommit? adventureCommit = null;
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
                // 幸运星只按 ±5 等级区间；冒险团「推荐地下城通关」另按 95 规则加入剧情路线、地轨中心与哈林全图，团本排除。
                var definition = dungeons.Get(checked((int)dungeon.Entry.DungeonId));
                bool luckyStar = DungeonLuckyStarPolicy.IsRecommendedLevel(definition, expected.Level);
                bool recommendedClear = DungeonLuckyStarPolicy.IsRecommendedClear(
                    definition, dungeon.Entry.QuestId, dungeon.Entry.RouteQuestId, expected.Level);
                commit = repository.CommitDungeonSettlement(owner, expected, dungeon.Run, prepared.Pending, growth,
                    grantLuckyStar: luckyStar, luckyStarLimit: luckyStarLimit, recommendedClear: recommendedClear);
            }
            if (commit.Character.Id != expected.Id || commit.Character.Revision < expected.Revision ||
                dungeon.Run.ConfirmCommittedSettlement(prepared.Pending) != DungeonRunOutcome.Accepted)
                throw new InvalidOperationException("已提交通关结算不能确认原准备");
            Selected = commit.Character;
            settlement.Card = commit.Card;
            settlement.MailId = commit.MailId;
            settlement.BlackDiamond = commit.BlackDiamond ?? prepared.Pending.Options?.BlackDiamond;
            settlement.GainedExperience = commit.GainedExperience;
            settlement.ProficiencyGained = commit.ProficiencyGained;
            settlement.LuckyStarGained = commit.LuckyStarGained;
            settlement.LuckyStarTotal = commit.LuckyStarTotal;
            adventureCommit = commit.Adventure;
            if (commit.Inventory is { } plan)
                inventory = new InventoryResult(true, 0, plan)
                {
                    Receipt = new(Selected, owner.Generation, SelectionEpoch)
                    {
                        Phase = Phase,
                        DungeonIdentity = dungeon.Run.Snapshot.Identity,
                        PreviousSkillSwitchInventory = expected.SkillSwitchInventory
                    }
                };
            settlement.Committed = true;
        }
        AssaultClearResult? assaultClear = null;
        if (settlement.Committed && !settlement.AssaultSettled && !dungeon.Entry.IsTutorial &&
            dungeon.Assault is { } assault && dungeon.AssaultRewards is { } assaultServices)
        {
            // 先置位：提交异常时宁可丢失本次强袭计数/奖励也不重复发放。
            settlement.AssaultSettled = true;
            assaultClear = CommitAssaultClear(assault, assaultServices);
        }
        CrackClearResult? crackClear = null;
        if (settlement.Committed && !settlement.CrackSettled && dungeon.CrackRewards is { } crackRewards &&
            !dungeon.Entry.IsTutorial && !dungeon.Entry.IsPractice && !dungeon.Entry.IsClassExperience &&
            !dungeon.Entry.DisableClearReward &&
            dungeons.Get(checked((int)dungeon.Entry.DungeonId)).IsCrackOfDimension)
        {
            settlement.CrackSettled = true;
            crackClear = CommitCrackClear(crackRewards);
        }
        return new FormalDungeonSettlementResult(prepared.Outcome, FormalReceipt(), settlement.Pending.Reward,
            settlement.Presentation, previous) { Card = settlement.Card, Inventory = inventory, MailId = settlement.MailId,
                AssaultClear = assaultClear, CrackClear = crackClear,
                GainedExperience = settlement.GainedExperience,
                ProficiencyGained = settlement.ProficiencyGained,
                LuckyStarGained = settlement.LuckyStarGained,
                LuckyStarTotal = settlement.LuckyStarTotal,
                Adventure = adventureCommit,
                Options = settlement.Pending.Options,BlackDiamond = settlement.BlackDiamond };
        });
    }

    public FormalDungeonReceipt? AdvanceFormalDungeonCards(bool showCards) => owner.Execute(() =>
    {
        if (!Ready || Phase != SessionPhase.Settlement || formalDungeon?.Entry.IsCosmo == true ||
            formalDungeon?.Settlement is not { Committed: true } settlement)
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
        if (!Ready || Phase != SessionPhase.Settlement || formalDungeon?.Entry.IsCosmo == true ||
            formalDungeon?.Settlement is not { Committed: true, CardsShown: true } settlement ||
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
                {
                    Phase = Phase,
                    DungeonIdentity = formalDungeon!.Run.Snapshot.Identity,
                    PreviousSkillSwitchInventory = claim.Expected.SkillSwitchInventory
                }
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
        formalDungeon?.Entry.IsCosmo != true && formalDungeon?.Run.Snapshot.Identity == receipt.Snapshot.Identity;

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
                {
                    Phase = Phase,
                    DungeonIdentity = formalDungeon.Run.Snapshot.Identity,
                    PreviousQuests = before.ActiveMainQuests,
                    PreviousSkillSwitchInventory = before.SkillSwitchInventory
                }
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
        if (pendingRental is not null || pendingConsumableUse is not null || HasPendingTownQuestCommit || HasPendingFormalCard ||
            formalDungeon?.HasPendingFatigue == true || formalDungeon?.Run.HasPendingDeath == true ||
            formalDungeon?.Run.HasPendingSettlement == true || formalDungeon?.Ground.HasPendingOperation == true ||
            formalDungeon?.PendingTutorialClear is not null || pendingQuestCompletion is not null ||
            pendingQuestLifecycle is not null || HasUncommittedQuestQuickClear || HasPendingQuestScene)
            throw new InvalidOperationException("当前业务结果待确认，不能覆盖账号契约快照");
        var result = repository.SetCardMembership(owner,name,kind,expires);
        if (Selected is { } selected)
            Selected = repository.LoadCharacter(owner, selected.Slot);
        cardMembershipPublication++;
        return result;
    });

    bool HasPendingFormalCard => formalDungeon?.Settlement?.Claim is not null;
    static bool HasFinishedDungeon(FormalDungeon? dungeon) => dungeon is not null &&
        (dungeon.Settlement is { Committed: true } || (dungeon.Entry.IsPractice || dungeon.Entry.IsCosmo) &&
            dungeon.SettlementEnabled && dungeon.Run.ClearFact is not null && !dungeon.Run.HasPendingSettlement);

    bool CanUseSettlementNpcServices() => !HasPendingFormalCard && Phase == SessionPhase.Settlement && formalDungeon is { } dungeon &&
        dungeon.Settlement is { Committed: true } && dungeon.Run.ClearFact is not null &&
        !dungeon.Run.HasPendingDeath && !dungeon.Run.HasPendingSettlement && !dungeon.Ground.HasPendingOperation &&
        pendingQuestCompletion is null && !HasPendingQuestScene;

    public FormalDungeonReceipt? FormalDungeonResultFocus() => owner.Execute(() =>
        Ready && !HasPendingFormalCard && Phase == SessionPhase.Settlement && HasFinishedDungeon(formalDungeon) ? FormalReceipt() : null);

    // 强袭副本不支持原地重开（再次挑战需重新走 CMD1547 授权与扣券）；handler 据此把再次挑战按返回城镇处理，避免客户端无应答卡死。
    public bool IsFormalDungeonAssault() => owner.Execute(() => formalDungeon?.Assault is not null);
    public DungeonSelectionReceipt? OpenFormalDungeonOtherSelection() => owner.Execute<DungeonSelectionReceipt?>(() =>
    {
        if (!Ready || HasPendingFormalCard || Phase != SessionPhase.Settlement || formalDungeon?.Entry.IsCosmo != false ||
            !HasFinishedDungeon(formalDungeon))
            return null;
        // 普通结算复用自己的退场合同；不借用深渊清波或祭坛免虚弱资格。
        if (ReturnFromFormalDungeon() is null) return null;
        CurrentDungeonSelection = new(++dungeonSelectionId, SelectionEpoch, Selected!.Id, Selected.Position, Selected.Position);
        SetState(SessionPhase.MapSelect);
        return new(Selected, owner.Generation, SelectionEpoch, dungeonSelectionId);
    });

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
            prepareDrops: original.PrepareDrops, practice: expected.Entry.IsPractice, crackRewards: original.CrackRewards,
            hellParty: expected.Entry.IsCosmo);
    }

    public bool CanRetryCosmoDungeon(FormalDungeonReceipt expected) => owner.Execute(() =>
    {
        if (!Ready || !expected.Entry.IsCosmo || !CanEnterFormalDungeon(expected) ||
            formalDungeon is not { HellTicket: { } ticket, LegendTicket: { } stone, HellPieces: { } pieces } dungeon ||
            Selected is not { } character) return false;
        if (dungeon.Entry.UsesFatigue && FatigueForClient(character).Remaining == 0 ||
            debugSettings?.InfiniteFatigue != true && dungeon.Entry.StartFatigue > 0 &&
            (character.Fatigue is not { } fatigue || fatigue.Remaining < dungeon.Entry.StartFatigue ||
             FatigueForClient(character).Remaining < dungeon.Entry.StartFatigue)) return false;
        if (!towns.Areas.TryGetValue((character.Position.Town, character.Position.Area), out var area) ||
            area.DungeonWorldMap is not { } world || !towns.DungeonMaps.TryGetValue(world, out var map)) return false;
        // 只规划，不扣票；实际再挑战仍复验任务、资源，并在原入场事务内扣票与疲劳。
        var now = DateTimeOffset.UtcNow;
        var invitations = HellPartyEntryCost.Plan(character.Inventory, map.HellItemConditions, ticket.Count, pieces, now);
        return invitations.Plan is { } plan &&
            HellPartyEntryCost.Plan(plan.After, [stone.ItemId], stone.Count, pieces, now).Ticket is not null;
    });
}
