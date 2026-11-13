using System.Text.Json;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Session;
using DfoServer.Game.Strongway2019;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class Strongway2019TailResourceTests
{
    public static void RunResources(PvfArchive pvf, TownCatalog towns, CharacterCatalog characters, ItemResources resources)
    {
        int before = Count;
        foreach (int id in new[] { 10309616, 10309617 })
        {
            That(resources.Items.Get(id) is { Kind: ItemKind.Consumable, StackableType: "[waste] 0", StackLimit: 1000,
                    MinimumLevel: 1, Jobs: ushort.MaxValue, Expiration.DailyDelete: 1 } &&
                resources.RecoveryConsumables.TryGet(id, out var definition) &&
                definition is { CoolTime: 10000, CoolGroup: 2, Unlimited: false } &&
                definition.AllowedDungeons is { } allowed && allowed.SetEquals(new uint[] { 5359, 5360, 5362, 5363, 5364 }),
                "真实专用药保留五图白名单、日删除及共享冷却：" + id);
            resources.RecoveryConsumables.TryGet(id, out var actual);
            That(actual.Allows(95) && actual.Jobs == ushort.MaxValue, "真实专用药保留等级及全职业资源声明：" + id);
        }
        resources.RecoveryConsumables.TryGet(10309617, out var globe);
        That(globe.EffectMilliseconds == 10000 && globe.StatChanges.SequenceEqual(
            new ConsumableStatChange[] { new("+", 1000, "all activestatus resistance") }),
            "真实世界仪保留原万分抗性字段和10秒期限，不推算客户端治疗/异常数值");

        ConsumableUseSessionTests.RunStrongway2019Restrictions(towns, characters, pvf, resources);
        RunAllowedAndExpiry(pvf, towns, resources);
        RunLottery(towns, resources);
        Console.WriteLine($"PASS {Count - before} 首期027及专用药真实资源/Session/期限断言（五图为资源名单，5359为实际正控）");
    }

    static void RunAllowedAndExpiry(PvfArchive pvf, TownCatalog towns, ItemResources resources)
    {
        var activity = new Strongway2019Catalog(pvf);
        using var f = new Strongway2019MatchingTests.Fixture(towns, activity);
        // 先走到06点前再入场，避免将活动3600秒限时与药品到期混为一谈。
        f.Clock.Advance(TimeSpan.FromHours(18) - TimeSpan.FromSeconds(30));
        var team = f.Team();
        foreach (var member in team) f.Parties.Detach(member);
        var session = team[0];
        var owner = session.CaptureSocialActor()!.Owner;
        ushort slot = session.Selected!.Slot;
        That(session.PlanAndCommitInventory(character => InventoryRewardGrantService.TryGrantBatch(character,
            [new(10309616, 4), new(10309617, 4)], resources.Items, resources.GoldLimits, resources.EpicPieces,
            utcNow: f.Clock.GetUtcNow())).Accepted, "普通Grant创建真实专用药及06点实例期限");
        var sources = session.Selected!.Inventory.Items.Where(item => item.Core.ItemId is 10309616 or 10309617).ToArray();
        long deadline = new DateTimeOffset(2026, 11, 2, 6, 0, 0, TimeSpan.FromHours(8)).ToUnixTimeSeconds();
        That(sources.Length == 2 && sources.All(item => item.Core.ExpireTime == deadline && item.Core.Count == 4),
            "两真实源共用固定次日06点期限，未手造无期限测试Core");
        var service = new InventoryConsumableUseService(resources.RecoveryConsumables, f.Clock);
        var dungeons = DungeonCatalog.Load(pvf);
        var rooms = DungeonRoomCatalog.Load(pvf);
        var drops = DungeonMonsterDropCatalog.Load(pvf);
        Enter(session);
        var first = Use(session, sources[0], 2);
        var remaining = session.Selected!.Inventory.Find(sources[0].Position);
        That(first.Outcome == ConsumableUseOutcome.Committed,
            $"实际5359雷米必须提交：outcome={first.Outcome}, reason={first.Reason}");
        That(!first.PreserveSource, "实际5359雷米不能走练习保源分支");
        That(remaining is { Core.Count: 3 }, "实际5359雷米只扣一件");
        That(remaining!.Uid == sources[0].Uid, "实际5359雷米保留原UID");
        That(remaining.Core.ExpireTime == deadline, "实际5359雷米保留原06点期限");
        string cooling = Saved(session);
        That(Use(session, sources[1], 3).Outcome == ConsumableUseOutcome.Rejected && Saved(session) == cooling,
            "两药使用真实组2共享冷却，换药仍拒绝且不扣件");
        f.Clock.Advance(TimeSpan.FromSeconds(10));
        That(Use(session, sources[1], 4).Outcome == ConsumableUseOutcome.Committed &&
            session.Selected!.Inventory.Find(sources[1].Position) is { Core.Count: 3 } globe && globe.Core.ExpireTime == deadline,
            "冷却等号后实际5359世界仪消费一次且保留原期限");
        That(session.ReturnFromFormalDungeon() is not null, "原5359正常退出回城，为重新装载准备持久位置");
        string persisted = Saved(session);
        session.ReturnToRoster();
        var reconnect = new GameSession(new CharacterRepository(f.DatabasePath), towns, owner,
            entry: new(new(2, 49), 55, "[dpl_pve]"), timeProvider: f.Clock, strongway2019Catalog: activity,
            strongway2019AntiEvil: _ => 5400, serverEvents: f.Events, consumables: resources.RecoveryConsumables);
        try
        {
            reconnect.Select(slot, resumeTown: true); reconnect.MarkReady();
            That(Saved(reconnect) == persisted && sources.All(source =>
                reconnect.Selected!.Inventory.Find(source.Position)!.Core.ExpireTime == deadline),
                "新Repository及新Session恢复消费后源和原06点期限，不从重连时刻续期");
            Enter(reconnect);
            f.Clock.Advance(TimeSpan.FromSeconds(10));
            That(Use(reconnect, sources[0], 5).Outcome == ConsumableUseOutcome.Committed &&
                reconnect.Selected!.Inventory.Find(sources[0].Position) is { Core.Count: 2 } restored && restored.Core.ExpireTime == deadline,
                "重连后原未过期源在实际5359继续消费，仅再扣一件");
            f.Clock.Advance(TimeSpan.FromSeconds(10));
            foreach (var source in sources)
            {
                string expired = Saved(reconnect);
                That(Use(reconnect, source, 6).Outcome == ConsumableUseOutcome.Rejected && Saved(reconnect) == expired &&
                    reconnect.Selected!.Inventory.Find(source.Position)!.Core.ExpireTime == deadline,
                    "精确06点两药均到期整拒，保原剩余源及期限：" + source.Core.ItemId);
            }
        }
        finally { reconnect.End(); }

        ConsumableUseResult Use(GameSession current, InventoryItem source, uint check) =>
            current.UseConsumable(new(source.Position, check, source.Core.ItemId, 0), service);
        string Saved(GameSession current) => JsonSerializer.Serialize(new CharacterRepository(f.DatabasePath).LoadCharacter(owner, slot)) +
            ":" + JsonSerializer.Serialize(current.Selected);
        void Enter(GameSession current)
        {
            That(current.EnterFormalDungeon(5359, 0, 2, dungeons, rooms, drops, items: resources.Items,
                pieces: resources.EpicPieces) is { Entry.HasFreeCombatConsumption: false } &&
                current.CompleteFormalDungeonLoading() is not null && current.Phase == SessionPhase.Dungeon,
                "真实5359原入口和加载屏障成功，未注入白名单假实例：" + current.LastFormalDungeonRejection);
        }
    }

    static void RunLottery(TownCatalog towns, ItemResources resources)
    {
        const int pot = 490013027;
        var definition = resources.Boosters.Get(pot);
        var pool = definition.Pools.Single();
        string poolFingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            string.Join(";", pool.Rewards.Select(row => $"{row.ItemId},{row.Weight},{row.Count}")))));
        That(resources.Items.Get(pot) is { StackableType: "[upgradable legacy] 1", StackLimit: 1 } &&
            definition is { Available: true, Lottery: true, LotteryGoldCost: 0, LotteryUseNeedItem: null, Selection: null } &&
            pool.Draws == 1 && pool.Rewards.Length == 849 && pool.Rewards.Select(row => row.ItemId).Distinct().Count() == 849 &&
            pool.Rewards.Count(row => row.Weight == 10) == 236 && pool.Rewards.Count(row => row.Weight == 73) == 613 &&
            pool.Rewards.Sum(row => row.Weight) == 47109 && pool.Rewards.All(row => row.Count == 1) &&
            poolFingerprint == "430DFE019634E7BB443FF6F1C7401B5CA28EC956542BFE095F3132CA98B0FB48" &&
            pool.Rewards[0] == new BoosterReward(102000579, 10, 1) &&
            pool.Rewards.First(row => row.Weight == 73) == new BoosterReward(108040528, 73, 1) &&
            pool.Rewards[^1] == new BoosterReward(108020483, 73, 1) &&
            pool.Rewards.All(row => resources.Items.Get(row.ItemId).Kind == ItemKind.Equipment),
            "027真实849件独立装备奖池保完整权重47109；need material不是开罐材料");
        using var f = new Strongway2019MatchingTests.Fixture(towns);
        var session = f.Team()[0];
        var owner = session.CaptureSocialActor()!.Owner;
        That(session.PlanAndCommitInventory(character => InventoryRewardGrantService.TryGrantBatch(character,
            [new(pot, 1)], resources.Items, resources.GoldLimits, resources.EpicPieces,
            utcNow: f.Clock.GetUtcNow())).Accepted, "普通Grant创建第一只真实stack1罐");
        var firstPot = session.Selected!.Inventory.Items.Single(item => item.Core.ItemId == pot);
        var inventory = new InventoryService(resources.Items, resources.GoldLimits, resources.EpicPieces);
        var cargo = new InventorySlot(InventoryListType.PersonalCargo, 0);
        // 普通发放不能绕过同页满堆；先经正常移动暂存第一罐，再单独获取第二罐。
        That(session.MoveInventory(new(firstPot.Position, pot, 1, cargo, 0, 0, -1, 0, 0), inventory).Accepted &&
            session.Selected!.Inventory.Find(cargo) == (firstPot with { Position = cargo }),
            "第一罐经正式移动暂存个人仓，保留完整Core和原UID");
        That(session.PlanAndCommitInventory(character => InventoryRewardGrantService.TryGrantBatch(character,
            [new(pot, 1), new(490012699, 9)], resources.Items, resources.GoldLimits, resources.EpicPieces,
            utcNow: f.Clock.GetUtcNow())).Accepted, "普通Grant创建第二只真实stack1罐及不应开罐扣除的商店材料");
        var returned = new InventorySlot(InventoryListType.Main, checked((short)(firstPot.Position.Index + 1)));
        That(session.MoveInventory(new(cargo, pot, 1, returned, 0, 0, -1, 0, 0), inventory).Accepted &&
            session.Selected!.Inventory.Find(returned) == (firstPot with { Position = returned }),
            "第一罐经正式移动回空消耗品槽，不通过合堆或重建改变原UID");
        var original = session.Selected!;
        var source = original.Inventory.Items.First(item => item.Core.ItemId == pot);
        var sentinel = original.Inventory.Items.Single(item => item.Core.ItemId == pot && item.Uid != source.Uid);
        That(source.Uid > 0 && sentinel.Uid > 0 && source.Uid != sentinel.Uid &&
            source.Core.Count == 1 && sentinel.Core.Count == 1 &&
            source.Position.List == InventoryListType.Main && sentinel.Position.List == InventoryListType.Main,
            "两只正常获取的真实stack1罐均回消耗品页并保持独立UID");
        var material = original.Inventory.Items.Single(item => item.Core.ItemId == 490012699);
        RunLotteryBoundaries(original, source, resources, f.Clock.GetUtcNow());
        var random = new CountingRandom();
        var service = new InventoryBoosterService(resources.Items, resources.Boosters, resources.GoldLimits,
            resources.EpicPieces, random, contracts: resources.Contracts, avatars: resources.AvatarPackages, clock: f.Clock);
        var frozen = service.OpenLottery(original, new(0, source.Position.Index), out var receipt, f.Clock.GetUtcNow());
        That(frozen.Accepted && frozen.Plan is not null && receipt is { Count: 1 } && random.Calls == 1 &&
            pool.Rewards.Any(row => row.ItemId == receipt.ItemId), "正式CMD27领域入口只正常随机一次，奖项属于完整真实池");
        var plan = frozen.Plan!;
        string before = Snapshot(), frozenAfter = JsonSerializer.Serialize(plan.After);
        f.Sql("CREATE TRIGGER fail_strong_pot AFTER INSERT ON inventory_audit_log WHEN NEW.action_name='lottery_open' " +
            "BEGIN SELECT RAISE(ABORT,'strong pot rollback'); END");
        Reject<SqliteException>(() => session.PlanAndCommitBooster(_ => frozen, service.UsePlaces(pot), pot),
            "027原库存和审计末段SQL故障不产生成功");
        That(Snapshot() == before && ReferenceEquals(session.Selected, original) && random.Calls == 1 &&
            JsonSerializer.Serialize(plan.After) == frozenAfter,
            "027晚失败整笔回滚源、奖励、完整Core/revision/审计/UID序列并保留单次冻结抽样");
        f.Sql("DROP TRIGGER fail_strong_pot");
        That(session.PlanAndCommitBooster(_ => frozen, service.UsePlaces(pot), pot).Accepted,
            "移除故障后只提交原冻结计划，不重新抽奖");
        var committed = session.Selected!;
        var reward = committed.Inventory.Find(receipt!.RewardSlot)!;
        var expected = plan.After.Find(receipt.RewardSlot)!;
        That(committed.Revision == original.Revision + 1 && committed.Inventory.Find(source.Position) is null &&
            committed.Inventory.Find(sentinel.Position) == sentinel && committed.Inventory.Find(material.Position) == material &&
            committed.Inventory.Gold == original.Inventory.Gold && reward.Uid > 0 &&
            original.Inventory.Items.All(item => item.Uid != reward.Uid) &&
            reward == (expected with { Uid = reward.Uid }) && reward.Core.Kind == ItemKind.Equipment &&
            reward.Position.List == InventoryListType.Main && ItemSlotBoundService.Allows(committed.Inventory,
                reward.Position, ItemKind.Equipment) && random.Calls == 1,
            "027只消耗指定一罐，另一罐、商店材料与金币不变，真实装备Core按冻结计划获独立UID");
        That(JsonSerializer.Serialize(new CharacterRepository(f.DatabasePath).LoadCharacter(owner, original.Slot)) ==
            JsonSerializer.Serialize(committed), "新Repository恢复027实际奖项及全部库存信息");
        before = Snapshot();
        Reject<InvalidOperationException>(() => session.PlanAndCommitBooster(_ => frozen, service.UsePlaces(pot), pot),
            "已提交的旧revision冻结抽样不能再次提交");
        That(Snapshot() == before && random.Calls == 1, "旧计划重放整拒不二次发奖或重新随机");

        string Snapshot() => JsonSerializer.Serialize(new CharacterRepository(f.DatabasePath).List(owner)) + ":" +
            Query("SELECT count(*) FROM inventory_audit_log") + ":" +
            Query("SELECT seq FROM sqlite_sequence WHERE name='character_inventory_items'");
        object? Query(string sql)
        {
            using var db = new SqliteConnection("Data Source=" + f.DatabasePath + ";Pooling=False");
            db.Open(); using var command = db.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar();
        }
    }

    // 捕获累计区间的 < / <=、少减一行、重抽和奖池截断错误；只替换随机输入，不复刻生产抽样器。
    static void RunLotteryBoundaries(Character original, InventoryItem source, ItemResources resources, DateTimeOffset now)
    {
        var rows = resources.Boosters.Get(490013027).Pools.Single().Rewards;
        string unchanged = JsonSerializer.Serialize(original);
        long start = 0;
        int draws = 0;
        foreach (var row in rows)
        {
            long end = checked(start + row.Weight);
            foreach (long sample in new[] { start, end - 1 })
            {
                var random = new BoundaryRandom(sample);
                var service = new InventoryBoosterService(resources.Items, resources.Boosters, resources.GoldLimits,
                    resources.EpicPieces, random, contracts: resources.Contracts, avatars: resources.AvatarPackages);
                var result = service.OpenLottery(original, new(0, source.Position.Index), out var receipt, now);
                That(result.Accepted && result.Plan is not null && receipt is { Count: 1 } &&
                    receipt.ItemId == row.ItemId && random.Calls == 1 && random.Maximum == 47109,
                    $"027真实累计区间[{start},{end})的输入{sample}准确抽到{row.ItemId}，只抽一次且上界47109不包含");
                var plan = result.Plan!;
                var reward = plan.After.Find(receipt!.RewardSlot);
                That(plan.Reason == "lottery_open" && plan.Before == original.Inventory &&
                    plan.After.Find(source.Position) is null && reward is { Core.Kind: ItemKind.Equipment, Core.Count: 1 } &&
                    reward.Core.ItemId == row.ItemId && plan.After.Items.Length == original.Inventory.Items.Length &&
                    original.Inventory.Items.Where(item => item.Uid != source.Uid)
                        .All(item => plan.After.Find(item.Position) == item) &&
                    plan.After.Gold == original.Inventory.Gold && original.Inventory.Find(source.Position) == source,
                    $"027区间输入{sample}规划只消费指定罐并产出指定真实装备，其他源/材料/金币完整保留");
                draws++;
            }
            start = end;
        }
        That(start == 47109 && draws == 1698 && JsonSerializer.Serialize(original) == unchanged,
            "027真实849项首尾累计边界共1698次规划，总区间[0,47109)，不提交也不改变原角色");
        foreach (var request in new LotteryOpenRequest[]
        {
            new(1, source.Position.Index), new(ushort.MaxValue, source.Position.Index),
            new(0, -1), new(0, short.MaxValue)
        })
        {
            var random = new BoundaryRandom(0);
            var service = new InventoryBoosterService(resources.Items, resources.Boosters, resources.GoldLimits,
                resources.EpicPieces, random, contracts: resources.Contracts, avatars: resources.AvatarPackages);
            var result = service.OpenLottery(original, request, out var receipt, now);
            That(!result.Accepted && result.Plan is null && receipt is null && random.Calls == 0 &&
                JsonSerializer.Serialize(original) == unchanged,
                $"027非法mode/负槽/不存在槽整拒，无抽样、计划、回执及源变化：{request}");
        }
    }

    sealed class BoundaryRandom(long value) : Random
    {
        public int Calls { get; private set; }
        public long Maximum { get; private set; }
        public override long NextInt64(long maximum)
        {
            Calls++;
            Maximum = maximum;
            if (value < 0 || value >= maximum)
                throw new InvalidOperationException("027测试随机输入不在NextInt64的合法半开区间内");
            return value;
        }
    }

    sealed class CountingRandom : Random
    {
        public int Calls { get; private set; }
        public override long NextInt64(long maximum) { Calls++; return base.NextInt64(maximum); }
    }
}

// 与原文件合并为partial，只复用原私有fixture，不扩大其生产访问范围。
internal static partial class ConsumableUseSessionTests
{
    internal static void RunStrongway2019Restrictions(TownCatalog towns, CharacterCatalog characters,
        PvfArchive archive, ItemResources resources)
    {
        resources.RecoveryConsumables.TryGet(10309617, out var globe);
        using var f = new Fixture(towns, characters, hp: globe, consumables: resources.RecoveryConsumables);
        f.Select();
        That(f.Session.MutateInventory(character => InventoryDeleteService.Plan(character.Inventory,
            f.Request.Position, f.Request.ItemId, 3)).Accepted, "移除旧fixture手造源，真实药源由普通Grant创建");
        That(f.Session.PlanAndCommitInventory(character => InventoryRewardGrantService.TryGrantBatch(character,
            [new(10309616, 3), new(10309617, 3)], resources.Items, resources.GoldLimits, resources.EpicPieces,
            utcNow: f.Clock.GetUtcNow())).Accepted, "原补给fixture正常Grant两种真实药并保存真实日删除期限");
        var sources = f.Session.Selected!.Inventory.Items.Where(item => item.Core.ItemId is 10309616 or 10309617).ToArray();
        That(f.Session.LeaveRoom() && f.Session.Travel(new(38, 2, 1085, 249, 5, 38, 0, 0)),
            "真实专用药复用既有旅行入口进入普通选图城镇");
        f.Session.BeginDungeonSelection();
        foreach (var source in sources) Refuse(source, "选图尚无正式身份", "invalid-state");
        That(f.Session.EnterFormalDungeon(3, 0, 0, global::DfoServer.Testing.SelfTestCatalogs.Dungeons(archive),
            global::DfoServer.Testing.SelfTestCatalogs.Rooms(archive)) is not null &&
            f.Session.CompleteFormalDungeonLoading() is not null, "真实专用药建立名单外的正式3号副本");
        foreach (var source in sources)
        {
            var result = f.Session.UseConsumable(new(source.Position, 8, source.Core.ItemId, 0), f.Service);
            That(result.Outcome == ConsumableUseOutcome.Committed &&
                f.Read().Inventory.Find(source.Position) == source with { Core = source.Core with { Value = 2 } },
                "服务端不重复客户端活动副本名单，仍沿普通事务只扣原UID一件并保留期限：" + source.Core.ItemId);
            Refuse(source, "真实共享组冷却", "cooldown");
            f.Clock.Advance(TimeSpan.FromSeconds(10));
        }
        void Refuse(InventoryItem source, string context, string expectedReason)
        {
            string persisted = JsonSerializer.Serialize(f.Read()), online = JsonSerializer.Serialize(f.Session.Selected);
            var rejected = f.Session.UseConsumable(new(source.Position, 7, source.Core.ItemId, 0), f.Service);
            That(rejected.Outcome == ConsumableUseOutcome.Rejected && rejected.Reason == expectedReason &&
                JsonSerializer.Serialize(f.Read()) == persisted &&
                JsonSerializer.Serialize(f.Session.Selected) == online,
                context + "拒绝真实专用药且不扣源/期限/revision：" + source.Core.ItemId);
        }
    }
}
