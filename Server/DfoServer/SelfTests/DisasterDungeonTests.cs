using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.PcRoom;
using DfoServer.Game.Settings;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Persistence;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

// #1214 哈林灾难领域：入场疲劳 8、每房 3、账号每周 10 次、通关礼盒 490012200。
internal static partial class DisasterDungeonTests
{
    public static void RunCore()
    {
        int before = Count;
        var entrance = new DungeonDefinition(DisasterDungeonPolicy.EntranceId, "dungeon/disaster.dgn", 93, 105, [], [])
            { DungeonType = DisasterDungeonPolicy.TypeName };
        var playable = entrance with { Id = DisasterDungeonPolicy.FirstPlayableId };
        var routedEntrance = entrance with { RawSections = [new("enter random dungeon list",
            Enumerable.Range(5341, 5).Select(id => new PvfToken(0, id)).ToArray())] };
        That(Enumerable.Range(0, 5).Select(index => DisasterDungeonPolicy.SelectPlayable(routedEntrance, _ => index))
            .SequenceEqual(Enumerable.Range(5341, 5)), "随机入口按PVF原顺序覆盖五张实图");
        That(DisasterDungeonPolicy.SelectPlayable(routedEntrance, _ => throw new InvalidOperationException(), 5345) == 5345,
            "队员复用队长实图且不再次随机");
        Reject<InvalidDataException>(() => DisasterDungeonPolicy.SelectPlayable(routedEntrance, _ => 5), "路线随机越界拒绝");
        Reject<InvalidDataException>(() => DisasterDungeonPolicy.SelectPlayable(routedEntrance, _ => 0, 3), "队伍实图不在列表拒绝");
        Reject<InvalidDataException>(() => DisasterDungeonPolicy.SelectPlayable(entrance, _ => 0), "入口缺失随机列表拒绝");
        var typeOnly = new DungeonDefinition(3, "dungeon/ordinary.dgn", 1, 1, [], [])
            { DungeonType = DisasterDungeonPolicy.TypeName };
        var idOnly = new DungeonDefinition(DisasterDungeonPolicy.FirstPlayableId, "dungeon/ordinary.dgn", 93, 105, [], []);
        That(DisasterDungeonPolicy.IsEntrance(entrance) && !DisasterDungeonPolicy.IsPlayable(entrance) &&
            DisasterDungeonPolicy.ClearReward(entrance) is null, "入口 5340 不是实图，不发通关礼盒");
        That(DisasterDungeonPolicy.IsPlayable(playable) && DisasterDungeonPolicy.AppliesFatigue(playable, false) &&
            !DisasterDungeonPolicy.AppliesFatigue(playable, true) && !DisasterDungeonPolicy.IsPlayable(typeOnly) &&
            !DisasterDungeonPolicy.IsPlayable(idOnly), "实图必须同时是 5341–5345 和 disaster dungeon，练习不套门槛");
        var reward = DisasterDungeonPolicy.ClearReward(playable);
        That(reward is { MailSender: "灾难领域", Items: [{ ItemId: DisasterDungeonPolicy.CrystalBoxId, Count: 1 }] } &&
            reward.Items[0].Probability == SpecialDungeonClearReward.ProbabilityScale &&
            reward.Roll(_ => 0) is [{ ItemId: DisasterDungeonPolicy.CrystalBoxId, Count: 1 }] &&
            reward.Roll(_ => int.MaxValue) is [{ ItemId: DisasterDungeonPolicy.CrystalBoxId, Count: 1 }],
            "通关固定发 1 个净化的异形结晶礼盒，不抽数量");
        That(DisasterDungeonPolicy.EntryFatigueRejection(0, false, 1, 1) is null &&
            DisasterDungeonPolicy.EntryFatigueRejection(8, true, 1, 1) is null &&
            DisasterDungeonPolicy.EntryFatigueRejection(8, false, null, 8) == "fatigue-owner-missing" &&
            DisasterDungeonPolicy.EntryFatigueRejection(8, false, 7, 8) == "start-fatigue-insufficient:8" &&
            DisasterDungeonPolicy.EntryFatigueRejection(8, false, 8, 7) == "start-fatigue-insufficient:8" &&
            DisasterDungeonPolicy.EntryFatigueRejection(8, false, 8, 8) is null &&
            DisasterDungeonPolicy.EntryFatigueRejection(8, false, 0, 0) == "start-fatigue-insufficient:8",
            "门槛 8：1–7 与库内/可见值取严；无限疲劳跳过；剩余 0 的 fatigue-exhausted 由会话先返回");
        var clamped = new CharacterFatigueState(154, 156, 0, 2, 0, 0).ConsumeOrdinaryRoom(3);
        That(clamped is { Used: 156, Remaining: 0 }, "每房 3 点在最后不足时夹到 0，已用不超过上限");
        That(new CharacterFatigueState(0, 156, 0, 8, 0, 0).ConsumeOrdinaryRoom(3) is { Used: 3, Remaining: 5 },
            "疲劳 8 的首房账本扣 3 后剩 5");
        That(FatigueAccelerationRules.Cost(3, 0) == 3 && FatigueAccelerationRules.Cost(3, 100) == 6,
            "燃烧只把每房基数 3 乘成账本 6");
        Console.WriteLine($"PASS disaster dungeon rules ({Count - before} checks)");
    }

    public static void Run(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters, ItemResources items,
        QuestCatalog quests, SkillLearningCatalog skills)
    {
        int before = Count;
        var catalog = SelfTestCatalogs.Dungeons(pvf);
        That(catalog.Get(5340).Mazes.Count == 0 && catalog.Get(5340).EnterRandomDungeons.SequenceEqual(Enumerable.Range(5341, 5)),
            "5340只读取入口随机列表，不伪造迷宫");
        var hero = new Character(99, 0, "Disaster", 1, new Location(38, 1, 450, 234), null) { Level = 95 };
        That(!DungeonEntryPlan.TryCreate(catalog, hero, DisasterDungeonPolicy.EntranceId, 0, 2, out _, out var entranceReason, draw: _ => 0, quests: quests),
            "5340不能跳过会话分流直接创建迷宫：" + entranceReason);
        for (int id = DisasterDungeonPolicy.FirstPlayableId; id <= DisasterDungeonPolicy.LastPlayableId; id++)
        {
            var definition = catalog.Get(id);
            That(string.Equals(definition.DungeonType?.Trim(), DisasterDungeonPolicy.TypeName, StringComparison.OrdinalIgnoreCase) &&
                definition.MinimumLevel == 93 && definition.BasisLevel == 105 && definition.SpecialClearReward is null &&
                !definition.DeclaresStartFatigue && !definition.DisableFatigueConsumption && !definition.DisableClearReward,
                id + " 是无疲劳标签的灾难实图");
            That(DungeonEntryPlan.TryCreate(catalog, hero, checked((uint)id), 0, 0, out var plan, out var reason, draw: _ => 0, quests: quests) &&
                plan is { StartFatigue: 0, RoomFatigueCost: DisasterDungeonPolicy.RoomFatigue,
                    MinimumEntryFatigue: DisasterDungeonPolicy.EntryMinimumFatigue, UsesFatigue: true, UsesRoomFatigue: true,
                    SpecialClearReward.Items: [{ ItemId: DisasterDungeonPolicy.CrystalBoxId, Count: 1 }] },
                id + " 计划门槛 8、每房 3、礼盒 490012200：" + reason);
        }
        That(DungeonEntryPlan.TryCreate(catalog, hero, 3, 0, 0, out var ordinary, out _) &&
            ordinary is { RoomFatigueCost: 1, MinimumEntryFatigue: 0 } &&
            ordinary.SpecialClearReward?.Items.Any(row => row.ItemId == DisasterDungeonPolicy.CrystalBoxId) != true,
            "普通副本不套灾难领域疲劳和礼盒");
        var box = items.Items.Get(DisasterDungeonPolicy.CrystalBoxId);
        var cards = new DungeonCardRewardPolicy(SelfTestCatalogs.DropRules(pvf), DungeonItemDropCatalog.Create([]),
            items.Items, items.GoldLimits, items.EpicPieces, prices: SelfTestCatalogs.CardPrices(pvf));
        for (int id = 5341; id <= 5345; id++)
        {
            var definition = catalog.Get(id);
            That(DungeonEntryPlan.TryCreate(catalog, hero, checked((uint)id), 0, 2, out var plan, out _, draw: _ => 0, quests: quests) &&
                plan!.SpecialClearReward is null, "默认难度2不在Boss死亡另发结晶礼盒，防止与翻牌重复");
            var card = cards.Prepare(definition, 2, 0, _ => 0, DateTimeOffset.UnixEpoch);
            That(card.Item is { ItemId: 490012200, Count: 1 } && card.RuleId == "disaster-crystal-card-v1" &&
                card.Items().Count() == 1, "难度2翻牌冻结一个真实490012200礼盒");
            var delivery = cards.PlanDelivery(hero with { Inventory = InventorySnapshot.Empty }, [card], hero.Level,
                utcNow: DateTimeOffset.UnixEpoch);
            That(delivery.Inventory.After.Items.Count(item => item.Core.ItemId == 490012200) == 1,
                "结晶礼盒通过既有选牌发放计划进入背包");
        }
        var crystal = items.Items.Get(DisasterDungeonPolicy.CrystalId);
        var booster = items.Boosters.Get(DisasterDungeonPolicy.CrystalBoxId);
        That(box.AttachType.Trim('`', ' ', '\r', '\n') == "[account]" && crystal.AttachType.Trim('`', ' ', '\r', '\n') == "[account]" &&
            box.AccountMaterial is null && box.Expiration.SupportsCreation && box.StackLimit >= 1 &&
            booster.HasFixedOrdinaryRewards && booster.Pools is [{ Draws: 1, Rewards: [{ ItemId: DisasterDungeonPolicy.CrystalId, Weight: 1000, Count: DisasterDungeonPolicy.CrystalCount }] }],
            "490012200 是账号绑定礼盒，打开固定 20 个 10309039");
        var disasterTable = pvf.ReadText("etc/dungeonetc/disasterdungeon.etc");
        var entranceScript = pvf.ReadText("dungeon/harlem/disaster/disaster.dgn");
        That(NextScriptLine(disasterTable, "[minimum enter fatigue]") == "8" &&
            NextScriptLine(disasterTable, "[fatigue by dungeon difficulty]") == "0 1 1 2 2 3" &&
            RewardData(disasterTable, 2) == "`normal` 10000 -1 490012200 `gold` 0 -1 490012200" &&
            !RewardData(disasterTable, 0).Contains("490012200", StringComparison.Ordinal) &&
            !RewardData(disasterTable, 1).Contains("490012200", StringComparison.Ordinal),
            "相关表难度 2 直接引用礼盒，难度 0/1 不引用；入场疲劳 8，难度 2 的每房疲劳对是 3");
        That(NextScriptLine(entranceScript, "[designate dungeon difficulty]") == "2" &&
            NextScriptLine(entranceScript, "[difficulty]") == "2" &&
            NextScriptLine(entranceScript, "[enter random dungeon list]") == "5341 5342 5343 5344 5345",
            "入口脚本指定难度 2 并列出五张实图");
        That(!pvf.ReadText("etc/itemdropinfo_clearreward.etc").Contains("490012200", StringComparison.Ordinal),
            "通用通关奖励表不引用 490012200");
        for (int id = DisasterDungeonPolicy.FirstPlayableId; id <= DisasterDungeonPolicy.LastPlayableId; id++)
            That(!pvf.ReadText(catalog.Get(id).Path).Contains("490012200", StringComparison.Ordinal),
                id + " 的 dgn 本身不写礼盒编号");
        var reward = DisasterDungeonPolicy.ClearReward(catalog.Get(DisasterDungeonPolicy.FirstPlayableId))!;
        var holder = hero with { Inventory = InventorySnapshot.Empty };
        var granted = InventoryRewardGrantService.TryGrantDirectItems(holder,
            reward.Roll(_ => 0).Select(row => new InventoryReward(row.ItemId, row.Count)).ToArray(),
            items.Items, DateTimeOffset.UnixEpoch, allowOverflowMail: true, reason: "special_dungeon_clear_reward",
            overflowMailSender: reward.MailSender, overflowMailText: reward.MailText);
        That(granted.Accepted && granted.Plan!.After.Items.Sum(row => row.Core.ItemId == DisasterDungeonPolicy.CrystalBoxId ? row.Core.Count : 0) == 1,
            "通关通道可以把礼盒放进背包，不在发放时拆成 20 个结晶");
        for (int id = 490012195; id <= 490012199; id++)
        {
            var stone = items.Items.Get(id);
            var stoneBox = items.Boosters.Get(id);
            That(stone.AttachType.Trim('`', ' ', '\r', '\n') == "[account]" && stoneBox.PendingRule is null && stoneBox.Pools.Length > 0,
                "时空石礼盒仍由开箱逻辑持有权重 " + id + " " + stoneBox.PendingRule);
        }

        var incounts = DungeonIncountCatalog.Load(pvf);
        var week = new DateTimeOffset(2026, 10, 22, 7, 0, 0, TimeSpan.FromHours(8));
        var nextWeek = week.AddDays(7);
        var charges = DungeonIncountPolicy.AccountCharges(incounts, 5341, week);
        int period = charges.Single(row => row.Dungeon == 5341).Period;
        That(charges.Length == 6 && charges.Single(row => row.Check).Dungeon == 5341 && charges.All(row => row.Period == period && row.Limit == 10) &&
            charges.Where(row => !row.Check).Select(row => row.Dungeon).Order().SequenceEqual(new[] { 5340, 5342, 5343, 5344, 5345 }) &&
            DungeonIncountPolicy.AccountCharges(incounts, 5340, week).Single(row => row.Check).Dungeon == 5340 &&
            DungeonIncountPolicy.AccountCharges(incounts, 5345, week).Where(row => !row.Check).Select(row => row.Dungeon).Order()
                .SequenceEqual(new[] { 5340, 5341, 5342, 5343, 5344 }),
            "5340–5345 账号每周 10 次互相连带，自身才拦截");
        var root = Path.Combine(Path.GetTempPath(), "disaster-1214-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var repository = new CharacterRepository(Path.Combine(root, "game.db"));
            var owners = new AccountOwners();
            using var owner = owners.Acquire("disaster-account", () => { });
            var first = repository.Create(owner, Role("DisasterA"));
            var second = repository.Create(owner, Role("DisasterB"));
            var countPeriod = DungeonIncountPolicy.Period(week);
            bool accepted = true;
            for (int run = 0; run < 10; run++)
                accepted &= repository.CommitDungeonIncountEntry(owner, run % 2 == 0 ? first : second, 5341, countPeriod, null, null, charges) is not null;
            That(accepted && Enumerable.Range(5340, 6).All(id => repository.LoadAccountDungeonIncount(owner, id, period) == 10) &&
                repository.CommitDungeonIncountEntry(owner, first, 5341, countPeriod, null, null, charges) is null &&
                repository.CommitDungeonIncountEntry(owner, second, 5345, countPeriod, null, null,
                    DungeonIncountPolicy.AccountCharges(incounts, 5345, week)) is null,
                "同账号两角色共享 10 次，第 11 次从另一张实图也被拒");
            using var otherOwner = owners.Acquire("disaster-other", () => { });
            var other = repository.Create(otherOwner, Role("DisasterC"));
            That(repository.CommitDungeonIncountEntry(otherOwner, other, 5341, countPeriod, null, null, charges) is not null &&
                repository.LoadAccountDungeonIncount(otherOwner, 5341, period) == 1 &&
                repository.LoadAccountDungeonIncount(owner, 5341, period) == 10,
                "另一个账号不共享这 10 次");
            // account_dungeon_incount 主键是账号+副本，period 列就是当前周期。跨周提交改写同一行，旧周键因此读成 0。
            var nextCharges = DungeonIncountPolicy.AccountCharges(incounts, 5342, nextWeek);
            int nextPeriod = nextCharges.Single(row => row.Dungeon == 5342).Period;
            That(nextPeriod != period && repository.LoadAccountDungeonIncount(owner, 5342, nextPeriod) == 0 &&
                repository.CommitDungeonIncountEntry(owner, first, 5342, DungeonIncountPolicy.Period(nextWeek), null, null, nextCharges) is not null &&
                repository.LoadAccountDungeonIncount(owner, 5342, nextPeriod) == 1 &&
                repository.LoadAccountDungeonIncount(owner, 5342, period) == 0 &&
                repository.LoadAccountDungeonIncount(owner, 5341, nextPeriod) == 1 &&
                repository.LoadAccountDungeonIncount(otherOwner, 5341, period) == 1,
                "下一周四周键与旧行不符时读为 0，新周可以再进；另一账号的旧周不受影响");

            var fighter = repository.Create(owner, Role("DisasterF"));
            var (plain, plainReason) = LoadingRun(fighter, 0);
            That(plain is not null, "灾难领域扣点实例可建立：" + plainReason);
            if (plain is not null)
            {
                var spent = repository.ConsumeDungeonRoomFatigue(owner, fighter, plain);
                That(spent.Fatigue!.Remaining == fighter.Fatigue!.Remaining - 3 && spent.Revision == fighter.Revision + 1 &&
                    repository.ConsumeDungeonRoomFatigue(owner, fighter, plain).Revision == spent.Revision,
                    "真实账本每房扣 3，同房回执不重复扣");
                plain.CompleteLoading(plain.Snapshot.Identity, Guid.NewGuid());
                That(plain.Snapshot.ConsumedFatigue == 3, "同一实例的应耗统计也是 3");
            }
            var burner = repository.Create(owner, Role("DisasterG"));
            var (burning, burningReason) = LoadingRun(burner, 100);
            That(burning is not null, "燃烧扣点实例可建立：" + burningReason);
            if (burning is not null)
            {
                var spent = repository.ConsumeDungeonRoomFatigue(owner, burner, burning);
                That(spent.Fatigue!.Remaining == burner.Fatigue!.Remaining - 6, "燃烧时账本按 Cost(3) 扣 6，应耗统计不乘这 2");
            }
        }
        finally
        {
            SqliteDatabaseBootstrap.ReleaseRuntimePool();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }

        VerifySessionGate(towns, pvf, characters, items, quests, skills, catalog, incounts);
        Console.WriteLine($"PASS disaster dungeon resources ({Count - before} checks)");
    }

    static void VerifySessionGate(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters, ItemResources items,
        QuestCatalog quests, SkillLearningCatalog skills, DungeonCatalog catalog, DungeonIncountCatalog incounts)
    {
        bool listed = true;
        HashSet<int> done;
        Location position;
        try
        {
            (done, position) = DungeonEntryCostTests.Selection(towns, DisasterDungeonPolicy.FirstPlayableId);
        }
        catch (InvalidOperationException)
        {
            listed = false;
            done = [];
            position = new Location(0, 0, 0, 0);
        }
        bool entranceListed = true;
        try { _ = DungeonEntryCostTests.Selection(towns, DisasterDungeonPolicy.EntranceId); }
        catch (InvalidOperationException) { entranceListed = false; }
        Console.WriteLine($"INFO disaster world map listed 5340={entranceListed} 5341={listed}");
        That(listed, "5341 必须列在世界地图上，否则入场与加载断言没有执行");
        var rooms = SelfTestCatalogs.Rooms(pvf);
        var drops = SelfTestCatalogs.MonsterDrops(pvf);
        var growth = SelfTestCatalogs.Growth(pvf, skills);
        var kills = SelfTestCatalogs.KillExperience(pvf);
        int[] completed = [.. done];
        bool fatigueSevenAsserted = false;
        for (int attempt = 0; attempt < 16 && !fatigueSevenAsserted; attempt++)
        {
            using var probe = Open(towns, characters, skills, growth, quests, incounts, items, position, completed, 7, null);
            var entered = Enter(probe, catalog, rooms, drops, items);
            var rejection = probe.Session.LastFormalDungeonRejection;
            if (entered is null && rejection is not null &&
                rejection.StartsWith("selection-quest-required:", StringComparison.Ordinal) &&
                int.TryParse(rejection["selection-quest-required:".Length..], out int quest) &&
                !completed.Contains(quest))
            {
                completed = [.. completed, quest];
                continue;
            }
            That(entered is null && rejection == "start-fatigue-insufficient:8",
                "补完选图任务后，疲劳 7 必须拒绝为门槛不足，实际 " + rejection);
            fatigueSevenAsserted = true;
        }
        That(fatigueSevenAsserted, "任务门槛重试结束后仍未执行疲劳 7 断言");
        var (entranceDone, entrancePosition) = DungeonEntryCostTests.Selection(towns, DisasterDungeonPolicy.EntranceId);
        foreach (int id in completed) entranceDone.Add(id);
        for (int route = 0; route < 5; route++)
        {
            using var gate = Open(towns, characters, skills, growth, quests, incounts, items, entrancePosition, [.. entranceDone], 8, null);
            int target = route;
            var entered = gate.Session.EnterFormalDungeon(5340, 0, 2, catalog, rooms, drops, items.Items,
                bound => bound == 5 ? target : 0, items.EpicPieces);
            That(entered is not null && entered.Entry.DungeonId == 5341 + route && entered.Entry.Difficulty == 2,
                "5340正式入口选择实图" + (5341 + route) + "，实际 " + gate.Session.LastFormalDungeonRejection);
            var loaded = gate.Session.CompleteFormalDungeonLoading();
            That(loaded is not null && gate.Read().Fatigue!.Remaining == 5,
                "随机入口首房沿实图规则扣3点疲劳");
            var entryPeriod = DungeonIncountPolicy.AccountCharges(incounts, 5341 + route, DateTimeOffset.UtcNow).First().Period;
            That(Enumerable.Range(5340, 6).All(id => gate.Repository.LoadAccountDungeonIncount(gate.Owner, id, entryPeriod) == 1),
                "随机入口按实图提交六行连带账号次数，各计一次");
            VerifyStoneReward(gate, loaded!, route, kills, growth, items);
        }
        for (int remaining = 0; remaining <= 7; remaining++)
        {
            using var gate = Open(towns, characters, skills, growth, quests, incounts, items, position, completed,
                checked((ushort)remaining), null);
            var entered = Enter(gate, catalog, rooms, drops, items);
            string expected = remaining == 0 ? "fatigue-exhausted" : "start-fatigue-insufficient:8";
            That(entered is null && gate.Session.LastFormalDungeonRejection == expected,
                "疲劳 " + remaining + " 必须拒绝为 " + expected + "，实际 " + gate.Session.LastFormalDungeonRejection);
        }
        using (var enough = Open(towns, characters, skills, growth, quests, incounts, items, position, completed, 8, null))
        {
            var opened = Enter(enough, catalog, rooms, drops, items);
            That(opened is not null, "疲劳 8 必须入场：" + enough.Session.LastFormalDungeonRejection);
            var loaded = enough.Session.CompleteFormalDungeonLoading();
            That(loaded is not null && enough.Read().Fatigue!.Remaining == 5 &&
                enough.Session.DungeonSnapshot is { ConsumedFatigue: 3 },
                "首房账本剩 5、应耗统计为 3，实际剩余 " + enough.Read().Fatigue?.Remaining +
                " 应耗 " + enough.Session.DungeonSnapshot?.ConsumedFatigue);
            var here = loaded!.Snapshot.CurrentMap.Position;
            var links = loaded.Entry.Maze.StaticTopology.Links;
            That(loaded.Entry.Maze.StaticTopology.Supported && links is { Count: > 0 },
                "灾难实图必须有可回访的静态连接：" + loaded.Entry.Maze.StaticTopology.UnsupportedReason);
            bool found = false;
            DungeonPoint next = default;
            foreach (var link in links!)
            {
                if (link.First == here) { next = link.Second; found = true; break; }
                if (link.Second == here) { next = link.First; found = true; break; }
            }
            That(found, "首房必须连着另一间房，否则回访断言没有执行");
            ClearBlocking(enough, loaded, kills, growth);
            That(enough.Session.MoveFormalDungeon(checked((byte)next.X), checked((byte)next.Y)) is not null, "离开首房");
            var second = enough.Session.CompleteFormalDungeonLoading();
            That(second is not null && enough.Read().Fatigue!.Remaining == 2 &&
                enough.Session.DungeonSnapshot is { ConsumedFatigue: 6 },
                "相邻新房间再扣 3，实际剩余 " + enough.Read().Fatigue?.Remaining +
                " 应耗 " + enough.Session.DungeonSnapshot?.ConsumedFatigue);
            ClearBlocking(enough, second!, kills, growth);
            That(enough.Session.MoveFormalDungeon(checked((byte)here.X), checked((byte)here.Y)) is not null, "回访首房");
            var back = enough.Session.CompleteFormalDungeonLoading();
            That(back is not null && enough.Read().Fatigue!.Remaining == 2 &&
                enough.Session.DungeonSnapshot is { ConsumedFatigue: 6 },
                "回访首房不重复扣，实际剩余 " + enough.Read().Fatigue?.Remaining +
                " 应耗 " + enough.Session.DungeonSnapshot?.ConsumedFatigue);
        }
        var burningDebug = new GameDebugSettings { PremiumPcRoom = true };
        using (var burning = Open(towns, characters, skills, growth, quests, incounts, items, position, completed, 8, burningDebug))
        {
            burning.Session.ConfigureFatigueAcceleration(100);
            var choice = burning.Session.ChangeFatigueAcceleration(true);
            That(choice is { Active: true, Error: 0 }, "网吧燃烧必须打开，错误 " + choice?.Error);
            var opened = Enter(burning, catalog, rooms, drops, items);
            That(opened is not null, "燃烧且疲劳 8 必须入场：" + burning.Session.LastFormalDungeonRejection);
            var loaded = burning.Session.CompleteFormalDungeonLoading();
            That(loaded is not null && burning.Read().Fatigue!.Remaining == 2 &&
                burning.Session.DungeonSnapshot is { ConsumedFatigue: 3 },
                "燃烧首房账本扣 6、应耗统计仍是 3，实际剩余 " + burning.Read().Fatigue?.Remaining +
                " 应耗 " + burning.Session.DungeonSnapshot?.ConsumedFatigue);
        }
        var infiniteDebug = new GameDebugSettings();
        infiniteDebug.SetInfiniteFatigue("scene-quest", true);
        using var infinite = Open(towns, characters, skills, growth, quests, incounts, items, position, completed, 1, infiniteDebug);
        var infiniteEntry = Enter(infinite, catalog, rooms, drops, items);
        That(infiniteEntry is not null, "无限疲劳剩余 1 必须入场：" + infinite.Session.LastFormalDungeonRejection);
        var infiniteLoaded = infinite.Session.CompleteFormalDungeonLoading();
        That(infiniteLoaded is not null && infinite.Read().Fatigue!.Remaining == 1 &&
            infinite.Session.DungeonSnapshot is { ConsumedFatigue: 3 },
            "无限疲劳必须加载且账本不扣，实际剩余 " + infinite.Read().Fatigue?.Remaining +
            " 应耗 " + infinite.Session.DungeonSnapshot?.ConsumedFatigue);
    }

    static QuestSceneTests.Fixture Open(TownCatalog towns, CharacterCatalog characters, SkillLearningCatalog skills,
        CharacterGrowthCatalog growth, QuestCatalog quests, DungeonIncountCatalog incounts, ItemResources items,
        Location position, int[] completed, ushort fatigue, GameDebugSettings? debug) =>
        new(towns, characters, skills, growth, quests, false, 95, completed, position: position, fatigueRemaining: fatigue,
            incounts: incounts, debugSettings: debug, itemCatalog: items.Items);

    static FormalDungeonReceipt? Enter(QuestSceneTests.Fixture fixture, DungeonCatalog catalog, DungeonRoomCatalog rooms,
        DungeonMonsterDropCatalog drops, ItemResources items) =>
        fixture.Session.EnterFormalDungeon(DisasterDungeonPolicy.FirstPlayableId, 0, 0, catalog, rooms, drops,
            items.Items, _ => 0, items.EpicPieces);

    static void ClearBlocking(QuestSceneTests.Fixture fixture, FormalDungeonReceipt receipt,
        DungeonKillExperienceCatalog kills, CharacterGrowthCatalog growth)
    {
        foreach (var monster in receipt.Monsters.Monsters)
        {
            if (!monster.IsBlocking || !monster.CanReportDeath || !receipt.Snapshot.LivingMonsters.Contains(monster.ObjectKey))
                continue;
            var death = fixture.Session.ObserveFormalDungeonDeath(
                FormalDungeonSessionTests.Observation(receipt, monster), kills, growth);
            That(death.Outcome == DungeonRunOutcome.Accepted, "清除阻挡怪 " + monster.MonsterId + " 后才能离房：" + death.Outcome);
        }
    }

    static string NextScriptLine(string text, string tag)
    {
        var lines = text.Replace("\r", "").Split('\n');
        for (int i = 0; i < lines.Length; i++)
            if (lines[i].Trim().Equals(tag, StringComparison.OrdinalIgnoreCase) && i + 1 < lines.Length)
                return lines[i + 1].Trim();
        return "";
    }

    static string RewardData(string text, int difficulty)
    {
        var lines = text.Replace("\r", "").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (!lines[i].Trim().Equals("[dungeon difficulty]", StringComparison.OrdinalIgnoreCase)) continue;
            if (i + 1 >= lines.Length || lines[i + 1].Trim() != difficulty.ToString()) continue;
            for (int j = i + 2; j < lines.Length; j++)
            {
                var tag = lines[j].Trim();
                if (tag.Equals("[dungeon difficulty]", StringComparison.OrdinalIgnoreCase)) break;
                if (tag.Equals("[data]", StringComparison.OrdinalIgnoreCase) && j + 1 < lines.Length)
                    return lines[j + 1].Trim();
            }
        }
        return "";
    }

    static NewCharacter Role(string name) => new(name, new Location(38, 1, 450, 234), new Location(38, 1, 450, 234))
    {
        DungeonState = CharacterDungeonState.NewOrdinary with { LastAttempt = 1 }
    };

    static (SinglePlayerDungeonRun? Run, string Reason) LoadingRun(Character character, int acceleration)
    {
        var map = new DungeonMap("boss", new(0, 0), 534101, "map/disaster-fixture.map");
        var maze = new DungeonMaze(0, 1, 1, "disaster-fixture", [map.Position], [map.Position], [map], []);
        var entry = new DungeonEntryPlan(character.Id, character.Revision, DisasterDungeonPolicy.FirstPlayableId, 0, byte.MaxValue, 0, maze, map, map)
            { RoomFatigueCost = DisasterDungeonPolicy.RoomFatigue, MinimumEntryFatigue = DisasterDungeonPolicy.EntryMinimumFatigue };
        var source = new DungeonRoomMonster(1, Array.AsReadOnly(new[] { 1, 0, 3, 100, 100, 0, 1, 1 }),
            "[fixed]", "[normal]", "monster/disaster-fixture.mob");
        var room = new DungeonRoomDefinition(map, [source], [], []);
        var plan = new DungeonMonsterPlan(room, [new(0, 20, 1, 3, source)]);
        if (!SinglePlayerDungeonRun.TryCreate(entry, character.DungeonState?.LastAttempt ?? 1, 1, new([plan], []), new DisasterAuthority(), out var run, out var reason))
            return (null, reason);
        if (acceleration > 0) run!.FreezeFatigueAcceleration(acceleration);
        return (run, "");
    }

    sealed class DisasterAuthority : DungeonCombatAuthority { }
}
