using static DfoServer.Testing.TestCheck;
using System.Buffers.Binary;
using System.Text.Json;
using System.Security.Cryptography;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using DfoServer.Network.Parsers;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using PvfLib;
using DfoServer.Infrastructure;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Skills;
using System.Collections.Immutable;

namespace DfoServer;

internal static partial class FormalDungeonSessionTests
{
    // 复用runner已打开的匹配PVF；观察明确为合成，不代表实机击杀或通知通过。
    public static Task RunAsync(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters, ItemResources itemResources)
    {
        int before = TestCheck.Count;
        var learning = global::DfoServer.Testing.SelfTestCatalogs.SkillLearning(pvf);
        var growth = global::DfoServer.Testing.SelfTestCatalogs.Growth(pvf);
        var kills = global::DfoServer.Testing.SelfTestCatalogs.KillExperience(pvf);
        var catalog = global::DfoServer.Testing.SelfTestCatalogs.Dungeons(pvf);
        var rooms = global::DfoServer.Testing.SelfTestCatalogs.Rooms(pvf);
        var quests = global::DfoServer.Testing.SelfTestCatalogs.Quests(pvf);
        VerifyShopRoomAdmission(towns, pvf, characters, itemResources);
        VerifyShopSceneAdmission(towns, pvf, characters, itemResources);
        VerifyShopExchangeMatrix(towns, pvf, characters, itemResources);
        VerifyShopSettlementAdmission(towns, pvf, characters, itemResources);
        VerifyShopBatchAndIdentityAdmission(towns, pvf, characters, itemResources);
        VerifyLoginWearWindow(towns, pvf, characters);
        OrdinaryHuntLifecycle();
        Lifecycle(0);
        Lifecycle(14);
        OrdinaryJobs();
        OtherAdmissions();
        LostLandSessionTests.Run(towns, pvf, characters);
        RejectedEntry();
        Reservation();
        ObjectKeyReuse();
        CacheLifecycle();
        UnknownOwner();
        Invalidation();
        RecordedNormalStatistics();
        FrozenLootTransaction();
        PerMonsterDrops();
        OrdinaryLifecycle();
        InfiniteFatigueProgress();
        OrdinaryRoutes();
        CharacterRevive();
        DeathReturnAndPurchase();
        RetreatStamina();
        EquipmentDurability();
        Console.WriteLine($"PASS {TestCheck.Count - before} 正式Session文件库、六房合成击杀、身份及回滚断言（非实机）");
        return Task.CompletedTask;

        void EquipmentDurability()
        {
            using var f = new Fixture("WearAtomic", towns, characters, learning, quests);
            f.SelectEntry();
            var service = new InventoryService(itemResources.Items, itemResources.GoldLimits, itemResources.EpicPieces);
            InventoryResult Wear() => f.Session.PlanAndCommitInventory(character => service.DecreaseDurability(character, 12),
                requireTown: false, requiredPhase: SessionPhase.Dungeon);
            var before = f.Read();
            Check(!Wear().Accepted && Same(before, f.Read()), "城镇的48报告不能损耗库存");
            Enter(f);
            Check(!Wear().Accepted, "未完成地图加载不能提前损耗穿戴");
            f.Session.CompleteFormalDungeonLoading();
            before = f.Read();
            var original = before.Inventory.Find(new(InventoryListType.Equipment, 12))!;
            f.Sql("CREATE TRIGGER wear_abort AFTER INSERT ON inventory_audit_log BEGIN SELECT RAISE(ABORT,'wear rollback'); END");
            Reject<SqliteException>(() => Wear(), "耐久及revision写入后的审计失败整笔回滚");
            Check(Same(before, f.Read()) && Same(before, f.Session.Selected!),
                "失败没有提前安装减损核心、半笔revision或写入原实例");
            f.Sql("DROP TRIGGER wear_abort");
            var worn = Wear();
            var committed = f.Read();
            Check(worn.Accepted && committed.Revision == before.Revision + 1 &&
                committed.Inventory.Find(original.Position) == (original with { Core = original.Core with { Durability = (ushort)(original.Core.Durability - 1) } }) &&
                committed.Learning.Experience == before.Learning.Experience && committed.Inventory.Gold == before.Inventory.Gold &&
                f.Session.CanPublishInventory(worn.Receipt!), "合法报告复用库存事务，只保存当前装备耐久及本次实际角色快照");
            var stale = f.Session.PlanAndCommitInventory(character =>
            {
                var result = service.DecreaseDurability(character, 12);
                f.Session.ReturnToRoster();
                return result;
            }, requireTown: false, requiredPhase: SessionPhase.Dungeon);
            Check(!stale.Accepted && Same(committed, f.Read()) && !f.Session.CanPublishInventory(worn.Receipt!),
                "锁外规划期间重选使旧耐久计划和通知失效，不扣另一实例装备");
        }

        void RetreatStamina()
        {
            using var f = new Fixture("RetreatWeak", towns, characters, learning, quests);
            f.Sql("UPDATE characters SET level=20");
            f.SelectEntry();
            Enter(f);
            f.Session.CompleteFormalDungeonLoading();
            var original = f.Session.Selected!;
            f.Sql("CREATE TRIGGER fail_retreat BEFORE UPDATE OF stamina_until ON characters WHEN NEW.stamina_until>OLD.stamina_until BEGIN SELECT RAISE(ABORT,'retreat rollback'); END");
            Reject<SqliteException>(() => f.Session.ReturnFromFormalDungeon(), "退图虚弱的SQL失败不发布回城成功");
            Check(ReferenceEquals(original, f.Session.Selected) && Same(original, f.Read()) && f.Session.Phase == SessionPhase.Dungeon,
                "退图失败回滚位置、期限及revision，原实例仍可重试");
            f.Sql("DROP TRIGGER fail_retreat");
            var retreated = f.Session.ReturnFromFormalDungeon()!.Character;
            long until = retreated.Inventory.StaminaUntil;
            Check(until > DateTimeOffset.UtcNow.ToUnixTimeSeconds() && Same(retreated, f.Read()),
                "20级普通角色退图按实际提交保存虚弱");
            Enter(f);
            f.Session.CompleteFormalDungeonLoading();
            long repeatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long repeatedUntil = f.Session.ReturnFromFormalDungeon()!.Character.Inventory.StaminaUntil;
            Check(repeatedUntil >= repeatedAt + 600 && repeatedUntil <= DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 600,
                "非豁免普通角色重复退图仍沿用原十分钟规则");
            Enter(f);
            f.Session.CompleteFormalDungeonLoading();
            f.Session.SetCardMembership(f.Created.Name, CardMembershipKind.BlackDiamond, DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds());
            var diamond = f.Session.Selected!;
            Check(diamond.Inventory.StaminaUntil == 0 && f.Session.ReturnFromFormalDungeon()!.Character.Inventory.StaminaUntil == 0 &&
                Same(f.Session.Selected!, f.Read()), "副本内开通黑钻沿当前资格免罚，不使用入场前冻结的过期资格");
            Enter(f);
            f.Session.CompleteFormalDungeonLoading();
            f.Session.SetCardMembership(f.Created.Name, CardMembershipKind.BlackDiamond, 0);
            Check(f.Session.ReturnFromFormalDungeon()!.Character.Inventory.StaminaUntil > 0,
                "副本内撤销黑钻后按当前资格恢复惩罚，不沿用旧会员资格");
            Check((string?)f.Query("PRAGMA integrity_check") == "ok", "连续免罚、恢复与失败重试后文件库保持完整");
        }

        void InfiniteFatigueProgress()
        {
            var settings = new DfoServer.Game.Settings.GameDebugSettings();
            using var f = new Fixture("FatigueStat", towns, characters, learning, quests, debugSettings: settings);
            f.Sql("DELETE FROM character_active_main_quests; UPDATE character_fatigue SET used=maximum,remaining=0");
            f.SelectEntry();
            Check(f.Session.EnterFormalDungeon(3, 0, 0, catalog, rooms) is { Snapshot.ConsumedFatigue: 0 },
                "无限模式真实零余额可入场，预留实例不计应耗疲劳");
            var stored = f.Read();
            var loaded = f.Session.CompleteFormalDungeonLoading()!;
            Check(loaded.Snapshot.ConsumedFatigue == 1 && Same(stored, f.Read()) &&
                f.Session.FatigueForClient(loaded.Character) is { Used: 0, Remaining: 156 } &&
                Convert.ToInt64(f.Query("SELECT count(*) FROM character_dungeon_fatigue_receipts")) == 0,
                "无限模式零余额仍累计服务端应耗疲劳，余额及存档不变且客户端显示满值");
            Check(f.Session.CompleteFormalDungeonLoading() is null &&
                f.Session.CurrentFormalDungeonScene()!.Snapshot.ConsumedFatigue == 1,
                "无限模式重复加载不重复计进度");
            var fullDisplay = f.Session.CaptureFatigueDisplay()!;
            Check(fullDisplay.State.Remaining == 156 && f.Session.CanPublishFatigueDisplay(fullDisplay),
                "在线疲劳刷新捕获当前选角的完整显示快照");
            settings.InfiniteFatigue = false;
            var realDisplay = f.Session.CaptureFatigueDisplay()!;
            Check(!f.Session.CanPublishFatigueDisplay(fullDisplay) && realDisplay.State.Remaining == 0 &&
                f.Session.CanPublishFatigueDisplay(realDisplay) && Same(stored, f.Read()),
                "关闭后旧满值通知不能盖回零余额，新显示捕获不写库");
            Check(f.Session.CurrentFormalDungeonScene()!.Snapshot.ConsumedFatigue == 1 &&
                f.Session.FatigueForClient(loaded.Character).Remaining == 0 &&
                f.Session.ReturnFromFormalDungeon() is not null &&
                f.Session.EnterFormalDungeon(3, 0, 0, catalog, rooms) is null,
                "关闭无限模式保留当局计数，恢复真实显示与零余额准入限制");
            Check(!f.Session.CanPublishFatigueDisplay(realDisplay), "转场后原副本疲劳显示收据失效");
            settings.InfiniteFatigue = true;
            Check(f.Session.EnterFormalDungeon(3, 0, 0, catalog, rooms) is { Snapshot.ConsumedFatigue: 0 } &&
                f.Session.CompleteFormalDungeonLoading() is { Snapshot.ConsumedFatigue: 1 },
                "重新入场使用新实例计数，不继承上局应耗疲劳");
        }

        void CharacterRevive()
        {
            using var f = new Fixture("Revive", towns, characters, learning, quests);
            f.Sql("UPDATE characters SET revive_coins=3");
            f.SelectEntry();
            Check(f.Session.ReportFormalCharacterDeath() is null && f.Session.ReviveFormalCharacter(f.Created.Id) is null,
                "城镇死亡或复活不改变资产");
            Enter(f);
            Check(f.Session.ReportFormalCharacterDeath() is null, "未加载副本不能报角色死亡");
            var loaded = f.Session.CompleteFormalDungeonLoading()!;
            Check(f.Session.ReviveFormalCharacter(f.Created.Id) is null, "活角色不能消费复活币");
            var dead = f.Session.ReportFormalCharacterDeath()!;
            var before = f.Session.Selected!;
            var firstDeath = f.Session.CurrentDungeonDeathReturn();
            Check(f.Session.ReportFormalCharacterDeath() is null && Same(before, f.Read()) &&
                f.Session.CurrentDungeonDeathReturn() == firstDeath, "重复死亡不回包、不写角色或钱包，也不推进死亡计时身份");
            Check(f.Session.ReviveFormalCharacter(unchecked((ushort)(f.Created.Id + 1))) is null, "只能复活本实例本人key");
            f.Sql("CREATE TRIGGER revive_late_failure BEFORE INSERT ON currency_audit_log BEGIN SELECT RAISE(ABORT,'revive failure'); END;");
            bool failed = false;
            try { f.Session.ReviveFormalCharacter(f.Created.Id); }
            catch (SqliteException) { failed = true; }
            Check(failed && ReferenceEquals(f.Session.Selected, before) && Same(before, f.Read()),
                "晚审计SQL失败回滚扣币及revision，在线角色保持死亡");
            f.Sql("DROP TRIGGER revive_late_failure");
            var used = f.Session.ReviveFormalCharacter(f.Created.Id)!;
            var committed = used.Receipt!.Character;
            Check(used.Accepted && committed.Inventory.ReviveCoins == 2 && committed.Revision == before.Revision + 1 &&
                Same(committed, f.Read()) && JsonSerializer.Serialize(committed.Inventory with { ReviveCoins = 3 }) == JsonSerializer.Serialize(before.Inventory) &&
                committed.Skills.SequenceEqual(before.Skills), "失败后同一死亡可重试，扣一币并保完整资产、技能与冷读");
            Check(f.Session.DungeonSnapshot!.Identity == loaded.Snapshot.Identity && !f.Session.CanPublishFormalDungeon(dead) &&
                f.Session.CanPublishInventory(used.Receipt), "复活保持原Run及房间，仅提交后回执可发布");
            Check(f.Session.ReviveFormalCharacter(f.Created.Id) is null && Same(committed, f.Read()), "重复复活不再次扣币");
            f.Session.ReportFormalCharacterDeath();
            long revision = f.Session.Selected!.Revision;
            f.Sql("UPDATE characters SET revision=revision+1");
            bool stale = false;
            try { f.Session.ReviveFormalCharacter(f.Created.Id); }
            catch (InvalidOperationException) { stale = true; }
            Check(stale && f.Read().Inventory.ReviveCoins == 2 && f.Session.Selected.Revision == revision,
                "revision冲突不扣币或发布复活");
            f.Sql($"UPDATE characters SET revision={revision}");
            for (int remaining = 1; remaining >= 0; remaining--)
            {
                f.Session.ReportFormalCharacterDeath();
                Check(f.Session.ReviveFormalCharacter(f.Created.Id)?.Receipt?.Character.Inventory.ReviveCoins == remaining,
                    "再次死亡可逐次使用剩余复活币");
            }
            f.Session.ReportFormalCharacterDeath();
            var empty = f.Session.Selected!;
            Check(f.Session.ReviveFormalCharacter(f.Created.Id) is { Accepted: false } && ReferenceEquals(empty, f.Session.Selected) &&
                Same(empty, f.Read()), "零余额不复活、不写revision、不生成免费币");
            Check(f.Session.ReportFormalCharacterDeath() is null && f.Session.ReturnFromFormalDungeon() is not null &&
                f.Session.ReviveFormalCharacter(f.Created.Id) is null, "死亡仍可放弃返回城镇，旧Run不能再扣币");
            Enter(f);
            f.Session.CompleteFormalDungeonLoading();
            Check(f.Session.ReviveFormalCharacter(f.Created.Id) is null, "新实例不继承旧死亡资格");
            f.Session.ReportFormalCharacterDeath();
            using var fresh = f.Owners.Acquire("formal-session", () => { });
            bool oldGeneration = false;
            try { f.Session.ReviveFormalCharacter(f.Created.Id); }
            catch (InvalidOperationException) { oldGeneration = true; }
            Check(oldGeneration, "旧generation不能使用死亡资格");
        }

        void DeathReturnAndPurchase()
        {
            using var f = new Fixture("DeathShop", towns, characters, learning, quests);
            var product = itemResources.CashShop.Product(100004)!;
            Check(product is { ItemId: 1, Count: 1, CeraPrice: 100 }, "匹配PVF死亡窗口商品100004是一币100点券");
            InventoryService.Commit(f.Repository, f.Owner, f.Read(),
                new DfoServer.Game.Currency.CurrencyService(itemResources.GoldLimits).Grant(f.Read(), DfoServer.Game.Currency.CurrencyKind.Cera, product.CeraPrice));
            var shop = new InventoryCashShopService(itemResources.Items, itemResources.CashShop, itemResources.GoldLimits, itemResources.EpicPieces, itemResources.Cargo);
            var request = DfoServer.Network.Parsers.Inventory.CashShopRequests.Buy(
                Convert.FromHexString("0000010000FF00A48601000000000000000000"));
            InventoryResult Buy() => f.Session.PlanAndCommitInventory(character => shop.Buy(character, request, out _), 17, requireTown: false);
            f.SelectEntry();
            Enter(f);
            Check(!Buy().Accepted, "商城跨场景购买仍拒绝加载中状态");
            f.Session.CompleteFormalDungeonLoading();
            f.Session.ReportFormalCharacterDeath();
            var previous = f.Session.CurrentDungeonDeathReturn()!;
            Check(f.Session.ReportFormalCharacterDeath() is null, "重复死亡报告不再次发布死亡状态");
            var scheduled = f.Session.CurrentDungeonDeathReturn()!;
            Check(previous == scheduled, "重复死亡保留原计时，不能借重放不断延长回城等待");
            var before = f.Read();
            f.Sql("CREATE TRIGGER death_shop_late_failure BEFORE INSERT ON currency_audit_log WHEN NEW.action_name='cash_shop_buy' BEGIN SELECT RAISE(ABORT,'death shop failure'); END");
            Reject<SqliteException>(() => Buy(), "死亡现场购买真实晚SQL失败");
            Check(Same(before, f.Read()) && Same(before, f.Session.Selected!) &&
                f.Session.CurrentDungeonDeathReturn() == scheduled, "现场购买失败完整回滚资产且保留原死亡计时");
            f.Sql("DROP TRIGGER death_shop_late_failure");
            var bought = Buy();
            Check(bought.Accepted && f.Read().Inventory.ReviveCoins == 1 && f.Read().Inventory.AccountWallet.Get(DfoServer.Game.Currency.CurrencyKind.Cera) == 0 &&
                f.Session.CurrentDungeonDeathReturn() == scheduled, "死亡现场购买扣100点券发一币，替换快照不丢失或重置计时");
            before = f.Read();
            Check(!Buy().Accepted && Same(before, f.Read()) && f.Session.CurrentDungeonDeathReturn() == scheduled,
                "点券不足不改变钱包、币或死亡计时");
            Check(f.Session.ReturnFromFormalDungeonOnDeath(scheduled) is not null && f.Session.Phase == SessionPhase.Town &&
                f.Session.GetState() == UserState.Town && f.Session.CheckState(UserState.Town) &&
                f.Read().Inventory.ReviveCoins == 1 && f.Session.ReturnFromFormalDungeonOnDeath(scheduled) is null,
                "购买后未复活仍可由原计时返回且仅执行一次，不自动消费新币");
            Enter(f); f.Session.CompleteFormalDungeonLoading(); f.Session.ReportFormalCharacterDeath();
            scheduled = f.Session.CurrentDungeonDeathReturn()!;
            Check(f.Session.ReviveFormalCharacter(f.Created.Id) is { Accepted: true } &&
                f.Session.ReturnFromFormalDungeonOnDeath(scheduled) is null && f.Session.CurrentDungeonDeathReturn() is null,
                "复活提交后旧回调失效，保持副本");
            f.Session.ReportFormalCharacterDeath();
            var newDeath = f.Session.CurrentDungeonDeathReturn()!;
            Check(f.Session.ReturnFromFormalDungeonOnDeath(scheduled) is null &&
                f.Session.ReviveFormalCharacter(f.Created.Id) is { Accepted: false } && f.Session.CurrentDungeonDeathReturn() == newDeath,
                "再次死亡旧回调无效，零币失败保持新死亡计时");
            Check(f.Session.ReturnFromFormalDungeon() is not null && f.Session.ReturnFromFormalDungeonOnDeath(newDeath) is null,
                "主动回城使死亡回调失效");
            Enter(f); f.Session.CompleteFormalDungeonLoading(); f.Session.ReportFormalCharacterDeath();
            scheduled = f.Session.CurrentDungeonDeathReturn()!;
            f.Session.ReturnToRoster();
            Check(f.Session.ReturnFromFormalDungeonOnDeath(scheduled) is null, "返回选角使死亡回调失效");
            f.SelectEntry(); Enter(f); f.Session.CompleteFormalDungeonLoading(); f.Session.ReportFormalCharacterDeath();
            scheduled = f.Session.CurrentDungeonDeathReturn()!;
            f.Session.End();
            Check(f.Session.ReturnFromFormalDungeonOnDeath(scheduled) is null, "断线结束使死亡回调失效");
        }

        void OrdinaryHuntLifecycle()
        {
            var drops = global::DfoServer.Testing.SelfTestCatalogs.MonsterDrops(pvf);
            var clears = global::DfoServer.Testing.SelfTestCatalogs.ClearExperience(pvf);
            var parameters = global::DfoServer.Testing.SelfTestCatalogs.QuestRewardParameters(pvf);
            var area = towns.Areas.Values.First(row => row.Level <= 45 && row.DungeonWorldMap is { } world &&
                towns.DungeonMaps[world].Options.Any(option => option.DungeonId == 40));
            var done = area.Quests.ToHashSet();
            if (area.AnyQuests.Length != 0) done.Add(area.AnyQuests[0]);
            var walk = area.Map(done).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains)).Bounds;
            var position = new Location(area.Town,area.Area,checked((ushort)(walk.X+walk.Width/2)),checked((ushort)(walk.Y+walk.Height/2)));
            var candidate = CharacterCreation.Prepare("HuntRoute",characters.Get(0),position,position,learning.Get);
            candidate = candidate with { Level=45,AcceptedStoryLevel=45,CompletedQuests=done.ToArray(),
                InitialLearning=SkillPointLedger.Synchronize(0,0,45,candidate.InitialLearning with { Experience=growth.GetLevelStartExperience(45) },learning) };
            using (var zero = new Fixture("HuntZero",towns,characters,learning,quests,prepared:candidate with { Name="HuntZero" }))
            {
                zero.Sql("UPDATE character_fatigue SET remaining=0");
                zero.Session.Select(zero.Created.Slot,resumeTown:true); zero.Session.MarkReady();
                Check(zero.Session.ChangeQuest(2948,DfoServer.Game.Quests.QuestLifecycleAction.Accept,quests,itemResources.Items,
                    itemResources.GoldLimits,itemResources.EpicPieces)?.Commit is not null,"匿名信经生产接取生成原身份");
                var beforeZero = zero.Read();
                Check(zero.Session.EnterFormalDungeon(40,2948,0,catalog,rooms,drops,itemResources.Items,_ => 0,itemResources.EpicPieces) is null &&
                    Same(beforeZero,zero.Read()) && zero.Session.DungeonSnapshot is null,"普通猎杀疲劳零拒绝且不预留attempt、revision或资产");
            }
            using var f = new Fixture("HuntRoute",towns,characters,learning,quests,prepared:candidate);
            f.Session.Select(f.Created.Slot,resumeTown:true); f.Session.MarkReady();
            Check(f.Session.ChangeQuest(2948,DfoServer.Game.Quests.QuestLifecycleAction.Accept,quests,itemResources.Items,
                itemResources.GoldLimits,itemResources.EpicPieces)?.Commit is not null,"代表性真实匿名信保留完整生产接取事务");
            var before = f.Session.Selected!;
            Guid acceptance = before.ActiveMainQuests.Single(row => row.QuestId == 2948).AcceptanceId;
            var daily = before.ActiveMainQuests.Single(row => row.QuestId == 13031);
            Check(before.ActiveMainQuests.Length == 2,"45级普通猎杀与地区每日各有独立活动槽");
            Check(before.Position.Town == area.Town && before.Position.Area == area.Area &&
                towns.SelectionReturn(before.Position,before.CompletedQuests.ToHashSet()) is not null && before.Fatigue is { Remaining:>0 },
                "匿名信测试使用实际入口城镇及可返回位置，原疲劳余额有效");
            FormalDungeonReceipt current = f.Session.EnterFormalDungeon(40,2948,0,catalog,rooms,drops,itemResources.Items,_ => 0,itemResources.EpicPieces)
                ?? throw new InvalidOperationException("真实匿名信Session入口未建立普通猎杀路线");
            for (int attempt = 1; attempt <= 2; attempt++)
            {
                ushort initialFatigue = current.Character.Fatigue!.Used;
                Check(current.Entry is { IsOrdinaryHunt:true,UsesFatigue:true,RouteQuestId:0,QuestId:2948 } &&
                    current.Entry.QuestAcceptanceId == acceptance && current.Snapshot.Identity.QuestId == 2948 &&
                    current.Snapshot.Identity.TaskAttempt == attempt,"普通猎杀及再挑战保留原任务和接取，独立预留新attempt");
                var initialIdentity = current.Snapshot.Identity;
                var maze = current.Entry.Maze;
                var target = current.Entry.BossMap.Position;
                var from = new Dictionary<DungeonPoint,DungeonPoint?> { [current.Entry.StartMap.Position]=null };
                var queue = new Queue<DungeonPoint>(); queue.Enqueue(current.Entry.StartMap.Position);
                while (queue.TryDequeue(out var point) && !from.ContainsKey(target))
                foreach (var link in maze.StaticTopology.Links!)
                {
                    DungeonPoint? next = link.First == point ? link.Second : link.Second == point ? link.First : null;
                    if (next is not { } adjacent || from.ContainsKey(adjacent)) continue;
                    from.Add(adjacent,point); queue.Enqueue(adjacent);
                }
                var path = new Stack<DungeonPoint>();
                for (var point=target; from[point] is { } parent; point=parent) path.Push(point);
                while (true)
                {
                    current = f.Session.CompleteFormalDungeonLoading() ?? throw new InvalidOperationException("普通猎杀房间不能加载");
                    Check(f.Session.CompleteFormalDungeonLoading() is null && current.Character.Fatigue!.Used - initialFatigue ==
                        f.Transient?.Fatigue.Values.Count(value => value.Committed),"普通猎杀首次加载实际扣点，重复加载不重复扣点");
                    foreach (var monster in current.Monsters.Monsters.Where(row => row.IsBlocking).OrderBy(row => row.SpawnType == 3).ToArray())
                    {
                        uint scalar = f.Session.Selected!.ActiveMainQuests.Single(row => row.QuestId == 2948).ConditionState;
                        var observation = Observation(current,monster);
                        if (monster.SpawnType == 3) observation = observation with { RewriteFlag=1,Unknown18=9000,Unknown26=30 };
                        var death = f.Session.ObserveFormalDungeonDeath(observation,kills,growth);
                        Check(death.Outcome == DungeonRunOutcome.Accepted && death.Receipt is not null &&
                            f.Session.Selected!.ActiveMainQuests.Single(row => row.QuestId == 2948).ConditionState == scalar-(monster.MonsterId == 1014 ? 1u : 0u),
                            "真实死亡只按目标模板推进原猎杀计数，非目标不以清房代替");
                        current = death.Receipt!;
                        Check(f.Session.ObserveFormalDungeonDeath(observation,kills,growth).Outcome == DungeonRunOutcome.Duplicate,
                            "重复真实死亡不重复推进普通猎杀进度");
                    }
                    if (!path.TryPop(out var next)) break;
                    current = f.Session.MoveFormalDungeon(checked((byte)next.X),checked((byte)next.Y))
                        ?? throw new InvalidOperationException("普通猎杀资源通路不能过房");
                }
                if (attempt == 2)
                    Check(f.Session.FinishQuest(2948,0,quests,parameters,itemResources.Items,itemResources.GoldLimits,itemResources.EpicPieces,growth).Receipt is not null,
                        "原目标死亡两次后才沿生产领奖，保留职业武器奖励合同");
                var settled = f.Session.SettleFormalDungeon(f.Created.Id,99,99,[1,2,3,4],catalog,clears,growth);
                Check(settled.Outcome == DungeonRunOutcome.Accepted && Same(f.Session.Selected!,f.Read()),"普通猎杀原非零实例身份沿生产结算持久提交");
                Check(f.Session.RetryFormalDungeon(settled.Receipt! with { Entry=settled.Receipt.Entry with { IsOrdinaryHunt=false } },
                    catalog,rooms,drops,itemResources.Items,itemResources.EpicPieces) is null,"篡改普通猎杀标志的回执不能替换旧结算");
                if (attempt == 1)
                {
                    var committed = f.Session.Selected!;
                    f.Sql("CREATE TRIGGER hunt_retry_fail AFTER UPDATE ON character_dungeon_state BEGIN SELECT RAISE(ABORT,'hunt retry rollback'); END");
                    Reject<SqliteException>(() => f.Session.RetryFormalDungeon(settled.Receipt!,catalog,rooms,drops,itemResources.Items,itemResources.EpicPieces),
                        "普通猎杀重试预留失败保留旧结算");
                    Check(Same(committed,f.Read()) && f.Session.CanPublishFormalDungeon(settled.Receipt!),"失败预留不改原实例、接取及角色revision");
                    f.Sql("DROP TRIGGER hunt_retry_fail");
                }
                current = f.Session.RetryFormalDungeon(settled.Receipt!,catalog,rooms,drops,itemResources.Items,itemResources.EpicPieces)
                    ?? throw new InvalidOperationException("普通猎杀不能再挑战");
                Check(current.Snapshot.Identity.InstanceId != initialIdentity.InstanceId && !f.Session.CanPublishFormalDungeon(settled.Receipt!),
                    "成功重试建立独立实例并使旧结算回执失效");
            }
            Check(f.Read().ActiveMainQuests.Single().QuestId == 13031 &&
                f.Read().ActiveMainQuests.Single().AcceptanceId == daily.AcceptanceId,
                "猎杀领奖仅清原目标，地区每日保留原接取身份");
            Check(current.Entry is { QuestId:0,IsOrdinary:true,IsOrdinaryHunt:false } &&
                f.Session.CompleteFormalDungeonLoading() is not null && f.Session.ReturnFromFormalDungeon() is not null &&
                f.Read().Position == towns.SelectionReturn(before.Position,before.CompletedQuests.ToHashSet()),
                "匿名信已领奖后再挑战恢复普通0身份，实际加载扣点并沿原安全锚点回城");
        }

        void OrdinaryRoutes()
        {
            var clearRewards = global::DfoServer.Testing.SelfTestCatalogs.ClearExperience(pvf);
            foreach (int dungeonId in new[] { 5, 6, 7, 8, 9, 1000, 51 })
            {
                using var f = new Fixture("Map" + dungeonId, towns, characters, learning, quests);
                var definition = catalog.Get(dungeonId);
                byte level = checked((byte)definition.MinimumLevel!.Value);
                f.Sql($"DELETE FROM character_active_main_quests; UPDATE characters SET level={level},experience={growth.GetLevelStartExperience(level)}; " +
                    $"UPDATE character_skill_points SET sp=sp+{growth.GetSpDelta(1, level)}");
                if (dungeonId == 51)
                {
                    var area = towns.Areas.Values.First(area => area.DungeonWorldMap is { } world &&
                        towns.DungeonMaps[world].Options.Any(option => option.DungeonId == dungeonId && option.QuestId == -1));
                    var done = area.Quests.Concat(area.AnyQuests.Take(1)).ToHashSet();
                    done.UnionWith(area.Map(done).DungeonGates.Where(gate => gate.Conditions.Length == 0).SelectMany(gate => gate.Quests));
                    foreach (int quest in done) f.Sql($"INSERT INTO character_completed_quests(character_id,quest_id) VALUES({f.Created.Id},{quest})");
                    var walk = area.Map(done).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains)).Bounds;
                    var position = new Location(area.Town,area.Area,checked((ushort)(walk.X+walk.Width/2)),checked((ushort)(walk.Y+walk.Height/2)));
                    f.Repository.Save(f.Owner,f.Read(),position,position);
                    f.Session.Select(f.Created.Slot,resumeTown:true); f.Session.MarkReady();
                }
                else f.SelectEntry();
                var entry = f.Session.EnterFormalDungeon(checked((uint)dungeonId), 0, 0, catalog, rooms);
                Check(entry is not null && entry.Entry.Maze.SourceOrdinal == 0 && entry.Entry.Maze.QuestConnectionTokens.Count == 0,
                    "普通路线使用完整资源maze D" + dungeonId + ": " + f.Session.LastFormalDungeonRejection);
                var current = f.Session.CompleteFormalDungeonLoading() ?? throw new InvalidOperationException("普通路线不能首次加载");
                var loadedMaps = new HashSet<DungeonMap>();
                bool checkedDummyRollback = false;
                int bossMoves = 0;
                ClearCurrent();
                var maze = entry!.Entry.Maze;
                foreach (var target in maze.MapSpecification!.Where(map => map != entry.Entry.StartMap && map != entry.Entry.BossMap)
                    .Concat([entry.Entry.BossMap]).Concat(maze.DummyStages))
                {
                    if (current.Snapshot.Phase == SinglePlayerDungeonPhase.Settlement) break;
                    var queue = new Queue<DungeonPoint>();
                    var from = new Dictionary<DungeonPoint, DungeonPoint?> { [current.Snapshot.CurrentMap.Position] = null };
                    queue.Enqueue(current.Snapshot.CurrentMap.Position);
                    while (queue.TryDequeue(out var point) && !from.ContainsKey(target.Position))
                    foreach (var link in maze.StaticTopology.Links!)
                    {
                        DungeonPoint? next = link.First == point ? link.Second : link.Second == point ? link.First : null;
                        if (next is not { } adjacent || from.ContainsKey(adjacent) ||
                            maze.DummyStages.Count == 0 && adjacent == entry.Entry.BossMap.Position && adjacent != target.Position) continue;
                        from.Add(adjacent, point); queue.Enqueue(adjacent);
                    }
                    var path = new Stack<DungeonPoint>();
                    for (var point = target.Position; from[point] is { } parent; point = parent) path.Push(point);
                    while (path.TryPop(out var next))
                    {
                        Check(f.Session.MoveFormalDungeon(checked((byte)next.X), checked((byte)next.Y)) is not null,
                            "普通路线每次过房沿资源真实连接且前房阻塞目标已清");
                        current = f.Session.CompleteFormalDungeonLoading() ?? throw new InvalidOperationException("普通路线过房不能加载");
                        ClearCurrent();
                        if (current.Snapshot.Phase == SinglePlayerDungeonPhase.Settlement) break;
                    }
                }
                Check(maze.DummyStages.All(loadedMaps.Contains),"dummy路线必须实际加载全部假Boss阶段后才允许真Boss结算");
                Check(maze.DummyStages.Count == 0 || checkedDummyRollback && bossMoves == 2,
                    "真实dummy死亡覆盖SQL回滚、原候选恢复及两次唯一目标推进");
                var settled = f.Session.SettleFormalDungeon(f.Created.Id, 99, 99, [1, 2, 3, 4], catalog, clearRewards, growth);
                Check(settled.Outcome == DungeonRunOutcome.Accepted && settled.Receipt!.Snapshot.CurrentMap ==
                    (maze.DummyStages.LastOrDefault() ?? entry.Entry.BossMap) &&
                    f.Transient?.Fatigue.Values.Count(value => value.Committed) == loadedMaps.Select(map => map.Id).Distinct().Count() &&
                    current.Snapshot.ConsumedFatigue == loadedMaps.Select(map => map.Id).Distinct().Count() &&
                    f.Session.ReturnFromFormalDungeon() is not null, "各普通地图完整静态敌人、资源房间费用、通关与返回通过真实SQL调用");

                void ClearCurrent()
                {
                    loadedMaps.Add(current.Snapshot.CurrentMap);
                    foreach (var monster in current.Monsters.Monsters.Where(monster => monster.IsBlocking).OrderBy(monster => monster.SpawnType == 3).ToArray())
                    {
                        var observation = Observation(current, monster);
                        if (monster.SpawnType == 3) observation = observation with { RewriteFlag = 1, Unknown18 = 9000, Unknown26 = 30 };
                        var oldBoss = current.Snapshot.BossPosition;
                        if (monster.IsDummyMapBoss && current.Snapshot.CurrentMap.DummyStageIndex == 0 && !checkedDummyRollback)
                        {
                            var before = f.Read(); var beforeSnapshot = current.Snapshot;
                            f.Sql("CREATE TRIGGER fail_dummy_death BEFORE INSERT ON character_dungeon_growth_receipts BEGIN SELECT RAISE(ABORT,'dummy rollback'); END");
                            Reject<SqliteException>(()=>f.Session.ObserveFormalDungeonDeath(observation,kills,growth),
                                "当前dummy死亡的奖励事务失败不能发布下一目标");
                            Check(Same(before,f.Read()) && f.Session.DungeonSnapshot!.BossPosition == oldBoss &&
                                f.Session.DungeonSnapshot.Identity == beforeSnapshot.Identity,
                                "dummy死亡回滚保留完整原资产、当前目标和原epoch");
                            f.Sql("DROP TRIGGER fail_dummy_death");
                            checkedDummyRollback = true;
                        }
                        var killed = f.Session.ObserveFormalDungeonDeath(observation, kills, growth);
                        Check(killed.Outcome == DungeonRunOutcome.Accepted && killed.Receipt is not null && killed.Reward is not null,
                            "全房普通资源怪物使用实际计划等级和原生Boss统计核验授予成长");
                        current = killed.Receipt!;
                        if (monster.IsDummyMapBoss)
                        {
                            Check(killed.BossPositionUpdate == current.Snapshot.BossPosition,
                                "Session发布原死亡提交结果的Boss坐标，不借下一次移动时重算");
                            if (oldBoss != current.Snapshot.BossPosition) bossMoves++;
                        }
                    }
                }
            }
        }

        void OrdinaryLifecycle()
        {
            var clearRewards = global::DfoServer.Testing.SelfTestCatalogs.ClearExperience(pvf);
            var drops = global::DfoServer.Testing.SelfTestCatalogs.MonsterDrops(pvf);
            foreach (int state in new[] { 0, 1, 2 })
            {
                using var f = new Fixture("Normal" + state, towns, characters, learning, quests);
                if (state != 0) f.Sql("DELETE FROM character_active_main_quests");
                if (state == 1) f.Sql($"INSERT INTO character_completed_quests(character_id,quest_id) VALUES({f.Created.Id},3145)");
                if (state == 1)
                {
                    int actors = catalog.Get(3).Mazes[0].MapSpecification!.Sum(map =>
                    {
                        var plan = DungeonMonsterPlan.Create(rooms, map, f.Created.Id, 1, 5);
                        return plan.Monsters.Count + plan.SpecialObjects.Count;
                    });
                    f.Sql($"UPDATE character_dungeon_state SET next_object_key={65536 - actors}");
                }
                if (state == 2) f.Sql("UPDATE character_fatigue SET remaining=1");
                f.SelectEntry();
                var before = f.Read();
                Check(f.Session.EnterFormalDungeon(3, 0, 1, catalog, rooms) is null && Same(before, f.Read()),
                    "普通非法难度拒绝不扣疲劳、不预留身份、不改任务资产");
                // #259: 拒绝进图时必须能取得拒绝原因（写入 formal_dungeon_entry 日志的 RejectReason）。
                Check(f.Session.LastFormalDungeonRejection is { } rejectReason && rejectReason.StartsWith("entry-plan:", StringComparison.Ordinal),
                    "#259 拒绝进图时 LastFormalDungeonRejection 带出 DungeonEntryPlan 原因");
                var entry = f.Session.EnterFormalDungeon(3, 0, 0, catalog, rooms) ?? throw new InvalidOperationException("普通资源实例未建立");
                Check(entry.Entry.IsOrdinary && entry.Entry.Maze.SourceOrdinal == 0 && entry.Snapshot.Identity.QuestId == 0 &&
                    entry.Entry.QuestAcceptanceId == Guid.Empty && entry.Entry.StartMap.Id == 58548 && entry.Entry.BossMap.Id == 58553 &&
                    f.Session.EnterFormalDungeon(3, 0, 0, catalog, rooms) is null, "普通独立身份使用真实普通六房，重复进入不预留新尝试");
                var route = new (byte X, byte Y)[] { (0, 1), (1, 1), (1, 0), (1, 1), (2, 1), (2, 0), (3, 0) };
                if (state == 0)
                {
                    var loading = f.Session.Selected!;
                    f.Sql("CREATE TRIGGER fail_normal_fatigue AFTER UPDATE ON character_fatigue BEGIN SELECT RAISE(ABORT,'fatigue fixture rollback'); END");
                    Reject<SqliteException>(() => f.Session.CompleteFormalDungeonLoading(), "普通加载扣点SQL故障不宣布完成");
                    Check(Same(loading, f.Read()) && ReferenceEquals(loading, f.Session.Selected) &&
                        f.Session.Phase == SessionPhase.Loading && f.Session.ReturnFromFormalDungeon() is null &&
                        Convert.ToInt64(f.Query("SELECT count(*) FROM character_dungeon_fatigue_receipts")) == 0,
                        "费用与revision回滚，结果未决时不能退出丢弃原加载上下文");
                    f.Sql("DROP TRIGGER fail_normal_fatigue");
                }
                FormalDungeonReceipt current = entry;
                uint totalExperience = 0;
                DungeonClearFact? clear = null;
                for (int index = 0; index < route.Length; index++)
                {
                    if (index > 0) current = f.Session.MoveFormalDungeon(route[index].X, route[index].Y) ?? throw new InvalidOperationException("普通真实通路不能过房");
                    current = f.Session.CompleteFormalDungeonLoading() ?? throw new InvalidOperationException("普通加载不能完成");
                    Check(current.Snapshot.ConsumedFatigue == checked((uint)(index < 3 ? index + 1 : index)),
                        "普通扣点与独立应耗疲劳累计一致，回访房间不重复计数");
                    Check(f.Session.CompleteFormalDungeonLoading() is null, "普通重复加载不重复扣点");
                    if (index == 3)
                        Check(current.Snapshot.LivingMonsters.Count == 0 && current.Character.Fatigue!.Used == 3, "普通支路回访不重生、不重复收疲劳");
                    foreach (var monster in current.Monsters.Monsters.OrderBy(monster => monster.SpawnType == 3))
                    {
                        if (monster.IsSpecialChild && index == 2)
                        {
                            var unchanged = f.Session.Selected!;
                            long receipts = f.Count();
                            var observation = Observation(current, monster);
                            Check(f.Session.ObserveFormalDungeonDeath(observation with { ChecksumMatches = false }, kills, growth).Outcome == DungeonRunOutcome.Rejected,
                                "未核验的机关子怪报告不能收尾");
                            var retirement = f.Session.ObserveFormalDungeonDeath(observation, kills, growth);
                            Check(retirement.SpecialChildRetired && retirement.Reward is null && retirement.ClearFact is null &&
                                f.Count() == receipts && ReferenceEquals(unchanged, f.Session.Selected) && Same(unchanged, f.Read()) &&
                                f.Session.ObserveFormalDungeonDeath(observation, kills, growth).Outcome == DungeonRunOutcome.Duplicate,
                                "机关子actor经原生统计核验只收尾，不借此伪造普通经验、任务或重复通知");
                            current = retirement.Receipt!;
                            continue;
                        }
                        if (!current.Snapshot.LivingMonsters.Contains(monster.ObjectKey) || !monster.IsBlocking) continue;
                        var native = Observation(current, monster);
                        if (monster.SpawnType == 3)
                        {
                            Check(f.Session.ObserveFormalDungeonDeath(native with { ChecksumMatches = false }, kills, growth)
                                .Outcome == DungeonRunOutcome.Rejected, "普通Boss报告仍须通过正文检查和当前实例资格");
                            native = native with { RewriteFlag = 1, Unknown18 = 9000, Unknown26 = 30 };
                        }
                        var death = f.Session.ObserveFormalDungeonDeath(native, kills, growth);
                        Check(death.Outcome == DungeonRunOutcome.Accepted && death.Receipt is not null, "普通全资源怪物通过正式死亡/成长事务");
                        totalExperience = checked(totalExperience + death.Reward!.Amount);
                        current = death.Receipt!;
                        clear = death.ClearFact ?? clear;
                    }
                }
                var killed = f.Read();
                Check(clear is { DungeonId: 3 } && clear.Identity.QuestId == 0 && clear.ClearMap!.Id == 58553 &&
                    killed.Fatigue!.Used == 6 && killed.Fatigue.Remaining == (state == 2 ? 0 : 150) &&
                    killed.Learning.Experience == before.Learning.Experience + totalExperience &&
                    killed.ActiveMainQuests.SequenceEqual(before.ActiveMainQuests) && killed.CompletedQuests.SequenceEqual(before.CompletedQuests) &&
                    Convert.ToInt64(f.Query("SELECT count(*) FROM character_quest_clear_receipts")) == 0 &&
                    f.Transient?.Fatigue.Values.Count(value => value.Committed) == 6,
                    "普通ClearFact进入原结算合同，六个首次房间费用、成长与任务隔离真实落库");
                var settled = f.Session.SettleFormalDungeon(killed.Id, 99, 99, [1, 2, 3, 4], catalog, clearRewards, growth);
                Check(settled.Outcome == DungeonRunOutcome.Accepted && Same(f.Session.Selected!, f.Read()) &&
                    Convert.ToInt64(f.Query("SELECT quest_id FROM character_dungeon_settlement_receipts")) == 0,
                    "普通结算保存零任务身份及完整经验，不伪造任务或奖励资格");
                Check(f.Session.RetryFormalDungeon(settled.Receipt! with { Entry = settled.Receipt.Entry with { Difficulty = 1 } },
                    catalog, rooms, drops, itemResources.Items) is null && f.Session.Phase == SessionPhase.Settlement,
                    "篡改再挑战的原路线回执不能先消费已提交结算上下文");
                if (state == 1)
                {
                    var last = f.Session.Selected!;
                    Check(last.DungeonState!.NextObjectKey == 65536, "再挑战前本局恰好用尽对象编号");
                    f.Sql("CREATE TRIGGER fail_key_retry AFTER UPDATE ON character_dungeon_state BEGIN SELECT RAISE(ABORT,'key retry rollback'); END");
                    Reject<SqliteException>(() => f.Session.RetryFormalDungeon(settled.Receipt!, catalog, rooms, drops, itemResources.Items),
                        "回绕预留失败不能消费原结算实例");
                    Check(f.Session.Phase == SessionPhase.Settlement && f.Session.CanPublishFormalDungeon(settled.Receipt!) && Same(last, f.Read()),
                        "回绕SQL回滚保留原实例、角色、资产和高水位");
                    f.Sql("DROP TRIGGER fail_key_retry");
                    var recovered = f.Session.RetryFormalDungeon(settled.Receipt!, catalog, rooms, drops, itemResources.Items);
                    Check(recovered is not null && recovered.Monsters.Monsters[0].ObjectKey == CharacterDungeonState.FirstObjectKey &&
                        recovered.Snapshot.Identity.TaskAttempt == last.DungeonState.LastAttempt + 1 &&
                        recovered.Snapshot.Identity.InstanceId != settled.Receipt!.Snapshot.Identity.InstanceId &&
                        !f.Session.CanPublishFormalDungeon(settled.Receipt!), "耗尽后再挑战复用编号但创建新的尝试身份");
                    Check(f.Session.CompleteFormalDungeonLoading() is not null && f.Session.ReturnFromFormalDungeon() is not null,
                        "回绕再挑战完成加载后仍可合法返回城镇");
                }
                else if (state == 2)
                {
                    Check(f.Session.RetryFormalDungeon(settled.Receipt!, catalog, rooms, drops, itemResources.Items) is null &&
                        f.Session.Phase == SessionPhase.Settlement, "疲劳耗尽再挑战拒绝保留原结算页");
                    Check(f.Session.ReturnFromFormalDungeon() is not null && f.Session.EnterFormalDungeon(3, 0, 0, catalog, rooms) is null,
                        "已开始普通实例可完成，返回后疲劳零禁止新挑战");
                }
                else
                {
                    var beforeRetry = f.Session.Selected!;
                    f.Sql("CREATE TRIGGER fail_normal_retry AFTER UPDATE ON character_dungeon_state BEGIN SELECT RAISE(ABORT,'retry fixture rollback'); END");
                    Reject<SqliteException>(() => f.Session.RetryFormalDungeon(settled.Receipt!, catalog, rooms, drops, itemResources.Items),
                        "新尝试预留SQL失败不结束原结算或宣布加载成功");
                    Check(ReferenceEquals(beforeRetry, f.Session.Selected) && Same(beforeRetry, f.Read()) &&
                        f.Session.Phase == SessionPhase.Settlement && f.Session.CanPublishFormalDungeon(settled.Receipt!) &&
                        f.Session.DungeonSnapshot!.Identity == settled.Receipt!.Snapshot.Identity,
                        "再挑战SQL回滚保留原结算、角色revision、费用和完整实例身份");
                    f.Sql("DROP TRIGGER fail_normal_retry");
                    var repeated = f.Session.RetryFormalDungeon(settled.Receipt!, catalog, rooms, drops, itemResources.Items);
                    Check(repeated is not null && repeated.Snapshot.Identity.InstanceId != entry.Snapshot.Identity.InstanceId &&
                        repeated.Snapshot.Identity.TaskAttempt == 2 && repeated.Entry.IsOrdinary, "普通再挑战预留新独立身份，仍沿普通maze");
                    Check(f.Session.CompleteFormalDungeonLoading() is not null && f.Session.ReturnFromFormalDungeon() is not null,
                        "普通再挑战加载后返回保留成长、资产与疲劳");
                }
                var final = f.Read();
                Check(final.Position == towns.SelectionReturn(before.Position, before.CompletedQuests.ToHashSet()) &&
                    JsonSerializer.Serialize(final.Inventory) == JsonSerializer.Serialize(before.Inventory),
                    "普通退出返回入口外安全点，完整资产不被位置保存覆盖");
                f.Session.ReturnToRoster();
                var reopened = new GameSession(new CharacterRepository(f.Path), towns, f.Owner);
                reopened.Select(f.Created.Slot);
                reopened.MarkReady();
                Check(reopened.Selected!.Fatigue == final.Fatigue && reopened.Selected.Learning.Experience == final.Learning.Experience &&
                    reopened.Selected.CompletedQuests.SequenceEqual(before.CompletedQuests), "普通断线式重建会话恢复成长、任务和已扣疲劳");
            }
        }

        void Lifecycle(byte job)
        {
            using var f = new Fixture("FormalJ" + job, towns, characters, learning, quests, job: job);
            Check(f.Created.DungeonState == CharacterDungeonState.NewOrdinary && f.Created.Learning.Initialized &&
                f.Created.Learning.Trees.Count == 2, "正常建角明确初始化副本owner与两树绑定");
            ulong startExperience = growth.GetCumulativeExperienceBoundary(1) - 104;
            f.Sql($"UPDATE characters SET experience={startExperience} WHERE id={f.Created.Id}");
            f.Sql("UPDATE character_dungeon_state SET capsule_experience=11,proficiency_level=2,proficiency_experience=33");
            f.SelectEntry();
            var opening = f.Session.BeginDungeonSelection()!;
            var initial = f.Session.Selected!;
            Check(f.Session.CachedGameContexts == 0, "选图尚未安装副本缓存");
            Check(f.Session.ReadClearedQuest(3145) is null, "未清图不能因客户端报告获取达成回执");
            var entry = Enter(f);
            var state = entry.Character.DungeonState!;
            Check(f.Session.CachedGameContexts == 1, "Loading安装唯一副本缓存，后续阶段复用原上下文");
            Check(state.LastAttempt == 1 && state.NextObjectKey > 256 && entry.Character.Revision == initial.Revision + 1 &&
                ReferenceEquals(entry.Character, f.Session.Selected) && Same(entry.Character, f.Read()),
                "正式进场预留完整路线身份并安装实际提交角色");
            Check(f.Session.CurrentDungeonSelection is null, "正式入场成功消费原选图上下文");
            var preservedState = state;
            Check(f.Session.Phase == SessionPhase.Loading && f.Session.CanPublishFormalDungeon(entry) &&
                f.Session.EnterFormalDungeon(3, 3145, 0, catalog, rooms) is null, "Loading门禁防止重复预留");
            var notLoaded = Observation(entry, entry.Monsters.Monsters[0]);
            Check(f.Session.ObserveFormalDungeonDeath(notLoaded, kills, growth).Outcome == DungeonRunOutcome.Rejected && f.Count() == 0,
                "正式加载前报告不能签发或落账");
            var receipt = f.Session.CompleteFormalDungeonLoading() ?? throw new InvalidOperationException("FAIL 正式加载不能完成");
            Check(f.Session.Phase == SessionPhase.Dungeon && f.Session.CompleteFormalDungeonLoading() is null && f.Session.CachedGameContexts == 1,
                "真实Session加载一次进入Dungeon，重复不初始化");
            Check(!f.Session.CanPublishFormalDungeon(entry), "加载epoch推进后旧初始化回执不能发布");
            var maps = new[] { 76121, 76122, 76123, 76124, 76125, 76126 };
            var targets = new (byte X, byte Y)[] { (1, 1), (2, 1), (2, 0), (3, 0), (4, 0) };
            var allKeys = new HashSet<ushort>();
            var allSpawnKeys = new HashSet<ushort>();
            ulong total = 0;
            NativeDungeonDeathObservation? oldRoomObservation = null;
            FormalDungeonReceipt? finalReceipt = null;
            for (int room = 0; room < maps.Length; room++)
            {
                Check(receipt!.Snapshot.CurrentMap.Id == maps[room] && receipt.Monsters.Monsters.Count > 0,
                    "真实3145六房逐房保留资源行与当前地图");
                if (oldRoomObservation is not null)
                {
                    var ignored = f.Session.ObserveFormalDungeonDeath(oldRoomObservation,kills,growth);
                    Check(ignored is { Outcome: DungeonRunOutcome.Duplicate, Reward:null, Receipt:null, ClearFact:null } &&
                        ignored.MonsterKey == oldRoomObservation.MonsterKey,
                        "新房旧目标仅作审计，不发送死亡通知或恢复旧房奖励事实");
                }
                Check(f.Session.MoveFormalDungeon(0, 0) is null, "未清房不能过房");
                foreach (var actor in receipt.Monsters.SpecialObjects)
                    Check(allSpawnKeys.Add(actor.ObjectKey) && actor.ObjectKey != f.Created.Id &&
                        actor.ObjectKey >= 256 && actor.ObjectKey < state.NextObjectKey,
                        "特殊父对象同样占用真实预留key，全程唯一且不伪造敌方死亡");
                foreach (var monster in receipt.Monsters.Monsters)
                {
                    Check(allSpawnKeys.Add(monster.ObjectKey) && monster.ObjectKey != f.Created.Id &&
                        monster.ObjectKey >= 256 && monster.ObjectKey < state.NextObjectKey,
                        "所有真实房间怪key在进场预留范围内且全程唯一");
                    Check(allKeys.Add(monster.ObjectKey), "敌方死亡对象key全实例唯一");
                    var observation = Observation(receipt, monster);
                    if (!monster.IsBlocking)
                    {
                        long beforeFriendly = f.Count();
                        var beforeFriendlyCharacter = f.Session.Selected!;
                        Check(f.Session.ObserveFormalDungeonDeath(observation,kills,growth) is
                            { Outcome: DungeonRunOutcome.Accepted, Reward:null, Receipt:not null, ClearFact:null } &&
                            f.Count() == beforeFriendly && ReferenceEquals(f.Session.Selected,beforeFriendlyCharacter) &&
                            !f.Session.DungeonSnapshot!.LivingMonsters.Contains(monster.ObjectKey),
                            "任务友方接受视觉死亡且移除对象，不写击杀经验、掉落或清图事实");
                        Check(f.Session.ObserveFormalDungeonDeath(observation,kills,growth) is
                            { Outcome: DungeonRunOutcome.Accepted, Reward:null, Receipt:not null, ClearFact:null } && f.Count() == beforeFriendly,
                            "当前路线友方重复退场仍可通知，不新增奖励或清图");
                        continue;
                    }
                    if (monster.MonsterId == 107000903)
                    {
                        // 本版Boss实收：来源和创建HP保留为观察，独立核对正文解析及检查值。
                        byte[] body = Convert.FromHexString("1B0100000100000000000000000000000000CA220000000000001D0000000111010100010A0000000000000C005E0403010000000003360103000000B2080000000000003F03EF00A4030301150012002F00000020B11FCC");
                        Check(DungeonRequests.TryParseDeath(body, out var capturedBoss) && capturedBoss!.ObjectKey == 0x11B &&
                            capturedBoss.Reporter == 1 && capturedBoss.SinglePlayerStatistics is { ChecksumMatches: true, Participant.ObjectKey: 1 },
                            "匹配Boss实收88B保留历史目标及本人key，原始检查值独立匹配");
                        // 当前路线新增特殊父对象后key会变化；合成报告重新绑定实例，原抓包仍单独核验。
                        BinaryPrimitives.WriteUInt32LittleEndian(body, monster.ObjectKey);
                        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(4), receipt.Character.Id);
                        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(33), receipt.Character.Id);
                        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(84), ClientBodyChecksum.Compute(body.AsSpan(0, 84)));
                        Check(DungeonRequests.TryParseDeath(body, out var parsedBoss) && parsedBoss!.ObjectKey == monster.ObjectKey &&
                            parsedBoss.Reporter == receipt.Character.Id && parsedBoss.SinglePlayerStatistics is { ChecksumMatches: true } &&
                            parsedBoss.SinglePlayerStatistics.Participant.ObjectKey == receipt.Character.Id,
                            "合成Boss报告绑定当前实例key与本人，修改后正文摘要重新匹配");
                        var boss = parsedBoss!.SinglePlayerStatistics!;
                        observation = new(parsedBoss.ObjectKey, parsedBoss.Reporter, boss.Participant.Kind, boss.Participant.ObjectKey,
                            boss.Participant.CumulativeHpDecrease, boss.Participant.PositiveHpDecreaseCount, boss.RewriteFlag,
                            boss.SpawnLevel, boss.SpawnHpSnapshot, boss.Unknown18, boss.Unknown26, boss.ChecksumMatches,
                            Convert.ToHexString(SHA256.HashData(body)));
                    }
                    var selectedBefore = f.Session.Selected!;
                    long count = f.Count();
                    if (job == 0 && monster.MonsterId == 107000903)
                    {
                        Guid acceptance = selectedBefore.ActiveMainQuests.Single().AcceptanceId;
                        f.Sql($"UPDATE character_active_main_quests SET acceptance_id='{Guid.NewGuid():N}'");
                        Reject<InvalidOperationException>(() => f.Session.ObserveFormalDungeonDeath(observation, kills, growth),
                            "同revision重新接取身份改变不能被旧实例清图推进");
                        Check(ReferenceEquals(selectedBefore, f.Session.Selected) && f.Count() == count &&
                            f.Read().ActiveMainQuests.Single().ConditionState == 1 &&
                            Convert.ToInt64(f.Query("SELECT count(*) FROM character_quest_clear_receipts")) == 0,
                            "旧接取拒绝不写经验SP或清图receipt");
                        f.Sql($"UPDATE character_active_main_quests SET acceptance_id='{acceptance:N}'");
                        f.Sql("CREATE TRIGGER clear_fail AFTER INSERT ON character_quest_clear_receipts BEGIN SELECT RAISE(ABORT,'late clear failure'); END");
                        Reject<SqliteException>(() => f.Session.ObserveFormalDungeonDeath(observation, kills, growth),
                            "末次死亡清图账本失败回滚经验、SP、任务条件和revision");
                        Check(ReferenceEquals(selectedBefore, f.Session.Selected) && Same(f.Read(), selectedBefore) &&
                            f.Count() == count && Convert.ToInt64(f.Query("SELECT count(*) FROM character_quest_clear_receipts")) == 0 &&
                            f.Read().ActiveMainQuests.Single().ConditionState == 1 && f.Session.Phase == SessionPhase.Dungeon,
                            "清图失败不提前置0或进入Settlement，原候选可精确重试");
                        f.Sql("DROP TRIGGER clear_fail");
                    }
                    if (count == 0)
                    {
                        Check(f.Session.ObserveFormalDungeonDeath(observation with { ChecksumMatches = false }, kills, growth).Outcome == DungeonRunOutcome.Rejected,
                            "Native正式入口拒绝检查失败，不借诊断签发");
                        f.Sql("CREATE TRIGGER formal_fail AFTER INSERT ON character_dungeon_growth_receipts BEGIN SELECT RAISE(ABORT,'formal late receipt failure'); END");
                        Reject<SqliteException>(() => f.Session.ObserveFormalDungeonDeath(observation, kills, growth), "晚期receipt触发器引起真实SQL整批回滚");
                        Check(ReferenceEquals(selectedBefore, f.Session.Selected) && Same(f.Read(), selectedBefore) &&
                            f.Count() == count && f.Session.DungeonSnapshot!.LivingMonsters.Contains(monster.ObjectKey),
                            "SQL失败保留在线角色、原死亡候选和活怪，不加经验SP或receipt");
                        f.Sql("DROP TRIGGER formal_fail");
                    }
                    var result = f.Session.ObserveFormalDungeonDeath(observation, kills, growth);
                    Check(result.Outcome == DungeonRunOutcome.Accepted && result.Receipt is not null && result.MonsterKey == monster.ObjectKey,
                        "Session私有Native权威经过Prepare/SQL/Confirm发布实际回执");
                    finalReceipt = result.Receipt!;
                    uint expectedGain = monster.CandidateLevel == 5 ? 137u : selectedBefore.Level < 3 ? 104u : 93u;
                    Check(result.Reward!.Amount == expectedGain, "六房逐次使用击杀前等级及真实怪物等级计算经验");
                    total += expectedGain;
                    Check(f.Count() == count + 1 && Same(finalReceipt.Character, f.Read()) &&
                        finalReceipt.Character.DungeonState == preservedState && f.Session.CanPublishFormalDungeon(finalReceipt) &&
                        !f.Session.CanPublishFormalDungeon(receipt), "成长只落一条并保留副本附属owner，旧角色回执失效");
                    if (count == 0)
                        Check(Convert.ToInt64(f.Query("SELECT combat_sequence FROM character_dungeon_growth_receipts")) == 1 &&
                            finalReceipt.Character.Level == 2, "回滚重试保留第一战斗序号并完成真实等级边界升级");
                    var after = f.Session.Selected!;
                    var repeated = f.Session.ObserveFormalDungeonDeath(observation,kills,growth);
                    Check(repeated is { Outcome: DungeonRunOutcome.Duplicate, Reward:null, Receipt:null } &&
                        repeated.MonsterKey == monster.ObjectKey && ReferenceEquals(after,f.Session.Selected) && f.Count() == count+1,
                        "普通敌人重复只审计忽略，不重发通知或奖励");
                    var changed = f.Session.ObserveFormalDungeonDeath(observation with { BodySha256 = new string('B',64) },kills,growth);
                    Check(changed is { Outcome: DungeonRunOutcome.Duplicate, Reward:null, Receipt:null } &&
                        changed.MonsterKey == monster.ObjectKey && f.Count() == count+1 && ReferenceEquals(after,f.Session.Selected),
                        "同key变更正文只作审计，不重发通知或原提交奖励");
                    oldRoomObservation = observation;
                    if (room == maps.Length - 1 && result.ClearFact is not null)
                        Check(result.ClearFact.ConfirmedDeathEvents.Count == f.Count(), "Boss清图事实只包含已提交敌对击杀，不伪造任务友方死亡");
                }
                if (room < targets.Length)
                {
                    var next = f.Session.MoveFormalDungeon(targets[room].X, targets[room].Y);
                    Check(next is not null && f.Session.Phase == SessionPhase.Loading && !f.Session.CanPublishFormalDungeon(finalReceipt!),
                        "升级后清房连续过房，旧房回执失效");
                    Check(f.Session.ObserveFormalDungeonDeath(oldRoomObservation!, kills, growth).Outcome == DungeonRunOutcome.Rejected,
                        "新房Loading不处理旧报告");
                    receipt = f.Session.CompleteFormalDungeonLoading() ?? throw new InvalidOperationException("FAIL 新房加载不能完成");
                }
            }
            var final = f.Session.Selected!;
            Check(f.Session.Phase == SessionPhase.Settlement && final.Learning.Experience == startExperience + total &&
                (uint)allSpawnKeys.Count == preservedState.NextObjectKey - initial.DungeonState!.NextObjectKey &&
                final.Learning.Trees.All(tree => tree.Sp == initial.Learning.Trees.Single(t => t.Context == tree.Context).Sp + growth.GetSpDelta(1, final.Level)),
                "六房敌方实际等级经验及两树SP一致，剧情actor不要求死亡，Boss进入Settlement");
            Check(final.ActiveMainQuests.Single().ConditionState == 0 &&
                final.ActiveMainQuests.Single().AcceptanceId == initial.ActiveMainQuests.Single().AcceptanceId &&
                final.CompletedQuests.Length == 0 && Convert.ToInt64(f.Query("SELECT count(*) FROM character_quest_clear_receipts")) == 1 &&
                f.Query("SELECT acceptance_id FROM character_quest_clear_receipts") as string == final.ActiveMainQuests.Single().AcceptanceId.ToString("N"),
                "通关只达成本次接取条件，清图receipt唯一，未提前领奖或完成表");
            var clearReceipt = f.Session.ReadClearedQuest(3145);
            Check(clearReceipt is not null && f.Session.CanPublishQuestCondition(clearReceipt) &&
                clearReceipt.Clear.InstanceId == f.Session.DungeonSnapshot!.Identity.InstanceId &&
                clearReceipt.Clear.Revision == final.Revision && f.Session.ReadClearedQuest(3146) is null,
                "Settlement任务查询必须绑定同事务持久clear与实际本实例身份");
            Check(f.Session.ReturnFromFormalDungeon()?.Character == final && f.Session.Phase == SessionPhase.Town && f.Session.DungeonSnapshot is null &&
                f.Session.CachedGameContexts == 0 &&
                !f.Session.CanPublishFormalDungeon(finalReceipt!), "回城清私有run，保留全部已提交成长");
            Check(!f.Session.CanPublishQuestCondition(clearReceipt!) && f.Session.ReadClearedQuest(3145) is { DungeonIdentity: null },
                "旧结算任务回执回城后失效，城镇从同一次接取持久清图恢复");
            Check(f.Session.ObserveFormalDungeonDeath(oldRoomObservation!, kills, growth).Outcome == DungeonRunOutcome.Rejected,
                "已回城拒绝旧死亡");
            Check(f.Session.Move(new(1084, 249, 5, 0, 0, 0)), "正式退出后可合法移动到新的入口位置");
            var moved = f.Session.Selected!;
            var reopenedSelection = f.Session.BeginDungeonSelection()!;
            Check(reopenedSelection.SelectionId > opening.SelectionId &&
                f.Session.CurrentDungeonSelection?.OriginPosition == moved.Position &&
                f.Session.CanPublishDungeonSelection(reopenedSelection) && !f.Session.CanPublishDungeonSelection(opening),
                "正式退出后再选图使用新的SelectionId和实际OriginPosition，不复用旧返回身份");
            Check(f.Session.ReturnFromDungeonSelection() is not null && f.Session.Phase == SessionPhase.Town &&
                f.Session.CurrentDungeonSelection is null && f.Session.Selected!.DungeonState == final.DungeonState &&
                f.Session.Selected.Learning.Experience == final.Learning.Experience && Same(f.Session.Selected, f.Read()),
                "重新选图仍可132返回且保留成长、attempt和对象key高水位");
            f.Session.ReturnToRoster();
            var reopened = new GameSession(new CharacterRepository(f.Path), towns, f.Owner);
            reopened.Select(f.Created.Slot);
            reopened.MarkReady();
            var reselected = reopened.Selected!;
            Check(Same(final with { Revision = reselected.Revision, Position = reselected.Position, Return = reselected.Return }, reselected),
                "仓储重开/重选只执行合法回房保存，成长与副本高水位恢复（非停服重启）");
            MoveToEntry(reopened);
            var second = reopened.EnterFormalDungeon(3, 3145, 0, catalog, rooms)!;
            Check(second is not null && second.Snapshot.Identity.TaskAttempt == 2 &&
                second.Snapshot.Identity.InstanceId != entry.Snapshot.Identity.InstanceId &&
                second.Monsters.Monsters.All(monster => monster.ObjectKey >= state.NextObjectKey),
                "重开再进场不重用旧attempt、instance或怪key");
            Check(new CharacterRepository(f.Path).LoadQuestClear(f.Owner, reopened.Selected!, 3145) == clearReceipt!.Clear,
                "真实清图允许再次挑战，同次接取仍只保留首个持久清图凭据");
        }

        void OrdinaryJobs()
        {
            foreach (byte job in new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 11, 12, 13, 15 })
            {
                using var f = new Fixture("FormalJ" + job, towns, characters, learning, quests, job: job);
                Check(f.Created.Job == job && f.Created.GrowType == 0 && f.Created.Learning.Initialized &&
                    f.Created.Learning.Trees.Count == 2 && f.Created.DungeonState == CharacterDungeonState.NewOrdinary,
                    $"普通职业{job}真实建角初始化双树与副本owner");
                f.SelectEntry();
                var selection = f.Session.BeginDungeonSelection()!;
                Check(f.Session.Phase == SessionPhase.MapSelect && f.Session.CanPublishDungeonSelection(selection),
                    $"普通职业{job}经合法旅行进入选图阶段");
                var before = f.Session.Selected!;
                var entry = Enter(f);
                var loaded = f.Session.CompleteFormalDungeonLoading()!;
                var monster = loaded.Monsters.Monsters[0];
                var death = f.Session.ObserveFormalDungeonDeath(Observation(loaded, monster), kills, growth);
                Check(death.Outcome == DungeonRunOutcome.Accepted && f.Count() == 1 &&
                    f.Session.Selected!.Learning.Experience == before.Learning.Experience + 104 &&
                    Same(f.Session.Selected, f.Read()) && entry.Character.DungeonState!.LastAttempt == 1,
                    $"普通职业{job}正式入场加载及一次死亡成长成功");
                var final = f.Session.Selected;
                Check(f.Session.ReturnFromFormalDungeon()?.Character == final && f.Session.Phase == SessionPhase.Town &&
                    f.Session.DungeonSnapshot is null && Same(final!, f.Read()),
                    $"普通职业{job}退出保留已提交经验与双树，不留下实例");
            }
        }

        void OtherAdmissions()
        {
            foreach (var identity in new[] { (Job: (byte)9, Grow: 0), (Job: (byte)10, Grow: 0), (Job: (byte)14, Grow: 1), (Job: (byte)0, Grow: 37) })
            {
                using var f = new Fixture("EntryJ" + identity.Job, towns, characters, learning, quests, job: identity.Job);
                if (identity.Grow != 0)
                    f.Session.UpdateProgress(new(CharacterProgressField.Grow, f.Owner.Account, f.Created.Name, identity.Grow, 0));
                f.SelectEntry();
                f.Session.BeginDungeonSelection();
                var before = f.Session.Selected!;
                var entry = Enter(f);
                var loaded = f.Session.CompleteFormalDungeonLoading();
                Check(loaded is not null && f.Session.Phase == SessionPhase.Dungeon &&
                    entry.Character.Job == identity.Job && entry.Character.GrowType == identity.Grow &&
                    entry.Character.DungeonState!.LastAttempt == 1 && entry.Character.Revision == before.Revision + 1 &&
                    Same(before with { Revision = entry.Character.Revision, DungeonState = entry.Character.DungeonState }, entry.Character) &&
                    Same(f.Session.Selected!, f.Read()),
                    $"特殊职业/转职{identity.Job}/{identity.Grow}允许真实准入加载，保留原技能状态而不伪造初始化");
                Check(f.Session.ReturnFromFormalDungeon() is not null && f.Session.Phase == SessionPhase.Town &&
                    f.Session.DungeonSnapshot is null && f.Count() == 0 && Same(f.Session.Selected!, f.Read()),
                    "特殊职业/转职可退出且无击杀成长写入；其成长支持另行处理");
            }
        }

        void RejectedEntry()
        {
            using var ordinary = new Fixture("RejectRoute", towns, characters, learning, quests, job: 14);
            ordinary.SelectEntry();
            var current = ordinary.Session.BeginDungeonSelection()!;
            var selected = ordinary.Session.Selected!;
            foreach (var request in new[] { (Dungeon: 4u, Task: 3145u, Difficulty: (byte)0), (Dungeon: 3u, Task: 3146u, Difficulty: (byte)0),
                (Dungeon: 3u, Task: 3145u, Difficulty: (byte)1) })
                Check(ordinary.Session.EnterFormalDungeon(request.Dungeon, request.Task, request.Difficulty, catalog, rooms) is null &&
                    ReferenceEquals(selected, ordinary.Session.Selected) && Same(selected, ordinary.Read()) &&
                    ordinary.Session.CanPublishDungeonSelection(current) && ordinary.Session.DungeonSnapshot is null,
                    "放开普通职业仍保留副本/任务/难度资格，拒绝不预留attempt或key");
            foreach (bool missing in new[] { false, true })
            {
                using var f = new Fixture(missing ? "TaskMissing" : "TaskReached", towns, characters, learning, quests, job: 14);
                f.Sql(missing ? "DELETE FROM character_active_main_quests" : "UPDATE character_active_main_quests SET condition_state=0");
                f.SelectEntry();
                var selection = f.Session.BeginDungeonSelection()!;
                var context = f.Session.CurrentDungeonSelection;
                var before = f.Session.Selected!;
                Check(f.Session.EnterFormalDungeon(3, 3145, 0, catalog, rooms) is null &&
                    ReferenceEquals(before, f.Session.Selected) && Same(before, f.Read()) &&
                    ReferenceEquals(context, f.Session.CurrentDungeonSelection) && f.Session.CanPublishDungeonSelection(selection) &&
                    f.Session.DungeonSnapshot is null, "真实任务缺失或已达成仍拒绝，不改attempt/key/revision和返回身份");
            }
        }

        void Reservation()
        {
            using (var f = new Fixture("CreateAtomic", towns, characters, learning, quests))
            {
                var original = f.Read();
                var itemCount = f.Query("SELECT count(*) FROM character_inventory_items");
                f.Sql("CREATE TRIGGER create_owner_fail AFTER INSERT ON character_dungeon_state BEGIN SELECT RAISE(ABORT,'new owner failure'); END");
                Reject<SqliteException>(() => f.Session.CreateCharacter("OwnerFail", characters.Get(0), learning),
                    "新建owner写入失败回滚正常建角事务");
                Check(Convert.ToInt64(f.Query("SELECT count(*) FROM characters")) == 1 &&
                    Convert.ToInt64(f.Query("SELECT count(*) FROM character_dungeon_state")) == 1 &&
                    Equals(itemCount, f.Query("SELECT count(*) FROM character_inventory_items")) && Same(original, f.Read()),
                    "新建失败不留角色/owner/初始物品，不改既有角色");
                f.Sql("DROP TRIGGER create_owner_fail");
            }
            foreach (string table in new[] { "characters", "character_dungeon_state" })
            {
                using var f = new Fixture(table == "characters" ? "ReserveRole" : "ReserveOwner", towns, characters, learning, quests);
                f.SelectEntry();
                var selected = f.Session.Selected!;
                f.Sql($"CREATE TRIGGER reserve_fail AFTER UPDATE ON {table} BEGIN SELECT RAISE(ABORT,'reservation failure'); END");
                Reject<SqliteException>(() => Enter(f), "预留中途更新失败触发文件库回滚");
                Check(ReferenceEquals(selected, f.Session.Selected) && Same(selected, f.Read()) && f.Session.DungeonSnapshot is null &&
                    f.Session.Phase == SessionPhase.Town, "预留失败不留下角色revision、owner高水位或run");
                f.Sql("DROP TRIGGER reserve_fail");
                var success = Enter(f);
                Check(success.Character.DungeonState!.LastAttempt == 1, "解除故障后仅预留一次");
                Reject<InvalidOperationException>(() => f.Repository.ReserveDungeonAttempt(f.Owner, selected, success.Character.DungeonState.NextObjectKey + 1),
                    "旧revision及旧owner快照不能再次预留");
            }
            using (var f = new Fixture("ReserveCAS", towns, characters, learning, quests))
            {
                f.SelectEntry();
                var selected = f.Session.Selected!;
                f.Sql("UPDATE character_active_main_quests SET condition_state=0");
                Reject<InvalidOperationException>(() => f.Repository.ReserveDungeonAttempt(f.Owner, selected, 300),
                    "同revision任务前提改变也拒绝预留");
                Check(f.Read().DungeonState == selected.DungeonState && f.Read().Revision == selected.Revision,
                    "任务CAS拒绝不消费attempt或key");
            }
            using (var f = new Fixture("ReserveFull", towns, characters, learning, quests))
            {
                f.Sql("UPDATE character_dungeon_state SET next_object_key=65536");
                f.SelectEntry();
                var selected = f.Session.Selected!;
                var recovered = f.Session.EnterFormalDungeon(3, 3145, 0, catalog, rooms);
                Check(recovered is not null && recovered.Monsters.Monsters[0].ObjectKey == CharacterDungeonState.FirstObjectKey &&
                    f.Read().DungeonState!.LastAttempt == 1 && f.Read().Revision == selected.Revision + 1,
                    "存量65536游标在新实例恢复完整路线，仅提交一次尝试与revision");
            }
            using (var f = new Fixture("ResAttempt", towns, characters, learning, quests))
            {
                f.Sql($"UPDATE character_dungeon_state SET last_attempt={long.MaxValue}");
                f.SelectEntry();
                var selected = f.Session.Selected!;
                Check(f.Session.EnterFormalDungeon(3, 3145, 0, catalog, rooms) is null && Same(selected, f.Read()),
                    "尝试号耗尽拒绝，不发生加法回绕");
            }
            using (var f = new Fixture("ReserveLast", towns, characters, learning, quests))
            {
                f.Sql("UPDATE character_dungeon_state SET next_object_key=65535");
                f.SelectEntry();
                var selected = f.Session.Selected!;
                var recovered = f.Session.EnterFormalDungeon(3, 3145, 0, catalog, rooms);
                Check(recovered is not null && recovered.Monsters.Monsters[0].ObjectKey == CharacterDungeonState.FirstObjectKey &&
                    recovered.Character.DungeonState!.LastAttempt == 1 && Same(recovered.Character, f.Read()),
                    "末尾不足整条路线时从起点准备新实例，不提交失败的部分预留");
                Reject<InvalidOperationException>(() => f.Repository.ReserveDungeonAttempt(f.Owner, selected, 300),
                    "回收后旧revision和旧游标不能重复提交尝试");
            }
        }

        void UnknownOwner()
        {
            using var f = new Fixture("LegacyFormal", towns, characters, learning, quests);
            var original = f.Read();
            DatabaseSchemaTests.RewindQuestSystemToV13(f.Path);
            f.Sql("DROP TABLE character_tp_sources; DROP TABLE character_skill_commands; DROP TABLE character_skill_combos; DROP TABLE character_skill_point_sources; DROP TABLE account_expert_contracts; DROP TABLE character_dungeon_settlement_receipts; DROP TABLE character_quest_completion_receipts; DROP TABLE character_quest_clear_receipts; DROP INDEX character_active_quest_acceptance; ALTER TABLE character_active_main_quests DROP COLUMN acceptance_id; DROP TABLE character_dungeon_pickup_receipts");
            f.Sql("DROP TABLE character_dungeon_loot_receipts");
            f.Sql("DROP TABLE character_dungeon_state");
            DatabaseSchemaTests.RewindRaidLifecycle(f.Path);
            f.Sql("PRAGMA user_version=7");
            var legacy = new CharacterRepository(f.Path).LoadCharacter(f.Owner, f.Created.Slot);
            Check(legacy.DungeonState is null &&
                Same(original with { DungeonState = null, Fatigue = null, ActiveMainQuests = legacy.ActiveMainQuests, Learning = original.Learning with { BonusSp = null, BonusTp = null } }, legacy) &&
                original.ActiveMainQuests.Single().ConditionState == legacy.ActiveMainQuests.Single().ConditionState &&
                legacy.ActiveMainQuests.Single().AcceptanceId != Guid.Empty &&
                Convert.ToInt64(f.Query("SELECT count(*) FROM character_dungeon_state")) == 0,
                "真实v7到v8迁移旧角色保持unknown，不改资产或补aux/key");
            f.SelectEntry();
            var selection = f.Session.BeginDungeonSelection()!;
            var context = f.Session.CurrentDungeonSelection;
            var selected = f.Session.Selected!;
            Check(f.Session.EnterFormalDungeon(3, 3145, 0, catalog, rooms) is null && f.Session.DungeonSnapshot is null &&
                ReferenceEquals(selected, f.Session.Selected) && Same(selected, f.Read()) &&
                f.Session.Phase == SessionPhase.MapSelect && ReferenceEquals(context, f.Session.CurrentDungeonSelection) &&
                f.Session.CanPublishDungeonSelection(selection), "旧unknown角色拒绝不改角色、revision或原选图返回上下文");
            Check(f.Session.ReturnFromDungeonSelection() is not null, "缺owner拒绝后仍可返回城镇");
        }

        void ObjectKeyReuse()
        {
            // 500次连续入场只验证对象key单调与跨局复用，房间疲劳不是本用例对象；按调试开关恒满疲劳隔离。
            using var f = new Fixture("KeyReuse", towns, characters, learning, quests,
                debugSettings: new DfoServer.Game.Settings.GameDebugSettings { InfiniteFatigue = true });
            var drops = global::DfoServer.Testing.SelfTestCatalogs.MonsterDrops(pvf);
            var plan = new DungeonDropGenerationPlan("key-reuse-fixture", [new(10000, null, 15, 15, new(1, 1, 0))]);
            FormalDungeonReceipt? previous = null;
            uint cursor = CharacterDungeonState.FirstObjectKey;
            int wraps = 0, pickupRuns = 0;
            uint? reusedGroundKey = null;
            for (int attempt = 1; attempt <= 500; attempt++)
            {
                f.SelectEntry();
                var entry = f.Session.EnterFormalDungeon(3, 3145, 0, catalog, rooms, drops, itemResources.Items,
                    draw: _ => 0, prepareDrops: _ => _ => plan)
                    ?? throw new InvalidOperationException("连续入场被拒：" + f.Session.LastFormalDungeonRejection);
                bool wrapped = entry.Character.DungeonState!.NextObjectKey < cursor;
                if (wrapped) wraps++;
                Check(entry.Snapshot.Identity.TaskAttempt == attempt && entry.Character.DungeonState.NextObjectKey <= 65536 &&
                    (previous is null || !f.Session.CanPublishFormalDungeon(previous)), "跨局复用保留单调attempt并拒绝旧实例发布");
                f.Session.CompleteFormalDungeonLoading();
                if (wrapped && previous is not null)
                {
                    var stale = f.Session.ObserveFormalDungeonDeath(Observation(previous, previous.Monsters.Monsters[0]), kills, growth);
                    Check(stale.Reward is null && stale.Receipt is null, "回绕前一局的高位怪物请求不能给当前低位怪物发奖");
                }
                if (attempt == 1 || wrapped)
                {
                    var observation = Observation(entry, entry.Monsters.Monsters[0]);
                    var death = f.Session.ObserveFormalDungeonDeath(observation, kills, growth);
                    Check(death.Drops.Length == 15, "跨局复用仍提交完整15件冻结掉落");
                    var ground = death.Drops[0];
                    reusedGroundKey ??= ground.Reservation.GroundKey;
                    Check(ground.Reservation.GroundKey == reusedGroundKey, "测试确实复用了历史地面编号");
                    var pickup = f.Session.PickFormalDungeonItem(ground.Reservation.GroundKey, itemResources.Items, itemResources.EpicPieces, itemResources.GoldLimits);
                    Check(pickup.Outcome == DungeonRunOutcome.Accepted &&
                        f.Session.PickFormalDungeonItem(ground.Reservation.GroundKey, itemResources.Items, itemResources.EpicPieces, itemResources.GoldLimits).Outcome == DungeonRunOutcome.Rejected &&
                        f.Session.ObserveFormalDungeonDeath(observation, kills, growth).Outcome == DungeonRunOutcome.Duplicate,
                        "跨实例同key可以拾取，同实例死亡和拾取重复不多发奖励");
                    pickupRuns++;
                }
                previous = entry;
                cursor = f.Session.Selected!.DungeonState!.NextObjectKey;
                f.Session.ReturnToRoster();
            }
            Check(wraps >= 2 && f.Read().DungeonState!.LastAttempt == 500 &&
                Convert.ToInt64(f.Query("SELECT count(*) FROM character_dungeon_drop_objects")) == 15 * pickupRuns &&
                Convert.ToInt64(f.Query("SELECT count(*) FROM character_dungeon_drop_pickups WHERE receipt_format=1")) == pickupRuns && f.Count() == pickupRuns &&
                Convert.ToInt64(f.Query("SELECT count(*) FROM pragma_foreign_key_check")) == 0,
                "500次真实Session入场跨过多轮65535，实际资产、成长及各实例完整冻结保留且外键完整");
        }

        void CacheLifecycle()
        {
            using var f = new Fixture("RuntimeCache", towns, characters, learning, quests);
            const byte level = 15;
            f.Sql($"DELETE FROM character_active_main_quests; UPDATE characters SET level={level},experience={growth.GetLevelStartExperience(level)}; " +
                $"UPDATE character_skill_points SET sp=sp+{growth.GetSpDelta(1, level)}");
            f.SelectEntry();
            Check(f.Session.CachedGameContexts == 0 && f.Session.GetState() == UserState.Town,
                "角色在城镇时没有副本缓存");
            string[] retired = ["character_dungeon_fatigue_receipts", "character_dungeon_loot_receipts",
                "character_dungeon_pickup_receipts", "character_dungeon_passive_pickups"];
            f.Sql($$"""
                INSERT INTO character_dungeon_fatigue_receipts VALUES(
                  {{f.Created.Id}},'formal-session','ffffffffffffffffffffffffffffffff',1,42,1,1,'ignored-history',0,1,'{}','{}')
                """);
            foreach (string table in retired)
                f.Sql($"CREATE TRIGGER no_new_{table} BEFORE INSERT ON {table} BEGIN SELECT RAISE(ABORT,'retired transient table'); END");
            var drops = Testing.SelfTestCatalogs.MonsterDrops(pvf);
            var gold = new DungeonDropGenerationPlan("cache-fixture", [new(10000, null, 1, 1, new(25, 25, 0))]);
            var first = f.Session.EnterFormalDungeon(3, 0, 0, catalog, rooms, drops, itemResources.Items,
                draw: _ => 0, prepareDrops: _ => _ => gold)
                ?? throw new InvalidOperationException("缓存测试的普通副本不能入场");
            Check(f.Session.CachedGameContexts == 1 && f.Session.GetState() == UserState.Dungeon,
                "副本Loading已属于Dungeon粗状态，安装一个缓存而非按房间新建缓存");
            Check(f.Session.EnterFormalDungeon(5, 0, 0, catalog, rooms) is null &&
                f.Session.CachedGameContexts == 1 && f.Session.CanPublishFormalDungeon(first),
                "不合法的换副本请求不能提前失效原缓存");
            var loaded = f.Session.CompleteFormalDungeonLoading()!;
            Check(f.Session.CachedGameContexts == 1 && loaded.Snapshot.Identity.InstanceId == first.Snapshot.Identity.InstanceId,
                "Loading到Dungeon只推进原实例阶段，不更换缓存");
            var monster = loaded.Monsters.Monsters.First(row => row.IsBlocking && row.CanReportDeath);
            var death = f.Session.ObserveFormalDungeonDeath(Observation(loaded, monster), kills, growth);
            var ground = death.Drops.Single();
            uint beforeGold = f.Session.Selected!.Inventory.Gold;
            var picked = f.Session.PickFormalDungeonItem(ground.Reservation.GroundKey, itemResources.Items,
                itemResources.EpicPieces, itemResources.GoldLimits);
            Check(picked.Outcome == DungeonRunOutcome.Accepted && f.Session.Selected!.Inventory.Gold == beforeGold + 25 &&
                f.Session.PickFormalDungeonItem(ground.Reservation.GroundKey, itemResources.Items, itemResources.EpicPieces, itemResources.GoldLimits)
                    .Outcome == DungeonRunOutcome.Rejected &&
                retired.All(table => Convert.ToInt64(f.Query("SELECT count(*) FROM " + table)) ==
                    (table == "character_dungeon_fatigue_receipts" ? 1 : 0)) &&
                Convert.ToInt64(f.Query("SELECT count(*) FROM character_dungeon_drop_batches WHERE freeze_format=1")) == 1 &&
                Convert.ToInt64(f.Query("SELECT count(*) FROM character_dungeon_drop_objects")) == 1 &&
                Convert.ToInt64(f.Query("SELECT count(*) FROM character_dungeon_drop_pickups WHERE receipt_format=1")) == 1,
                "四张退役明细仍禁止新增，原死亡完整冻结与实际拾取各持久一次，旧疲劳行不处理");
            Check(f.Session.ReturnFromFormalDungeon() is not null && f.Session.CachedGameContexts == 0 &&
                f.Session.GetState() == UserState.Town && DungeonTransientReceipts.Find(f.Owner, first.Snapshot.Identity) is null,
                "正式回城结束缓存和临时回执，保留已提交的角色资产");
            var same = f.Session.EnterFormalDungeon(3, 0, 0, catalog, rooms)
                ?? throw new InvalidOperationException("缓存测试的同index新实例不能入场");
            Check(same.Snapshot.Identity.InstanceId != first.Snapshot.Identity.InstanceId &&
                f.Session.CachedGameContexts == 1 && !f.Session.CanPublishFormalDungeon(first) &&
                same.Snapshot.LivingMonsters.Count > 0,
                "相同副本index的新挑战也新建实例，不能因index相同继承死亡状态");
            Check(f.Session.ReturnFromFormalDungeon() is not null && f.Session.CachedGameContexts == 0,
                "取消加载同样清除原副本缓存");
            var other = f.Session.EnterFormalDungeon(5, 0, 0, catalog, rooms)
                ?? throw new InvalidOperationException("缓存测试的另一副本不能入场：" + f.Session.LastFormalDungeonRejection);
            Check(other.Entry.DungeonId == 5 && f.Session.FormalEnteredDungeonId == 5 && f.Session.CachedGameContexts == 1 &&
                !f.Session.CanPublishFormalDungeon(same) && other.Snapshot.LivingMonsters.Count > 0,
                "另一副本index只有自己的对象、房间和掉落上下文，旧实例回执不能发布");
            f.Session.End();
            Check(f.Session.GetState() == UserState.CharacterSelect && f.Session.CachedGameContexts == 0 &&
                f.Session.DungeonSnapshot is null, "断线结束会话缓存，不能保留另一副本的运行数据");
        }

        void Invalidation()
        {
            using var f = new Fixture("FormalEpoch", towns, characters, learning, quests);
            f.SelectEntry();
            var first = Enter(f);
            f.Session.ReturnToRoster();
            Check(f.Session.DungeonSnapshot is null && f.Session.CachedGameContexts == 0 &&
                !f.Session.CanPublishFormalDungeon(first), "返回选角清正式实例和缓存并拒绝旧epoch回执");
            f.Session.Select(f.Created.Slot);
            f.Session.MarkReady();
            MoveToEntry(f.Session);
            var second = Enter(f);
            Check(second.SelectionEpoch != first.SelectionEpoch && second.Snapshot.Identity.InstanceId != first.Snapshot.Identity.InstanceId &&
                second.Snapshot.Identity.TaskAttempt == 2, "同角色重新选角建立新正式身份");
            Check(!f.Session.CanPublishFormalDungeon(second with { SelectionEpoch = first.SelectionEpoch }) &&
                !f.Session.CanPublishFormalDungeon(second with { Snapshot = second.Snapshot with { Identity = first.Snapshot.Identity } }),
                "伪造旧selection epoch或instance的回执不能发布");
            f.Session.CompleteFormalDungeonLoading();
            var beforeOldReport = f.Session.Selected!;
            var ignoredOldRun = f.Session.ObserveFormalDungeonDeath(Observation(first,first.Monsters.Monsters[0]),kills,growth);
            Check(ignoredOldRun is { Outcome: DungeonRunOutcome.Accepted, Reward:null, Receipt:null, ClearFact:null } &&
                ignoredOldRun.MonsterKey == first.Monsters.Monsters[0].ObjectKey &&
                f.Count() == 0 && ReferenceEquals(beforeOldReport,f.Session.Selected),
                "新实例旧key只作审计，不发送死亡通知或产生当前实例奖励");
            using var replacement = f.Owners.Acquire(f.Owner.Account, () => { });
            Reject<InvalidOperationException>(() => f.Session.CanPublishFormalDungeon(second), "旧generation不能发布正式回执");
            Reject<InvalidOperationException>(() => f.Session.CompleteFormalDungeonLoading(), "旧generation不能完成新加载");
            Reject<InvalidOperationException>(() => f.Session.EnterFormalDungeon(3, 3145, 1, catalog, rooms, retry: second),
                "再挑战参数不匹配的零变化拒绝仍校验原lease，旧generation不能绕过owner");
            f.Session.End();
            Check(new CharacterRepository(f.Path).LoadCharacter(replacement, f.Created.Slot).DungeonState == second.Character.DungeonState,
                "旧Session结束不回退已预留身份或释放新owner");
        }

        void RecordedNormalStatistics()
        {
            using var f = new Fixture("RecordedKill", towns, characters, learning, quests, secondCharacter: true);
            f.SelectEntry();
            var entry = Enter(f);
            Check(entry.Character.Id == 2 && entry.Monsters.Monsters.Select(monster => monster.ObjectKey).SequenceEqual(new ushort[] { 256, 257, 258, 259 }),
                "本版统计回归fixture明确绑定本人2及首房预留key");
            f.Session.CompleteFormalDungeonLoading();
            // 四种匹配版本的正常统计形状，含非零前缀和不同受击次数；不是合成死亡的实机奖励证据。
            string[] bodies =
            [
                "0201000002000000000000000000000000009A02000000000000020000000111010200140200000000000002000403DC000000000084670003000000F8010000000000008302D0006102ED00150001002500000040B77FF1",
                "0001000002000C0000000000000001000000EE0200000000000002000000011101020045020000000000000200A102E200000000006C3D0003000000F8010000000000003D02C5001B02E20015000100250000004818B0DC",
                "030100000200000000000000000000000000500300000000000004000000011101020010030000000000000400C602E0000000000049160003000000F80100000000000087020701A702E000150001002A000000B653753C",
                "0101000002000000000000000000000000006E02000000000000070000000111010200530200000000000007002C01EF00000000003C150003000000F8010000000000002D0218018902F400150001002A000000B0355B52"
            ];
            int index = 0;
            foreach (string body in bodies)
            {
                byte[] bytes = Convert.FromHexString(body);
                Check(DungeonRequests.TryParseDeath(bytes, out var parsed) && parsed!.SinglePlayerStatistics is { ChecksumMatches: true },
                    "实际统计形状的完整88B与MD5折叠匹配");
                var statistics = parsed!.SinglePlayerStatistics!;
                var part = statistics.Participant;
                var observation = new NativeDungeonDeathObservation(parsed.ObjectKey, parsed.Reporter, part.Kind, part.ObjectKey,
                    part.CumulativeHpDecrease, part.PositiveHpDecreaseCount, statistics.RewriteFlag, statistics.SpawnLevel,
                    statistics.SpawnHpSnapshot, statistics.Unknown18, statistics.Unknown26, statistics.ChecksumMatches,
                    Convert.ToHexString(SHA256.HashData(bytes)));
                var committed = f.Session.ObserveFormalDungeonDeath(observation, kills, growth);
                Check(committed.Outcome == DungeonRunOutcome.Accepted && committed.ClearFact is null &&
                    f.Count() == ++index && f.Session.Selected!.Learning.Experience == (ulong)index * 104,
                    "正式Native合同接受不同计数/前缀，每key仅落一次真实成长事务");
                Check(f.Session.ObserveFormalDungeonDeath(observation,kills,growth) is
                    { Outcome: DungeonRunOutcome.Duplicate, Reward:null, Receipt:null } && f.Count() == index,
                    "不同真实形状的普通重放只审计，不重复通知或经验");
            }
            Check(f.Session.DungeonSnapshot!.LivingMonsters.Count == 0 && f.Session.Phase == SessionPhase.Dungeon &&
                f.Session.MoveFormalDungeon(1, 1) is not null && Same(f.Read(), f.Session.Selected!),
                "首房四怪提交后才可过房，未越过Boss清图资格");
        }

        void FrozenLootTransaction()
        {
            using var f = new Fixture("LootReceipt", towns, characters, learning, quests, job: 14);
            f.SelectEntry();
            f.Session.BeginDungeonSelection();
            var drops = global::DfoServer.Testing.SelfTestCatalogs.MonsterDrops(pvf);
            int calls = 0;
            bool dropping = false;
            var entry = f.Session.EnterFormalDungeon(3, 3145, 0, catalog, rooms, drops, itemResources.Items,
                bound => { if (!dropping) return bound - 1; calls++; return 0; }) ?? throw new InvalidOperationException("FAIL 显式池准入失败");
            Check(entry.Character.DungeonState!.NextObjectKey > 285 && Same(entry.Character, f.Read()),
                "准入同事务预留完整怪物与最多一件地面key范围");
            // 本用例保留原普通怪顺序，只核对死亡掉落随机与SQL回滚；精英另有专项覆盖。
            dropping = true;
            f.Session.CompleteFormalDungeonLoading();
            var first = entry.Monsters.Monsters[0];
            var observation = Observation(entry, first) with
            {
                CumulativeHpDecrease = 696, PositiveHpDecreaseCount = 2, Unknown18 = 831, Unknown26 = 3
            };
            var changedObservation = observation with { Unknown18 = 832, ReporterKey = ushort.MaxValue,
                Participants = [new(0xABCD,65535,0,0)], BodySha256 = new string('B',64) };
            var original = f.Session.Selected!;
            f.Sql("CREATE TRIGGER loot_fail AFTER INSERT ON character_dungeon_growth_receipts BEGIN SELECT RAISE(ABORT,'late loot failure'); END");
            Reject<SqliteException>(() => f.Session.ObserveFormalDungeonDeath(observation, kills, growth),
                "掉落账本晚期故障回滚同事务经验与成长receipt");
            Check(calls == 2 && f.Count() == 0 && Same(original, f.Read()) && ReferenceEquals(original, f.Session.Selected) &&
                Convert.ToInt64(f.Query("SELECT count(*) FROM character_dungeon_loot_receipts")) == 0,
                "冻结随机后SQL回滚不留经验或物品，也不提前发布地面");
            Reject<SqliteException>(() => f.Session.ObserveFormalDungeonDeath(changedObservation,kills,growth),
                "来源和统计变化仍恢复冻结候选，未解除的SQL故障继续完整回滚");
            Check(calls == 2 && f.Count() == 0 && Same(original,f.Read()) && ReferenceEquals(original,f.Session.Selected) &&
                Convert.ToInt64(f.Query("SELECT count(*) FROM character_dungeon_loot_receipts")) == 0,
                "改变审计观察不更换事件、不重抽掉落，也不提前提交经验");
            f.Sql("DROP TRIGGER loot_fail");
            var death = f.Session.ObserveFormalDungeonDeath(changedObservation,kills,growth);
            var ground = death.Drop ?? throw new InvalidOperationException("FAIL 明确合成命中没有地面对象");
            Check(calls == 2 && death.Outcome == DungeonRunOutcome.Accepted && ground.Core.ItemId == 1000 && ground.Core.Count == 1 &&
                f.Count() == 1 && f.Read().Learning.Experience == 104 &&
                Convert.ToInt64(f.Query("SELECT count(*) FROM character_dungeon_loot_receipts")) == 0 && f.Transient?.Loot.Count == 1,
                "原事实重试只复用冻结结果，一次提交XP及完整地面核心");
            Check(f.Session.ObserveFormalDungeonDeath(observation,kills,growth) is
                { Outcome: DungeonRunOutcome.Duplicate, Reward:null, Receipt:null } &&
                f.Session.ObserveFormalDungeonDeath(changedObservation,kills,growth) is
                { Outcome: DungeonRunOutcome.Duplicate, Reward:null, Receipt:null } && calls == 2 &&
                f.Count() == 1 && f.Read().Learning.Experience == 104 &&
                Convert.ToInt64(f.Query("SELECT count(*) FROM character_dungeon_loot_receipts")) == 0 && f.Transient?.Loot.Count == 1,
                "控制死亡提交后原观察与变化观察均只审计，不重发通知、经验、抽奖或地面");
            uint key = ground.Reservation.GroundKey;
            Check(f.Session.PickFormalDungeonItem(key + 1000, itemResources.Items, itemResources.EpicPieces).Outcome == DungeonRunOutcome.Rejected,
                "客户端任意key不提供拾取资格");
            var beforePickup = f.Session.Selected!;
            f.Sql("CREATE TRIGGER pickup_fail AFTER INSERT ON inventory_audit_log BEGIN SELECT RAISE(ABORT,'late pickup failure'); END");
            Reject<SqliteException>(() => f.Session.PickFormalDungeonItem(key, itemResources.Items, itemResources.EpicPieces),
                "拾取receipt晚期故障整批回滚新UID、库存和revision");
            Check(Same(beforePickup, f.Read()) && ReferenceEquals(beforePickup, f.Session.Selected) &&
                f.Session.ReturnFromFormalDungeon() is null && f.Session.MoveFormalDungeon(1, 1) is null &&
                f.Session.ObserveFormalDungeonDeath(Observation(entry, entry.Monsters.Monsters[1]), kills, growth).Outcome == DungeonRunOutcome.Rejected,
                "存储异常保留拾取准备并拒绝新死亡或过房退出，不丢原地面");
            f.Sql("DROP TRIGGER pickup_fail");
            var pickup = f.Session.PickFormalDungeonItem(key, itemResources.Items, itemResources.EpicPieces);
            Check(pickup.Outcome == DungeonRunOutcome.Accepted && pickup.Commit is { Duplicate: false, ItemUid: > 0, ProjectionCurrent: true, Destination.Index: 65 } &&
                f.Read().Inventory.Items.Single(item => item.Core.ItemId == 1000).Core == ground.Core && Same(f.Read(), f.Session.Selected!),
                "重试提交实际UID/槽和完整原核心，再确认地面消费");
            long uid = pickup.Commit!.ItemUid;
            Check(f.Session.PickFormalDungeonItem(key, itemResources.Items, itemResources.EpicPieces).Outcome == DungeonRunOutcome.Rejected &&
                Convert.ToInt64(f.Query("SELECT count(*) FROM character_dungeon_pickup_receipts")) == 0 &&
                f.Transient?.Pickups.Values.Count(value => value.Committed) == 1,
                "重复拾取无第二份资产或receipt");
            var second = f.Session.ObserveFormalDungeonDeath(Observation(entry, entry.Monsters.Monsters[1]), kills, growth);
            var merged = f.Session.PickFormalDungeonItem(second.Drop!.Reservation.GroundKey, itemResources.Items, itemResources.EpicPieces);
            Check(merged.Commit!.ItemUid == uid && f.Read().Inventory.Items.Single(item => item.Uid == uid).Core == (ground.Core with { Value = 2 }),
                "第二个真实来源核心只加一件，复用已有堆叠UID及全部属性");
            DungeonGroundObject? cached = null;
            foreach (var monster in entry.Monsters.Monsters.Skip(2))
                cached = f.Session.ObserveFormalDungeonDeath(Observation(entry, monster), kills, growth).Drop;
            var nextRoom = f.Session.MoveFormalDungeon(1, 1)!;
            Check(nextRoom is not null &&
                f.Session.PickFormalDungeonItem(cached!.Reservation.GroundKey, itemResources.Items, itemResources.EpicPieces).Outcome == DungeonRunOutcome.Rejected &&
                f.Session.CompleteFormalDungeonLoading() is not null &&
                f.Session.PickFormalDungeonItem(cached.Reservation.GroundKey, itemResources.Items, itemResources.EpicPieces).Outcome == DungeonRunOutcome.Rejected,
                "加载及另一个房间拒绝旧房key，保留原物品供合法回访");
            foreach (var monster in nextRoom!.Monsters.Monsters)
                Check(f.Session.ObserveFormalDungeonDeath(Observation(nextRoom, monster), kills, growth).Outcome == DungeonRunOutcome.Accepted,
                    "按原正式死亡合同清理相邻房间，不能跳过回访准入");
            var start = entry.Entry.StartMap.Position;
            Check(f.Session.MoveFormalDungeon(checked((byte)start.X), checked((byte)start.Y)) is not null && f.Session.CompleteFormalDungeonLoading() is not null,
                "清房后经原静态连接回访起始房间");
            var revisited = f.Session.PickFormalDungeonItem(cached!.Reservation.GroundKey, itemResources.Items, itemResources.EpicPieces);
            Check(revisited.Outcome == DungeonRunOutcome.Accepted && revisited.Commit?.ItemUid == uid &&
                f.Read().Inventory.Items.Single(item => item.Uid == uid).Core == (ground.Core with { Value = 3 }) &&
                Same(f.Read(), f.Session.Selected!) &&
                f.Session.PickFormalDungeonItem(cached.Reservation.GroundKey, itemResources.Items, itemResources.EpicPieces).Outcome == DungeonRunOutcome.Rejected &&
                Convert.ToInt64(f.Query("SELECT count(*) FROM character_dungeon_pickup_receipts")) == 0 &&
                f.Transient?.Pickups.Values.Count(value => value.Committed) == 3,
                "回访原地面同事务加一件并保留UID，重复不追加资产或receipt");
            var persistedExperience = f.Session.Selected!.Learning.Experience;
            f.Session.ReturnToRoster();
            f.Session.Select(f.Created.Slot);
            f.Session.MarkReady();
            Check(f.Session.Selected!.Learning.Experience == persistedExperience && f.Session.Selected.Inventory.Items.Single(item => item.Uid == uid).Core.Value == 3 &&
                f.Session.Selected.DungeonState!.NextObjectKey == entry.Character.DungeonState.NextObjectKey &&
                new CharacterRepository(f.Path).LoadCharacter(f.Owner, f.Created.Slot).Inventory.Items.Single(item => item.Uid == uid).Core.Value == 3,
                "重选/仓储重开保留拾取资产、XP和不回绕高水位；非实机重启替代");
            using var miss = new Fixture("LootMiss", towns, characters, learning, quests);
            miss.SelectEntry();
            int missCalls = 0;
            var missEntry = miss.Session.EnterFormalDungeon(3, 3145, 0, catalog, rooms, drops, itemResources.Items,
                bound => { missCalls++; return bound - 1; })!;
            missCalls = 0;
            miss.Session.CompleteFormalDungeonLoading();
            var missObservation = Observation(missEntry, missEntry.Monsters.Monsters[0]);
            Check(miss.Session.ObserveFormalDungeonDeath(missObservation, kills, growth).Drop is null && missCalls == 2 &&
                Convert.ToInt64(miss.Query("SELECT count(*) FROM character_dungeon_loot_receipts")) == 0 && miss.Transient?.Loot.Count == 1 &&
                miss.Session.ObserveFormalDungeonDeath(missObservation, kills, growth).Outcome == DungeonRunOutcome.Duplicate && missCalls == 2,
                "未命中也保存冻结事实，精确重放不能改成物品或再次抽奖");
            using var full = new Fixture("LootFull", towns, characters, learning, quests);
            var empty = full.Read();
            var range = ItemSlotBoundService.Range(DfoServer.GameWorld.Items.ItemKind.Consumable, empty.Inventory.Expansion);
            var fullCore = InventoryRewardGrantService.Create(itemResources.Items.Get(1047), 1000);
            // 快捷栏全部空闲，消耗品满页仍必须拒绝新堆。
            var occupied = Enumerable.Range(range.Start, range.End - range.Start + 1)
                .Select(slot => new InventoryItem(0, new(InventoryListType.Main, checked((short)slot)), fullCore)).ToImmutableArray();
            full.Repository.CommitInventory(full.Owner, empty, new(empty.Inventory,
                empty.Inventory with { Items = empty.Inventory.Items.AddRange(occupied) }, "explicit-full-bag-fixture"));
            full.SelectEntry();
            bool fullDropping = false;
            var fullEntry = full.Session.EnterFormalDungeon(3, 3145, 0, catalog, rooms, drops, itemResources.Items,
                bound => fullDropping ? 0 : bound - 1)!;
            fullDropping = true;
            full.Session.CompleteFormalDungeonLoading();
            var fullDeath = full.Session.ObserveFormalDungeonDeath(Observation(fullEntry, fullEntry.Monsters.Monsters[0]), kills, growth);
            var fullGround = fullDeath.Drop!;
            var fullBefore = full.Session.Selected!;
            Check(full.Session.PickFormalDungeonItem(fullGround.Reservation.GroundKey, itemResources.Items, itemResources.EpicPieces).Outcome == DungeonRunOutcome.Rejected &&
                Same(fullBefore, full.Read()) && Convert.ToInt64(full.Query("SELECT count(*) FROM character_dungeon_pickup_receipts")) == 0,
                "快捷栏全空但消耗品满页仍拒绝，不提交UID/资产/revision或消耗冻结地面");
            var stored = full.Read();
            var freed = stored.Inventory with { Items = stored.Inventory.Items.Where(item => item.Position.Index != range.Start || item.Position.List != InventoryListType.Main).ToImmutableArray() };
            full.Repository.CommitInventory(full.Owner, stored, new(stored.Inventory, freed, "explicit-free-slot-fixture"));
            // 模拟在线快照恢复；不重建Run或抽奖，合法原地面仍可取。
            var expected = full.Session.Selected!;
            Check(full.Session.PickFormalDungeonItem(fullGround.Reservation.GroundKey, itemResources.Items, itemResources.EpicPieces).Outcome == DungeonRunOutcome.Rejected,
                "陈旧满包快照仍拒绝，不覆盖已保存空槽");
            Check(full.Read().Inventory.Items.Length == stored.Inventory.Items.Length - 1 && ReferenceEquals(expected, full.Session.Selected),
                "旧库存冲突不造成第二份资产或自动重抽");
            full.Session.UpdateProgress(new(CharacterProgressField.Level, full.Owner.Account, full.Created.Name, expected.Level, 0));
            var resumedPickup = full.Session.PickFormalDungeonItem(fullGround.Reservation.GroundKey, itemResources.Items, itemResources.EpicPieces);
            var resumedItem = full.Read().Inventory.Find(new(InventoryListType.Main, range.Start));
            Check(resumedPickup.Outcome == DungeonRunOutcome.Accepted && resumedItem?.Core == fullGround.Core,
                $"测试显式恢复后拾取原地面；outcome={resumedPickup.Outcome},slot={resumedPickup.Commit?.Destination.Index},count={resumedItem?.Core.Count},phase={full.Session.Phase}");
        }

        void PerMonsterDrops()
        {
            using var f = new Fixture("RuleMonsters", towns, characters, learning, quests);
            f.SelectEntry();
            var drops = global::DfoServer.Testing.SelfTestCatalogs.MonsterDrops(pvf);
            var pool = new DungeonItemDropPool(new DungeonWeightedDropItem[] { new(itemResources.Items.Get(1000), 50) });
            var ordinary = new DungeonDropGenerationPlan("明示普通怪规则", [new(10000, pool, 3, 3)]);
            var boss = new DungeonDropGenerationPlan("明示Boss规则", [new(10000, null, 2, 2, new(5, 17, 0))]);
            var beforeEntry = f.Session.Selected!;
            Check(f.Session.EnterFormalDungeon(3, 3145, 0, catalog, rooms, drops, itemResources.Items,
                prepareDrops: _ => _ => throw new NotSupportedException("明确未解释的fixture规则")) is null &&
                ReferenceEquals(beforeEntry, f.Session.Selected) && Same(beforeEntry, f.Read()) && f.Session.DungeonSnapshot is null,
                "未支持的逐怪规则在预留attempt/key前拒绝准入，不静默换用显式掉率或写资产");
            var inputs = new Dictionary<ushort, (int Level, byte Type, byte Difficulty)>();
            int draws = 0;
            var entry = f.Session.EnterFormalDungeon(3, 3145, 0, catalog, rooms, drops, itemResources.Items,
                draw: _ => { draws++; return 0; }, prepareDrops: route => monster =>
                {
                    inputs.Add(monster.ObjectKey, (monster.CandidateLevel, monster.SpawnType, route.Difficulty));
                    return monster.IsBoss ? boss : ordinary;
                })!;
            int preparedCount = inputs.Count;
            int championDraws = draws;
            Check(preparedCount > entry.Monsters.Monsters.Count && inputs.Values.Any(row => row.Type == 3) &&
                inputs.Values.Select(row => row.Level).Distinct().Count() > 1 && inputs.Values.All(row => row.Difficulty == 0) &&
                inputs.Values.Any(row => row.Type == 1) && championDraws > 0,
                "准入锁外先沿注入随机冻结精英，再为全路线各怪准备实际等级/类型/难度规则");
            var maps = entry.Entry.Maze.MapSpecification!;
            var receipt = f.Session.CompleteFormalDungeonLoading()!;
            int deaths = 0, seenBoss = 0;
            for (int mapIndex = 0; mapIndex < maps.Count; mapIndex++)
            {
                var map = maps[mapIndex];
                Check(receipt.Snapshot.CurrentMap == map, "逐怪规则沿原批准路线与房间身份消费");
                foreach (var monster in receipt.Monsters.Monsters.Where(row => row.IsBlocking))
                {
                    var observation = monster.IsBoss
                        ? new NativeDungeonDeathObservation(monster.ObjectKey, f.Created.Id, 0x0111, f.Created.Id,
                            600, 3, 0, 6, 0, 620, 3, true, new string('C', 64))
                        : Observation(receipt, monster);
                    int beforeDraws = draws;
                    if (deaths == 0)
                    {
                        f.Sql("CREATE TRIGGER per_monster_fail AFTER INSERT ON character_dungeon_growth_receipts BEGIN SELECT RAISE(ABORT,'per monster failure'); END");
                        Reject<SqliteException>(() => f.Session.ObserveFormalDungeonDeath(observation, kills, growth),
                            "逐怪集合在真实SQL晚期失败后保留原冻结结果");
                        beforeDraws = draws;
                        f.Sql("DROP TRIGGER per_monster_fail");
                    }
                    var result = f.Session.ObserveFormalDungeonDeath(observation, kills, growth);
                    Check(result.Outcome == DungeonRunOutcome.Accepted && inputs.Count == preparedCount &&
                        result.Drops.All(row => row.ResourceIdentity == (monster.IsBoss ? boss : ordinary).ResourceSource),
                        "真实Session死亡只消费原monster计划，保留每怪来源，不重新解析或改换规则");
                    if (monster.IsBoss)
                    {
                        seenBoss++;
                        Check(result.Drops.Length == 2 && result.Drops.All(row => row.IsGold && row.GoldAmount == 17),
                            "Boss保留两枚独立金币数量，不能套用普通怪的物品计划或容量");
                    }
                    else
                        Check(result.Drops.Length == 1 && result.Drops[0].Core.Count == 3 && !result.Drops[0].IsGold,
                            "普通怪沿自身规则生成三件完整堆叠，不能套用Boss金币规则");
                    int afterDraws = draws;
                    Check((deaths != 0 || afterDraws == beforeDraws) &&
                        f.Session.ObserveFormalDungeonDeath(observation, kills, growth).Outcome == DungeonRunOutcome.Duplicate && draws == afterDraws,
                        "首次SQL失败重试与精确重复均不重抽已冻结的逐怪结果");
                    deaths++;
                }
                if (mapIndex + 1 < maps.Count)
                {
                    var nextMap = maps[mapIndex + 1].Position;
                    Check(f.Session.MoveFormalDungeon((byte)nextMap.X, (byte)nextMap.Y) is not null, "逐怪集合清房后合法过房");
                    receipt = f.Session.CompleteFormalDungeonLoading()!;
                }
            }
            Check(seenBoss == 1 && f.Count() == deaths &&
                Convert.ToInt64(f.Query("SELECT count(*) FROM character_dungeon_drop_batches WHERE freeze_format=1")) == deaths &&
                f.Transient!.Loot.Values.Count(value => value.ResourceIdentity == "明示Boss规则") == 1,
                "普通/Boss分别记账，全部死亡集合与成长同事务保留正确规则来源");
        }

        FormalDungeonReceipt Enter(Fixture f) => f.Session.EnterFormalDungeon(3, 3145, 0, catalog, rooms)
            ?? throw new InvalidOperationException("FAIL 正式3145资源入口未建立");
        void MoveToEntry(GameSession session) => Check(session.LeaveRoom() && session.Travel(new(38, 2, 1085, 249, 5, 38, 0, 0)),
            "重新选角后经合法出房旅行到左入口");
    }

    internal static void VerifyShopSettlementAdmission(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters, ItemResources itemResources)
    {
        int beforeChecks = TestCheck.Count;
        const int targetShop = 400000097, nolan = 400001107, material = 490705400;
        const int otherShop = 1000, otherNpc = 1000, otherItem = 2600655, cube = 3037;
        (int Item, int Cost, int? Limit)[] rows =
        [
            (490705437, 4, 8), (490705421, 9, 2), (490705286, 4, 3), (490705424, 6, 1),
            (490705422, 4, 2), (490703887, 3, 1), (490700411, 3, 1), (490013680, 1, null)
        ];
        var learning = SelfTestCatalogs.SkillLearning(pvf);
        var quests = SelfTestCatalogs.Quests(pvf);
        var dungeons = SelfTestCatalogs.Dungeons(pvf);
        var rooms = SelfTestCatalogs.Rooms(pvf);
        var kills = SelfTestCatalogs.KillExperience(pvf);
        var growth = SelfTestCatalogs.Growth(pvf);
        var clear = SelfTestCatalogs.ClearExperience(pvf);
        var clock = ShopFixtureClock(itemResources, rows.Select(row => row.Item).Concat([material, otherItem, cube]));
        var now = clock.GetUtcNow();
        var shop = new InventoryShopService(itemResources.Items, itemResources.Shops, itemResources.GoldLimits, itemResources.EpicPieces, clock);
        using var fixture = new Fixture("ShopSettle", towns, characters, learning, quests, timeProvider: clock);
        fixture.SelectEntry();
        // 沿既有Delilah夹具用真实发放服务初始化3037账号钱包，不伪造背包币种或装备。
        Check(fixture.Session.MutateInventory(character => InventoryRewardGrantService.TryGrantBatch(character,
            [new(material, 500), new(cube, 41)], itemResources.Items, itemResources.GoldLimits, itemResources.EpicPieces, now)).Accepted,
            "结算商店夹具真实提交兑换材料与无色账号钱包");
        var prepared = fixture.Read();
        Check(CountItem(prepared, material) == 500 && CubeBalance(prepared) == 41 && CountItem(prepared, cube) == 0 &&
            CountItem(prepared, otherItem) == 0 && rows.All(row => CountItem(prepared, row.Item) == 0 &&
                !prepared.Inventory.ShopPurchases.Any(value => value.ItemId == row.Item)) && ShopSame(prepared, fixture.Session.Selected!),
            "结算负例保留八项首次购买资格且3037只存在于真实账号钱包");
        Check(fixture.Session.BeginDungeonSelection() is not null, "结算场景沿正式Session选择入口");
        var entry = fixture.Session.EnterFormalDungeon(3, 3145, 0, dungeons, rooms)
            ?? throw new InvalidOperationException("FAIL 结算商店夹具无法进入正式3145");
        var maps = dungeons.Get(3).Mazes.Single(maze => maze.ConnectsQuest(3145)).MapSpecification!;
        var loaded = fixture.Session.CompleteFormalDungeonLoading()
            ?? throw new InvalidOperationException("FAIL 结算商店夹具首次加载失败");
        bool routeValid = loaded.Snapshot.Phase == SinglePlayerDungeonPhase.Dungeon && fixture.Session.Phase == SessionPhase.Dungeon;
        int deathCount = 0;
        DungeonClearFact? clearFact = null;
        for (int i = 0; i < maps.Count; i++)
        {
            routeValid &= loaded.Snapshot.CurrentMap == maps[i];
            foreach (var monster in loaded.Monsters.Monsters.Where(monster => monster.IsBlocking))
            {
                // 与既有真实结算用例相同的合成死亡观察；不代表客户端击杀或通知验证。
                var death = fixture.Session.ObserveFormalDungeonDeath(new(monster.ObjectKey, fixture.Created.Id, 0x0111, fixture.Created.Id,
                    600, 3, 0, monster.SpawnType == 3 ? 6u : (uint)monster.CandidateLevel,
                    monster.SpawnType == 3 ? 0UL : 504UL, 620, 3, true, new string('A', 64)), kills, growth);
                if (death.Outcome != DungeonRunOutcome.Accepted || death.Receipt is null)
                    throw new InvalidOperationException("FAIL 结算商店夹具正式死亡未提交");
                deathCount++;
                if (death.ClearFact is { } fact)
                {
                    routeValid &= clearFact is null && i == maps.Count - 1;
                    clearFact = fact;
                }
            }
            if (i < maps.Count - 1)
            {
                var target = maps[i + 1].Position;
                if (fixture.Session.MoveFormalDungeon((byte)target.X, (byte)target.Y) is null)
                    throw new InvalidOperationException("FAIL 结算商店夹具资源批准连接过房失败");
                loaded = fixture.Session.CompleteFormalDungeonLoading()
                    ?? throw new InvalidOperationException("FAIL 结算商店夹具过房加载失败");
            }
        }
        Check(routeValid && deathCount > 0 && clearFact is { } cleared && cleared.ConfirmedDeathEvents.Count == deathCount &&
            cleared.Identity.InstanceId == entry.Snapshot.Identity.InstanceId &&
            fixture.Count() == deathCount && fixture.Session.Phase == SessionPhase.Settlement &&
            fixture.Session.DungeonSnapshot?.Phase == SinglePlayerDungeonPhase.Settlement,
            "资源批准路线真实加载、blocking击杀与过房产生唯一已提交清图事实");
        byte[] report = [1, 2, 3, 4];
        var settled = fixture.Session.SettleFormalDungeon(fixture.Created.Id, 99, 99, report, dungeons, clear, growth);
        Check(settled.Outcome == DungeonRunOutcome.Accepted && settled.Receipt is not null &&
            fixture.Session.CanPublishFormalDungeon(settled.Receipt) && fixture.Session.FormalDungeonResultFocus() is not null &&
            ShopSame(settled.Receipt.Character, fixture.Read()) && ShopSame(fixture.Read(), fixture.Session.Selected!),
            "结算成功后才取得已提交角色和结果焦点，不伪造Phase或ClearFact");

        // 取基线前核对普通通关账本保存本次真实身份、死亡集合和报告；后续拒绝只比较该基线。
        var completedClear = clearFact ?? throw new InvalidOperationException("FAIL 结算商店夹具缺少真实清图事实");
        var identity = completedClear.Identity;
        string deathsHex = Convert.ToHexString(completedClear.ConfirmedDeathEvents.SelectMany(value => value.ToByteArray()).ToArray());
        long settlementRows = Convert.ToInt64(fixture.Query("SELECT count(*) FROM character_dungeon_settlement_receipts"));
        long matchingSettlementRows = Convert.ToInt64(fixture.Query($"SELECT count(*) FROM character_dungeon_settlement_receipts WHERE character_id={fixture.Created.Id} AND instance_id='{identity.InstanceId:N}' AND quest_id={identity.QuestId} AND task_attempt={identity.TaskAttempt} AND epoch={identity.Epoch} AND dungeon_id=3 AND map_id={completedClear.ClearMap!.Id} AND generation={fixture.Owner.Generation} AND death_events=X'{deathsHex}' AND report_body=X'{Convert.ToHexString(report)}'"));
        var baseline = fixture.Read();
        var selectedBaseline = fixture.Session.Selected!;
        string sqlBaseline = SqlSnapshot(fixture);
        Check(settlementRows == 1 && matchingSettlementRows == 1 && fixture.Session.Phase == SessionPhase.Settlement &&
            ShopSame(baseline, selectedBaseline) && CountItem(baseline, material) == 500 && CubeBalance(baseline) == 41 &&
            CountItem(baseline, otherItem) == 0 && rows.All(row => CountItem(baseline, row.Item) == 0 &&
                !baseline.Inventory.ShopPurchases.Any(value => value.ItemId == row.Item)),
            "真实结算receipt和死亡集合持久化后冻结首次兑换及资产基线");
        Console.WriteLine(JsonSerializer.Serialize(new { kind = "settlement-entry", dungeonId = 3, questId = 3145,
            phase = fixture.Session.Phase.ToString(), mapCount = maps.Count, deathCount, settlementRows, matchingSettlementRows,
            characterMatchesReload = ShopSame(baseline, selectedBaseline), materialBalance = CountItem(baseline, material), cubeBalance = CubeBalance(baseline) }));

        foreach (var row in rows)
        {
            var request = new DfoServer.Game.Inventory.Models.BuyItemRequest(row.Item, 1, targetShop, nolan);
            var legal = shop.Buy(baseline, request, out var legalReceipt, now);
            long legalDebit = legal.Plan is { } plan ? CountItem(baseline, material) - CountItem(baseline with { Inventory = plan.After }, material) : -1;
            long legalReward = legal.Plan is { } rewardPlan ? CountItem(baseline with { Inventory = rewardPlan.After }, row.Item) - CountItem(baseline, row.Item) : -1;
            bool quotaPlanValid = legal.Plan is { } quotaPlan && (row.Limit is null ?
                !quotaPlan.After.ShopPurchases.Any(value => value.ItemId == row.Item) :
                quotaPlan.After.ShopPurchases.SingleOrDefault(value => value.ItemId == row.Item) == new ShopPurchaseCount(nolan, row.Item, true, false, 0, 1));
            bool purePlanUncommitted = ReferenceEquals(selectedBaseline, fixture.Session.Selected) &&
                ShopSame(baseline, fixture.Read()) && ShopSame(baseline, fixture.Session.Selected!) && SqlSnapshot(fixture) == sqlBaseline;
            Check(legal.Accepted && legalReceipt is { Count: 1, Core: { } core } && core.ItemId == row.Item &&
                legalDebit == row.Cost && legalReward == 1 && quotaPlanValid && purePlanUncommitted,
                "Settlement目标负例先证明纯规划精确成本、单件和首次限额合法且未提交 " + row.Item);
            int planned = 0;
            var result = fixture.Session.PlanAndCommitShopBuy(targetShop, character =>
            {
                planned++;
                return shop.Buy(character, request, out _, now);
            });
            bool characterUnchanged = ReferenceEquals(selectedBaseline, fixture.Session.Selected) &&
                ShopSame(baseline, fixture.Read()) && ShopSame(baseline, fixture.Session.Selected!);
            bool sqlUnchanged = SqlSnapshot(fixture) == sqlBaseline;
            bool rejected = result is { Accepted: false, Error: 17, Receipt: null } && planned == 0 && characterUnchanged && sqlUnchanged;
            Check(rejected, "Settlement目标wrapper在capture拒绝17、零规划且完整角色和全表SQL不变 " + row.Item);
            Console.WriteLine(JsonSerializer.Serialize(new { kind = "settlement-target", itemId = row.Item, legalPlan = legal.Accepted,
                legalDebit, legalReward, quotaPlanValid, purePlanUncommitted, planned, accepted = result.Accepted, error = result.Error,
                characterUnchanged, sqlUnchanged, rejected }));
        }

        // 精确复用既有CMD21商品合同；此处验证Session提交，不把它声称为wire或ACK证据。
        var template = itemResources.Items.Get(otherItem);
        Check(template.ShopPrice.Gold == 0 && template.ShopPrice.Materials is [{ ItemId: cube, Count: 20 }] &&
            DfoServer.Game.Currency.CurrencyWallet.RewardKind(cube) == DfoServer.Game.Currency.CurrencyKind.ClearCube,
            "非目标对照使用1000商店2600655的真实20无色价格和账号钱包币种");
        var otherRequest = new DfoServer.Game.Inventory.Models.BuyItemRequest(otherItem, 1, otherShop, otherNpc);
        var otherLegal = shop.Buy(baseline, otherRequest, out var otherLegalReceipt, now);
        Check(otherLegal.Accepted && otherLegal.Plan is { } otherPlan && otherLegalReceipt is { Count: 1, Core: { ItemId: otherItem } } &&
            CubeBalance(baseline with { Inventory = otherPlan.After }) == 21 &&
            CountItem(baseline with { Inventory = otherPlan.After }, otherItem) == 1 &&
            ReferenceEquals(selectedBaseline, fixture.Session.Selected) && ShopSame(baseline, fixture.Read()) && SqlSnapshot(fixture) == sqlBaseline,
            "非目标纯规划合法且仍未提交，防止错误测试输入掩盖全局收紧");
        int otherPlanned = 0;
        ShopPurchaseReceipt? otherReceipt = null;
        var bought = fixture.Session.PlanAndCommitShopBuy(otherShop, character =>
        {
            otherPlanned++;
            return shop.Buy(character, otherRequest, out otherReceipt, now);
        });
        var after = fixture.Read();
        var granted = after.Inventory.Items.SingleOrDefault(item => item.Core.ItemId == otherItem);
        bool committed = bought.Accepted && otherPlanned == 1 && otherReceipt is { Count: 1, Core: { } boughtCore } &&
            granted is { Uid: > 0 } && granted.Core == boughtCore && granted.Core == otherLegalReceipt!.Core && granted.Position == otherReceipt.Position &&
            otherReceipt.Materials is [{ ItemId: cube, Remaining: 21 }] &&
            CubeBalance(after) == CubeBalance(baseline) - 20 && CountItem(after, otherItem) == CountItem(baseline, otherItem) + 1 &&
            after.Revision == baseline.Revision + 1 && after.Inventory.AccountWallet.Revision == baseline.Inventory.AccountWallet.Revision + 1 &&
            bought.Receipt is not null && fixture.Session.CanPublishInventory(bought.Receipt) &&
            ShopSame(after, bought.Receipt.Character) && ShopSame(after, fixture.Session.Selected!) && SqlSnapshot(fixture) != sqlBaseline;
        Check(committed, "Settlement非目标wrapper真实规划一次、扣20发1并发布持久化UID和完整提交回执");
        var expectedInventory = baseline.Inventory with
        {
            Items = baseline.Inventory.Items.Add(granted!).OrderBy(item => item.Position.List).ThenBy(item => item.Position.Index).ToImmutableArray(),
            AccountWallet = baseline.Inventory.AccountWallet.Set(DfoServer.Game.Currency.CurrencyKind.ClearCube, 21) with
                { Revision = baseline.Inventory.AccountWallet.Revision + 1 }
        };
        bool exactChangesOnly = ShopSame(baseline with { Revision = baseline.Revision + 1, Inventory = expectedInventory }, after);
        Check(exactChangesOnly, "合法非目标购买只增加实际UID单件并扣无色，八项材料、额度及完整其他角色字段不变");
        var cold = new CharacterRepository(fixture.Path, quests).LoadCharacter(fixture.Owner, fixture.Created.Slot);
        bool coldMatches = ShopSame(after, cold) && ShopSame(cold, fixture.Session.Selected!) &&
            bought.Receipt is not null && ShopSame(cold, bought.Receipt.Character);
        Check(coldMatches, "非目标提交在重开仓储完整读回一致，不把纯计划临时UID当成提交结果");
        Console.WriteLine(JsonSerializer.Serialize(new { kind = "settlement-other-shop", shopId = otherShop, npcId = otherNpc, itemId = otherItem,
            planned = otherPlanned, accepted = bought.Accepted, error = bought.Error, cubeDebit = CubeBalance(baseline) - CubeBalance(after),
            rewardIncrease = CountItem(after, otherItem) - CountItem(baseline, otherItem), grantedUid = granted?.Uid,
            committed, exactChangesOnly, coldMatches, assertions = TestCheck.Count - beforeChecks }));

        static long CountItem(Character character, int item) => character.Inventory.Items
            .Where(value => value.Core.ItemId == item).Sum(value => (long)value.Core.Count) +
            character.Inventory.AccountWallet.Material(item).Balance;
        static long CubeBalance(Character character) => DfoServer.Game.Currency.CurrencyWallet.Get(character.Inventory,
            DfoServer.Game.Currency.CurrencyKind.ClearCube);
        static Character Normalize(Character character) => character with
        {
            Inventory = character.Inventory with
            {
                ShopPurchases = character.Inventory.ShopPurchases.OrderBy(row => row.NpcId).ThenBy(row => row.ItemId)
                    .ThenByDescending(row => row.AccountWide).ThenBy(row => row.ResetDaily)
                    .ThenBy(row => row.Day).ThenBy(row => row.Count).ToImmutableArray()
            }
        };
        static bool ShopSame(Character first, Character second) =>
            JsonSerializer.Serialize(Normalize(first)) == JsonSerializer.Serialize(Normalize(second));

        // 比较当前夹具全部表及审计；仅限临时库，不写结算表或业务数据。
        static string SqlSnapshot(Fixture fixture)
        {
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = fixture.Path, Pooling = false, ForeignKeys = true }.ToString());
            db.Open();
            using var read = db.BeginTransaction();
            using var namesCommand = db.CreateCommand();
            namesCommand.Transaction = read;
            namesCommand.CommandText = "SELECT name FROM sqlite_schema WHERE type='table' ORDER BY name";
            var names = new List<string>();
            using (var reader = namesCommand.ExecuteReader())
                while (reader.Read()) names.Add(reader.GetString(0));
            var tables = new SortedDictionary<string, string[]>(StringComparer.Ordinal);
            foreach (string name in names)
            {
                using var command = db.CreateCommand();
                command.Transaction = read;
                command.CommandText = "SELECT * FROM \"" + name.Replace("\"", "\"\"") + "\"";
                using var reader = command.ExecuteReader();
                var values = new List<string>();
                while (reader.Read())
                    values.Add(JsonSerializer.Serialize(Enumerable.Range(0, reader.FieldCount)
                        .Select(index => reader.IsDBNull(index) ? null : reader.GetValue(index)).ToArray()));
                tables[name] = values.Order(StringComparer.Ordinal).ToArray();
            }
            return JsonSerializer.Serialize(tables);
        }
    }

    static Testing.Inventory.DailyBoosterTestClock ShopFixtureClock(ItemResources resources, IEnumerable<int> items)
    {
        var now = DateTimeOffset.UtcNow;
        long deadline = items.Select(id => resources.Items.Get(id).Expiration.AbsoluteTime)
            .Where(value => value > 0).DefaultIfEmpty(long.MaxValue).Min();
        // 准入用例统一准备可正常发放的材料与商品，不把历史活动日期混入场景资格回归。
        if (deadline <= now.ToUnixTimeSeconds()) now = DateTimeOffset.FromUnixTimeSeconds(deadline).AddHours(-1);
        return new(now);
    }

    internal static void VerifyShopRoomAdmission(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters, ItemResources itemResources)
    {
        const int targetShop = 400000097, nolan = 400001107, material = 490705400;
        (int Item, int Cost, int? Limit)[] rows =
        [
            (490705437, 4, 8), (490705421, 9, 2), (490705286, 4, 3), (490705424, 6, 1),
            (490705422, 4, 2), (490703887, 3, 1), (490700411, 3, 1), (490013680, 1, null)
        ];
        var learning = SelfTestCatalogs.SkillLearning(pvf);
        var quests = SelfTestCatalogs.Quests(pvf);
        var clock = ShopFixtureClock(itemResources, rows.Select(row => row.Item).Append(material));
        var now = clock.GetUtcNow();
        var shop = new InventoryShopService(itemResources.Items, itemResources.Shops, itemResources.GoldLimits, itemResources.EpicPieces, clock);
        bool allRoomAccepted = true;
        foreach (var row in rows)
        {
            using var room = new Fixture("ShopRoom" + Array.IndexOf(rows, row), towns, characters, learning, quests, timeProvider: clock);
            PrepareRoom(room);
            var before = room.Read();
            ShopPurchaseReceipt? receipt = null;
            var result = room.Session.PlanAndCommitShopBuy(targetShop,
                character => shop.Buy(character, new(row.Item, 1, targetShop, nolan), out receipt, now));
            var after = room.Read();
            bool quotaKeyValid = row.Limit is null ? after.Inventory.ShopPurchases.IsEmpty :
                after.Inventory.ShopPurchases is [{ NpcId: nolan, ItemId: var item, AccountWide: true, ResetDaily: false, Day: 0, Count: 1 }] && item == row.Item;
            bool purchaseValid = result.Accepted && receipt is { Count: 1, Core: { } granted } && granted.ItemId == row.Item &&
                CountItem(after, material) == CountItem(before, material) - row.Cost &&
                CountItem(after, row.Item) == CountItem(before, row.Item) + 1 && after.Inventory.Gold == before.Inventory.Gold &&
                after.Revision == before.Revision + 1 && quotaKeyValid && result.Receipt is not null &&
                Same(after, result.Receipt.Character) && Same(after, room.Session.Selected!);
            allRoomAccepted &= purchaseValid;
            Console.WriteLine(JsonSerializer.Serialize(new { kind = "room", itemId = row.Item,
                accepted = result.Accepted, error = result.Error, quotaKeyValid, purchaseValid }));
        }
        using var outside = new Fixture("ShopOutside", towns, characters, learning, quests, timeProvider: clock);
        PrepareRoom(outside);
        Check(outside.Session.LeaveRoom() && outside.Session.Travel(new(38, 2, 1085, 249, 5, 38, 0, 0)) &&
            outside.Session.Phase == SessionPhase.Town && !InRoom(outside), "外城负例真实出房并旅行到原38/2且仍为Ready Town");
        var nonTarget = outside.Session.PlanAndCommitShopBuy(2,
            character => shop.Buy(character, new(3175, 1, 2, 1), out _, now));
        bool nonTargetMatchesReload = Same(outside.Read(), outside.Session.Selected!);
        Console.WriteLine(JsonSerializer.Serialize(new { kind = "other-shop-outside", shopId = 2, itemId = 3175,
            accepted = nonTarget.Accepted, error = nonTarget.Error, characterMatchesReload = nonTargetMatchesReload }));
        Check(nonTarget.Accepted && nonTargetMatchesReload, "非目标shop2保留同一外城真实购买");
        bool allOutsideRejected = true;
        foreach (var row in rows)
        {
            var before = outside.Read();
            Check(!before.Inventory.ShopPurchases.Any(value => value.ItemId == row.Item) && CountItem(before, row.Item) == 0,
                "外城样本仍是该SKU首次购买 " + row.Item);
            var request = new DfoServer.Game.Inventory.Models.BuyItemRequest(row.Item, 1, targetShop, nolan);
            var legal = shop.Buy(before, request, out var legalReceipt, now);
            Check(legal.Accepted && legalReceipt is { Count: 1 }, "外城负例先证真实材料、商品和额度合法 " + row.Item);
            string sql = SqlSnapshot(outside);
            int planned = 0;
            var result = outside.Session.PlanAndCommitShopBuy(targetShop, character =>
            {
                planned++;
                return shop.Buy(character, request, out _, now);
            });
            var after = outside.Read();
            bool materialUnchanged = CountItem(before, material) == CountItem(after, material);
            bool inventoryUnchanged = JsonSerializer.Serialize(before.Inventory) == JsonSerializer.Serialize(after.Inventory);
            bool quotaUnchanged = before.Inventory.ShopPurchases.SequenceEqual(after.Inventory.ShopPurchases);
            bool characterUnchanged = Same(before, after) && Same(before, outside.Session.Selected!);
            bool sqlUnchanged = SqlSnapshot(outside) == sql;
            bool rejected = result is { Accepted: false, Error: 17 } && planned == 0 && materialUnchanged &&
                inventoryUnchanged && quotaUnchanged && characterUnchanged && sqlUnchanged;
            allOutsideRejected &= rejected;
            Console.WriteLine(JsonSerializer.Serialize(new { kind = "outside-town", itemId = row.Item, legalPlan = legal.Accepted,
                planned, accepted = result.Accepted, error = result.Error, materialUnchanged, inventoryUnchanged,
                quotaUnchanged, characterUnchanged, sqlUnchanged, rejected }));
        }
        using var leaving = new Fixture("ShopLeaving", towns, characters, learning, quests, timeProvider: clock);
        PrepareRoom(leaving);
        Character? transition = null;
        string? transitionSql = null;
        int transitionPlanned = 0;
        var changed = leaving.Session.PlanAndCommitShopBuy(targetShop, character =>
        {
            transitionPlanned++;
            var plan = shop.Buy(character, new(490705437, 1, targetShop, nolan), out _, now);
            Check(plan.Accepted, "出房回归先取得真实房间内合法购买计划");
            Check(leaving.Session.LeaveRoom() && leaving.Session.Phase == SessionPhase.Town && !InRoom(leaving),
                "锁外规划期间真实出房且仍在Ready Town");
            transition = leaving.Read();
            transitionSql = SqlSnapshot(leaving);
            return plan;
        });
        bool transitionCharacterUnchanged = transition is not null && Same(transition, leaving.Read()) && Same(transition, leaving.Session.Selected!);
        bool transitionSqlUnchanged = SqlSnapshot(leaving) == transitionSql;
        bool transitionRejected = changed is { Accepted: false, Error: 17 } && transitionPlanned == 1 &&
            transitionCharacterUnchanged && transitionSqlUnchanged;
        Console.WriteLine(JsonSerializer.Serialize(new { kind = "room-to-outside", itemId = 490705437,
            planned = transitionPlanned, accepted = changed.Accepted, error = changed.Error, phase = leaving.Session.Phase.ToString(),
            isRoom = InRoom(leaving), characterUnchanged = transitionCharacterUnchanged, sqlUnchanged = transitionSqlUnchanged,
            rejected = transitionRejected }));
        Check(allRoomAccepted, "八项真实房间内合法兑换均精确扣材发奖并提交完整永久限额键");
        Check(allOutsideRejected, "八项首次合法兑换在房间外Town均须capture拒绝17且零规划，材料库存配额完整角色与全SQL不变");
        Check(transitionRejected, "真实出房后拒绝旧购买计划，出房之外不扣材发奖或写限额审计");

        bool InRoom(Fixture fixture) => fixture.Session.Selected is { } character &&
            towns.IsRoom(character.Position, DfoServer.Game.Quests.QuestEligibility.Completed(character));
        void PrepareRoom(Fixture fixture)
        {
            fixture.Session.Select(fixture.Created.Slot);
            fixture.Session.MarkReady();
            Check(fixture.Session.Phase == SessionPhase.Town && InRoom(fixture), "目标商店夹具真实选角并Ready且IsRoom为真");
            InventoryResult? planned = null;
            var granted = fixture.Session.MutateInventory(character => planned = InventoryRewardGrantService.TryGrantBatch(character,
                [new(material, 500)], itemResources.Items, itemResources.GoldLimits, itemResources.EpicPieces, now));
            var template = itemResources.Items.Get(material);
            Check(granted.Accepted,
                $"房间夹具通过真实服务取得兑换材料：item={material}, error={granted.Error}, reason={granted.RejectReason}, " +
                $"planned={planned is not null}, planError={planned?.Error}, kind={template.Kind}, stack={template.StackLimit}, expiration={template.Expiration}");
            fixture.Session.SetCharacterGold(fixture.Created.Name, 1_000_000);
        }
        static long CountItem(Character character, int item) => character.Inventory.Items
            .Where(value => value.Core.ItemId == item).Sum(value => (long)value.Core.Count) +
            character.Inventory.AccountWallet.Material(item).Balance;
    }

    // 只比较本用例现有临时文件库，覆盖全部表的行及审计，不建立另一套持久模型。
    static string SqlSnapshot(Fixture fixture)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = fixture.Path, Pooling = false, ForeignKeys = true }.ToString());
        db.Open();
        using var read = db.BeginTransaction();
        using var namesCommand = db.CreateCommand();
        namesCommand.Transaction = read;
        namesCommand.CommandText = "SELECT name FROM sqlite_schema WHERE type='table' ORDER BY name";
        var names = new List<string>();
        using (var reader = namesCommand.ExecuteReader())
            while (reader.Read()) names.Add(reader.GetString(0));
        var tables = new SortedDictionary<string, string[]>(StringComparer.Ordinal);
        foreach (string name in names)
        {
            using var command = db.CreateCommand();
            command.Transaction = read;
            command.CommandText = "SELECT * FROM \"" + name.Replace("\"", "\"\"") + "\"";
            using var reader = command.ExecuteReader();
            var values = new List<string>();
            while (reader.Read())
                values.Add(JsonSerializer.Serialize(Enumerable.Range(0, reader.FieldCount)
                    .Select(index => reader.IsDBNull(index) ? null : reader.GetValue(index)).ToArray()));
            tables[name] = values.Order(StringComparer.Ordinal).ToArray();
        }
        return JsonSerializer.Serialize(tables);
    }

    internal static void VerifyShopSceneAdmission(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters, ItemResources itemResources)
    {
        const int targetShop = 400000097, nolan = 400001107, otherNolan = 400001112, material = 490705400;
        (int Item, int Cost, int? Limit)[] rows =
        [
            (490705437, 4, 8), (490705421, 9, 2), (490705286, 4, 3), (490705424, 6, 1),
            (490705422, 4, 2), (490703887, 3, 1), (490700411, 3, 1), (490013680, 1, null)
        ];
        var sortingWitness = new Character(1, 0, "ShopSort", 0, new(0, 0, 0, 0), null)
        {
            Inventory = InventorySnapshot.Empty with { ShopPurchases =
                [new(nolan, 490705437, true, false, 0, 1), new(nolan, 490705421, true, false, 0, 1)] }
        };
        var sortingRows = sortingWitness.Inventory.ShopPurchases;
        Character WithSortingRows(ImmutableArray<ShopPurchaseCount> values) => sortingWitness with
            { Inventory = sortingWitness.Inventory with { ShopPurchases = values } };
        Check(ShopSame(sortingWitness, WithSortingRows(sortingRows.Reverse().ToImmutableArray())) &&
            !ShopSame(sortingWitness, WithSortingRows(sortingRows.SetItem(0, sortingRows[0] with { Count = 2 }))) &&
            !ShopSame(sortingWitness, WithSortingRows(sortingRows.SetItem(0, sortingRows[0] with { Day = 1 }))) &&
            !ShopSame(sortingWitness, WithSortingRows(sortingRows.Add(sortingRows[0]))) &&
            !ShopSame(sortingWitness, sortingWitness with { Revision = 1 }) &&
            !ShopSame(sortingWitness, sortingWitness with { Inventory = sortingWitness.Inventory with { Gold = 1 } }),
            "限购行仅消除排列差异，Count、Day、重复行及完整角色其他字段仍严格比较");
        var learning = SelfTestCatalogs.SkillLearning(pvf);
        var quests = SelfTestCatalogs.Quests(pvf);
        var catalog = SelfTestCatalogs.Dungeons(pvf);
        var rooms = SelfTestCatalogs.Rooms(pvf);
        var clock = ShopFixtureClock(itemResources, rows.Select(row => row.Item).Append(material));
        var shop = new InventoryShopService(itemResources.Items, itemResources.Shops, itemResources.GoldLimits, itemResources.EpicPieces, clock);
        using (var town = new Fixture("ShopTown", towns, characters, learning, quests, timeProvider: clock))
        {
            Prepare(town, room: true);
            foreach (var row in rows)
            {
                var before = town.Read();
                ShopPurchaseReceipt? receipt = null;
                var result = town.Session.PlanAndCommitShopBuy(targetShop,
                    character => shop.Buy(character, new(row.Item, 1, targetShop, nolan), out receipt));
                var after = town.Read();
                bool characterMatchesReload = ShopSame(after, town.Session.Selected!);
                var counters = after.Inventory.ShopPurchases.Where(value => value.ItemId == row.Item).ToArray();
                bool quotaKeyValid = row.Limit is null ? counters.Length == 0 :
                    counters is [{ NpcId: nolan, AccountWide: true, ResetDaily: false, Count: 1 }];
                bool purchaseValid = result.Accepted && receipt is { Count: 1, Core: { } granted } && granted.ItemId == row.Item &&
                    CountItem(after, material) == CountItem(before, material) - row.Cost &&
                    CountItem(after, row.Item) == CountItem(before, row.Item) + 1 &&
                    characterMatchesReload && result.Receipt is not null;
                Console.WriteLine(JsonSerializer.Serialize(new { kind = "town", itemId = row.Item, accepted = result.Accepted,
                    error = result.Error, materialDebit = CountItem(before, material) - CountItem(after, material),
                    rewardIncrease = CountItem(after, row.Item) - CountItem(before, row.Item), characterMatchesReload, quotaKeyValid, purchaseValid }));
                Check(purchaseValid,
                    "Town首次真实购买提交完整单件和精确材料 " + row.Item);
                Check(quotaKeyValid,
                    "八行首次购买使用完整NPC账号永久限额键 " + row.Item);
            }
            foreach (var row in rows)
            {
                var before = town.Read();
                string sql = SqlSnapshot(town);
                var result = town.Session.PlanAndCommitShopBuy(targetShop,
                    character => shop.Buy(character, new(row.Item, 1, targetShop, otherNolan), out _));
                bool characterUnchanged = ShopSame(before, town.Read()) && ShopSame(before, town.Session.Selected!);
                bool sqlUnchanged = SqlSnapshot(town) == sql;
                Console.WriteLine(JsonSerializer.Serialize(new { kind = "npc112", itemId = row.Item, npcId = otherNolan,
                    accepted = result.Accepted, error = result.Error, characterUnchanged, sqlUnchanged }));
                Check(result is { Accepted: false, Error: 17 } && characterUnchanged && sqlUnchanged,
                    "完整NPC400001112仍拒绝且整角色与SQL不变 " + row.Item);
            }
            Character? transition = null;
            string? transitionSql = null;
            var changed = town.Session.PlanAndCommitShopBuy(targetShop, character =>
            {
                var plan = shop.Buy(character, new(490705437, 1, targetShop, nolan), out _);
                Check(plan.Accepted, "转场回归先取得真实可购买计划");
                Check(town.Session.LeaveRoom() && town.Session.Travel(new(38, 2, 1085, 249, 5, 38, 0, 0)),
                    "锁外规划期间真实出房并旅行到原选图入口");
                Check(town.Session.BeginDungeonSelection() is not null, "锁外规划期间通过真实入口转入MapSelect");
                transition = town.Read();
                transitionSql = SqlSnapshot(town);
                return plan;
            });
            bool transitionCharacterUnchanged = transition is not null && ShopSame(transition, town.Read()) && ShopSame(transition, town.Session.Selected!);
            bool transitionSqlUnchanged = SqlSnapshot(town) == transitionSql;
            Console.WriteLine(JsonSerializer.Serialize(new { kind = "town-to-mapselect", itemId = 490705437,
                accepted = changed.Accepted, error = changed.Error, phase = town.Session.Phase.ToString(),
                characterUnchanged = transitionCharacterUnchanged, sqlUnchanged = transitionSqlUnchanged }));
            Check(changed is { Accepted: false, Error: 17 } && transitionCharacterUnchanged && transitionSqlUnchanged,
                "提交前转场拒绝旧购买计划，转场之外不扣材发奖或写限额审计");
        }

        // 独立副本夹具从未买过八行，避免一次限额耗尽掩盖场景准入。
        using var dungeon = new Fixture("ShopDungeon", towns, characters, learning, quests, timeProvider: clock);
        Prepare(dungeon);
        Check(dungeon.Session.BeginDungeonSelection() is not null &&
            dungeon.Session.EnterFormalDungeon(3, 3145, 0, catalog, rooms) is not null &&
            dungeon.Session.CompleteFormalDungeonLoading() is not null && dungeon.Session.Phase == SessionPhase.Dungeon,
            "目标商店场景回归建立真实Ready正式副本");
        bool allRejected = true;
        foreach (var row in rows)
        {
            var before = dungeon.Read();
            Check(!before.Inventory.ShopPurchases.Any(value => value.ItemId == row.Item) && CountItem(before, row.Item) == 0,
                "副本样本仍是该SKU首次购买 " + row.Item);
            var request = new DfoServer.Game.Inventory.Models.BuyItemRequest(row.Item, 1, targetShop, nolan);
            var legal = shop.Buy(before, request, out var legalReceipt);
            Check(legal.Accepted && legalReceipt is { Count: 1 }, "副本负例的真实商品材料和额度规划合法 " + row.Item);
            string sql = SqlSnapshot(dungeon);
            int planned = 0;
            var result = dungeon.Session.PlanAndCommitShopBuy(targetShop, character =>
            {
                planned++;
                return shop.Buy(character, request, out _);
            });
            bool characterUnchanged = ShopSame(before, dungeon.Read()) && ShopSame(before, dungeon.Session.Selected!);
            bool sqlUnchanged = SqlSnapshot(dungeon) == sql;
            bool rejected = result is { Accepted: false, Error: 17 } && planned == 0 && characterUnchanged && sqlUnchanged;
            allRejected &= rejected;
            Console.WriteLine(JsonSerializer.Serialize(new { kind = "dungeon", itemId = row.Item, legalPlan = legal.Accepted,
                planned, accepted = result.Accepted, error = result.Error, characterUnchanged, sqlUnchanged, rejected }));
        }
        var nonTarget = dungeon.Session.PlanAndCommitShopBuy(2,
            character => shop.Buy(character, new(3175, 1, 2, 1), out _));
        bool nonTargetMatchesReload = ShopSame(dungeon.Read(), dungeon.Session.Selected!);
        Console.WriteLine(JsonSerializer.Serialize(new { kind = "other-shop", shopId = 2, itemId = 3175,
            accepted = nonTarget.Accepted, error = nonTarget.Error, characterMatchesReload = nonTargetMatchesReload }));
        Check(nonTarget.Accepted && nonTargetMatchesReload, "非目标shop2保留副本真实购买");
        Check(allRejected, "八项首次合法兑换在Ready副本均须capture拒绝17且零规划，完整角色与SQL不变");

        static Character Normalize(Character character) => character with
        {
            Inventory = character.Inventory with
            {
                ShopPurchases = character.Inventory.ShopPurchases.OrderBy(row => row.NpcId).ThenBy(row => row.ItemId)
                    .ThenByDescending(row => row.AccountWide).ThenBy(row => row.ResetDaily)
                    .ThenBy(row => row.Day).ThenBy(row => row.Count).ToImmutableArray()
            }
        };
        static bool ShopSame(Character first, Character second) =>
            JsonSerializer.Serialize(Normalize(first)) == JsonSerializer.Serialize(Normalize(second));

        void Prepare(Fixture fixture, bool room = false)
        {
            if (room)
            {
                fixture.Session.Select(fixture.Created.Slot);
                fixture.Session.MarkReady();
                Check(fixture.Session.Phase == SessionPhase.Town && towns.IsRoom(fixture.Session.Selected!.Position,
                    DfoServer.Game.Quests.QuestEligibility.Completed(fixture.Session.Selected!)), "目标商店合法夹具留在真实赛丽亚房间");
            }
            else fixture.SelectEntry();
            Check(fixture.Session.MutateInventory(character => InventoryRewardGrantService.TryGrantBatch(character,
                [new(material, 500)], itemResources.Items, itemResources.GoldLimits, itemResources.EpicPieces, clock.GetUtcNow())).Accepted,
                "场景夹具使用真实兑换材料实例");
            fixture.Session.SetCharacterGold(fixture.Created.Name, 1_000_000);
        }
        static long CountItem(Character character, int item) => character.Inventory.Items
            .Where(value => value.Core.ItemId == item).Sum(value => (long)value.Core.Count) +
            character.Inventory.AccountWallet.Material(item).Balance;

    }

    internal static void VerifyShopExchangeMatrix(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters, ItemResources itemResources)
    {
        const int targetShop = 400000097, nolan = 400001107, material = 490705400;
        (int Item, int Cost, int? Limit)[] rows =
        [
            (490705437, 4, 8), (490705421, 9, 2), (490705286, 4, 3), (490705424, 6, 1),
            (490705422, 4, 2), (490703887, 3, 1), (490700411, 3, 1), (490013680, 1, null)
        ];
        var learning = SelfTestCatalogs.SkillLearning(pvf);
        var quests = SelfTestCatalogs.Quests(pvf);
        var clock = ShopFixtureClock(itemResources, rows.Select(row => row.Item).Append(material));
        var now = clock.GetUtcNow();
        var shop = new InventoryShopService(itemResources.Items, itemResources.Shops, itemResources.GoldLimits, itemResources.EpicPieces, clock);
        foreach (var row in rows)
        {
            var template = itemResources.Items.Get(row.Item);
            var expectedKind = row.Item is 490705286 or 490700411 or 490013680 ? DfoServer.GameWorld.Items.ItemKind.Material : DfoServer.GameWorld.Items.ItemKind.Consumable;
            int expectedStack = row.Item == 490705286 ? 1000 : row.Item == 490700411 ? 1 : int.MaxValue;
            Check(template.Kind == expectedKind && template.StackLimit == expectedStack && template.ShopPrice.Gold == 0 &&
                template.ShopPrice.Materials is [{ ItemId: material, Count: var cost }] && cost == row.Cost &&
                (row.Limit is { } cap ? template.ShopPrice.Limit is { AccountWide: true, ResetDaily: false } limit && limit.Count == cap :
                    template.ShopPrice.Limit is null), "八项矩阵使用已保存定义的真实模板、材料、永久账号上限 " + row.Item);
        }
        Console.WriteLine("T03_MATRIX_FIXTURE_KINDS " + JsonSerializer.Serialize(new { materialId = material,
            materialKind = itemResources.Items.Get(material).Kind.ToString(), fillerId = 490705421,
            fillerKind = itemResources.Items.Get(490705421).Kind.ToString() }));
        Check(itemResources.Items.Get(material).Kind == DfoServer.GameWorld.Items.ItemKind.Material && itemResources.Items.Get(490705421).Kind == DfoServer.GameWorld.Items.ItemKind.Consumable,
            "容量夹具只用已证材料和消耗品分类");
        using (var caps = new Fixture("ShopCaps", towns, characters, learning, quests, timeProvider: clock))
        {
            Prepare(caps);
            foreach (var row in rows.Where(value => value.Limit is not null))
            {
                for (int i = 0; i < row.Limit!.Value; i++) BuyOne(caps, row);
                var before = caps.Read();
                string sql = SqlSnapshot(caps);
                var result = caps.Session.PlanAndCommitShopBuy(targetShop,
                    character => shop.Buy(character, new(row.Item, 1, targetShop, nolan), out _, now));
                bool unchanged = ShopSame(before, caps.Read()) && ShopSame(before, caps.Session.Selected!) && SqlSnapshot(caps) == sql;
                var quota = before.Inventory.ShopPurchases.Single(value => value.ItemId == row.Item);
                bool exactCap = quota == new ShopPurchaseCount(nolan, row.Item, true, false, 0, row.Limit.Value);
                Console.WriteLine(JsonSerializer.Serialize(new { kind = "matrix-cap", itemId = row.Item, limit = row.Limit,
                    accepted = result.Accepted, error = result.Error, exactCap, unchanged }));
                Check(result is { Accepted: false, Error: 0x85 } && exactCap && unchanged, "永久账号上限已足额且超购不扣材 " + row.Item);
            }
            Check(CountItem(caps.Read(), material) == 418 && caps.Read().Inventory.ShopPurchases.Length == 7 &&
                caps.Query("SELECT count(*) FROM shop_purchase_counts WHERE account='formal-session' AND character_id=0 AND npc_id=400001107 AND reset_daily=0 AND day=0") is 7L &&
                caps.Query("SELECT count(*) FROM shop_purchase_counts") is 7L,
                "七项十八次真实购买独立合计扣82材料且仅七个永久账号键");
            var sibling = caps.Repository.Create(caps.Owner,
                CharacterCreation.Prepare("CapSibling", characters.Get(0), towns.Room, towns.InitialReturn, learning.Get));
            caps.Session.ReturnToRoster(); caps.Session.Select(sibling.Slot); caps.Session.MarkReady();
            Check(caps.Session.Phase == SessionPhase.Town && towns.IsRoom(caps.Session.Selected!.Position,
                DfoServer.Game.Quests.QuestEligibility.Completed(caps.Session.Selected!)) &&
                caps.Session.MutateInventory(character => InventoryRewardGrantService.TryGrantBatch(character,
                    [new(material, 500)], itemResources.Items, itemResources.GoldLimits, itemResources.EpicPieces, now)).Accepted,
                "同库兄弟角色真实选角留在赛丽亚房间并备足兑换材料");
            caps.Session.SetCharacterGold(sibling.Name, 1_000_000);
            var reopened = new CharacterRepository(caps.Path, quests);
            var cold = reopened.LoadCharacter(caps.Owner, sibling.Slot);
            Check(ShopSame(cold, caps.Session.Selected!), "兄弟角色与重开仓储完整快照相等");
            foreach (var row in rows.Where(value => value.Limit is not null))
            {
                var before = caps.Repository.LoadCharacter(caps.Owner, sibling.Slot);
                string sql = SqlSnapshot(caps);
                var result = caps.Session.PlanAndCommitShopBuy(targetShop,
                    character => shop.Buy(character, new(row.Item, 1, targetShop, nolan), out _, now));
                var nextDay = shop.Buy(cold, new(row.Item, 1, targetShop, nolan), out _, now.AddDays(1));
                bool coldCap = cold.Inventory.ShopPurchases.Single(value => value.ItemId == row.Item) ==
                    new ShopPurchaseCount(nolan, row.Item, true, false, 0, row.Limit!.Value);
                bool unchanged = ShopSame(before, caps.Repository.LoadCharacter(caps.Owner, sibling.Slot)) && ShopSame(before, caps.Session.Selected!) && SqlSnapshot(caps) == sql;
                Console.WriteLine(JsonSerializer.Serialize(new { kind = "matrix-sibling-reopen", itemId = row.Item,
                    accepted = result.Accepted, error = result.Error, nextDayAccepted = nextDay.Accepted, nextDayError = nextDay.Error, coldCap, unchanged }));
                Check(result is { Accepted: false, Error: 0x85 } && nextDay is { Accepted: false, Error: 0x85 } && coldCap && unchanged,
                    "兄弟和冷读跨日仍共享永久账号已耗尽上限 " + row.Item);
            }
            BuyOne(caps, rows[7]); BuyOne(caps, rows[7]);
            var unlimited = caps.Repository.LoadCharacter(caps.Owner, sibling.Slot);
            bool noArtificialKey = !unlimited.Inventory.ShopPurchases.Any(value => value.ItemId == rows[7].Item) &&
                unlimited.Inventory.ShopPurchases.Length == 7 && CountItem(unlimited, rows[7].Item) == 2 && CountItem(unlimited, material) == 498 &&
                caps.Query("SELECT count(*) FROM shop_purchase_counts WHERE item_id=490013680") is 0L;
            Console.WriteLine(JsonSerializer.Serialize(new { kind = "matrix-unlimited", itemId = rows[7].Item, purchases = 2, noArtificialKey }));
            Check(noArtificialKey && ShopSame(unlimited, reopened.LoadCharacter(caps.Owner, sibling.Slot)),
                "第八项两次兑换不制造限购键且完整重开一致");
        }
        foreach (var test in new[]
        {
            (Name: "CapConsFull", Row: rows[0], Merge: false, FreeCost: false, Accepted: false),
            (Name: "CapConsMerge", Row: rows[0], Merge: true, FreeCost: false, Accepted: true),
            (Name: "CapMatFull", Row: rows[2], Merge: false, FreeCost: false, Accepted: false),
            (Name: "CapMatFree", Row: rows[2], Merge: false, FreeCost: true, Accepted: true),
            (Name: "CapSingle", Row: rows[6], Merge: false, FreeCost: false, Accepted: false)
        })
        {
            var seed = CapacitySeed(test.Name, test.Row, test.Merge, test.FreeCost);
            using var full = new Fixture(test.Name, towns, characters, learning, quests, prepared: seed, timeProvider: clock);
            SelectRoom(full);
            var before = full.Read(); string sql = SqlSnapshot(full);
            var template = itemResources.Items.Get(test.Row.Item);
            var range = ItemSlotBoundService.Range(template.Kind, before.Inventory);
            Check(before.Inventory.Items.Count(value => value.Position.List == InventoryListType.Main &&
                value.Position.Index >= range.Start && value.Position.Index <= range.End) == 56,
                "真实奖励目标页确实全满且不占用快捷栏 " + test.Name);
            var result = test.Accepted ? BuyOne(full, test.Row) : full.Session.PlanAndCommitShopBuy(targetShop,
                character => shop.Buy(character, new(test.Row.Item, 1, targetShop, nolan), out _, now));
            bool unchanged = ShopSame(before, full.Read()) && ShopSame(before, full.Session.Selected!) && SqlSnapshot(full) == sql;
            if (!test.Accepted)
                Check(result is { Accepted: false, Error: 4 } && unchanged, "目标满页拒绝保持完整角色、配额和全SQL " + test.Name);
            bool placement = !test.Accepted || (test.Merge ?
                full.Read().Inventory.Find(new(InventoryListType.Main, 65)) is { Core.ItemId: 490705437, Core.Count: 2 } :
                CountItem(full.Read(), material) == 0 && full.Read().Inventory.Find(new(InventoryListType.Main, 121)) is { Core.ItemId: 490705286, Core.Count: 1 });
            Check(placement, "成功使用兼容原栈或成本实际释放槽 " + test.Name);
            Console.WriteLine(JsonSerializer.Serialize(new { kind = "matrix-capacity", name = test.Name, itemId = test.Row.Item,
                accepted = result.Accepted, error = result.Error, unchanged, placement }));
        }
        using (var late = new Fixture("ShopLate", towns, characters, learning, quests, timeProvider: clock))
        {
            Prepare(late);
            var request = new DfoServer.Game.Inventory.Models.BuyItemRequest(490705424, 1, targetShop, nolan);
            Check(shop.Buy(late.Read(), request, out _, now).Accepted, "晚SQL夹具先证真实商品和材料合法且未消耗配额");
            late.Sql("CREATE TRIGGER t03_exact_quota AFTER INSERT ON shop_purchase_counts WHEN NEW.account='formal-session' AND NEW.character_id=0 AND NEW.npc_id=400001107 AND NEW.item_id=490705424 AND NEW.reset_daily=0 AND NEW.day=0 AND NEW.count=1 BEGIN SELECT RAISE(ABORT,'t03 exact quota late'); END");
            var before = late.Read(); string sql = SqlSnapshot(late); bool failed = false;
            try { late.Session.PlanAndCommitShopBuy(targetShop, character => shop.Buy(character, request, out _, now)); }
            catch (SqliteException error) when (error.SqliteErrorCode == 19 && error.Message.Contains("t03 exact quota late", StringComparison.Ordinal)) { failed = true; }
            bool unchanged = ShopSame(before, late.Read()) && ShopSame(before, late.Session.Selected!) && SqlSnapshot(late) == sql;
            Console.WriteLine(JsonSerializer.Serialize(new { kind = "matrix-late-rollback", itemId = request.ItemId, failed, unchanged }));
            Check(failed && unchanged, "精确配额AFTER INSERT故障完整回滚UID、审计、revision、成本、奖励和配额");
            late.Sql("DROP TRIGGER t03_exact_quota");
            BuyOne(late, rows[3], request);
            var after = late.Read();
            bool once = CountItem(after, material) == 494 && CountItem(after, request.ItemId) == 1 &&
                after.Inventory.ShopPurchases is [{ NpcId: nolan, ItemId: 490705424, AccountWide: true, ResetDaily: false, Day: 0, Count: 1 }] &&
                late.Query("SELECT count(*) FROM shop_purchase_counts WHERE account='formal-session' AND character_id=0 AND npc_id=400001107 AND item_id=490705424 AND reset_daily=0 AND day=0 AND count=1") is 1L &&
                late.Query("SELECT count(*) FROM shop_purchase_counts") is 1L &&
                ShopSame(after, new CharacterRepository(late.Path, quests).LoadCharacter(late.Owner, late.Created.Slot));
            Console.WriteLine(JsonSerializer.Serialize(new { kind = "matrix-late-retry", itemId = request.ItemId, sameRequest = true, once }));
            Check(once, "完全同请求重试只扣6材、发1件、累计1次且完整重开相等");
        }

        void Prepare(Fixture fixture)
        {
            SelectRoom(fixture);
            Check(fixture.Session.MutateInventory(character => InventoryRewardGrantService.TryGrantBatch(character,
                [new(material, 500)], itemResources.Items, itemResources.GoldLimits, itemResources.EpicPieces, now)).Accepted,
                "矩阵夹具通过真实服务取得兑换材料");
            fixture.Session.SetCharacterGold(fixture.Created.Name, 1_000_000);
        }
        void SelectRoom(Fixture fixture)
        {
            fixture.Session.Select(fixture.Created.Slot);
            fixture.Session.MarkReady();
            Check(fixture.Session.Phase == SessionPhase.Town && towns.IsRoom(fixture.Session.Selected!.Position,
                DfoServer.Game.Quests.QuestEligibility.Completed(fixture.Session.Selected!)), "目标商店矩阵夹具留在真实赛丽亚房间");
        }
        InventoryResult BuyOne(Fixture fixture, (int Item, int Cost, int? Limit) row, DfoServer.Game.Inventory.Models.BuyItemRequest? sameRequest = null)
        {
            var before = fixture.Session.Selected!;
            ShopPurchaseReceipt? receipt = null;
            var request = sameRequest ?? new DfoServer.Game.Inventory.Models.BuyItemRequest(row.Item, 1, targetShop, nolan);
            var result = fixture.Session.PlanAndCommitShopBuy(targetShop, character => shop.Buy(character, request, out receipt, now));
            var after = fixture.Repository.LoadCharacter(fixture.Owner, before.Slot);
            int previous = before.Inventory.ShopPurchases.SingleOrDefault(value => value.ItemId == row.Item)?.Count ?? 0;
            bool quota = row.Limit is null ? !after.Inventory.ShopPurchases.Any(value => value.ItemId == row.Item) :
                after.Inventory.ShopPurchases.SingleOrDefault(value => value.ItemId == row.Item) == new ShopPurchaseCount(nolan, row.Item, true, false, 0, previous + 1);
            Check(result.Accepted && receipt is { Count: 1, Core: { } core } && core.ItemId == row.Item &&
                CountItem(after, material) == CountItem(before, material) - row.Cost && CountItem(after, row.Item) == CountItem(before, row.Item) + 1 &&
                after.Inventory.Gold == before.Inventory.Gold && after.Revision == before.Revision + 1 && quota &&
                result.Receipt is not null && ShopSame(after, result.Receipt.Character) && ShopSame(after, fixture.Session.Selected!),
                "真实购买精确扣材发奖、永久键、revision及完整已发布读回 " + row.Item);
            return result;
        }
        NewCharacter CapacitySeed(string name, (int Item, int Cost, int? Limit) row, bool merge, bool freeCost)
        {
            var seed = CharacterCreation.Prepare(name, characters.Get(0), towns.Room, towns.InitialReturn, learning.Get);
            var template = itemResources.Items.Get(row.Item);
            var filler = itemResources.Items.Get(template.Kind == DfoServer.GameWorld.Items.ItemKind.Consumable ? 490705421 : row.Item == 490700411 ? 490705286 : 490700411);
            var range = ItemSlotBoundService.Range(template.Kind, seed.Inventory);
            var items = seed.Inventory.Items.ToBuilder();
            for (short slot = range.Start; slot <= range.End; slot++)
            {
                var item = merge && slot == range.Start ? template : filler;
                items.Add(InventoryRewardGrantService.CreateItem(item, new(InventoryListType.Main, slot), InventoryRewardGrantService.Create(item, 1, now)));
            }
            var materialTemplate = itemResources.Items.Get(material);
            var costSlot = new InventorySlot(InventoryListType.Main, 121);
            var cost = InventoryRewardGrantService.CreateItem(materialTemplate, costSlot,
                InventoryRewardGrantService.Create(materialTemplate, freeCost ? row.Cost : row.Cost + 1, now));
            int index = -1;
            for (int i = 0; i < items.Count; i++) if (items[i].Position == costSlot) { index = i; break; }
            if (index < 0) items.Add(cost); else items[index] = cost;
            return seed with { Inventory = seed.Inventory with { Gold = 1_000_000, Items = items.ToImmutable() } };
        }
        static Character Normalize(Character character) => character with
        {
            Inventory = character.Inventory with
            {
                ShopPurchases = character.Inventory.ShopPurchases.OrderBy(row => row.NpcId).ThenBy(row => row.ItemId)
                    .ThenByDescending(row => row.AccountWide).ThenBy(row => row.ResetDaily)
                    .ThenBy(row => row.Day).ThenBy(row => row.Count).ToImmutableArray()
            }
        };
        static bool ShopSame(Character first, Character second) =>
            JsonSerializer.Serialize(Normalize(first)) == JsonSerializer.Serialize(Normalize(second));

        static long CountItem(Character character, int item) => character.Inventory.Items
            .Where(value => value.Core.ItemId == item).Sum(value => (long)value.Core.Count) +
            character.Inventory.AccountWallet.Material(item).Balance;

        // 只比较本用例现有临时文件库，覆盖全部表的行及审计，不建立另一套持久模型。
        static string SqlSnapshot(Fixture fixture)
        {
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = fixture.Path, Pooling = false, ForeignKeys = true }.ToString());
            db.Open();
            using var read = db.BeginTransaction();
            using var namesCommand = db.CreateCommand();
            namesCommand.Transaction = read;
            namesCommand.CommandText = "SELECT name FROM sqlite_schema WHERE type='table' ORDER BY name";
            var names = new List<string>();
            using (var reader = namesCommand.ExecuteReader())
                while (reader.Read()) names.Add(reader.GetString(0));
            var tables = new SortedDictionary<string, string[]>(StringComparer.Ordinal);
            foreach (string name in names)
            {
                using var command = db.CreateCommand();
                command.Transaction = read;
                command.CommandText = "SELECT * FROM \"" + name.Replace("\"", "\"\"") + "\"";
                using var reader = command.ExecuteReader();
                var values = new List<string>();
                while (reader.Read())
                    values.Add(JsonSerializer.Serialize(Enumerable.Range(0, reader.FieldCount)
                        .Select(index => reader.IsDBNull(index) ? null : reader.GetValue(index)).ToArray()));
                tables[name] = values.Order(StringComparer.Ordinal).ToArray();
            }
            return JsonSerializer.Serialize(tables);
        }
    }

    internal static void VerifyShopBatchAndIdentityAdmission(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters, ItemResources itemResources)
    {
        int beforeChecks = TestCheck.Count;
        const int targetShop = 400000097, nolan = 400001107, material = 490705400;
        (int Item, int Cost, int? Limit)[] rows =
        [
            (490705437, 4, 8), (490705421, 9, 2), (490705286, 4, 3), (490705424, 6, 1),
            (490705422, 4, 2), (490703887, 3, 1), (490700411, 3, 1), (490013680, 1, null)
        ];
        var learning = SelfTestCatalogs.SkillLearning(pvf);
        var quests = SelfTestCatalogs.Quests(pvf);
        var clock = ShopFixtureClock(itemResources, rows.Select(row => row.Item).Append(material));
        var now = clock.GetUtcNow();
        var shop = new InventoryShopService(itemResources.Items, itemResources.Shops, itemResources.GoldLimits, itemResources.EpicPieces, clock);
        Check(rows.All(row => itemResources.Items.Get(row.Item).ShopPrice.Materials is [{ ItemId: material, Count: var cost }] && cost == row.Cost &&
            (row.Limit is { } cap ? itemResources.Items.Get(row.Item).ShopPrice.Limit is { AccountWide: true, ResetDaily: false } limit && limit.Count == cap :
                itemResources.Items.Get(row.Item).ShopPrice.Limit is null)), "批量夹具仍绑定八项真实成本和七项永久账号上限，第八项无限购");

        int sample = 0;
        foreach (var row in rows.Where(row => row.Limit is not null))
        foreach (int delta in new[] { -1, 0, 1 })
        {
            int count = row.Limit!.Value + delta;
            using var f = Prepare("Batch" + sample++);
            var before = f.Read();
            string sql = SqlSnapshot(f);
            Check(before.Inventory.ShopPurchases.IsEmpty && CountItem(before, row.Item) == 0,
                "批量边界初始已购量为零，Count由真实cap计算 " + row.Item + "/" + count);
            ShopPurchaseReceipt? receipt = null;
            var result = f.Session.PlanAndCommitShopBuy(targetShop,
                character => shop.Buy(character, new(row.Item, count, targetShop, nolan), out receipt, now));
            bool accepted = count > 0 && count <= row.Limit.Value;
            if (accepted) Committed(f, before, result, receipt, row, count);
            else Rejected(f, before, sql, result, 0x85, "批量越界不扣材、不发奖、不占额度 " + row.Item + "/" + count);
            Console.WriteLine(JsonSerializer.Serialize(new { kind = "batch-cap", itemId = row.Item, initialPurchased = 0,
                cap = row.Limit, count, delta, expectedAccepted = accepted, result.Accepted, result.Error }));
        }

        using (var unlimited = Prepare("BatchNoCap"))
        {
            // 9已超过其余商品的最大cap；013680不借用这些上限，也不为它制造cap±1。
            var before = unlimited.Read();
            ShopPurchaseReceipt? receipt = null;
            var result = unlimited.Session.PlanAndCommitShopBuy(targetShop,
                character => shop.Buy(character, new(rows[7].Item, 9, targetShop, nolan), out receipt, now));
            Committed(unlimited, before, result, receipt, rows[7], 9);
            Check(unlimited.Query("SELECT count(*) FROM shop_purchase_counts") is 0L,
                "无限购商品单请求九件不制造任何限购键");
            Console.WriteLine(JsonSerializer.Serialize(new { kind = "batch-unlimited", itemId = rows[7].Item, count = 9, quotaRows = 0 }));
        }

        // 共同扣材分支只用一种真实SKU；不足、锁定及过期均保留原实例与全库基线。
        foreach (var test in new[]
        {
            (Name: "SplitCost", Second: 6, Locked: false, Expiring: false, Accepted: true),
            (Name: "ShortCost", Second: 4, Locked: false, Expiring: false, Accepted: false),
            (Name: "LockedCost", Second: 6, Locked: true, Expiring: false, Accepted: false),
            (Name: "ExpiredCost", Second: 6, Locked: false, Expiring: true, Accepted: false)
        })
        {
            int expires = test.Expiring ? checked((int)now.AddDays(1).ToUnixTimeSeconds()) : 0;
            using var f = Prepare(test.Name, Seed(test.Name, 3, test.Second, test.Locked, expires));
            var before = f.Read();
            var first = before.Inventory.Find(new(InventoryListType.Main, 121))!;
            var second = before.Inventory.Find(new(InventoryListType.Main, 122))!;
            var purchaseTime = test.Expiring ? now.AddDays(2) : now;
            Check(first is { Uid: > 0, Core.Count: 3 } && second.Uid > 0 && second.Uid != first.Uid &&
                second.Core.Count == test.Second && second.Core.EquipmentLockId == (test.Locked ? 1 : 0) &&
                second.Core.ExpireTime == expires && !second.Core.IsExpired(now.ToUnixTimeSeconds()) &&
                second.Core.IsExpired(purchaseTime.ToUnixTimeSeconds()) == test.Expiring,
                "扣材夹具使用两个已持久化真实材料槽且锁定/到期时点明确 " + test.Name);
            string sql = SqlSnapshot(f);
            ShopPurchaseReceipt? receipt = null;
            var result = f.Session.PlanAndCommitShopBuy(targetShop,
                character => shop.Buy(character, new(rows[0].Item, 2, targetShop, nolan), out receipt, purchaseTime));
            if (test.Accepted)
            {
                var after = Committed(f, before, result, receipt, rows[0], 2);
                Check(after.Inventory.Find(first.Position) is null && after.Inventory.Find(second.Position) is { } rest &&
                    rest.Uid == second.Uid && rest.Core == (second.Core with { Value = 1 }) &&
                    receipt!.Materials is [{ ItemId: material, Remaining: 0 }],
                    "单请求跨两槽精确扣八材，首槽删除、后槽保留原UID及一件，ACK材料基数对应首旧槽");
            }
            else Rejected(f, before, sql, result, 10, "可用材料不足不消费其他合格槽或占用额度 " + test.Name);
            Console.WriteLine(JsonSerializer.Serialize(new { kind = "batch-material", test.Name, firstCount = 3, secondCount = test.Second,
                test.Locked, test.Expiring, count = 2, required = 8, result.Accepted, result.Error }));
        }

        using (var day = Prepare("BatchNextDay"))
        {
            var before = day.Read();
            ShopPurchaseReceipt? receipt = null;
            var first = day.Session.PlanAndCommitShopBuy(targetShop,
                character => shop.Buy(character, new(rows[0].Item, 7, targetShop, nolan), out receipt, now));
            var seven = Committed(day, before, first, receipt, rows[0], 7);
            string sql = SqlSnapshot(day);
            var over = day.Session.PlanAndCommitShopBuy(targetShop,
                character => shop.Buy(character, new(rows[0].Item, 2, targetShop, nolan), out _, now));
            Rejected(day, seven, sql, over, 0x85, "已购七件后单请求两件超永久八件上限且整批不扣材");
            receipt = null;
            var next = day.Session.PlanAndCommitShopBuy(targetShop,
                character => shop.Buy(character, new(rows[0].Item, 1, targetShop, nolan), out receipt, now.AddDays(1)));
            var eight = Committed(day, seven, next, receipt, rows[0], 1);
            sql = SqlSnapshot(day);
            var exhausted = day.Session.PlanAndCommitShopBuy(targetShop,
                character => shop.Buy(character, new(rows[0].Item, 1, targetShop, nolan), out _, now.AddDays(1)));
            Rejected(day, eight, sql, exhausted, 0x85, "次日实际提交最后一件后永久额度仍耗尽，不只验证纯规划");
            Console.WriteLine(JsonSerializer.Serialize(new { kind = "batch-next-day", initialPurchased = 7, count = 1,
                committedPurchased = 8, persistentDay = 0, accepted = next.Accepted, exhausted.Error }));
        }

        using (var epoch = Prepare("BatchEpoch"))
        {
            Character? transition = null;
            string? transitionSql = null;
            int planned = 0;
            var result = epoch.Session.PlanAndCommitShopBuy(targetShop, character =>
            {
                planned++;
                var plan = shop.Buy(character, new(rows[0].Item, 2, targetShop, nolan), out _, now);
                Check(plan.Accepted, "旧epoch负例先取得目标商店合法批量计划");
                epoch.Session.ReturnToRoster(); epoch.Session.Select(epoch.Created.Slot); epoch.Session.MarkReady();
                transition = epoch.Read(); transitionSql = SqlSnapshot(epoch);
                return plan;
            });
            Check(result is { Accepted: false, Error: 17, Receipt: null } && planned == 1 && transition is not null &&
                Same(transition, epoch.Read()) && Same(transition, epoch.Session.Selected!) && SqlSnapshot(epoch) == transitionSql,
                "重选同一角色的新epoch拒绝旧目标批量计划，过渡之外的资产、额度和全表均不变");
            Console.WriteLine(JsonSerializer.Serialize(new { kind = "batch-old-epoch", planned, result.Accepted, result.Error }));
        }

        using (var replaced = Prepare("BatchFresh"))
        {
            var stale = replaced.Session.Selected!;
            Character? winner = null;
            string? winnerSql = null;
            int planned = 0;
            var result = replaced.Session.PlanAndCommitShopBuy(targetShop, character =>
            {
                planned++;
                var plan = shop.Buy(character, new(rows[0].Item, 2, targetShop, nolan), out _, now);
                Check(plan.Accepted && replaced.Session.MutateInventory(current => InventoryRewardGrantService.TryGrantBatch(current,
                    [new(material, 1)], itemResources.Items, itemResources.GoldLimits, itemResources.EpicPieces, now)).Accepted,
                    "同Session旧计划负例先合法规划，再真实提交一件材料替换Selected");
                winner = replaced.Session.Selected!;
                winnerSql = SqlSnapshot(replaced);
                return plan;
            });
            Check(result is { Accepted: false, Error: 17, Plan: null, Receipt: null } && planned == 1 &&
                winner is not null && !ReferenceEquals(stale, winner) && ReferenceEquals(winner, replaced.Session.Selected) &&
                winner.Revision == stale.Revision + 1 && CountItem(winner, material) == CountItem(stale, material) + 1 &&
                CountItem(winner, rows[0].Item) == 0 && winner.Inventory.ShopPurchases.IsEmpty &&
                Same(winner, replaced.Read()) && SqlSnapshot(replaced) == winnerSql,
                "同Session新Selected使旧目标计划在提交复核拒绝17，不重规划且winner完整角色、成本、配额和全SQL不变");
            Console.WriteLine(JsonSerializer.Serialize(new { kind = "batch-replaced-selected", planned, result.Accepted, result.Error,
                oldRevision = stale.Revision, winnerRevision = winner!.Revision }));
        }

        using (var revision = Prepare("BatchRev"))
        {
            var stale = revision.Session.Selected!;
            var other = new GameSession(revision.Repository, towns, revision.Owner, questCatalog: quests, timeProvider: clock);
            other.Select(stale.Slot); other.MarkReady();
            Character? winner = null;
            string? winnerSql = null;
            bool conflicted = false;
            try
            {
                revision.Session.PlanAndCommitShopBuy(targetShop, character =>
                {
                    var plan = shop.Buy(character, new(rows[0].Item, 2, targetShop, nolan), out _, now);
                    Check(plan.Accepted && other.MutateInventory(current => InventoryRewardGrantService.TryGrantBatch(current,
                        [new(material, 1)], itemResources.Items, itemResources.GoldLimits, itemResources.EpicPieces, now)).Accepted,
                        "旧revision负例用另一正式Session真实提交一件材料，保留原计划角色引用");
                    winner = revision.Read(); winnerSql = SqlSnapshot(revision);
                    return plan;
                });
            }
            catch (InvalidOperationException error) when (error.Message.Contains("owner/revision", StringComparison.Ordinal)) { conflicted = true; }
            Check(conflicted && winner is not null && winner.Revision == stale.Revision + 1 &&
                CountItem(winner, material) == CountItem(stale, material) + 1 && winner.Inventory.ShopPurchases.IsEmpty &&
                ReferenceEquals(stale, revision.Session.Selected) && Same(winner, revision.Read()) && SqlSnapshot(revision) == winnerSql,
                "目标wrapper的旧revision在真实仓储CAS拒绝，只保留另一Session已提交的材料和审计");
            Console.WriteLine(JsonSerializer.Serialize(new { kind = "batch-old-revision", conflicted }));
        }

        using (var takeover = Prepare("BatchOwner"))
        {
            var before = takeover.Read(); string sql = SqlSnapshot(takeover);
            AccountOwners.Lease? replacement = null;
            int planned = 0;
            bool staleOwner = false;
            try
            {
                takeover.Session.PlanAndCommitShopBuy(targetShop, character =>
                {
                    planned++;
                    var plan = shop.Buy(character, new(rows[0].Item, 2, targetShop, nolan), out _, now);
                    Check(plan.Accepted, "接管负例先取得原owner/generation的合法目标计划");
                    replacement = takeover.Owners.Acquire(takeover.Owner.Account, () => { });
                    return plan;
                });
            }
            catch (InvalidOperationException error) when (error.Message == "Stale account generation") { staleOwner = true; }
            using (replacement)
            {
                Check(staleOwner && planned == 1 && replacement is { IsCurrent: true } &&
                    replacement.Generation > takeover.Owner.Generation && !takeover.Owner.IsCurrent &&
                    Same(before, takeover.Repository.LoadCharacter(replacement, before.Slot)) && SqlSnapshot(takeover) == sql,
                    "接管使旧owner和generation在提交前失效，不扣目标成本、发奖、写配额或审计");
                takeover.Owner.Dispose();
                var current = new GameSession(takeover.Repository, towns, replacement!, questCatalog: quests, timeProvider: clock);
                current.Select(before.Slot); current.MarkReady();
                ShopPurchaseReceipt? receipt = null;
                var result = current.PlanAndCommitShopBuy(targetShop,
                    character => shop.Buy(character, new(rows[0].Item, 2, targetShop, nolan), out receipt, now));
                Committed(takeover, before, result, receipt, rows[0], 2, current, replacement);
                Check(replacement!.IsCurrent, "旧lease释放不撤销新owner，原批量请求由新generation正常提交一次");
                Console.WriteLine(JsonSerializer.Serialize(new { kind = "batch-old-owner-generation", planned, staleOwner,
                    oldGeneration = takeover.Owner.Generation, newGeneration = replacement.Generation, currentAccepted = result.Accepted }));
            }
        }

        using (var race = Prepare("BatchRaceA"))
        {
            var before = race.Read();
            ShopPurchaseReceipt? initialReceipt = null;
            var initial = race.Session.PlanAndCommitShopBuy(targetShop,
                character => shop.Buy(character, new(rows[0].Item, 7, targetShop, nolan), out initialReceipt, now));
            Committed(race, before, initial, initialReceipt, rows[0], 7);
            var sibling = race.Repository.Create(race.Owner, Seed("BatchRaceB"));
            var secondSession = new GameSession(race.Repository, towns, race.Owner, questCatalog: quests, timeProvider: clock);
            secondSession.Select(sibling.Slot); secondSession.MarkReady();
            var a = race.Session.Selected!;
            var b = secondSession.Selected!;
            Check(a.Id != b.Id && a.Inventory.ShopPurchases.Single().Count == 7 && b.Inventory.ShopPurchases.Single().Count == 7,
                "同账号A/B两角色从相同永久七件额度快照竞争最后一件");
            InventoryResult? secondResult = null;
            ShopPurchaseReceipt? secondReceipt = null;
            Character? committedB = null;
            string? winnerSql = null;
            int firstPlans = 0, secondPlans = 0;
            bool firstConflicted = false;
            try
            {
                race.Session.PlanAndCommitShopBuy(targetShop, character =>
                {
                    firstPlans++;
                    var firstPlan = shop.Buy(character, new(rows[0].Item, 1, targetShop, nolan), out _, now);
                    Check(firstPlan.Accepted, "A锁外规划最后一件时仍合法");
                    // 确定性交错两个规划/提交窗口，不用睡眠、线程调度或两份互相接管的lease冒充竞争。
                    secondResult = secondSession.PlanAndCommitShopBuy(targetShop, current =>
                    {
                        secondPlans++;
                        return shop.Buy(current, new(rows[0].Item, 1, targetShop, nolan), out secondReceipt, now);
                    });
                    committedB = race.Repository.LoadCharacter(race.Owner, b.Slot);
                    winnerSql = SqlSnapshot(race);
                    return firstPlan;
                });
            }
            catch (InvalidOperationException error) when (error.Message.Contains("owner/revision", StringComparison.Ordinal)) { firstConflicted = true; }
            var afterA = race.Repository.LoadCharacter(race.Owner, a.Slot);
            var afterB = Committed(race, b, secondResult!, secondReceipt, rows[0], 1, secondSession);
            var expectedA = a with { Inventory = a.Inventory with { ShopPurchases = afterB.Inventory.ShopPurchases } };
            Check(firstConflicted && firstPlans == 1 && secondPlans == 1 && committedB is not null &&
                Same(expectedA, afterA) && Same(committedB, afterB) && ReferenceEquals(a, race.Session.Selected) &&
                afterA.Inventory.ShopPurchases.Single() == new ShopPurchaseCount(nolan, rows[0].Item, true, false, 0, 8) &&
                CountItem(afterA, rows[0].Item) + CountItem(afterB, rows[0].Item) == 8 &&
                CountItem(afterA, material) + CountItem(afterB, material) == 1000 - 8 * rows[0].Cost &&
                race.Query("SELECT count(*) FROM shop_purchase_counts WHERE account='formal-session' AND character_id=0 AND npc_id=400001107 AND item_id=490705437 AND reset_daily=0 AND day=0 AND count=8") is 1L &&
                SqlSnapshot(race) == winnerSql && Same(afterA, new CharacterRepository(race.Path, quests).LoadCharacter(race.Owner, a.Slot)),
                "B真实提交最后一件后A旧额度CAS拒绝，合计八件、最后额度只扣一次成本，A自有资产不变且双角色冷读一致");
            Console.WriteLine(JsonSerializer.Serialize(new { kind = "batch-account-last-quota", firstPlans, secondPlans, firstConflicted,
                winner = "B", finalPurchased = 8, firstRevisionUnchanged = afterA.Revision == a.Revision, secondAccepted = secondResult!.Accepted }));
        }
        Console.WriteLine("T03_BATCH_IDENTITY_ASSERTIONS " + (TestCheck.Count - beforeChecks));

        Fixture Prepare(string name, NewCharacter? seed = null)
        {
            var f = new Fixture(name, towns, characters, learning, quests, prepared: seed ?? Seed(name), timeProvider: clock);
            try
            {
                f.Session.Select(f.Created.Slot); f.Session.MarkReady();
                Check(f.Session.Phase == SessionPhase.Town && f.Session.Selected is { } character &&
                    towns.IsRoom(character.Position, DfoServer.Game.Quests.QuestEligibility.Completed(character)),
                    "新增验收夹具沿真实选角Ready留在赛丽亚房间 " + name);
                return f;
            }
            catch { f.Dispose(); throw; }
        }
        NewCharacter Seed(string name, int first = 500, int second = 0, bool locked = false, int expires = 0)
        {
            var seed = CharacterCreation.Prepare(name, characters.Get(0), towns.Room, towns.InitialReturn, learning.Get);
            var template = itemResources.Items.Get(material);
            var items = seed.Inventory.Items.ToBuilder();
            items.Add(InventoryRewardGrantService.CreateItem(template, new(InventoryListType.Main, 121),
                InventoryRewardGrantService.Create(template, first, now)));
            if (second > 0)
                items.Add(InventoryRewardGrantService.CreateItem(template, new(InventoryListType.Main, 122),
                    InventoryRewardGrantService.Create(template, second, now) with { EquipmentLockId = (byte)(locked ? 1 : 0), ExpireTime = expires }));
            return seed with { Inventory = seed.Inventory with { Gold = 1_000_000, Items = items.ToImmutable() } };
        }
        Character Committed(Fixture f, Character before, InventoryResult result, ShopPurchaseReceipt? receipt,
            (int Item, int Cost, int? Limit) row, int count, GameSession? session = null, AccountOwners.Lease? owner = null)
        {
            session ??= f.Session; owner ??= f.Owner;
            var after = f.Repository.LoadCharacter(owner, before.Slot);
            int previous = before.Inventory.ShopPurchases.SingleOrDefault(value => value.ItemId == row.Item)?.Count ?? 0;
            bool quota = row.Limit is null ? after.Inventory.ShopPurchases.IsEmpty : after.Inventory.ShopPurchases is [{ } key] &&
                key == new ShopPurchaseCount(nolan, row.Item, true, false, 0, previous + count);
            // 账号共通材料不占Main实体：ACK21只带核心和购买计数，余额沿563发布，不伪造背包UID。
            bool accountMaterial = itemResources.Items.Get(row.Item).AccountMaterial is not null;
            var received = receipt is null ? null : after.Inventory.Find(receipt.Position);
            bool delivery = receipt is not null && (accountMaterial
                ? received is null && receipt.Position == new InventorySlot(InventoryListType.Main, -1) &&
                    receipt.Core is { } grantedCore && grantedCore.ItemId == row.Item && grantedCore.Count == count
                : received is { Uid: > 0 } && received.Core == receipt.Core &&
                    receipt.Position != new InventorySlot(InventoryListType.Main, -1));
            var allowed = before with { Revision = before.Revision + 1, Inventory = before.Inventory with
                {
                    Items = after.Inventory.Items, ShopPurchases = after.Inventory.ShopPurchases,
                    AccountWallet = accountMaterial ? after.Inventory.AccountWallet : before.Inventory.AccountWallet
                } };
            Check(result.Accepted && result.Receipt is { } committed && session.CanPublishInventory(committed) &&
                receipt is not null && receipt.ItemId == row.Item && receipt.Count == count && delivery &&
                CountItem(after, material) == CountItem(before, material) - (long)row.Cost * count &&
                CountItem(after, row.Item) == CountItem(before, row.Item) + count && quota && Same(allowed, after) &&
                before.Inventory.Items.Where(item => item.Core.ItemId != material && item.Core.ItemId != row.Item)
                    .SequenceEqual(after.Inventory.Items.Where(item => item.Core.ItemId != material && item.Core.ItemId != row.Item)) &&
                Same(after, session.Selected!) && Same(after, committed.Character) &&
                Same(after, new CharacterRepository(f.Path, quests).LoadCharacter(owner, before.Slot)),
                "单批真实提交精确总成本、增量、永久键、持久UID和完整冷读，仅允许本次库存及revision变化 " + row.Item + "/" + count);
            return after;
        }
        void Rejected(Fixture f, Character before, string sql, InventoryResult result, ushort error, string name) =>
            Check(result is { Accepted: false, Plan: null, Receipt: null } && result.Error == error &&
                Same(before, f.Read()) && Same(before, f.Session.Selected!) && SqlSnapshot(f) == sql, name);
        static long CountItem(Character character, int itemId) => character.Inventory.Items
            .Where(item => item.Core.ItemId == itemId).Sum(item => (long)item.Core.Count) +
            character.Inventory.AccountWallet.Material(itemId).Balance;
    }

    internal static void VerifyLoginWearWindow(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters)
    {
        var clock = new Testing.Inventory.DailyBoosterTestClock(DateTimeOffset.Parse("2026-10-23T00:00:00Z"));
        using var f = new Fixture("LoginWindow", towns, characters, SelfTestCatalogs.SkillLearning(pvf),
            SelfTestCatalogs.Quests(pvf), timeProvider: clock);
        Check(!f.Session.LoginWearRestatementWindowOpen, "未选角不能吸收登录穿戴复述");
        f.SelectEntry();
        Check(f.Session.LoginWearRestatementWindowOpen, "首次入城保留五秒登录穿戴复述兼容");
        Check(f.Session.BeginDungeonSelection() is not null && !f.Session.LoginWearRestatementWindowOpen,
            "时间未推进，进入选图立即结束登录复述窗口");
        Check(f.Session.ReturnFromDungeonSelection(restorePosition: false) is not null &&
            !f.Session.LoginWearRestatementWindowOpen, "快速取消选图返回城镇不能复活旧登录窗口");
        Check(f.Session.BeginDungeonSelection() is not null &&
            f.Session.EnterFormalDungeon(3, 3145, 0, SelfTestCatalogs.Dungeons(pvf), SelfTestCatalogs.Rooms(pvf)) is not null &&
            f.Session.Phase == SessionPhase.Loading && !f.Session.LoginWearRestatementWindowOpen,
            "同一时刻快速进图加载不吸收真实卸装");
        Check(f.Session.CompleteFormalDungeonLoading() is not null && f.Session.Phase == SessionPhase.Dungeon &&
            !f.Session.LoginWearRestatementWindowOpen, "副本阶段不吸收真实卸装");
        Check(f.Session.ReturnFromFormalDungeon() is not null && f.Session.Phase == SessionPhase.Town &&
            !f.Session.LoginWearRestatementWindowOpen, "副本返回城镇不重开旧登录窗口");
        f.Session.ReturnToRoster(); f.Session.Select(f.Created.Slot); f.Session.MarkReady();
        Check(f.Session.LoginWearRestatementWindowOpen, "真正重选角色重新开启登录复述兼容");
        clock.Advance(TimeSpan.FromMilliseconds(4999));
        Check(f.Session.LoginWearRestatementWindowOpen, "入城4999毫秒仍保留兼容窗口");
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Check(!f.Session.LoginWearRestatementWindowOpen, "入城五秒边界不再吸收真实卸装");
    }

    internal static NativeDungeonDeathObservation Observation(FormalDungeonReceipt receipt, DungeonPlannedMonster monster) =>
        new(monster.ObjectKey, receipt.Character.Id, 0x0111, receipt.Character.Id, 600, 3, 0,
            checked((uint)monster.CandidateLevel), 504, 620, 3, true, new string('A', 64));
    static bool Same(Character first, Character second) => JsonSerializer.Serialize(first) == JsonSerializer.Serialize(second);
    static void Check(bool condition, string name) => TestCheck.That(condition, name);

    sealed class Fixture : IDisposable
    {
        readonly string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "S5A9-formal-session-" + Guid.NewGuid().ToString("N"));
        public string Path { get; }
        public CharacterRepository Repository { get; }
        public AccountOwners Owners { get; } = new();
        public AccountOwners.Lease Owner { get; }
        public GameSession Session { get; }
        public Character Created { get; }
        public DungeonTransientReceipts? Transient => Session.DungeonSnapshot is { } snapshot
            ? DungeonTransientReceipts.Find(Owner, snapshot.Identity) : null;
        public Fixture(string name, TownCatalog towns, CharacterCatalog characters, SkillLearningCatalog learning, DfoServer.GameWorld.Quests.QuestCatalog quests, bool secondCharacter = false, byte job = 0,
            DfoServer.Game.Settings.GameDebugSettings? debugSettings = null, NewCharacter? prepared = null,
            TimeProvider? timeProvider = null, SpringTreasureDropPolicy? springTreasure = null)
        {
            Path = System.IO.Path.Combine(root, "game.db");
            Repository = new(Path, quests);
            Owner = Owners.Acquire("formal-session", () => { });
            Session = new(Repository, towns, Owner, questCatalog:prepared is null ? null : quests, debugSettings: debugSettings, timeProvider: timeProvider, springTreasure: springTreasure);
            if (secondCharacter) Session.CreateCharacter("RecordDummy", characters.Get(0), learning);
            Created = Repository.Create(Owner, prepared ?? CharacterCreation.Prepare(name, characters.Get(job), towns.Room, towns.InitialReturn, learning.Get)
                with { ActiveMainQuests = [new(0,3145,1)] });
        }
        public void SelectEntry()
        {
            Session.Select(Created.Slot);
            Session.MarkReady();
            Check(Session.LeaveRoom() && Session.Travel(new(38, 2, 1085, 249, 5, 38, 0, 0)),
                "正常Session合法出房和左侧入口旅行");
        }
        public Character Read() => Repository.LoadCharacter(Owner, Created.Slot);
        public long Count() => Convert.ToInt64(Query("SELECT count(*) FROM character_dungeon_growth_receipts"));
        public object? Query(string sql)
        {
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path, Pooling = false, ForeignKeys = true }.ToString());
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = sql;
            return command.ExecuteScalar();
        }
        public void Sql(string sql)
        {
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path, Pooling = false, ForeignKeys = true }.ToString());
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
        public void Dispose()
        {
            Session.End();
            Owner.Dispose();
            if (Directory.Exists(root)) TestFiles.DeleteDirectory(root, recursive: true);
        }
    }
}
