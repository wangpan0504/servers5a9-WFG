using static DfoServer.Testing.TestCheck;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using DfoServer.Infrastructure;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using PvfLib;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;

namespace DfoServer;

internal static class DungeonSettlementSessionTests
{
    // 使用匹配资源与明确合成死亡，只验证正式Session和文件库，不代表实机通关。
    public static void Run(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters, ItemResources itemResources)
    {
        int before = TestCheck.Count;
        var learning = SkillLearningCatalog.Load(pvf);
        var growth = CharacterGrowthCatalog.Load(pvf);
        var kills = DungeonKillExperienceCatalog.Load(pvf);
        var dungeons = DungeonCatalog.Load(pvf);
        var rooms = DungeonRoomCatalog.Load(pvf);
        var quests = DfoServer.GameWorld.Quests.QuestCatalog.Load(pvf);
        var clear = DungeonClearExperienceCatalog.Load(pvf);
        var definition = dungeons.Get(3);
        var maps = definition.Mazes.Single(maze => maze.ConnectsQuest(3145)).MapSpecification!;
        var expectedReward = clear.Calculate(definition, 0, 99);
        CommittedSettlement();
        ReturnWithoutSettlement();
        PublicationGuard();
        RetryCollectionRules();
        FreeCards(false);
        FreeCards(true);
        LostCardResult();
        PremiumCards(false,false);
        PremiumCards(true,false);
        PremiumCards(true,true);
        MembershipExpiryAndDay();
        LostPaidResult();
        Console.WriteLine($"PASS {TestCheck.Count - before} 通关Session经验、冻结报告、空翻牌及返回断言（非实机）");

        void PremiumCards(bool member,bool full)
        {
            using var f = new Fixture("Prem"+(member ? "M" : "N")+(full ? "F" : "E"),towns,characters,learning,quests);
            if (full) f.FillEquipment(itemResources.Items.Get(10400));
            f.Session.SetCharacterGold(f.Created.Name,10000);
            if (member)
            {
                f.Session.SetCardMembership(f.Created.Name,CardMembershipKind.BlackDiamond,DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds());
                f.Session.SetCardMembership(f.Created.Name,CardMembershipKind.GoldCard,DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds());
            }
            f.SelectEntry();Enter(f);ClearRun(f);
            var previous = f.Session.Selected!;
            int draws = 0;
            var policy = PremiumPolicy(_ => {draws++;return 0;});
            f.Sql("CREATE TRIGGER premium_fail BEFORE INSERT ON character_dungeon_card_options BEGIN SELECT RAISE(ABORT,'premium rollback'); END");
            Reject<SqliteException>(() => Finish(),"会员计划末段失败回滚通关、两份免费资产和邮件");
            int frozen = draws;
            Check(Same(previous,f.Read()) && f.Settlements == 0 && f.Count("character_dungeon_card_options") == 0 && f.Count("mailbox_messages") == 0,
                "黑钻或付费计划失败不留下部分资产及资格");
            f.Sql("DROP TRIGGER premium_fail");
            var cleared = Finish();
            Check(draws == frozen && cleared.Options is not null && cleared.Options.UseGoldCard == member &&
                cleared.Options.Price == (member ? 0 : DungeonCardPriceCatalog.Load(pvf).Price(definition.BasisLevel!.Value)) && (cleared.BlackDiamond is not null) == member,
                $"首份报告冻结资源付费价格或黄金牌免费资格，黑钻单独生成额外奖励（member={member},draws={draws}/{frozen},price={cleared.Options?.Price},basis={definition.BasisLevel}）");
            Check(f.Read().Inventory.Gold == previous.Inventory.Gold + cleared.Card!.Gold + (cleared.BlackDiamond?.Gold ?? 0),
                "免费和黑钻金币均按实际入库量合并提交，不提前扣付费牌金币");
            if (full) Check(f.Count("mailbox_attachments") == 2,"满装备页黑钻和普通两份完整核心共用永久邮件且不遗漏");
            f.Session.AdvanceFormalDungeonCards(false);f.Session.AdvanceFormalDungeonCards(true);
            if (!member)
            {
                f.Session.SetCharacterGold(f.Created.Name,0);
                string beforeReject = JsonSerializer.Serialize(f.Read());
                Check(f.Session.SelectFormalDungeonPaidCard(1,0,policy) is null && JsonSerializer.Serialize(f.Read()) == beforeReject &&
                    f.Query("SELECT paid_reward_json FROM character_dungeon_card_options") is DBNull,
                    "余额不足的付费请求不写回执、不发奖、不使用会员次数");
                f.Session.SetCharacterGold(f.Created.Name,10000);
            }
            var beforePaid = f.Session.Selected!;
            f.Sql("CREATE TRIGGER paid_fail BEFORE UPDATE OF paid_reward_json ON character_dungeon_card_options BEGIN SELECT RAISE(ABORT,'paid rollback'); END");
            Reject<SqliteException>(() => f.Session.SelectFormalDungeonPaidCard(2,0,policy),"付费回执末段失败回滚扣费、奖励、邮件和黄金次数");
            Check(Same(beforePaid,f.Read()) && ReferenceEquals(beforePaid,f.Session.Selected) && f.Count("character_gold_card_uses") == 0,
                "付费失败没有金币减少或免费次数消耗");
            f.Sql("DROP TRIGGER paid_fail");
            var selected = f.Session.SelectFormalDungeonPaidCard(1,0,policy)!;
            var paid = f.Session.Selected!;
            Check(selected is not null && draws == frozen && paid.Inventory.Gold == beforePaid.Inventory.Gold - cleared.Options!.Price + selected.Reward.Gold &&
                Same(paid,f.Read()) && f.Query("SELECT paid_cost FROM character_dungeon_card_options") is long cost && cost == cleared.Options.Price,
                "付费牌同一事务先验证扣费再增加冻结奖励，不因重试重新抽牌");
            if (member) Check(f.Query("SELECT sum(used) FROM character_gold_card_uses") is 1L,"黄金会员一次成功选牌只消费一次角色当日权益");
            else Check(f.Count("character_gold_card_uses") == 0,"普通金币付费牌不消费黄金会员计数");
            var repeat = f.Session.SelectFormalDungeonPaidCard(2,0,policy);
            Check(repeat is { Duplicate:true } && repeat.Reward == selected!.Reward && Same(paid,f.Read()) &&
                f.Session.SelectFormalDungeonPaidCard(1,1,policy) is null,
                "两种付费请求选择同一本人席重复回放，其他席不取得奖励");
            f.Session.SelectFormalDungeonFreeCard(0,0);
            var view = f.Session.FormalCardState(f.Session.FormalDungeonResultFocus()!)!;
            Check(view.FreeSelected && view.Paid == selected!.Reward,"手动或自动免费揭示保留此前付费牌面，不覆盖已付款结果");
            if (full) Check(f.Count("mailbox_attachments") == 3,"满背包付费奖励也进入永久邮件，三份核心各保存一次");
            FormalDungeonSettlementResult Finish() => f.Session.SettleFormalDungeon(f.Created.Id,99,99,[10,11,12],dungeons,clear,growth,policy);
        }

        void MembershipExpiryAndDay()
        {
            using var f = new Fixture("ExpiryCards",towns,characters,learning,quests);
            var now = DateTimeOffset.UtcNow;
            f.Session.SetCardMembership(f.Created.Name,CardMembershipKind.GoldCard,now.AddDays(30).ToUnixTimeSeconds());
            f.SelectEntry();Enter(f);ClearRun(f);
            var policy = PremiumPolicy(_ => 0);
            var cleared = f.Session.SettleFormalDungeon(f.Created.Id,99,99,[13,14,15],dungeons,clear,growth,policy);
            f.Session.AdvanceFormalDungeonCards(false);f.Session.AdvanceFormalDungeonCards(true);
            f.Session.SetCardMembership(f.Created.Name,CardMembershipKind.GoldCard,0);
            var before = f.Read();
            Check(f.Session.SelectFormalDungeonPaidCard(1,0,policy,now) is null && Same(before,f.Read()) && f.Count("character_gold_card_uses") == 0,
                "已广告免费后会员被关闭时拒绝领取，不暗中按金币收费");
            f.Session.SetCardMembership(f.Created.Name,CardMembershipKind.GoldCard,now.AddDays(30).ToUnixTimeSeconds());
            int day = CardMembership.ServiceDay(now);
            f.Sql($"INSERT INTO character_gold_card_uses VALUES({f.Created.Id},'{f.Account}',{day},10)");
            Check(f.Session.SelectFormalDungeonPaidCard(2,0,policy,now) is null && Same(before,f.Read()),
                "当天权益已耗尽的已广告免费牌不多扣次数、不暗中扣金币");
            var selected = f.Session.SelectFormalDungeonPaidCard(2,0,policy,now.AddDays(1));
            Check(selected is not null && f.Query("SELECT sum(used) FROM character_gold_card_uses") is 11L &&
                f.Query("SELECT paid_cost FROM character_dungeon_card_options") is 0L,
                "跨业务日重置计数且保留上一日历史，下一日选牌使用一次免费权益");
            f.Session.ReturnFromFormalDungeon();
            Check(f.Session.SelectFormalDungeonPaidCard(1,0,policy) is null,"退出后迟到付费请求不能作用旧实例");
        }

        DungeonCardRewardPolicy PremiumPolicy(Func<int,int> draw) => new(DungeonDropRuleCatalog.Load(pvf),itemResources.DungeonDropItems,
            itemResources.Items,itemResources.GoldLimits,itemResources.EpicPieces,draw,DungeonCardPriceCatalog.Load(pvf));

        void LostPaidResult()
        {
            using var f = new Fixture("PaidLost",towns,characters,learning,quests,losePaidResult:true);
            f.Session.SetCardMembership(f.Created.Name,CardMembershipKind.GoldCard,DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds());
            f.SelectEntry();Enter(f);ClearRun(f);
            int draws = 0;
            var policy = PremiumPolicy(_ => {draws++;return 0;});
            f.Session.SettleFormalDungeon(f.Created.Id,99,99,[16,17,18],dungeons,clear,growth,policy);
            f.Session.AdvanceFormalDungeonCards(false);f.Session.AdvanceFormalDungeonCards(true);
            var before = f.Session.Selected!;
            int frozenDraws = draws;
            Reject<IOException>(() => f.Session.SelectFormalDungeonPaidCard(1,0,policy),"付费或次数消费COMMIT后返回丢失保留同一冻结准备");
            var persisted = f.Read();
            Check(ReferenceEquals(before,f.Session.Selected) && f.Query("SELECT sum(used) FROM character_gold_card_uses") is 1L,
                "结果不明时永久回执和一次黄金权益已提交，在线快照尚未安装");
            var restored = f.Session.SelectFormalDungeonPaidCard(2,0,policy);
            Check(restored is { Duplicate:true } && restored.Inventory?.Receipt is not null && Same(persisted,f.Session.Selected!) &&
                draws == frozenDraws && f.Query("SELECT sum(used) FROM character_gold_card_uses") is 1L,
                "结果恢复读取已有付费回执和实际角色，不重复随机、扣费或消费黄金次数");
        }

        void LostCardResult()
        {
            using var f = new Fixture("CardLost",towns,characters,learning,quests,loseCardResult:true);
            f.SelectEntry(); Enter(f); ClearRun(f);
            var previous = f.Session.Selected!;
            int draws = 0;
            var policy = new DungeonCardRewardPolicy(DungeonDropRuleCatalog.Load(pvf),itemResources.DungeonDropItems,
                itemResources.Items,itemResources.GoldLimits,itemResources.EpicPieces,_ => {draws++;return 0;});
            Reject<IOException>(() => SettleCard(),"COMMIT后返回结果丢失保留原在线快照及待确认实例");
            int frozenDraws = draws;
            var stored = f.Read();
            Check(ReferenceEquals(previous,f.Session.Selected) && stored.Inventory.Gold > previous.Inventory.Gold &&
                stored.Learning.Experience == previous.Learning.Experience + expectedReward.TotalExperience &&
                f.Count("character_dungeon_card_rewards") == 1 && f.Settlements == 1,
                "提交结果不明时数据库已有完整经验和免费奖励，内存未伪装成功");
            var restored = SettleCard();
            Check(draws == frozenDraws && Same(stored,f.Session.Selected!) && restored.Card is not null &&
                restored.Inventory?.Receipt is not null && f.Count("character_dungeon_card_rewards") == 1 && f.Settlements == 1,
                "原报告恢复只读真实回执，安装实际角色及库存发布回执，不再次抽牌或发资产");
            Check(SettleCard().Outcome == DungeonRunOutcome.Duplicate && Same(stored,f.Read()),
                "恢复确认后的重复报告继续保持资产和唯一回执");
            FormalDungeonSettlementResult SettleCard() => f.Session.SettleFormalDungeon(f.Created.Id,99,99,[7,8,9],dungeons,clear,growth,policy);
        }

        void FreeCards(bool full)
        {
            using var f = new Fixture(full ? "CardFull" : "CardReward", towns, characters, learning, quests);
            if (full) f.FillEquipment(itemResources.Items.Get(10400));
            f.SelectEntry();
            Enter(f);
            Check(!f.Session.SellAtShop(new(new(InventoryListType.Equipment,12),1,1000,1000,0),
                new(itemResources.Items,itemResources.Shops,itemResources.GoldLimits,itemResources.EpicPieces)).Accepted,
                "德利拉出售不能绕过未结算副本阶段");
            ClearRun(f);
            var previous = f.Session.Selected!;
            int draws = 0;
            var policy = new DungeonCardRewardPolicy(DungeonDropRuleCatalog.Load(pvf), itemResources.DungeonDropItems,
                itemResources.Items, itemResources.GoldLimits, itemResources.EpicPieces, _ => { draws++; return 0; });
            f.Sql("CREATE TRIGGER card_fail BEFORE INSERT ON character_dungeon_card_rewards BEGIN SELECT RAISE(ABORT,'card rollback'); END");
            Reject<SqliteException>(() => CardSettle(), "免费牌末段失败回滚经验、金币、物品、邮件与回执");
            int frozenDraws = draws;
            Check(Same(previous,f.Read()) && ReferenceEquals(previous,f.Session.Selected) && f.Settlements == 0 &&
                f.Count("character_dungeon_card_rewards") == 0 && f.Count("mailbox_messages") == 0,
                "翻牌SQL失败没有部分资产或成功在线快照");
            f.Sql("DROP TRIGGER card_fail");
            var result = CardSettle();
            var committed = f.Session.Selected!;
            Check(draws == frozenDraws && result.Card is { Gold: > 0, Item: not null } &&
                committed.Inventory.Gold == previous.Inventory.Gold + result.Card.Gold && Same(committed,f.Read()) &&
                f.Settlements == 1 && f.Count("character_dungeon_card_rewards") == 1,
                "同报告重试沿原随机核心提交一次金币、物品及永久回执");
            var core = result.Card!.Item!;
            if (full)
                Check(result.MailId is not null && committed.Inventory.Items.SequenceEqual(previous.Inventory.Items) &&
                    f.Count("mailbox_messages") == 1 && f.Count("mailbox_attachments") == 1 &&
                    f.Query("SELECT expires_at FROM mailbox_messages") is DBNull,
                    "满装备页不吞牌面物品，冻结完整核心进入同事务永久邮件");
            else
                Check(result.MailId is null && committed.Inventory.Items.Any(item => item.Uid > 0 && item.Core == core) &&
                    f.Count("mailbox_messages") == 0, "空位奖励使用真实UID及原随机品级完整入库");
            var replay = CardSettle();
            Check(draws == frozenDraws && replay.Card == result.Card && Same(committed,f.Read()) &&
                f.Count("character_dungeon_card_rewards") == 1 && f.Settlements == 1,
                "重复通关报告不重抽、不加经验金币、不重复邮件或物品");
            f.Session.AdvanceFormalDungeonCards(false);
            var cards = f.Session.AdvanceFormalDungeonCards(true)!;
            Check(f.Session.ScheduleFormalDungeonCard(cards) && !f.Session.ScheduleFormalDungeonCard(cards),
                "同一实例只能安排一次自动翻牌，重复70不能重置计时");
            var shop = new InventoryShopService(itemResources.Items,itemResources.Shops,itemResources.GoldLimits,itemResources.EpicPieces);
            Check(!f.Session.SellAtShop(new(new(InventoryListType.Equipment,12),1,317,95,0),shop).Accepted,
                "结算阶段不开放任意城镇商店或伪造NPC窗口");
            var sold = f.Session.SellAtShop(new(new(InventoryListType.Equipment,12),1,1000,1000,0),shop);
            Check(sold.Accepted && sold.Receipt?.Phase == SessionPhase.Settlement &&
                f.Session.Selected!.Inventory.Find(new(InventoryListType.Equipment,12)) is null && Same(f.Session.Selected,f.Read()),
                "德利拉1000/1000结算窗口实际扣除物品并同事务增加金币");
            var beforeFlip = f.Session.Selected!;
            Check(f.Session.RevealFormalDungeonCard(cards) is not null && f.Session.RevealFormalDungeonCard(cards) is null &&
                f.Session.SelectFormalDungeonFreeCard(0,0) is not null && ReferenceEquals(beforeFlip,f.Session.Selected) &&
                f.Count("character_dungeon_card_rewards") == 1,
                "出售后的当前角色可揭示原实例，自动/手动重复只展示一次奖励且不写资产");
            f.Session.ReturnFromFormalDungeon();
            Check(f.Session.RevealFormalDungeonCard(cards) is null && Same(beforeFlip,f.Read()),
                "回城后旧计时及旧牌面不影响新阶段，重开库保留全部已提交资产");

            FormalDungeonSettlementResult CardSettle() => f.Session.SettleFormalDungeon(f.Created.Id,99,99,[4,5,6],dungeons,clear,growth,policy);
        }

        void CommittedSettlement()
        {
            using var f = new Fixture("ClearSession", towns, characters, learning, quests);
            const uint expectedKills = 27 * 93 + 137;
            // 选择合法角色初值，使结算而非最后一次击杀跨越等级边界。
            ulong midpoint = expectedKills + expectedReward.TotalExperience / 2UL;
            ulong boundary = growth.CumulativeExperience.Skip(1).Take(CharacterGrowthCatalog.MaximumSupportedLevel - 1)
                .First(value => value >= growth.GetLevelStartExperience(3) + midpoint);
            ulong initialExperience = boundary - midpoint;
            byte initialLevel = checked((byte)growth.CumulativeExperience.Take(CharacterGrowthCatalog.MaximumSupportedLevel)
                .TakeWhile(value => value <= initialExperience).Count());
            f.Sql($"UPDATE characters SET level={initialLevel},experience={initialExperience} WHERE id={f.Created.Id}");
            f.Sql($"UPDATE character_skill_points SET sp=sp+{growth.GetSpDelta(1, initialLevel)} WHERE character_id={f.Created.Id}");
            f.SelectEntry();
            var entry = Enter(f);
            byte[] report = [1, 2, 3, 4];
            Check(f.Session.ConfirmFormalDungeonBoss(f.Created.Id, entry.Monsters.Monsters[0].ObjectKey, 3, 504) is null &&
                Settle(f, report).Outcome == DungeonRunOutcome.Rejected && f.Settlements == 0,
                "未加载未清图的Boss协调和结果报告不创建通关事实或奖励");
            Check(f.Session.AdvanceFormalDungeonCards(false) is null && f.Session.AdvanceFormalDungeonCards(true) is null &&
                f.Session.SelectFormalDungeonFreeCard(0, 0) is null && f.Session.FormalDungeonResultFocus() is null,
                "结算前不开放评分、翻牌和结果焦点");
            var completed = ClearRun(f);
            var killed = f.Session.Selected!;
            Check(completed.Experience == expectedKills && killed.Learning.Experience == initialExperience + expectedKills &&
                f.Kills == completed.Count && f.Settlements == 0 && f.Session.Phase == SessionPhase.Settlement,
                "六房正式确认只落击杀经验，通关奖励等待结果报告");
            Check(f.Session.ConfirmFormalDungeonBoss((ushort)(f.Created.Id + 1), completed.Boss.ObjectKey, 6, 0) is null &&
                f.Session.ConfirmFormalDungeonBoss(f.Created.Id, (ushort)(completed.Boss.ObjectKey + 1), 6, 0) is null &&
                f.Session.ConfirmFormalDungeonBoss(f.Created.Id, completed.Boss.ObjectKey, 4, 0) is null &&
                f.Session.ConfirmFormalDungeonBoss(f.Created.Id, completed.Boss.ObjectKey, 6, 1) is null,
                "Boss协调绑定当前角色、已死亡Boss key及已接受报告的等级/HP原值");
            var bossReceipt = f.Session.ConfirmFormalDungeonBoss(f.Created.Id, completed.Boss.ObjectKey, 6, 0);
            Check(bossReceipt is not null && f.Session.ConfirmFormalDungeonBoss(f.Created.Id, completed.Boss.ObjectKey, 6, 0) is not null &&
                ReferenceEquals(killed, f.Session.Selected) && Same(killed, f.Read()) && f.Settlements == 0,
                "不同生成等级及零HP的已接受报告可协调Boss，重复不击杀、不加经验或写通关账本");
            var enable = f.Session.EnableFormalDungeonSettlement(completed.Boss.ObjectKey);
            Check(enable is { } accepted && accepted.BossKey == completed.Boss.ObjectKey &&
                accepted.Receipt.Snapshot.LivingMonsters.Count == 1 && f.Kills == completed.Count,
                "结算通知引用已确认敌对Boss，任务友方仍存活且不伪造击杀");
            f.Session.ConfirmFormalDungeonSettlementEnabled(enable!.Value.Receipt);
            Check(f.Session.EnableFormalDungeonSettlement(completed.Boss.ObjectKey) is null,
                "已发布开启通知的同Boss协调不再次开启结果页");
            Check(f.Session.SettleFormalDungeon((ushort)(f.Created.Id + 1), 99, 99, report, dungeons, clear, growth).Outcome == DungeonRunOutcome.Rejected &&
                f.Session.SettleFormalDungeon(f.Created.Id, 99, 98, report, dungeons, clear, growth).Outcome == DungeonRunOutcome.Rejected &&
                Settle(f, []).Outcome == DungeonRunOutcome.Rejected && f.Settlements == 0,
                "不匹配本人/最高评分及空报告不能冻结通关准备");

            // 早期存储拒绝用于验证Session待提交门禁；晚期回滚和提交回执恢复由仓储用例覆盖。
            f.Sql("CREATE TRIGGER clear_session_fail BEFORE UPDATE OF experience ON characters BEGIN SELECT RAISE(ABORT,'clear session pending'); END");
            Reject<SqliteException>(() => Settle(f, report), "经验写入失败保留同一结算准备");
            Check(ReferenceEquals(killed, f.Session.Selected) && Same(killed, f.Read()) && f.Settlements == 0 &&
                f.Kills == completed.Count && f.Session.ReturnFromFormalDungeon() is null &&
                f.Session.PickFormalDungeonItem(ushort.MaxValue, itemResources.Items, itemResources.EpicPieces).Outcome == DungeonRunOutcome.Rejected,
                "结算存储失败不提前更新角色，待提交阶段拒绝退出及拾取");
            Check(f.Session.ConfirmFormalDungeonBoss(f.Created.Id, completed.Boss.ObjectKey, 6, 0) is null &&
                f.Session.AdvanceFormalDungeonCards(false) is null && f.Session.AdvanceFormalDungeonCards(true) is null &&
                f.Session.SelectFormalDungeonFreeCard(0, 0) is null && f.Session.FormalDungeonResultFocus() is null &&
                Settle(f, [9, 2, 3, 4]).Outcome == DungeonRunOutcome.Rejected,
                "待提交准备不开放结果流程，篡改报告不替换原准备");
            f.Sql("DROP TRIGGER clear_session_fail");
            var settled = Settle(f, report);
            var committed = f.Session.Selected!;
            Check(settled.Outcome == DungeonRunOutcome.Accepted && settled.Receipt is not null && settled.Reward == expectedReward &&
                settled.Presentation is { RankPoint: 99, RankGrade: 99 } presentation && presentation.MonsterExperience == expectedKills &&
                f.Settlements == 1 && f.Kills == completed.Count && Same(committed, f.Read()) &&
                f.Session.CanPublishFormalDungeon(settled.Receipt) && !f.Session.CanPublishFormalDungeon(bossReceipt!),
                "原报告重试提交一条通关receipt，展示冻结评分与击杀总量并安装实际角色回执");
            Check(committed.Learning.Experience == killed.Learning.Experience + expectedReward.TotalExperience &&
                committed.Level > killed.Level && committed.Revision == killed.Revision + 1 &&
                committed.Learning.Trees.All(tree => tree.Sp == killed.Learning.Trees.Single(old => old.Context == tree.Context).Sp +
                    growth.GetSpDelta(killed.Level, committed.Level)),
                "通关仅加基础加评分经验，跨级后两树SP按实际等级差提交，不重加击杀经验");
            Check(JsonSerializer.Serialize(committed.Inventory) == JsonSerializer.Serialize(killed.Inventory) &&
                committed.DungeonState == killed.DungeonState && committed.ActiveMainQuests.SequenceEqual(killed.ActiveMainQuests) &&
                committed.CompletedQuests.SequenceEqual(killed.CompletedQuests),
                "通关经验不改变金币、物品、副本高水位或任务奖励进度");

            byte[] frozenReport = report.ToArray();
            report[0] = 9;
            var replay = Settle(f, frozenReport);
            Check(replay.Outcome == DungeonRunOutcome.Duplicate && replay.Reward == settled.Reward && replay.Presentation == settled.Presentation &&
                ReferenceEquals(committed, f.Session.Selected) && Same(committed, f.Read()) && f.Settlements == 1 && f.Kills == completed.Count,
                "首报告复制冻结，调用方修改原数组后精确报告重放不再发经验或改展示");
            Check(Settle(f, report).Outcome == DungeonRunOutcome.Rejected && Settle(f, [1, 2, 3]).Outcome == DungeonRunOutcome.Rejected &&
                ReferenceEquals(committed, f.Session.Selected) && f.Settlements == 1,
                "已提交通关拒绝变更字段或宽度的报告");

            Check(f.Session.AdvanceFormalDungeonCards(true) is null && f.Session.SelectFormalDungeonFreeCard(0, 0) is null,
                "提交经验后仍须69评分完成才允许70展示卡牌及71选择");
            Check(f.Session.AdvanceFormalDungeonCards(false) is not null && f.Session.AdvanceFormalDungeonCards(false) is not null &&
                f.Session.AdvanceFormalDungeonCards(true) is not null && f.Session.AdvanceFormalDungeonCards(true) is not null,
                "69和70合法顺序及精确重复只推进结算展示状态");
            Check(f.Session.SelectFormalDungeonFreeCard(1, 0) is null && f.Session.SelectFormalDungeonFreeCard(0, 8) is null &&
                f.Session.SelectFormalDungeonFreeCard(0, 0) is not null && f.Session.SelectFormalDungeonFreeCard(0, 0) is not null &&
                f.Session.SelectFormalDungeonFreeCard(0, 4) is null && f.Session.FormalDungeonResultFocus() is not null,
                "只允许本人免费行，重复幂等、其他成员席拒绝，72焦点不退出或发奖");
            Check(ReferenceEquals(committed, f.Session.Selected) && Same(committed, f.Read()) && f.Settlements == 1 &&
                f.Kills == completed.Count && f.Session.Phase == SessionPhase.Settlement,
                "空翻牌全过程不新增物品金币、扣费用或再次写经验");
            Check(f.Session.ReturnFromFormalDungeon() is not null && f.Session.Phase == SessionPhase.Town && f.Session.DungeonSnapshot is null &&
                Same(committed, f.Read()) && !f.Session.CanPublishFormalDungeon(settled.Receipt!),
                "空翻牌后42清理实例并保留实际经验，旧结算通知失效");
            Check(Settle(f, frozenReport).Outcome == DungeonRunOutcome.Rejected && f.Session.AdvanceFormalDungeonCards(false) is null &&
                f.Session.SelectFormalDungeonFreeCard(0, 3) is null && f.Session.FormalDungeonResultFocus() is null,
                "退出后迟到报告、翻牌及焦点不访问旧实例");
        }

        void ReturnWithoutSettlement()
        {
            using var f = new Fixture("ClearReturn", towns, characters, learning, quests);
            f.SelectEntry();
            Enter(f);
            ClearRun(f);
            var killed = f.Session.Selected!;
            Check(f.Session.ReturnFromFormalDungeon() is not null && f.Session.Phase == SessionPhase.Town &&
                f.Session.DungeonSnapshot is null && Same(killed, f.Read()) && f.Settlements == 0,
                "未报告结算的清图实例仍能42返回，只保留已提交击杀经验");
        }

        void PublicationGuard()
        {
            using var f = new Fixture("ClearGuard", towns, characters, learning, quests);
            f.SelectEntry(); Enter(f); ClearRun(f);
            var result = Settle(f, [1, 2, 3]);
            var receipt = result.Receipt!;
            Check(f.Session.CanPublishDungeonSettlement(receipt), "结算投影绑定实际提交角色和原实例");
            f.Session.UpdateProgress(new(CharacterProgressField.Level, f.Account, f.Created.Name, f.Session.Selected!.Level, 0));
            Reject<InvalidOperationException>(() => f.Session.CanPublishDungeonSettlement(receipt),
                "同阶段GM替换快照不能静默跳过结算通知，旧投影明确失效");
            f.Session.ReturnToRoster();
            Check(!f.Session.CanPublishDungeonSettlement(receipt), "旧选角结果不可发往后续名册阶段");
        }

        void RetryCollectionRules()
        {
            using var f = new Fixture("ClearRetry", towns, characters, learning, quests);
            f.SelectEntry();
            var drops = DungeonMonsterDropCatalog.Load(pvf);
            var pool = new DungeonItemDropPool(new DungeonWeightedDropItem[] { new(itemResources.Items.Get(1000), 50) });
            // 明示fixture概率和数量只验证规则传递，不改变Runtime资源掉率。
            var generation = new DungeonDropGenerationPlan("明确合成再挑战规则/实际资源模板", [
                new(10000, pool, 3, 3), new(10000, null, 1, 1, new(1, 17, 0))]);
            var entry = f.Session.EnterFormalDungeon(3, 3145, 0, dungeons, rooms, drops, itemResources.Items,
                draw: _ => 0, prepareDrops: (_, _) => generation)!;
            var completed = ClearRun(f);
            var settled = Settle(f, [1, 2, 3]);
            var retry = f.Session.RetryFormalDungeon(settled.Receipt!, dungeons, rooms, drops, itemResources.Items);
            Check(retry is not null && retry.Snapshot.Identity.InstanceId != entry.Snapshot.Identity.InstanceId &&
                retry.Snapshot.Identity.TaskAttempt == entry.Snapshot.Identity.TaskAttempt + 1 &&
                retry.Character.DungeonState!.NextObjectKey > entry.Character.DungeonState!.NextObjectKey && f.Kills == completed.Count,
                "集合再挑战预留独立实例与更高对象key，准入不复用死亡或旧资产结果");
            var loaded = f.Session.CompleteFormalDungeonLoading()!;
            var monster = loaded.Monsters.Monsters.First(row => row.IsBlocking);
            var death = f.Session.ObserveFormalDungeonDeath(new(monster.ObjectKey, f.Created.Id, 0x0111, f.Created.Id,
                600, 3, 0, (uint)monster.CandidateLevel, 504, 620, 3, true, new string('B', 64)), kills, growth);
            Check(death.Outcome == DungeonRunOutcome.Accepted && death.Drops.Length == 2 &&
                death.Drops.Any(row => !row.IsGold && row.Core.ItemId == 1000 && row.Core.Count == 3) &&
                death.Drops.Any(row => row.IsGold && row.GoldAmount == 17) &&
                death.Drops.All(row => row.DeathIdentity.InstanceId == retry!.Snapshot.Identity.InstanceId) &&
                death.Drops.Select(row => row.Reservation.GroundKey).Distinct().Count() == 2 && f.Kills == completed.Count + 1,
                "再挑战首个真实Session死亡仍生成原完整物品与金币规则，不回退单件显式策略");
        }

        FormalDungeonReceipt Enter(Fixture f) => f.Session.EnterFormalDungeon(3, 3145, 0, dungeons, rooms)
            ?? throw new InvalidOperationException("FAIL 结算测试无法进入正式3145");

        (FormalDungeonReceipt Receipt, DungeonPlannedMonster Boss, uint Experience, int Count) ClearRun(Fixture f)
        {
            var receipt = f.Session.CompleteFormalDungeonLoading() ?? throw new InvalidOperationException("FAIL 结算测试加载失败");
            DungeonPlannedMonster? boss = null;
            FormalDungeonReceipt? final = null;
            uint experience = 0;
            int count = 0;
            for (int i = 0; i < maps.Count; i++)
            {
                Check(receipt.Snapshot.CurrentMap == maps[i], "结算测试使用批准六房路线及正式加载身份");
                foreach (var monster in receipt.Monsters.Monsters.Where(monster => monster.IsBlocking))
                {
                    if (monster.SpawnType == 3)
                    {
                        boss = monster;
                        Check(f.Session.ConfirmFormalDungeonBoss(f.Created.Id, monster.ObjectKey, (uint)monster.CandidateLevel, 504) is null,
                            "活Boss或未全清房间不接受117协调");
                    }
                    uint expectedGain = monster.CandidateLevel == 5 ? 137u : f.Session.Selected!.Level < 3 ? 104u : 93u;
                    var death = f.Session.ObserveFormalDungeonDeath(new(monster.ObjectKey, f.Created.Id, 0x0111, f.Created.Id,
                        600, 3, 0, monster.SpawnType == 3 ? 6u : (uint)monster.CandidateLevel,
                        monster.SpawnType == 3 ? 0UL : 504UL, 620, 3, true, new string('A', 64)), kills, growth);
                    Check(death.Outcome == DungeonRunOutcome.Accepted && death.Receipt is not null,
                        "合成死亡经正式权威、准备、存储和确认完成");
                    final = death.Receipt!;
                    experience = checked(experience + expectedGain);
                    count++;
                    if (death.ClearFact is not null)
                        Check(i == maps.Count - 1 && death.ClearFact.ConfirmedDeathEvents.Count == count,
                            "唯一清图事实包含所有已提交死亡且位于末房");
                }
                if (i < maps.Count - 1)
                {
                    var target = maps[i + 1].Position;
                    Check(f.Session.MoveFormalDungeon((byte)target.X, (byte)target.Y) is not null,
                        "清房后沿实际批准连接过房");
                    receipt = f.Session.CompleteFormalDungeonLoading() ?? throw new InvalidOperationException("FAIL 结算测试过房加载失败");
                }
            }
            return (final ?? throw new InvalidOperationException("FAIL 未产生死亡回执"),
                boss ?? throw new InvalidOperationException("FAIL 批准路线没有Boss"), experience, count);
        }

        FormalDungeonSettlementResult Settle(Fixture f, byte[] body) =>
            f.Session.SettleFormalDungeon(f.Created.Id, 99, 99, body, dungeons, clear, growth);
    }

    private static bool Same(Character first, Character second) => JsonSerializer.Serialize(first) == JsonSerializer.Serialize(second);
    private static void Check(bool valid, string name) => TestCheck.That(valid, name);

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "S5A9-clear-session-" + Guid.NewGuid().ToString("N"));
        private readonly string database;
        private readonly CharacterRepository repository;
        private readonly AccountOwners.Lease owner;
        public GameSession Session { get; }
        public string Account => owner.Account;
        public Character Created { get; }
        public long Kills => Count("character_dungeon_growth_receipts");
        public long Settlements => Count("character_dungeon_settlement_receipts");

        public Fixture(string name, TownCatalog towns, CharacterCatalog characters, SkillLearningCatalog learning,
            DfoServer.GameWorld.Quests.QuestCatalog quests, bool loseCardResult = false,bool losePaidResult = false)
        {
            database = Path.Combine(root, "game.db");
            repository = new(database, quests);
            owner = new AccountOwners().Acquire("clear-session", () => { });
            Session = new(loseCardResult || losePaidResult ? new FormalDungeonPublicationTests.LostResultRepository(repository,loseGrowth:false,
                loseSettlement:loseCardResult,losePaid:losePaidResult) : repository, towns, owner);
            Created = repository.Create(owner,CharacterCreation.Prepare(name,characters.Get(0),towns.Room,towns.InitialReturn,learning.Get)
                with { ActiveMainQuests = [new(0,3145,1)] });
        }

        public void SelectEntry()
        {
            Session.Select(Created.Slot);
            Session.MarkReady();
            Check(Session.LeaveRoom() && Session.Travel(new(38, 2, 1085, 249, 5, 38, 0, 0)),
                "结算测试角色经合法出房与旅行进入入口");
            Session.BeginDungeonSelection();
        }

        public Character Read() => repository.LoadCharacter(owner, Created.Slot);
        public long Count(string table)
        {
            using var db = Open();
            using var command = db.CreateCommand();
            command.CommandText = "SELECT count(*) FROM " + table;
            return Convert.ToInt64(command.ExecuteScalar());
        }
        public void Sql(string sql)
        {
            using var db = Open();
            using var command = db.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
        public object? Query(string sql)
        {
            using var db = Open();
            using var command = db.CreateCommand();
            command.CommandText = sql;
            return command.ExecuteScalar();
        }
        public void FillEquipment(ItemTemplate template)
        {
            var range = ItemSlotBoundService.Range(ItemKind.Equipment,Created.Inventory.Expansion);
            var filled = Created.Inventory with { Items = [.. Created.Inventory.Items,
                .. Enumerable.Range(range.Start,range.End-range.Start+1).Select(slot =>
                    new InventoryItem(0,new(InventoryListType.Main,checked((short)slot)),InventoryRewardGrantService.Create(template)))] };
            repository.CommitInventory(owner,Created,new(Created.Inventory,filled,"card_full_fixture"));
        }
        private SqliteConnection Open()
        {
            var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false, ForeignKeys = true }.ToString());
            db.Open();
            return db;
        }
        public void Dispose()
        {
            owner.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
