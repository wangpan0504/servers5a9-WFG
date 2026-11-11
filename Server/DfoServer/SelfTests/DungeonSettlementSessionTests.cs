using static DfoServer.Testing.TestCheck;
using System.Text.Json;
using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Adventure;
using DfoServer.Game.Auth;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Adventure;
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
        var learning = global::DfoServer.Testing.SelfTestCatalogs.SkillLearning(pvf);
        var growth = global::DfoServer.Testing.SelfTestCatalogs.Growth(pvf);
        var kills = global::DfoServer.Testing.SelfTestCatalogs.KillExperience(pvf);
        var dungeons = global::DfoServer.Testing.SelfTestCatalogs.Dungeons(pvf);
        var rooms = global::DfoServer.Testing.SelfTestCatalogs.Rooms(pvf);
        var quests = global::DfoServer.Testing.SelfTestCatalogs.Quests(pvf);
        DungeonSpecificCardRewardsTests.RunCosmoResources(towns, pvf, characters, itemResources, quests, learning);
        DungeonSpecificCardRewardsTests.RunOrdinarySelectionResources(towns, pvf, characters, itemResources, quests, learning);
        DungeonSpecificCardRewardsTests.RunTayberrsResources(towns, pvf, characters, itemResources, quests, learning);
        var clear = global::DfoServer.Testing.SelfTestCatalogs.ClearExperience(pvf);
        var definition = dungeons.Get(3);
        var maps = definition.Mazes.Single(maze => maze.ConnectsQuest(3145)).MapSpecification!;
        var expectedReward = clear.Calculate(definition, 0, 99);
        ZeroExperience();
        DungeonSettlementTests.RunSortedStackCards(pvf, itemResources);
        CommittedSettlement();
        ReturnWithoutSettlement();
        RaidStatisticsIsolation();
        PublicationGuard();
        RetryCollectionRules();
        FreeCards(false);
        FreeCards(true);
        DisabledClearCards();
        LostCardResult();
        LostFreeResult();
        DoubleCardRewards();
        PremiumCards(false,false);
        PremiumCards(true,false);
        PremiumCards(true,true);
        MembershipExpiryAndDay();
        GoldCardDailyLimit();
        LostPaidResult();
        DelilahServices();
        CappedSettlement();
        MaxedProficiencySettlement();
        AdventureSettlement();
        EquipmentRarityCards();
        GmEpicCards();
        Console.WriteLine($"PASS {TestCheck.Count - before} 通关Session经验、冻结报告、空翻牌及返回断言（非实机）");

        void ZeroExperience()
        {
            var isolated = DungeonCatalog.Load(pvf);
            var definitions = (System.Collections.Concurrent.ConcurrentDictionary<int, Lazy<DungeonDefinition>>)
                typeof(DungeonCatalog).GetField("definitions", System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic)!.GetValue(isolated)!;
            definitions[3] = new(() => definition with
                { RawSections = [.. definition.RawSections, new("experience increasing point", [new PvfToken(0, 0)])] });
            using var f = new Fixture("ZeroSession", towns, characters, learning, quests);
            f.SelectEntry();
            Check(f.Session.EnterFormalDungeon(3, 3145, 0, isolated, rooms) is { Entry.ExperienceWeight: 0 },
                "正式Session入场冻结显式零经验权重");
            ClearRun(f);
            var previous = f.Read();
            var result = f.Session.SettleFormalDungeon(f.Created.Id, 99, 99, [91, 92], isolated, clear, growth);
            Check(result.Outcome == DungeonRunOutcome.Accepted && result.Reward?.TotalExperience == 0 &&
                f.Settlements == 1 && f.Read().Learning.Experience == previous.Learning.Experience,
                "Session零经验结算通过成长预检、Run及文件库三处门禁，保存唯一凭证");
            var replay = f.Session.SettleFormalDungeon(f.Created.Id, 99, 99, [91, 92], isolated, clear, growth);
            Check(replay.Outcome == DungeonRunOutcome.Duplicate && f.Settlements == 1,
                "Session零经验结算重放不再增加凭证或经验");
        }

        void DisabledClearCards()
        {
            // 只在独立目录副本替换一份不可变定义，不改共享资源或增加生产测试入口。
            var isolated = DungeonCatalog.Load(pvf);
            var definitions = (System.Collections.Concurrent.ConcurrentDictionary<int, Lazy<DungeonDefinition>>)
                typeof(DungeonCatalog).GetField("definitions", System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic)!.GetValue(isolated)!;
            definitions[3] = new(() => definition with
                { RawSections = [.. definition.RawSections, new("disable clear reward", [])] });
            using var f = new Fixture("NoClearCards", towns, characters, learning, quests);
            f.Session.SetCardMembership(f.Created.Name, CardMembershipKind.BlackDiamond,
                DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds());
            f.SelectEntry();
            Check(f.Session.EnterFormalDungeon(3, 3145, 0, isolated, rooms) is { Entry.DisableClearReward: true },
                "普通正式入场冻结禁通关奖励声明，不依赖特殊模式分类");
            ClearRun(f);
            int draws = 0;
            var policy = PremiumPolicy(_ => { draws++; return 0; });
            var result = f.Session.SettleFormalDungeon(f.Created.Id, 99, 99, [71, 72], isolated, clear, growth, policy);
            Check(result.Outcome == DungeonRunOutcome.Accepted && result.Card is null && result.Options is null &&
                result.BlackDiamond is null && draws == 0 && f.Count("character_dungeon_card_rewards") == 0 &&
                f.Count("character_dungeon_card_options") == 0,
                "禁通关奖励的普通Run不抽免费付费或会员牌，也不保存虚假翻牌资格");
            Check(f.Session.SelectFormalDungeonFreeCard(0, 0) is null &&
                f.Session.ReturnFromFormalDungeon() is not null && f.Session.Phase == SessionPhase.Town,
                "无翻牌资格仍可沿正式结算回城，不强制领取空普通牌");
        }

        void EquipmentRarityCards()
        {
            using var f = new Fixture("RarityCards", towns, characters, learning, quests, items: itemResources.Items);
            var title = new InventoryItem(0, new(InventoryListType.Equipment, 13),
                InventoryRewardGrantService.Create(itemResources.Items.Get(400330027)) with { ExpireTime = 0 });
            f.EditInventory(inventory => inventory with
                { Items = inventory.Items.Where(item => item.Position != title.Position).Append(title).ToImmutableArray() });
            f.SelectEntry();
            Check(f.Session.EnterFormalDungeon(3, 3145, 0, dungeons, rooms,
                drops: global::DfoServer.Testing.SelfTestCatalogs.MonsterDrops(pvf), items: itemResources.Items) is not null,
                "个人掉率资源沿真实Session入场准备，不依赖无关玩法的可选目录参数");
            ClearRun(f);
            int level = definition.BasisLevel!.Value;
            var names = new Dictionary<int, string> { [1] = "[gold card cost table]", [2] = "[basis of rarity dicision]", [3] = "[item drop ref table]" };
            var prices = DungeonCardPriceCatalog.Parse(new PvfToken[]
            {
                new(3, 1), new(0, level), new(0, 100), new(3, 2),
                new(0, 200000), new(0, 400000), new(0, 600000), new(0, 800000), new(0, 900000), new(0, 990000), new(0, 1000000),
                new(3, 3), new(0, level), new(0, level - 1), new(0, 1)
            }, key => names[key]);
            var candidates = DungeonItemDropCatalog.Create(Enumerable.Range(0, 7).Select(rarity =>
                new ItemTemplate(9000 + rarity, ItemKind.Equipment, 12, 1, 10, 1, 0, rarity, 1, "[free]", ushort.MaxValue, 0, "CardFixture")
                    { CreationRate = 1 }));
            int draws = 0;
            var policy = new DungeonCardRewardPolicy(global::DfoServer.Testing.SelfTestCatalogs.DropRules(pvf), candidates,
                itemResources.Items, itemResources.GoldLimits, itemResources.EpicPieces,
                random: bound => { draws++; return bound == 1000000 ? 350000 : 0; }, prices: prices);
            var first = f.Session.SettleFormalDungeon(f.Created.Id, 99, 99, [7, 8, 9], dungeons, clear, growth, policy);
            Check(first.Card?.Item?.ItemId == 9003 && first.Options?.Paid.Item?.ItemId == 9003,
                "Session将本人称号神器200%传给普通免费/付费牌，不能只在纯策略测试生效");
            int frozenDraws = draws;
            var again = f.Session.SettleFormalDungeon(f.Created.Id, 99, 99, [7, 8, 9], dungeons, clear, growth, policy);
            Check(again.Card == first.Card && again.Options == first.Options && draws == frozenDraws,
                "结算重放读取已冻结的装备牌，不重新读取加成或重抽");
        }

        void GmEpicCards()
        {
            int level = definition.BasisLevel!.Value;
            var names = new Dictionary<int, string> { [1] = "[gold card cost table]", [2] = "[basis of rarity dicision]", [3] = "[item drop ref table]" };
            var prices = DungeonCardPriceCatalog.Parse(new PvfToken[]
            {
                new(3, 1), new(0, level), new(0, 100), new(3, 2),
                new(0, 200000), new(0, 400000), new(0, 600000), new(0, 800000), new(0, 900000), new(0, 990000), new(0, 1000000),
                new(3, 3), new(0, level), new(0, level - 1), new(0, 1)
            }, key => names[key]);
            var candidates = DungeonItemDropCatalog.Create(Enumerable.Range(0, 7).Select(rarity =>
                new ItemTemplate(9000 + rarity, ItemKind.Equipment, 12, 1, 10, 1, 0, rarity, 1, "[free]", ushort.MaxValue, 0, "CardFixture")
                    { CreationRate = 1 }));
            foreach (decimal initial in new[] { 0m, 2m })
            {
                var control = new DungeonDropControlSettings();
                control.Replace([new(DungeonDropScope.All, [], new(Equipment: initial))], true);
                using var f = new Fixture("GmEpicCards" + initial, towns, characters, learning, quests, dropControl: control);
                f.SelectEntry();
                Check(f.Session.EnterFormalDungeon(3, 3145, 0, dungeons, rooms) is { Entry.DropControlCaptured: true } entry &&
                    entry.Entry.DropMultipliers?.Equipment == initial, "真实Session入场冻结GM倍率，包括显式零倍率");
                control.Replace([new(DungeonDropScope.All, [], new(Equipment: initial == 0 ? 100 : 0))], true);
                ClearRun(f);
                int draws = 0;
                var policy = new DungeonCardRewardPolicy(global::DfoServer.Testing.SelfTestCatalogs.DropRules(pvf), candidates,
                    itemResources.Items, itemResources.GoldLimits, itemResources.EpicPieces,
                    random: bound => { draws++; return bound == 1000000 ? 850000 : 0; }, prices: prices);
                var first = f.Session.SettleFormalDungeon(f.Created.Id, 99, 99, [7, 8, 9], dungeons, clear, growth, policy);
                Check(first.Card?.Item is { } free && first.Options?.Paid.Item is { } paid &&
                    (free.ItemId == 9004) == (initial == 2) && (paid.ItemId == 9004) == (initial == 2),
                    "免费和付费翻牌均使用入场GM倍率，不读取结算前热改");
                int frozen = draws;
                var replay = f.Session.SettleFormalDungeon(f.Created.Id, 99, 99, [7, 8, 9], dungeons, clear, growth, policy);
                Check(replay.Card == first.Card && replay.Options == first.Options && draws == frozen,
                    "GM热改后结算重放仍复用完整冻结装备且不重抽");
            }
        }

        void RaidStatisticsIsolation()
        {
            using var f = new Fixture("RaidStats", towns, characters, learning, quests);
            f.SelectEntry();
            Check(f.Session.AcceptFiendwarPlayResult(f.Created.Id, 90, 90, [f.Created.Id]) is null,
                "未入图的普通Session不能取得团本统计接纳资格");
            Enter(f);
            Check(f.Session.AcceptFiendwarPlayResult(f.Created.Id, 90, 90, [f.Created.Id]) is null,
                "普通战斗Run不因合法统计格式获得团本清图资格");
            ClearRun(f);
            var beforeReport = f.Read();
            long killsBefore = f.Kills;
            Check(f.Session.AcceptFiendwarPlayResult(f.Created.Id, 90, 90, [f.Created.Id]) is null &&
                Same(beforeReport, f.Read()) && f.Settlements == 0 && f.Kills == killsBefore,
                "普通ClearFact不能走漩涡统计接纳或绕过正式奖励事务");
            using var replacement = f.TakeOver();
            Reject<InvalidOperationException>(() => f.Session.AcceptFiendwarPlayResult(f.Created.Id, 90, 90, [f.Created.Id]),
                "接管后的旧owner仍被原租约边界拒绝，不能发布旧Run统计");
            Check(Same(beforeReport, f.Read(replacement)) && f.Settlements == 0 && f.Kills == killsBefore,
                "旧owner统计请求不写新owner角色或原死亡/结算账本");
        }

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
                cleared.Options.Membership.GoldCardActive(DateTimeOffset.UtcNow.ToUnixTimeSeconds()) == member &&
                cleared.Options.Price == (member ? 0 : global::DfoServer.Testing.SelfTestCatalogs.CardPrices(pvf).Price(definition.BasisLevel!.Value)) &&
                !cleared.Options.DoublePaidItems && !cleared.Options.LegacyReturningPlayer &&
                (cleared.BlackDiamond is not null) == member,
                $"有效58默认免金币单倍，普通账号保留原价；黑钻奖励独立（member={member},draws={draws}/{frozen},price={cleared.Options?.Price},basis={definition.BasisLevel}）");
            Check(f.Read().Inventory.Gold == previous.Inventory.Gold && f.Read().Inventory.Items.SequenceEqual(previous.Inventory.Items) &&
                f.Count("mailbox_attachments") == 0 && f.Query("SELECT claimed FROM character_dungeon_card_rewards") is 0L,
                "通关仅冻结免费与黑钻奖励，不提前发金币、装备、邮件或扣付费牌金币");
            f.Session.AdvanceFormalDungeonCards(false);f.Session.AdvanceFormalDungeonCards(true);
            f.Session.SetCharacterGold(f.Created.Name,0);
            if (!member)
            {
                string beforeReject = JsonSerializer.Serialize(f.Read());
                Check(f.Session.SelectFormalDungeonPaidCard(1,0,policy) is null && JsonSerializer.Serialize(f.Read()) == beforeReject &&
                    f.Query("SELECT paid_reward_json FROM character_dungeon_card_options") is DBNull,
                    "无契约时余额不足的付费请求不写回执、不发奖或耗次数");
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
            Check(selected!.Inventory?.Receipt is { } paidReceipt && f.Session.CanPublishInventory(paidReceipt),
                "付费及会员翻牌库存回执绑定当前副本身份，提交后可实际发布差量");
            Check(f.Count("character_gold_card_uses") == (member ? 1 : 0) &&
                (!member || f.Query("SELECT used FROM character_gold_card_uses") is 1L),
                "免费黄金牌只在实际领取时消费一次；普通付费牌不创建免费次数");
            var repeat = f.Session.SelectFormalDungeonPaidCard(2,0,policy);
            Check(repeat is { Duplicate:true } && repeat.Reward == selected!.Reward && Same(paid,f.Read()) &&
                f.Session.SelectFormalDungeonPaidCard(1,1,policy) is null &&
                (!member || f.Query("SELECT used FROM character_gold_card_uses") is 1L),
                "两种付费行请求同列重复回放，不再耗次；已领取后改选另一列拒绝");
            var beforeFree = f.Session.Selected!;
            var claimedFree = f.Session.SelectFormalDungeonFreeCard(0,3);
            Check(claimedFree?.Inventory?.Receipt is { } freeReceipt && f.Session.CanPublishInventory(freeReceipt) &&
                f.Session.Selected!.Inventory.Gold == beforeFree.Inventory.Gold + cleared.Card!.Gold + (cleared.BlackDiamond?.Gold ?? 0) &&
                f.Query("SELECT claimed FROM character_dungeon_card_rewards") is 1L,
                "选免费牌才同事务发放普通与黑钻奖励，付费选择不代替免费领取");
            var view = f.Session.FormalCardState(f.Session.FormalDungeonResultFocus()!)!;
            Check(view.FreeSelected && view.FreeChoice == 3 && view.PaidChoice == 0 && view.Paid == selected!.Reward,
                "第四列免费牌与第一列付费牌独立，免费揭示不覆盖已付款结果");
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
            var native = f.Session.CardMembershipForClient()!;
            Check(cleared.Options is { UseGoldCard: true, Price: 0, DoublePaidItems: false } &&
                native.Membership.GoldCardAvailable(now.ToUnixTimeSeconds()) && f.Session.CanPublishCardMembership(native),
                "有效58建立服务0及单倍免费黄金牌资格，不凭资格触发双倍");
            f.Session.SetCardMembership(f.Created.Name,CardMembershipKind.GoldCard,0);
            Check(!f.Session.CanPublishCardMembership(native), "GM关闭权益使旧服务状态回执失效，不迟到覆盖新资格");
            f.Session.SetCharacterGold(f.Created.Name,10000);
            var beforeReject = f.Read();
            Check(f.Session.SelectFormalDungeonPaidCard(1,0,policy,now) is null && Same(beforeReject,f.Read()) &&
                f.Query("SELECT paid_reward_json FROM character_dungeon_card_options") is DBNull &&
                f.Count("character_gold_card_uses") == 0,
                "已广告免费的牌失去契约资格时拒绝，不暗改金币扣费或消耗次数");
            f.Session.SetCardMembership(f.Created.Name,CardMembershipKind.GoldCard,now.AddDays(30).ToUnixTimeSeconds());
            Check(f.Session.SelectFormalDungeonPaidCard(1,0,policy,now) is not null &&
                f.Query("SELECT paid_cost FROM character_dungeon_card_options") is 0L &&
                f.Query("SELECT used FROM character_gold_card_uses") is 1L,
                "恢复契约后领取原冻结单倍牌，不重抽或追收金币");
            f.Session.ReturnFromFormalDungeon();
            Check(f.Session.SelectFormalDungeonPaidCard(1,0,policy) is null,"退出后迟到付费请求不能作用旧实例");
        }

        void GoldCardDailyLimit()
        {
            using var f = new Fixture("LimitCards",towns,characters,learning,quests);
            var now = DateTimeOffset.UtcNow;
            int day = CardMembership.ServiceDay(now);
            f.Session.SetCardMembership(f.Created.Name,CardMembershipKind.BlackDiamond,now.AddDays(30).ToUnixTimeSeconds());
            f.Session.SetCardMembership(f.Created.Name,CardMembershipKind.GoldCard,now.AddDays(30).ToUnixTimeSeconds());
            f.Sql($"INSERT INTO character_gold_card_uses(character_id,account,service_day,used) VALUES({f.Created.Id},'{f.Account}',{day},9)");
            f.SelectEntry();Enter(f);ClearRun(f);
            var policy = PremiumPolicy(_ => 0);
            var activeEvent = new DfoServer.Game.Events.ServerEventState(new(64,1,OverrideActive:true),true);
            var lastFree = f.Session.SettleFormalDungeon(f.Created.Id,99,99,[25,26,27],dungeons,clear,growth,policy,goldCardEvent:activeEvent);
            Check(lastFree.Options is { UseGoldCard:true, Price:0, DoublePaidItems:true } && lastFree.BlackDiamond is not null,
                "已用9次仍冻结最后一次免费双倍黄金牌，黑钻附加免费牌保持独立");
            f.Session.AdvanceFormalDungeonCards(false);f.Session.AdvanceFormalDungeonCards(true);
            uint beforeGold = f.Session.Selected!.Inventory.Gold;
            Check(f.Session.SelectFormalDungeonPaidCard(1,0,policy) is not null &&
                f.Session.Selected!.Inventory.Gold == beforeGold && f.Query("SELECT used FROM character_gold_card_uses") is 10L,
                "第10次免费双倍领取不扣金币，两份奖励只消耗一次并原子保存已用10次");
            Check(f.Session.SelectFormalDungeonPaidCard(2,0,policy) is { Duplicate:true } &&
                f.Session.SelectFormalDungeonFreeCard(0,0) is not null && f.Query("SELECT used FROM character_gold_card_uses") is 10L,
                "重复黄金牌及普通加黑钻免费牌不额外占用魔王每日次数");
            Check(f.Session.ReturnFromFormalDungeon() is not null,
                "次数边界测试通过正常回城建立下一次副本上下文");
            f.Session.ReturnToRoster();
            f.SelectEntry();Enter(f);ClearRun(f);
            var paid = f.Session.SettleFormalDungeon(f.Created.Id,99,99,[28,29,30],dungeons,clear,growth,policy,goldCardEvent:activeEvent);
            Check(paid.Options is { UseGoldCard:false, Price:>0, DoublePaidItems:true } &&
                f.Session.CardMembershipForClient()!.Membership is { GoldCardUsed:10 },
                "耗尽10次后的新副本恢复金币付费，但仍享活动双倍并保留真实服务次数");
            f.Session.AdvanceFormalDungeonCards(false);f.Session.AdvanceFormalDungeonCards(true);
            f.Session.SetCharacterGold(f.Created.Name,10000);
            Check(f.Session.SelectFormalDungeonPaidCard(1,0,policy) is not null &&
                f.Query("SELECT paid_cost FROM character_dungeon_card_options ORDER BY rowid DESC LIMIT 1") is long cost && cost == paid.Options!.Price &&
                f.Query("SELECT used FROM character_gold_card_uses") is 10L,
                "次数耗尽后仍可合法金币翻牌，不增加或溢出免费次数");
            var tomorrow = f.Membership(now.AddDays(1));
            var tomorrowOptions = policy.PrepareOptions(definition,0,1,tomorrow,now.AddDays(1),activeEvent);
            Check(tomorrow.GoldCardUsed == 0 && tomorrow.Day == day + 1 &&
                tomorrowOptions is { UseGoldCard:true, Price:0, DoublePaidItems:true } &&
                f.Query("SELECT used FROM character_gold_card_uses") is 10L,
                "新业务日恢复免费双倍牌的10次资格，不删除历史次数或补发旧奖励");
        }

        DungeonCardRewardPolicy PremiumPolicy(Func<int,int> draw) => new(global::DfoServer.Testing.SelfTestCatalogs.DropRules(pvf),itemResources.DungeonDropItems,
            itemResources.Items,itemResources.GoldLimits,itemResources.EpicPieces,draw,global::DfoServer.Testing.SelfTestCatalogs.CardPrices(pvf));

        void LostPaidResult()
        {
            using var f = new Fixture("PaidLost",towns,characters,learning,quests,losePaidResult:true);
            f.Session.SetCardMembership(f.Created.Name,CardMembershipKind.GoldCard,DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds());
            f.Session.SetCharacterGold(f.Created.Name,10000);
            f.SelectEntry();Enter(f);ClearRun(f);
            int draws = 0;
            var policy = PremiumPolicy(_ => {draws++;return 0;});
            f.Session.SettleFormalDungeon(f.Created.Id,99,99,[16,17,18],dungeons,clear,growth,policy);
            f.Session.AdvanceFormalDungeonCards(false);f.Session.AdvanceFormalDungeonCards(true);
            var before = f.Session.Selected!;
            int frozenDraws = draws;
            Reject<IOException>(() => f.Session.SelectFormalDungeonPaidCard(1,0,policy),"付费或次数消费COMMIT后返回丢失保留同一冻结准备");
            var persisted = f.Read();
            Check(ReferenceEquals(before,f.Session.Selected) && persisted.Inventory.Gold == before.Inventory.Gold &&
                f.Query("SELECT used FROM character_gold_card_uses") is 1L,
                "结果不明时保留免费黄金牌回执，完整奖励及一次计数已提交且没有扣金币");
            var restored = f.Session.SelectFormalDungeonPaidCard(2,0,policy);
            Check(restored is { Duplicate:true } && restored.Inventory?.Receipt is not null && Same(persisted,f.Session.Selected!) &&
                draws == frozenDraws && f.Query("SELECT used FROM character_gold_card_uses") is 1L,
                "结果恢复读取已有黄金牌回执和实际角色，不重复随机、发奖或耗次");
        }

        void LostCardResult()
        {
            using var f = new Fixture("CardLost",towns,characters,learning,quests,loseCardResult:true);
            f.SelectEntry(); Enter(f); ClearRun(f);
            var previous = f.Session.Selected!;
            int draws = 0;
            var policy = new DungeonCardRewardPolicy(global::DfoServer.Testing.SelfTestCatalogs.DropRules(pvf),itemResources.DungeonDropItems,
                itemResources.Items,itemResources.GoldLimits,itemResources.EpicPieces,_ => {draws++;return 0;});
            Reject<IOException>(() => SettleCard(),"COMMIT后返回结果丢失保留原在线快照及待确认实例");
            int frozenDraws = draws;
            var stored = f.Read();
            Check(ReferenceEquals(previous,f.Session.Selected) && stored.Inventory.Gold == previous.Inventory.Gold &&
                stored.Learning.Experience == previous.Learning.Experience + expectedReward.TotalExperience &&
                f.Count("character_dungeon_card_rewards") == 1 && f.Settlements == 1,
                "提交通关结果不明时只有经验与冻结计划已保存，免费资产尚未入库");
            var restored = SettleCard();
            Check(draws == frozenDraws && Same(stored,f.Session.Selected!) && restored.Card is not null &&
                restored.Inventory is null && f.Count("character_dungeon_card_rewards") == 1 && f.Settlements == 1,
                "原报告恢复只读真实回执，安装实际角色及库存发布回执，不再次抽牌或发资产");
            Check(f.Query("SELECT claimed FROM character_dungeon_card_rewards") is 0L,
                "通关结果恢复不误标记免费牌已领取，仍需实际选择");
            Check(SettleCard().Outcome == DungeonRunOutcome.Duplicate && Same(stored,f.Read()),
                "恢复确认后的重复报告继续保持资产和唯一回执");
            FormalDungeonSettlementResult SettleCard() => f.Session.SettleFormalDungeon(f.Created.Id,99,99,[7,8,9],dungeons,clear,growth,policy);
        }

        void LostFreeResult()
        {
            using var f = new Fixture("FreeLost",towns,characters,learning,quests,loseFreeResult:true);
            f.SelectEntry();Enter(f);ClearRun(f);
            int draws = 0;
            var policy = PremiumPolicy(_ => { draws++; return 0; });
            f.Session.SettleFormalDungeon(f.Created.Id,99,99,[19,20,21],dungeons,clear,growth,policy);
            f.Session.AdvanceFormalDungeonCards(false);f.Session.AdvanceFormalDungeonCards(true);
            var before = f.Session.Selected!;
            int frozen = draws;
            Reject<IOException>(() => f.Session.SelectFormalDungeonFreeCard(0,0),"免费领取COMMIT后返回丢失保留原准备");
            var stored = f.Read();
            Check(ReferenceEquals(before,f.Session.Selected) && stored.Inventory.Gold > before.Inventory.Gold &&
                f.Query("SELECT claimed FROM character_dungeon_card_rewards") is 1L && f.Session.ReturnFromFormalDungeon() is null,
                "结果不明时已保存领取事实，但不发布成功或放行回城");
            Reject<InvalidOperationException>(() => f.Session.SetCharacterGold(f.Created.Name,42),"领取结果不明不能覆盖原库存快照");
            var restored = f.Session.SelectFormalDungeonFreeCard(0,0);
            Check(restored is { Duplicate:true,Inventory.Receipt:not null } && draws == frozen && Same(stored,f.Session.Selected!) &&
                f.Session.CanPublishInventory(restored.Inventory.Receipt!),
                "重试从持久回执恢复实际库存和发布资格，不重复发奖或随机");
            Check(f.Session.SelectFormalDungeonFreeCard(0,0) is { Duplicate:true,Inventory:null } && Same(stored,f.Read()),
                "恢复后的再次选择不发送获得特效或再次保存库存");
        }

        void DoubleCardRewards()
        {
            foreach (var (active, devil) in new[] { (false,false),(true,false),(false,true),(true,true) })
            {
                using var f = new Fixture("Double"+(active ? "1" : "0")+(devil ? "1" : "0"),towns,characters,learning,quests);
                f.Session.SetCharacterGold(f.Created.Name,10000);
                if (devil) f.Session.SetCardMembership(f.Created.Name,CardMembershipKind.GoldCard,DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds());
                f.SelectEntry();Enter(f);ClearRun(f);
                var policy = PremiumPolicy(_ => 0);
                var eventState = new DfoServer.Game.Events.ServerEventState(new(64,1,OverrideActive:active),active);
                var settled = f.Session.SettleFormalDungeon(f.Created.Id,99,99,[22,23,24],dungeons,clear,growth,policy,goldCardEvent:eventState);
                bool doubled = active;
                Check(settled.Options!.DoublePaidItems == doubled && !settled.Options.LegacyReturningPlayer &&
                    settled.Options.UseGoldCard == devil && (devil ? settled.Options.Price == 0 : settled.Options.Price > 0) &&
                    (settled.Options.Paid.ExtraItem is not null) == doubled && settled.BlackDiamond is null,
                    "只有活动64决定双倍；魔王子服务0决定免金币，二者组合为免费双倍牌，不制造黑钻或回归资格");
                f.Session.AdvanceFormalDungeonCards(false);f.Session.AdvanceFormalDungeonCards(true);
                var before = f.Session.Selected!;
                if (active && devil)
                {
                    f.Sql("CREATE TRIGGER double_free_fail BEFORE UPDATE OF paid_reward_json ON character_dungeon_card_options BEGIN SELECT RAISE(ABORT,'double free rollback'); END");
                    Reject<SqliteException>(() => f.Session.SelectFormalDungeonPaidCard(1,0,policy),
                        "免费双倍牌回执末段失败必须同时回滚两件装备与使用次数");
                    Check(Same(before,f.Read()) && f.Count("character_gold_card_uses") == 0 &&
                        f.Query("SELECT paid_reward_json FROM character_dungeon_card_options") is DBNull,
                        "免费双倍SQL失败不留下半份奖励、扣金币或已用次数");
                    f.Sql("DROP TRIGGER double_free_fail");
                }
                var selected = f.Session.SelectFormalDungeonPaidCard(1,0,policy)!;
                var added = f.Session.Selected!.Inventory.Items.Where(item => before.Inventory.Items.All(old => old.Uid != item.Uid)).ToArray();
                Check(added.Length == (doubled ? 2 : 1) && added.Select(item => item.Uid).Distinct().Count() == added.Length &&
                    added.All(item => item.Core == settled.Options.Paid.Item) && selected.Reward.Gold == 0 &&
                    f.Query("SELECT claimed FROM character_dungeon_card_rewards") is 0L &&
                    f.Session.Selected!.Inventory.Gold == before.Inventory.Gold - settled.Options.Price &&
                    f.Count("character_gold_card_uses") == (devil ? 1 : 0) &&
                    (!devil || f.Query("SELECT used FROM character_gold_card_uses") is 1L),
                    "活动x2真实发两件独立UID同品级装备；契约仅免金币并耗一次，不乘金币或普通免费奖励");
                Check(f.Session.SelectFormalDungeonPaidCard(2,0,policy) is { Duplicate:true } && Same(f.Session.Selected!,f.Read()),
                    "免费或付费双倍的重复请求不重复扣费、耗次数或发奖");
            }
        }

        void FreeCards(bool full)
        {
            using var f = new Fixture(full ? "CardFull" : "CardReward", towns, characters, learning, quests);
            if (full) f.FillEquipment(itemResources.Items.Get(10400));
            f.SelectEntry();
            Enter(f);
            Check(!f.Session.SellAtShop(new(new(InventoryListType.Equipment,12),1,1000,1000,0),
                new(itemResources.Items,itemResources.Shops,itemResources.GoldLimits,itemResources.EpicPieces)).Accepted,
                "商店出售不能绕过副本加载阶段");
            ClearRun(f);
            var previous = f.Session.Selected!;
            int draws = 0;
            var policy = new DungeonCardRewardPolicy(global::DfoServer.Testing.SelfTestCatalogs.DropRules(pvf), itemResources.DungeonDropItems,
                itemResources.Items, itemResources.GoldLimits, itemResources.EpicPieces, _ => { draws++; return 0; });
            f.Sql("CREATE TRIGGER card_fail BEFORE INSERT ON character_dungeon_card_rewards BEGIN SELECT RAISE(ABORT,'card rollback'); END");
            Reject<SqliteException>(() => CardSettle(), "免费牌末段失败回滚经验、金币、物品、邮件与回执");
            int frozenDraws = draws;
            Check(Same(previous,f.Read()) && ReferenceEquals(previous,f.Session.Selected) && f.Settlements == 0 &&
                f.Count("character_dungeon_card_rewards") == 0 && f.Count("mailbox_messages") == 0,
                "翻牌SQL失败没有部分资产或成功在线快照");
            f.Sql("DROP TRIGGER card_fail");
            f.Sql("CREATE TRIGGER lucky_star_fail AFTER UPDATE OF rental_points ON account_wallets BEGIN SELECT RAISE(ABORT,'lucky star rollback'); END");
            Reject<SqliteException>(() => CardSettle(), "幸运星末段失败回滚已经写入的通关、翻牌和邮件");
            Check(draws == frozenDraws && Same(previous,f.Read()) && ReferenceEquals(previous,f.Session.Selected) &&
                f.Settlements == 0 && f.Count("character_dungeon_card_rewards") == 0 && f.Count("mailbox_messages") == 0,
                "幸运星SQL失败保留原钱包和随机计划，不留下翻牌资产、经验或邮件");
            f.Sql("DROP TRIGGER lucky_star_fail");
            var result = CardSettle();
            var committed = f.Session.Selected!;
            Check(draws == frozenDraws && result.Card is { Gold: > 0, Item: not null } &&
                committed.Inventory.Gold == previous.Inventory.Gold && committed.Inventory.Items.SequenceEqual(previous.Inventory.Items) && Same(committed,f.Read()) &&
                f.Settlements == 1 && f.Count("character_dungeon_card_rewards") == 1,
                "同报告重试沿原随机核心只冻结牌面，不提前提交金币或物品");
            Check(result.LuckyStarGained && result.LuckyStarTotal == previous.Inventory.AccountWallet.RentalPoints + 1 &&
                committed.Inventory.AccountWallet.RentalPoints == result.LuckyStarTotal &&
                committed.Inventory.AccountWallet.Revision == previous.Inventory.AccountWallet.Revision + 1 &&
                result.Inventory is null && f.Query("SELECT claimed FROM character_dungeon_card_rewards") is 0L,
                "幸运星仍随通关事务保存，免费牌保持未领取，不能误发库存增量");
            Check(f.Query("SELECT recommended_dungeon_clears FROM character_dungeon_state WHERE character_id=" + f.Created.Id) is 1L,
                "推荐地下城通关在结算事务内累计一次，前两次失败重试不留残值");
            var core = result.Card!.Item!;
            if (full)
                Check(result.MailId is null && f.Count("mailbox_messages") == 0 && f.Count("mailbox_attachments") == 0,
                    "满装备页也不能在实际翻牌前提前投递邮件");
            else
                Check(result.MailId is null && f.Count("mailbox_messages") == 0,
                    "空位牌面尚未创建库存UID，随机完整核心留在冻结计划");
            var replay = CardSettle();
            Check(draws == frozenDraws && replay.Card == result.Card && Same(committed,f.Read()) &&
                f.Count("character_dungeon_card_rewards") == 1 && f.Settlements == 1 &&
                f.Query("SELECT recommended_dungeon_clears FROM character_dungeon_state WHERE character_id=" + f.Created.Id) is 1L,
                "重复通关报告不重抽、不加经验金币、不重复邮件物品或推荐地下城计数");
            f.Session.AdvanceFormalDungeonCards(false);
            var cards = f.Session.AdvanceFormalDungeonCards(true)!;
            Check(f.Session.ScheduleFormalDungeonCard(cards) && !f.Session.ScheduleFormalDungeonCard(cards),
                "同一实例只能安排一次自动翻牌，重复70不能重置计时");
            var shop = new InventoryShopService(itemResources.Items,itemResources.Shops,itemResources.GoldLimits,itemResources.EpicPieces);
            var beforeQuery = f.Session.Selected!;
            Check(f.Session.ReadShopPurchaseCounts(1000,shop,out var quotaSnapshot) is not null &&
                quotaSnapshot is { Phase: SessionPhase.Settlement } && f.Session.CanPublishInventory(quotaSnapshot) &&
                ReferenceEquals(beforeQuery,f.Session.Selected) && Same(beforeQuery,f.Read()),
                "结算阶段限购查询读取当前库存，不修改资产或选角上下文");
            var sold = f.Session.SellAtShop(new(new(InventoryListType.Equipment,12),1,1000,1000,0),shop);
            Check(sold.Accepted && sold.Receipt?.Phase == SessionPhase.Settlement &&
                f.Session.Selected!.Inventory.Find(new(InventoryListType.Equipment,12)) is null && Same(f.Session.Selected,f.Read()) &&
                !f.Session.CanPublishInventory(quotaSnapshot!),
                "德利拉1000/1000结算窗口实际扣除物品并同事务增加金币");
            if (full) Check(f.Session.ReportFormalCharacterDeath() is not null,"结算残留攻击仍可建立死亡资格");
            var beforeFlip = f.Session.Selected!;
            var deathBeforeFlip = f.Session.CurrentDungeonDeathReturn();
            f.Sql("CREATE TRIGGER free_claim_fail BEFORE UPDATE OF claimed ON character_dungeon_card_rewards BEGIN SELECT RAISE(ABORT,'claim rollback'); END");
            Reject<SqliteException>(() => f.Session.RevealFormalDungeonCard(cards),"领取末段失败回滚金币、装备、邮件和领取位");
            Check(Same(beforeFlip,f.Read()) && f.Query("SELECT claimed FROM character_dungeon_card_rewards") is 0L,
                "领取失败保留原库存和同一份随机计划");
            Check(f.Session.ReportFormalCharacterDeath() is null && f.Session.ReviveFormalCharacter(f.Created.Id) is null &&
                ReferenceEquals(beforeFlip,f.Session.Selected) && Same(beforeFlip,f.Read()) &&
                f.Session.CurrentDungeonDeathReturn() == deathBeforeFlip,
                "未决翻牌期间死亡和扣币不改变冻结快照或死亡身份，恢复后仍可重试原领取");
            f.Sql("DROP TRIGGER free_claim_fail");
            var flipped = f.Session.RevealFormalDungeonCard(cards);
            Check(flipped?.Inventory?.Receipt is { } flipReceipt && f.Session.CanPublishInventory(flipReceipt) &&
                f.Session.Selected!.Inventory.Gold == beforeFlip.Inventory.Gold + result.Card.Gold &&
                f.Session.RevealFormalDungeonCard(cards) is null && f.Session.SelectFormalDungeonFreeCard(0,0) is { Inventory: null, Duplicate: true } &&
                f.Query("SELECT claimed FROM character_dungeon_card_rewards") is 1L && draws == frozenDraws,
                "出售后选牌使用当前库存，原计划重试一次发奖，自动/手动重复不再发资产");
            if (full)
                Check(flipped!.MailId is not null && f.Count("mailbox_attachments") == 1 && f.Query("SELECT expires_at FROM mailbox_messages") is DBNull,
                    "实际领取时满装备页完整核心进入永久邮件");
            else Check(f.Session.Selected!.Inventory.Items.Any(item => item.Uid > 0 && item.Core == core),
                "实际领取时创建真实UID并保持冻结品级");
            var afterFlip = f.Session.Selected!;
            f.Session.ReturnFromFormalDungeon();
            Check(f.Session.RevealFormalDungeonCard(cards) is null && Same(afterFlip,f.Read()),
                "回城后旧计时及旧牌面不影响新阶段，重开库保留全部已提交资产");

            FormalDungeonSettlementResult CardSettle() => f.Session.SettleFormalDungeon(f.Created.Id,99,99,[4,5,6],dungeons,clear,growth,policy,luckyStarLimit:999);
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
            Check(settled.Outcome == DungeonRunOutcome.Accepted && settled.Receipt is not null &&
                settled.Reward is { } settledReward && settledReward.BonusParts.IsDefaultOrEmpty &&
                (settledReward with { BonusParts = default }) == expectedReward &&
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
                f.Session.SelectFormalDungeonFreeCard(0, 3) is not null && f.Session.SelectFormalDungeonFreeCard(0, 3) is not null &&
                f.Session.SelectFormalDungeonFreeCard(0, 4) is null && f.Session.FormalDungeonResultFocus() is not null,
                "免费行任意有效列可选，同列重复幂等、领取后改列拒绝，72焦点不退出或发奖");
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
            var drops = global::DfoServer.Testing.SelfTestCatalogs.MonsterDrops(pvf);
            var pool = new DungeonItemDropPool(new DungeonWeightedDropItem[] { new(itemResources.Items.Get(1000), 50) });
            // 明示fixture概率和数量只验证规则传递，不改变Runtime资源掉率。
            var generation = new DungeonDropGenerationPlan("明确合成再挑战规则/实际资源模板", [
                new(10000, pool, 3, 3), new(10000, null, 1, 1, new(1, 17, 0))]);
            var entry = f.Session.EnterFormalDungeon(3, 3145, 0, dungeons, rooms, drops, itemResources.Items,
                draw: _ => 0, prepareDrops: _ => _ => generation)!;
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

        void CappedSettlement()
        {
            using var f = new Fixture("CapClear",towns,characters,learning,quests);
            // 满级经验条钉在95级入口线：从入口线起，击杀与通关都不再推进角色经验。
            ulong cap = growth.GetLevelStartExperience(95);
            f.Sql($"UPDATE characters SET level=95,experience={cap} WHERE id={f.Created.Id}");
            f.Sql($"UPDATE character_skill_points SET sp=sp+{growth.GetSpDelta(1,95)},tp=tp+{growth.GetTpDelta(1,95)} WHERE character_id={f.Created.Id}");
            f.SelectEntry(); Enter(f);
            var before = f.Session.Selected!;
            var completed = ClearRun(f,capped:true);
            var killed = f.Read();
            Check(killed.Learning.Experience == cap && f.Kills == completed.Count &&
                killed.DungeonState!.ProficiencyLevel == 0 && killed.DungeonState!.ProficiencyExperience == completed.Experience &&
                killed.Learning.Trees.Select(tree => (tree.Context,tree.Sp,tree.Tp)).SequenceEqual(
                    before.Learning.Trees.Select(tree => (tree.Context,tree.Sp,tree.Tp))),
                "Lv95经验停在入口线，击杀经验全额转入熟练度，不额外发SP/TP");
            var policy = new DungeonCardRewardPolicy(global::DfoServer.Testing.SelfTestCatalogs.DropRules(pvf),itemResources.DungeonDropItems,itemResources.Items,
                itemResources.GoldLimits,itemResources.EpicPieces,_ => 0,global::DfoServer.Testing.SelfTestCatalogs.CardPrices(pvf));
            var cleared = f.Session.SettleFormalDungeon(f.Created.Id,99,99,[21,22,23],dungeons,clear,growth,policy);
            uint clearCredit = expectedReward.TotalExperience;
            var settled = f.Read();
            Check(cleared.Outcome == DungeonRunOutcome.Accepted && cleared.GainedExperience == 0 &&
                cleared.ProficiencyGained == clearCredit && cleared.Presentation!.MonsterExperience == completed.Experience &&
                cleared.Card is not null && f.Settlements == 1 &&
                f.Count("character_dungeon_card_rewards") == 1 && settled.Learning.Experience == cap,
                "满级通关角色经验到账0，击杀与通关显示按熟练度有效经验全额");
            Check(settled.DungeonState!.ProficiencyLevel == 0 &&
                settled.DungeonState!.ProficiencyExperience == completed.Experience + clearCredit &&
                Convert.ToInt64(f.Query("SELECT proficiency_gain FROM character_dungeon_settlement_receipts")) == clearCredit &&
                Convert.ToInt64(f.Query("SELECT sum(proficiency_gain) FROM character_dungeon_growth_receipts")) == completed.Experience,
                "满级击杀与通关经验全额记入熟练度，两类回执保存各自到账额");
            var actual = f.Read();
            var replay = f.Session.SettleFormalDungeon(f.Created.Id,99,99,[21,22,23],dungeons,clear,growth,policy);
            Check(replay.Outcome == DungeonRunOutcome.Duplicate && replay.GainedExperience == 0 &&
                replay.ProficiencyGained == clearCredit && Same(actual,f.Read()),
                "满级通关精确重放保持经验和熟练度回执，不重复发资产");
            Check(f.Session.ReturnFromFormalDungeon() is not null && f.Session.Phase == SessionPhase.Town,
                "满级完整通关之后仍能正常回城");
        }

        void MaxedProficiencySettlement()
        {
            using var f = new Fixture("ProfCap",towns,characters,learning,quests);
            // 英雄90：熟练度已到289级封顶，击杀与通关都不再产生角色或熟练度到账，结算页保持全0。
            ulong cap = growth.GetLevelStartExperience(95);
            f.Sql($"UPDATE characters SET level=95,experience={cap} WHERE id={f.Created.Id}");
            f.Sql($"UPDATE character_skill_points SET sp=sp+{growth.GetSpDelta(1,95)},tp=tp+{growth.GetTpDelta(1,95)} WHERE character_id={f.Created.Id}");
            f.Sql($"UPDATE character_dungeon_state SET proficiency_level={ProficiencyCatalog.MaximumLevel},proficiency_experience=0 WHERE character_id={f.Created.Id}");
            f.SelectEntry(); Enter(f);
            var completed = ClearRun(f,capped:true);
            var killed = f.Read();
            Check(killed.Learning.Experience == cap && f.Kills == completed.Count &&
                killed.DungeonState!.ProficiencyLevel == ProficiencyCatalog.MaximumLevel &&
                killed.DungeonState!.ProficiencyExperience == 0 &&
                Convert.ToInt64(f.Query("SELECT sum(proficiency_gain) FROM character_dungeon_growth_receipts")) == 0,
                "熟练度封顶后击杀经验丢弃，回执到账额为0且不改变289级状态");
            var policy = new DungeonCardRewardPolicy(global::DfoServer.Testing.SelfTestCatalogs.DropRules(pvf),itemResources.DungeonDropItems,itemResources.Items,
                itemResources.GoldLimits,itemResources.EpicPieces,_ => 0,global::DfoServer.Testing.SelfTestCatalogs.CardPrices(pvf));
            var cleared = f.Session.SettleFormalDungeon(f.Created.Id,99,99,[31,32,33],dungeons,clear,growth,policy);
            var settled = f.Read();
            Check(cleared.Outcome == DungeonRunOutcome.Accepted && cleared.GainedExperience == 0 &&
                cleared.ProficiencyGained == 0 && cleared.Presentation!.MonsterExperience == 0 &&
                cleared.Card is not null && f.Settlements == 1 &&
                settled.Learning.Experience == cap && settled.DungeonState!.ProficiencyLevel == ProficiencyCatalog.MaximumLevel &&
                settled.DungeonState!.ProficiencyExperience == 0 &&
                Convert.ToInt64(f.Query("SELECT proficiency_gain FROM character_dungeon_settlement_receipts")) == 0,
                "熟练度封顶的满级通关经验全部丢弃，结算显示0且回执到账额0");
            var replay = f.Session.SettleFormalDungeon(f.Created.Id,99,99,[31,32,33],dungeons,clear,growth,policy);
            Check(replay.Outcome == DungeonRunOutcome.Duplicate && replay.GainedExperience == 0 &&
                replay.ProficiencyGained == 0 && Same(settled,f.Read()),
                "熟练度封顶的重复通关报告回放0到账，不重发资产");
        }

        void AdventureSettlement()
        {
            // 合成冒险团资源：满级3、倍率0.3、勇者结晶5经验1点、胶囊每格100最多2格，便于核对确定结果。
            var adventure = AdventureCoreTests.SyntheticCatalog();
            var policy = new DungeonCardRewardPolicy(global::DfoServer.Testing.SelfTestCatalogs.DropRules(pvf),itemResources.DungeonDropItems,itemResources.Items,
                itemResources.GoldLimits,itemResources.EpicPieces,_ => 0,global::DfoServer.Testing.SelfTestCatalogs.CardPrices(pvf));
            using (var f = new Fixture("AdvClear",towns,characters,learning,quests,adventure:adventure,growth:growth))
            {
                ulong cap = growth.GetLevelStartExperience(95);
                f.Sql($"UPDATE characters SET level=95,experience={cap} WHERE id={f.Created.Id}");
                f.Sql($"UPDATE character_skill_points SET sp=sp+{growth.GetSpDelta(1,95)},tp=tp+{growth.GetTpDelta(1,95)} WHERE character_id={f.Created.Id}");
                f.SelectEntry(); Enter(f); ClearRun(f,capped:true);
                // 击杀经验本身已按国服规则计入账号，结算期望必须从提交前的账号状态起算。
                var before = f.Adventure(adventure);
                var cleared = f.Session.SettleFormalDungeon(f.Created.Id,99,99,[41,42,43],dungeons,clear,growth,policy,adventure:adventure);
                uint total = cleared.Reward?.TotalExperience ?? 0;
                var expected = AdventureProgression.Advance(before, total * adventure.ExperienceRate, total, adventure);
                Check(cleared.Outcome == DungeonRunOutcome.Accepted && cleared.Reward is not null &&
                    cleared.Adventure is { } progress && progress.MaximumLevelExperience == total &&
                    progress.State.Revision == before.Revision + 1 && progress.State.Level == expected.Level &&
                    progress.State.Experience == expected.Experience && progress.State.Remainder == expected.Remainder &&
                    progress.State.BraveExperience == expected.BraveExperience &&
                    progress.State.CapsuleExperience == expected.CapsuleExperience &&
                    progress.State.Points.SequenceEqual(expected.Points) &&
                    // 建角色时里程为0，SQL直接改到满级的差额已在首次击杀时记为排除调整，账号总量不凭空增加。
                    before.ExcludedExperience == cap && progress.State.ExcludedExperience == cap &&
                    f.Query("SELECT level FROM account_adventures") is 3L &&
                    Convert.ToInt64(f.Query("SELECT experience FROM account_adventures")) == (long)expected.Experience &&
                    Convert.ToInt64(f.Query("SELECT excluded_experience FROM account_adventures")) == (long)cap &&
                    Convert.ToInt64(f.Query("SELECT revision FROM account_adventures")) == before.Revision + 1,
                    "满级通关把整单原始经验同事务折算推进账号等级、勇者结晶、胶囊与点数");
                Check(cleared.GainedExperience == 0 && f.Read().Learning.Experience == cap && f.Settlements == 1,
                    "角色经验仍按满级截断到账0，账号进度不能替代或追发角色经验");
                var replay = f.Session.SettleFormalDungeon(f.Created.Id,99,99,[41,42,43],dungeons,clear,growth,policy,adventure:adventure);
                Check(replay.Outcome == DungeonRunOutcome.Duplicate && replay.Adventure is null &&
                    Convert.ToInt64(f.Query("SELECT revision FROM account_adventures")) == before.Revision + 1 &&
                    Convert.ToInt64(f.Query("SELECT experience FROM account_adventures")) == (long)expected.Experience,
                    "重复通关报告不再推进账号冒险团，只读已提交receipt");
            }
            using (var f = new Fixture("AdvNormal",towns,characters,learning,quests,adventure:adventure,growth:growth))
            {
                // 只有40级及以上角色计入冒险团：把角色提升到40级入口，并在账本里同步基准，模拟自然到达40级。
                ulong entry = growth.GetLevelStartExperience(AdventureCredit.CountingLevel);
                f.Sql($"UPDATE characters SET level={AdventureCredit.CountingLevel},experience={entry} WHERE id={f.Created.Id}");
                f.Sql($"UPDATE character_skill_points SET sp=sp+{growth.GetSpDelta(1,AdventureCredit.CountingLevel)},tp=tp+{growth.GetTpDelta(1,AdventureCredit.CountingLevel)} WHERE character_id={f.Created.Id}");
                f.Sql($"UPDATE character_adventure_credits SET baseline_experience={entry},position_experience={entry} WHERE character_id={f.Created.Id}");
                f.SelectEntry(); Enter(f); ClearRun(f);
                // 清图击杀经验已计入账号，结算只按整单奖励在其后继续累计。
                var before = f.Adventure(adventure);
                var cleared = f.Session.SettleFormalDungeon(f.Created.Id,99,99,[44,45,46],dungeons,clear,growth,policy,adventure:adventure);
                uint total = cleared.Reward?.TotalExperience ?? 0;
                var expected = AdventureProgression.Advance(before, total, 0, adventure);
                Check(cleared.Outcome == DungeonRunOutcome.Accepted && cleared.Adventure is { } progress &&
                    progress.State.Revision == before.Revision + 1 &&
                    progress.State.Level == expected.Level && progress.State.Experience == expected.Experience &&
                    progress.State.Points.SequenceEqual(expected.Points) &&
                    progress.State.CapsuleExperience == expected.CapsuleExperience &&
                    progress.State.ExcludedExperience == 0 && progress.MaximumLevelExperience == 0 &&
                    Convert.ToInt64(f.Query("SELECT experience FROM account_adventures")) == (long)expected.Experience &&
                    Convert.ToInt64(f.Query("SELECT revision FROM account_adventures")) == before.Revision + 1 &&
                    f.Read().Learning.Experience > entry,
                    "40级及以上未满级角色的通关经验按100%计入账号冒险团，不产生结晶或胶囊溢出");
            }
            using (var f = new Fixture("AdvBonus",towns,characters,learning,quests,adventure:adventure,growth:growth))
            {
                // 账号冒险团等级2声明“通关经验值增加10%”；清图击杀经验会先推进账号，须在结算前预置合法等级2状态。
                f.InitializeAdventure(adventure);
                f.SelectEntry(); Enter(f); ClearRun(f);
                f.Sql("UPDATE account_adventures SET level=2,experience=10");
                var cleared = f.Session.SettleFormalDungeon(f.Created.Id,99,99,[47,48,49],dungeons,clear,growth,policy,adventure:adventure);
                uint bonus = AdventureBenefitPolicy.ClearExperienceBonus(cleared.Reward?.BaseExperience ?? 0, 2, adventure);
                Check(cleared.Outcome == DungeonRunOutcome.Accepted && cleared.Reward is { } reward && bonus > 0 &&
                    reward.AdventureExperience == bonus && bonus == reward.BaseExperience / 10 &&
                    Convert.ToInt64(f.Query("SELECT adventure_experience FROM character_dungeon_settlement_receipts")) == (long)bonus &&
                    Convert.ToInt64(f.Query("SELECT total_experience FROM character_dungeon_settlement_receipts")) == (long)reward.TotalExperience &&
                    reward.TotalExperience == reward.BaseExperience + reward.RankExperience + reward.GrowthContractExperience +
                        reward.PotionExperience + reward.AdventureExperience,
                    "账号等级2的通关按资源声明的10%加成基础经验，同事务写入回执且分解守恒");
                f.Sql("UPDATE account_adventures SET level=3,experience=20");
                var replay = f.Session.SettleFormalDungeon(f.Created.Id,99,99,[47,48,49],dungeons,clear,growth,policy,adventure:adventure);
                Check(replay.Outcome == DungeonRunOutcome.Duplicate && replay.Reward?.AdventureExperience == bonus &&
                    Convert.ToInt64(f.Query("SELECT adventure_experience FROM character_dungeon_settlement_receipts")) == (long)bonus,
                    "重复通关报告沿用冻结的冒险团加成，不按新账号等级重算");
            }
        }

        void DelilahServices()
        {
            using var f = new Fixture("Delilah",towns,characters,learning,quests);
            var repair = new InventoryRepairService(itemResources.Items,itemResources.Repair);
            var disjoint = new InventoryDisjointService(itemResources.Items,itemResources.Disjoint,itemResources.GoldLimits,
                itemResources.EpicPieces,new DelilahRandom());
            var damaged = InventoryRewardGrantService.Create(itemResources.Items.Get(10400)) with
            {
                Durability = 0, SortLockFlag = 1, EquipmentLockId = 1, EnchantCardId = 3175,
                EnchantUpgrade = 11, AmplifyType = 2, AmplifyValue = 21, SealFlag = 1, Rune = 8,
                IsRental = true, ExpireTime = checked((int)DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds())
            };
            var source = InventoryRewardGrantService.Create(itemResources.Items.Get(10400)) with { SortLockFlag = 1, SealFlag = 1 };
            f.EditInventory(inventory => inventory with
            {
                Gold = 1000,
                Items = inventory.Items.Replace(inventory.Find(new(InventoryListType.Equipment,14))!,
                    new(0,new(InventoryListType.Equipment,14),InventoryRewardGrantService.Create(itemResources.Items.Get(10402)) with { Durability = 0 }))
                    .Add(new(0,Bag(3),damaged)).Add(new(0,Bag(9),InventoryRewardGrantService.Create(itemResources.Items.Get(10402)) with { Durability = 0 }))
                    .Add(new(0,Bag(10),source)).Add(new(0,Bag(11),source)).Add(new(0,Bag(12),source with { EquipmentLockId = 1 })).Add(new(0,Bag(13),source))
                    .Add(new(0,Bag(65),InventoryRewardGrantService.Create(itemResources.Items.Get(2600468),2)))
            });
            Unavailable("未选角");
            f.Session.Select(f.Created.Slot);
            Unavailable("选角未Ready");
            f.Session.MarkReady();
            f.BeginEntry();
            Unavailable("选图");
            Enter(f);
            Unavailable("加载");
            ClearRun(f,whileDungeon: () => Unavailable("战斗",repairBlocked:false));
            Unavailable("清图未提交结算",repairBlocked:false);
            f.Sql("CREATE TRIGGER delilah_clear_fail BEFORE UPDATE OF experience ON characters BEGIN SELECT RAISE(ABORT,'pending settlement'); END");
            Reject<SqliteException>(() => Settle(f,[31,32,33]),"待提交结算测试注入真实SQL回滚");
            Unavailable("结算回滚后仍待提交");
            f.Sql("DROP TRIGGER delilah_clear_fail");
            Settle(f,[31,32,33]);
            f.Session.AdvanceFormalDungeonCards(false);
            var cards = f.Session.AdvanceFormalDungeonCards(true)!;

            var before = f.Session.Selected!;
            var single = f.Session.RepairEquipment(new(Bag(3),-1,0,false),repair);
            var restored = f.Session.Selected!.Inventory.Find(Bag(3))!;
            Check(single.Accepted && single.Receipt?.Phase == SessionPhase.Settlement &&
                restored.Uid == before.Inventory.Find(Bag(3))!.Uid && restored.Core == (damaged with { Durability = 33 }) &&
                f.Read().Inventory.Gold == before.Inventory.Gold-19,"结算单件修理同事务扣实际PVF费用，仅改耐久且保留锁、租赁期限与完整属性");
            before = f.Session.Selected!;
            var repeated = f.Session.RepairEquipment(new(Bag(3),-1,0,false),repair);
            Check(repeated.Accepted && repeated.Plan is { Changed: false } && ReferenceEquals(before,f.Session.Selected) && Same(before,f.Read()),
                "已修满的结算重复请求合法零变化，不扣费或增加revision");
            f.Sql("CREATE TRIGGER delilah_repair_fail BEFORE UPDATE ON character_inventory_items BEGIN SELECT RAISE(ABORT,'repair rollback'); END");
            Reject<SqliteException>(() => f.Session.RepairEquipment(new(Bag(9),-1,0,true),repair),"结算修理失败回滚金币与耐久");
            Check(ReferenceEquals(before,f.Session.Selected) && Same(before,f.Read()),"失败修理不安装规划快照");
            f.Sql("DROP TRIGGER delilah_repair_fail");
            var quick = f.Session.RepairEquipment(new(Bag(9),-1,0,true),repair);
            Check(quick.Accepted && f.Read().Inventory.Gold == before.Inventory.Gold-240,"结算快速单件修理保留150%资源倍率");
            before = f.Session.Selected!;
            var all = f.Session.RepairEquipment(new(Bag(-1),-1,0,false),repair);
            Check(all.Accepted && all.Plan!.ChangedSlots.SequenceEqual(new[] { new InventorySlot(InventoryListType.Equipment,14) }) &&
                f.Read().Inventory.Gold == before.Inventory.Gold-160,"结算全部修理只覆盖穿戴及装备快捷栏，与城镇候选和逐件费用一致");
            Check(!f.Session.RepairEquipment(new(Bag(3),-1,1,false),repair).Accepted &&
                !f.Session.DisjointEquipment(new(Bag(12),-1,0),disjoint,out _).Accepted,"结算不会取得契约修理资格或绕过分解安全锁");
            before = f.Session.Selected!;
            f.Sql("CREATE TRIGGER delilah_disjoint_fail BEFORE INSERT ON currency_audit_log BEGIN SELECT RAISE(ABORT,'disjoint rollback'); END");
            Reject<SqliteException>(() => f.Session.DisjointEquipment(new(Bag(10),65,0),disjoint,out _),"结算分解钱包审计失败回滚源装备、工具及全部产物");
            Check(ReferenceEquals(before,f.Session.Selected) && Same(before,f.Read()),"失败分解不留下半份资产或新在线快照");
            f.Sql("DROP TRIGGER delilah_disjoint_fail");
            var npc = f.Session.DisjointEquipment(new(Bag(10),-1,0x12345678),disjoint,out var npcReceipt);
            Check(npc.Accepted && npc.Receipt?.Phase == SessionPhase.Settlement && npcReceipt is { Rewards.Length: 2 } &&
                npcReceipt.Request.Context == 0x12345678 && f.Read().Inventory.Find(Bag(10)) is null &&
                f.Read().Inventory.Find(Bag(65))!.Core.Count == 2,"结算NPC分解保留完整context，不伪造资格或消耗便携工具");
            var portable = f.Session.DisjointEquipment(new(Bag(11),65,0),disjoint,out var portableReceipt);
            Check(portable.Accepted && portableReceipt is { Rewards.Length: 2 } && f.Read().Inventory.Find(Bag(11)) is null &&
                f.Read().Inventory.Find(Bag(65))!.Core.Count == 1 && Same(f.Session.Selected!,f.Read()),
                "结算便携分解源装备、工具与全部钱包产物原子提交并读回一致");
            before = f.Session.Selected!;
            Check(!f.Session.DisjointEquipment(new(Bag(11),65,0),disjoint,out var emptyReceipt).Accepted && emptyReceipt is null &&
                Same(before,f.Read()),"空源分解重放不再次消耗工具或发奖");
            Check(f.Session.SelectFormalDungeonFreeCard(0,0) is not null && f.Session.CanPublishDungeonCard(cards),
                "修理和分解推进库存revision后原结算牌面仍可揭示，不重抽或重发奖励");
            Check(f.Session.CanPublishInventory(portable.Receipt!),"结算投影使用实际提交后的当前角色");
            var conflict = new InventoryDisjointService(itemResources.Items,itemResources.Disjoint,itemResources.GoldLimits,
                itemResources.EpicPieces,new DelilahRandom(() => f.Session.SetCharacterGold(f.Created.Name,before.Inventory.Gold+1)));
            Check(!f.Session.DisjointEquipment(new(Bag(13),-1,0),conflict,out var conflictReceipt).Accepted && conflictReceipt is null &&
                f.Read().Inventory.Find(Bag(13)) is not null && f.Read().Inventory.Gold == before.Inventory.Gold+1,
                "规划中GM替换角色快照时拒绝原随机结果，不覆盖新金币或重抽后提交");
            var returnDuringPlan = new InventoryDisjointService(itemResources.Items,itemResources.Disjoint,itemResources.GoldLimits,
                itemResources.EpicPieces,new DelilahRandom(() => f.Session.ReturnFromFormalDungeon()));
            Check(!f.Session.DisjointEquipment(new(Bag(13),-1,0),returnDuringPlan,out var oldInstanceReceipt).Accepted && oldInstanceReceipt is null &&
                f.Read().Inventory.Find(Bag(13)) is not null && f.Session.Phase == SessionPhase.Town,
                "规划中回城使原实例失效，不能把结算分解计划提交到城镇");
            Check(!f.Session.CanPublishInventory(portable.Receipt!) && !f.Session.CanPublishDungeonCard(cards) && Same(f.Session.Selected!,f.Read()),
                "回城使旧结算物品投影与牌面失效，资产仍已保存");
            var town = f.Session.RepairEquipment(new(Bag(3),-1,0,false),repair);
            Check(town.Accepted && town.Receipt?.Phase == SessionPhase.Town,"回城后沿同一入口继续合法修理");
            f.Session.ReturnToRoster();f.Session.Select(f.Created.Slot);f.Session.MarkReady();
            Check(!f.Session.CanPublishInventory(town.Receipt!) && Same(f.Session.Selected!,f.Read()),"重选使旧回执失效且重登保留全部修理分解结果");
            var durable = f.Read();
            using var replacement = f.TakeOver();
            Reject<InvalidOperationException>(() => f.Session.RepairEquipment(new(Bag(3),-1,0,false),repair),"被接管的原owner不能执行零变化修理");
            Reject<InvalidOperationException>(() => f.Session.DisjointEquipment(new(Bag(13),-1,0),disjoint,out _),"被接管的原owner不能分解");
            Check(Same(durable,f.Read(replacement)),"接管及重开库保留已提交资产，不允许旧会话追加或覆盖");

            void Unavailable(string phase,bool repairBlocked=true)
            {
                var saved = f.Read();
                // 战斗/清图本身不阻止商店修理；未就绪和未决提交仍须拒绝，正向由RepairTcpTests覆盖。
                if(repairBlocked)
                    Check(!f.Session.RepairEquipment(new(Bag(3),-1,0,false),repair).Accepted,phase+"拒绝未就绪商店修理");
                Check(!f.Session.DisjointEquipment(new(Bag(10),-1,0),disjoint,out var rejectedReceipt).Accepted && rejectedReceipt is null &&
                    Same(saved,f.Read()),phase+"拒绝德利拉物品服务并保留资产");
            }
            static InventorySlot Bag(short slot) => new(InventoryListType.Main,slot);
        }

        FormalDungeonReceipt Enter(Fixture f) => f.Session.EnterFormalDungeon(3, 3145, 0, dungeons, rooms)
            ?? throw new InvalidOperationException("FAIL 结算测试无法进入正式3145");

        (FormalDungeonReceipt Receipt, DungeonPlannedMonster Boss, uint Experience, int Count) ClearRun(Fixture f, bool capped = false, Action? whileDungeon = null)
        {
            var receipt = f.Session.CompleteFormalDungeonLoading() ?? throw new InvalidOperationException("FAIL 结算测试加载失败");
            whileDungeon?.Invoke();
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
                    uint expectedGain = capped ? monster.CandidateLevel == 5 ? 6u : 4u :
                        monster.CandidateLevel == 5 ? 137u : f.Session.Selected!.Level < 3 ? 104u : 93u;
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
        private readonly AccountOwners owners = new();
        private readonly AccountOwners.Lease owner;
        public GameSession Session { get; }
        public string Account => owner.Account;
        public Character Created { get; }
        public long Kills => Count("character_dungeon_growth_receipts");
        public long Settlements => Count("character_dungeon_settlement_receipts");

        public Fixture(string name, TownCatalog towns, CharacterCatalog characters, SkillLearningCatalog learning,
            DfoServer.GameWorld.Quests.QuestCatalog quests, bool loseCardResult = false,bool losePaidResult = false, bool loseFreeResult = false,
            AdventureCatalog? adventure = null, CharacterGrowthCatalog? growth = null, ItemCatalog? items = null, DungeonDropControlSettings? dropControl = null)
        {
            database = Path.Combine(root, "game.db");
            // 只有显式传入冒险团与成长资源时才启用经验账本，其余夹具保持原有无账号进度的边界。
            repository = new(database, quests, adventure, growth);
            // account_adventures 以外键引用 accounts；夹具按真实登录路径先建账号行。
            new AuthenticationService(new AuthenticationRepository(database)).Create("clear-session", "fixture-password");
            owner = owners.Acquire("clear-session", () => { });
            Session = new(loseCardResult || losePaidResult || loseFreeResult ? new FormalDungeonPublicationTests.LostResultRepository(repository,loseGrowth:false,
                loseSettlement:loseCardResult,losePaid:losePaidResult,loseFree:loseFreeResult) : repository, towns, owner, items: items, dropControl: dropControl);
            Created = repository.Create(owner,CharacterCreation.Prepare(name,characters.Get(0),towns.Room,towns.InitialReturn,learning.Get)
                with { ActiveMainQuests = [new(0,3145,1)] });
        }

        public void SelectEntry()
        {
            Session.Select(Created.Slot);
            Session.MarkReady();
            BeginEntry();
        }

        public void BeginEntry()
        {
            Check(Session.LeaveRoom() && Session.Travel(new(38, 2, 1085, 249, 5, 38, 0, 0)),
                "结算测试角色经合法出房与旅行进入入口");
            Session.BeginDungeonSelection();
        }

        public void EditInventory(Func<InventorySnapshot,InventorySnapshot> edit)
        {
            var current = Read();
            repository.CommitInventory(owner,current,new(current.Inventory,edit(current.Inventory),"delilah_fixture"));
        }

        public void InitializeAdventure(AdventureCatalog catalog) =>
            repository.InitializeAdventure(owner, catalog, DateTimeOffset.UtcNow);
        public AdventureState Adventure(AdventureCatalog catalog) =>
            repository.ReadAdventure(owner, catalog, DateTimeOffset.UtcNow).State;
        public Character Read(AccountOwners.Lease? reader = null) => repository.LoadCharacter(reader ?? owner, Created.Slot);
        public CardMembership Membership(DateTimeOffset now) => repository.LoadCardMembership(owner, Created.Id, now);
        public AccountOwners.Lease TakeOver() => owners.Acquire(owner.Account,() => { });
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
            if (Directory.Exists(root)) TestFiles.DeleteDirectory(root, recursive: true);
        }
    }

    sealed class DelilahRandom(Action? firstDraw = null) : Random
    {
        bool drawn;
        public override int Next(int maxValue)
        {
            if (!drawn)
            {
                drawn = true;
                firstDraw?.Invoke();
            }
            return maxValue-1;
        }
    }
}
