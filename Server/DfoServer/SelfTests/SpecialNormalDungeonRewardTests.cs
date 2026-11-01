using System.Text.Json;
using System.Collections.Immutable;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Quests;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;
using Microsoft.Data.Sqlite;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Towns;
using DfoServer.Network.Builders;
using DfoServer.Testing;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class SpecialNormalDungeonRewardTests
{
    public static void Run(TcpTestResources resources)
    {
        int before = Count;
        PreyNormalRewardTests.Run(resources);
        var pvf = resources.Archive;
        var items = resources.ItemResources;
        var dungeons = SelfTestCatalogs.Dungeons(pvf);
        var rooms = SelfTestCatalogs.Rooms(pvf);
        var monsters = SelfTestCatalogs.MonsterDrops(pvf);
        var growth = SelfTestCatalogs.Growth(pvf, resources.Learning);
        var kills = SelfTestCatalogs.KillExperience(pvf);
        var clears = SelfTestCatalogs.ClearExperience(pvf);
        var drops = new DungeonResourceDropPolicy(SelfTestCatalogs.DropRules(pvf), items.DungeonDropItems,
            monsters.Get, items.Items.Get, items.DungeonSpecialDrops);
        var cards = new DungeonCardRewardPolicy(SelfTestCatalogs.DropRules(pvf), items.DungeonDropItems,
            items.Items, items.GoldLimits, items.EpicPieces, random: _ => 0, prices: SelfTestCatalogs.CardPrices(pvf));
        var withoutCards = DfoServer.GameWorld.Dungeons.DungeonSpecialDropCatalog.Create(
            items.DungeonSpecialDrops.IndependentByMonster.Values.SelectMany(rows => rows), items.DungeonSpecialDrops.WorldByLevel.Values);
        var noCardDrops = new DungeonResourceDropPolicy(SelfTestCatalogs.DropRules(pvf), items.DungeonDropItems,
            monsters.Get, items.Items.Get, withoutCards);
        var lukeCards = items.DungeonSpecialDrops.OrdinaryLukeCards;
        That(lukeCards.Count == 24 && lukeCards.Values.Count(row => row.Probabilities[2] == 1000) == 5 &&
            lukeCards[66184].ItemId == 10159168 && !lukeCards.ContainsKey(66468) && !lukeCards.ContainsKey(66467) && !lukeCards.ContainsKey(66473),
            "24卡原映射含最终控制对象，不给小/光/暗变身阶段重复造奖励");
        var checkedCards = new HashSet<int>();
        bool cardFullBagChecked = false;
        var fiendBoss = items.DungeonSpecialDrops.IndependentByMonster[110994].Single(row => row.ListFlag == 1);
        That(fiendBoss.Probabilities.SequenceEqual([40000,40000,40000,40000,40000]) &&
            fiendBoss.Attempts.SequenceEqual([1,2,3,4,1]) && fiendBoss.Items.Any(row => row.ItemId == 401000048 && row.Weight == 900),
            "复核超时空Boss原独立掉落概率、五档次数及原候选权重");
        var independent = drops.Prepare(110994,120,3,2,120).Trials.Single(row => row.ProbabilityDenominator == 1000000 && row.Attempts == 3);
        var independentPlan = new DungeonDropGenerationPlan("独立掉落边界回归", [independent]);
        That(independentPlan.Generate(_ => 0,DateTimeOffset.UtcNow).Length == 3 &&
            independentPlan.Generate(bound => bound - 1,DateTimeOffset.UtcNow).IsEmpty,
            "超时空独立掉落命中三次与全未命中均沿真实计划，不提高生产概率");
        var errors = new List<string>();
        foreach (int id in SpecialNormalDungeonEntryTests.DungeonIds)
        {
            try { Verify(id); }
            catch (Exception error) { errors.Add($"{id}: {error}"); }
        }
        foreach (var scenario in new[] { (Quest: 10019, Route: new[] { 9599 }), (Quest: 2683, Route: new[] { 9595, 9597, 9599, 9603 }),
            (Quest: 2902, Route: new[] { 5101, 5102, 5103, 5104, 5105, 5106 }),
            (Quest: 13200, Route: new[] { 9607, 9607, 9607, 9607, 9607 }),
            (Quest: 8683, Route: new[] { 400000016, 400000016, 400000016, 400000016, 400000016 }) })
        {
            try { VerifyQuest(scenario.Quest, scenario.Route); }
            catch (Exception error) { errors.Add($"quest {scenario.Quest}: {error}"); }
        }
        try { VerifyLukeDailyDrops(false); VerifyLukeDailyDrops(true); }
        catch (Exception error) { errors.Add($"Luke daily drops: {error}"); }
        try { VerifyAntonRoute(); }
        catch (Exception error) { errors.Add($"Anton sequence: {error}"); }
        try { VerifyLukeDayBoundary(); }
        catch (Exception error) { errors.Add($"Luke day: {error}"); }
        try { VerifyPreyRoutes(); VerifyPreyPrerequisite(); }
        catch (Exception error) { errors.Add($"quest 8925: {error}"); }
        foreach (ushort id in new ushort[] { 2903,8672 })
        {
            try { VerifyOtherRaidPrerequisite(id); }
            catch (Exception error) { errors.Add($"quest {id}: {error}"); }
        }
        That(checkedCards.Count == 24 && cardFullBagChecked, "六图实际路线覆盖全部24种卡片源怪：" + string.Join("\n", errors));
        That(errors.Count == 0, "普通特殊副本完整奖励链：" + string.Join("\n", errors));
        Console.WriteLine($"PASS {Count - before} 普通安徒恩、卢克、超时空和普雷正式死亡、拾取、通关与翻牌断言");

        (Location Position, HashSet<int> Completed) Entrance(int id)
        {
            var area = resources.Towns.Areas.Values.First(row => row.DungeonWorldMap is { } world &&
                resources.Towns.DungeonMaps[world].Options.Any(option => option.DungeonId == id));
            var done = new HashSet<int>(area.Quests);
            if (area.AnyQuests.Length != 0) done.Add(area.AnyQuests[0]);
            var options = resources.Towns.DungeonMaps[area.DungeonWorldMap!.Value].Options.Where(option => option.DungeonId == id).ToArray();
            if (!options.Any(option => option.QuestId == -1)) done.Add(options.First(option => !option.InProgress).QuestId);
            done.UnionWith(area.Map(done).DungeonGates.First(gate => gate.Conditions.Length == 0).Quests);
            var bounds = area.Map(done).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains)).Bounds;
            var position = new Location(area.Town, area.Area, (ushort)(bounds.X + bounds.Width / 2), (ushort)(bounds.Y + bounds.Height / 2));
            return (position, done);
        }

        void Verify(int id)
        {
            var definition = dungeons.Get(id);
            var (position, done) = Entrance(id);
            if (definition.IsOrdinaryLuke)
            {
                That(done.Contains(4872),"六白图原选图前置为4872，不借团本2903");
                using var missing = new QuestSceneTests.Fixture(resources.Towns,resources.Characters,resources.Learning,
                    growth,resources.Quests,false,95,done.Where(quest => quest != 4872).ToArray(),position:position);
                string before = JsonSerializer.Serialize(missing.Read());
                That(missing.Session.EnterFormalDungeon((uint)id,0,2,dungeons,rooms,monsters,items.Items,
                    pieces:items.EpicPieces,prepareDrops:value => drops.PrepareEntry(value)) is null &&
                    missing.Session.LastFormalDungeonRejection == "selection-quest-required:4872" &&
                    JsonSerializer.Serialize(missing.Read()) == before,"缺白图前置时拒绝且不扣资产/疲劳");
            }
            using var fixture = new QuestSceneTests.Fixture(resources.Towns, resources.Characters, resources.Learning,
                growth, resources.Quests, false, 95, done.ToArray(), position: position,
                items: definition.OrdinaryEntryTicket is { } ticket ? [(ticket.ItemId, ticket.Count)] : []);
            if (id is >= 5101 and <= 5106)
            {
                fixture.Session.ReturnToRoster();
                fixture.Sql("UPDATE characters SET revive_coins=14");
                // 独立奖励/限币用例明确准备前序；完整真实顺序另由2902六图链验证。
                fixture.Sql("INSERT INTO character_luke_normal_progress(character_id,account,day,progress) SELECT id,account,$p0,$p1 FROM characters",
                    QuestCycle.Day(DateTimeOffset.UtcNow), id - 5101);
                fixture.Session.Select(0,resumeTown:true);
                fixture.Session.MarkReady();
            }
            if (definition.IsOrdinaryAnton) SeedAntonPredecessors(fixture, id, QuestCycle.Day(DateTimeOffset.UtcNow));
            Play(fixture, id);
        }

        void Play(QuestSceneTests.Fixture fixture, int id, uint questId = 0, byte difficulty = 2, Action? beforeSettlement = null)
        {
            var definition = dungeons.Get(id);
            var fee = definition.OrdinaryEntryTicket;
            long CountItem(int itemId) => fixture.Read().Inventory.Items.Where(row => row.Core.ItemId == itemId).Sum(row => (long)row.Core.Count);
            long beforeFee = fee is { } cost ? CountItem(cost.ItemId) : 0;
            var random = new Random(id);
            int draws = 0;
            bool rollbackChecked = false;
            bool lukeRevival = id is >= 5101 and <= 5106 && questId == 0;
            int revivals = 0;
            if (definition.IsOrdinaryLuke)
            {
                string noCardBefore = JsonSerializer.Serialize(fixture.Read());
                That(fixture.Session.EnterFormalDungeon((uint)id, questId, difficulty, dungeons, rooms, monsters, items.Items,
                    pieces: items.EpicPieces, prepareDrops: value => noCardDrops.PrepareEntry(value)) is null &&
                    fixture.Session.LastFormalDungeonRejection == "drop-plan-unsupported" && JsonSerializer.Serialize(fixture.Read()) == noCardBefore,
                    "缺卡片合同只拒绝普通卢克且不扣费，包括首房无怪的机械王座");
            }
            var entry = fixture.Session.EnterFormalDungeon((uint)id, questId, difficulty, dungeons, rooms, monsters, items.Items,
                draw: bound => { draws++; return (id == 400000016 || definition.IsOrdinaryLuke) && bound == 1000000 ? 0 : random.Next(bound); },
                pieces: items.EpicPieces, prepareDrops: value => drops.PrepareEntry(value));
            That(entry is not null, $"{id}真实掉落准备后入场：{fixture.Session.LastFormalDungeonRejection}");
            uint? nextDungeon = definition.IsOrdinaryLuke ? id < 5106 ? (uint)id + 1 : null :
                definition.IsOrdinaryPrey ? PreyNext() : definition.IsOrdinaryAnton ? id switch
                { 9595 => 9596u, 9596 => 9597u, 9597 => 9599u, >= 9599 and <= 9602 => (uint)id + 4,
                    >= 9603 and <= 9606 => 9607u, 9607 => null, _ => (uint)id } : (uint)id;
            That(OrdinaryDungeonContinuation.Target(entry!.Entry,dungeons) == nextDungeon,
                "真实普通入场冻结的续图目标对应六图顺序、当前普雷分支及全部安徒恩变体");
            That(OrdinaryDungeonContinuation.Target(entry.Entry with { IsPractice=true },dungeons) == id &&
                OrdinaryDungeonContinuation.Target(entry.Entry with { IsTutorial=true },dungeons) == id,
                "练习和任务身份不套用普通序列续图");
            uint? PreyNext()
            {
                var admission = entry!.Entry.PreyNormal!;
                var path = admission.Catalog.Route(admission.Branch);
                int index = path.IndexOf(id);
                return index < 4 ? (uint)path[index+1] : null;
            }
            if (fee is { } paid) That(CountItem(paid.ItemId) == beforeFee - paid.Count, "按资源完整数量扣入场材料");
            var route = QuestStoryRouteTests.Path(entry!.Entry.Maze.StaticTopology, entry.Entry.StartMap.Position, entry.Entry.BossMap.Position);
            FormalDungeonDeathResult? last = null;
            foreach (var point in route)
            {
                var loaded = fixture.Session.CompleteFormalDungeonLoading()!;
                That(loaded is not null, $"{id}/{point}正式加载");
                if (lukeRevival && (point == route[0] || point == route[^1]))
                    for (int count = 0; count < 6; count++) ReviveOnce();
                foreach (var monster in loaded!.Monsters.Monsters.Where(row => row.IsBlocking && row.CanReportDeath && !row.IsFriendly)
                    .OrderBy(row => row.IsBoss).ThenBy(row => row.MonsterId == 66184).ToArray())
                {
                    if (definition.IsOrdinaryLuke && lukeCards.TryGetValue(monster.MonsterId, out var cardRow))
                    {
                        var plan = drops.PrepareEntry(entry.Entry)(monster);
                        var trial = plan.Trials.Single(row => row.Items?.Entries.Any(item => item.Template.Id == cardRow.ItemId) == true);
                        That(trial.Threshold == cardRow.Probabilities[2] && trial.ProbabilityDenominator == 1000000 && trial.Attempts == 1 &&
                            !trial.GrowthContractEligible, "普通卢克单卡按批准的百万分母单次抽取，不受成长契约放大");
                        var onlyCard = new DungeonDropGenerationPlan("普通卢克卡片边界测试", [trial]);
                        foreach (int roll in new[] { 0, trial.Threshold - 1, trial.Threshold, 999999 })
                        {
                            var generated = onlyCard.Generate(bound => bound == 1000000 ? roll : 0, DateTimeOffset.UtcNow);
                            That(generated.Length == (roll < trial.Threshold ? 1 : 0) &&
                                generated.All(row => row.Item?.ItemId == cardRow.ItemId && row.Item.Count == 1),
                                "卡片阈值两侧命中/未命中，不改概率制造通过");
                        }
                        foreach (var outside in new[] { entry.Entry with { IsOrdinaryLuke = false },
                            entry.Entry with { DungeonId = 9595 }, entry.Entry with { IsPractice = true }, entry.Entry with { IsTutorial = true } })
                            That(drops.PrepareEntry(outside)(monster).Trials.All(row =>
                                row.Items?.Entries.All(item => item.Template.Id != cardRow.ItemId) != false),
                                "同怪物在非卢克/练习/教学身份不新增卡片奖励");
                        That(noCardDrops.PrepareEntry(entry.Entry with { IsOrdinaryLuke = false })(monster).Trials.Length == plan.Trials.Length - 1,
                            "缺卡片表不改变非卢克原掉落准备");
                        That(items.Items.Get(cardRow.ItemId).IsMonsterCard, "真实item category明确标记monster card，不以副职业分类代替卡片类型");
                        checkedCards.Add(cardRow.ItemId);
                    }
                    var observation = FormalDungeonSessionTests.Observation(loaded, monster);
                    if (id == 5106 && monster.MonsterId == 66468)
                    {
                        string beforeUnknown = JsonSerializer.Serialize(fixture.Read());
                        int beforeDraws = draws;
                        var unknown = fixture.Session.ObserveFormalDungeonDeath(observation with { MonsterKey = 65534 }, kills, growth);
                        That(unknown.Receipt is null && unknown.ClearFact is null && unknown.Drops.Length == 0 && unknown.GainedExperience == 0 &&
                            JsonSerializer.Serialize(fixture.Read()) == beforeUnknown && draws == beforeDraws,
                            "未知召唤对象键不凭静态SUMMON制造正式死亡、掉落或进度");
                    }
                    if (point == entry.Entry.BossMap.Position && monster.IsBoss &&
                        monster.ObjectKey == loaded.Monsters.Monsters.Where(row => row.IsBlocking && row.CanReportDeath && !row.IsFriendly)
                            .OrderBy(row => row.IsBoss).Last().ObjectKey &&
                        fixture.Read().ActiveMainQuests.Any(row => row.ConditionState == 5 &&
                        QuestDungeonClearProgress.CountOncePerDay(resources.Quests.Get(row.QuestId))))
                    {
                        var beforeFailure = JsonSerializer.Serialize(fixture.Read());
                        fixture.Sql("CREATE TRIGGER daily608_fail AFTER INSERT ON character_quest_daily_clear BEGIN SELECT RAISE(ABORT,'daily clear rollback'); END");
                        Reject<SqliteException>(() => fixture.Session.ObserveFormalDungeonDeath(observation,kills,growth),
                            "每日计数写入失败回滚终末死亡、任务、掉落与成长");
                        That(JsonSerializer.Serialize(fixture.Read()) == beforeFailure &&
                            fixture.Query("SELECT count(*) FROM character_quest_daily_clear") is 0L,
                            "失败不占用当天资格或留下部分任务进度，原冻结死亡可重试");
                        fixture.Sql("DROP TRIGGER daily608_fail");
                    }
                    int? frozenDraws = null;
                    if (!rollbackChecked && (id == 400000016 || definition.IsOrdinaryLuke && lukeCards.ContainsKey(monster.MonsterId)))
                    {
                        var beforeFailure = JsonSerializer.Serialize(fixture.Read());
                        fixture.Sql("CREATE TRIGGER reward608_fail AFTER INSERT ON character_dungeon_growth_receipts BEGIN SELECT RAISE(ABORT,'zero reward rollback'); END");
                        Reject<SqliteException>(() => fixture.Session.ObserveFormalDungeonDeath(observation,kills,growth),
                            "零经验死亡写入失败回滚掉落与任务进度");
                        That(JsonSerializer.Serialize(fixture.Read()) == beforeFailure,"失败不能保留半份资产或任务计数");
                        fixture.Sql("DROP TRIGGER reward608_fail");
                        frozenDraws = draws;
                        rollbackChecked = true;
                    }
                    var death = fixture.Session.ObserveFormalDungeonDeath(observation, kills, growth);
                    if (frozenDraws is { } originalDraws) That(draws == originalDraws, "死亡事务失败重试复用原冻结掉落，不重新抽卡");
                    That(death is { Outcome: DungeonRunOutcome.Accepted, Receipt: not null }, $"{id}/{point}/{monster.MonsterId}首次死亡");
                    if (definition.DisableMonsterAndClearExperience)
                        That(death.GainedExperience == 0 && death.ProficiencyGained == 0, "禁经验副本仍提交死亡与掉落但不增加经验或熟练度");
                    if (id == 400000016 && monster.MonsterId == 110994)
                        That(death.Drops.Count(row => !row.IsGold && fiendBoss.Items.Any(item => item.ItemId == row.Core.ItemId)) >= 3,
                            "边界抽样命中的三件Boss独立掉落全部进入真实地面账本");
                    if (definition.IsOrdinaryLuke && lukeCards.TryGetValue(monster.MonsterId, out var declaredCard))
                        That(death.Drops.Count(row => !row.IsGold && row.Core.ItemId == declaredCard.ItemId && row.Core.Count == 1) == 1,
                            "命中的专属卡片进入原正式死亡地面账本，恰好一张");
                    if (id == 5106 && monster.MonsterId == 66468)
                        That(death.ClearFact is null && fixture.Session.Phase == SessionPhase.Dungeon,
                            "模拟小卢克死亡不越过仍存活的66184控制对象提前终点；不代表自然退场wire已验收");
                    if (id == 5106 && monster.MonsterId == 66184)
                        That(death.ClearFact is not null && death.Drops.Any(row => !row.IsGold && row.Core.ItemId == 10159168),
                            "控制对象真实正式报告形成最终通关，保留其原卡片奖励载体");
                    last = death;
                    var after = JsonSerializer.Serialize(fixture.Read());
                    int beforeDuplicateDraws = draws;
                    fixture.Session.ObserveFormalDungeonDeath(observation, kills, growth);
                    That(JsonSerializer.Serialize(fixture.Read()) == after && draws == beforeDuplicateDraws, "重复死亡不增加资产和任务进度，也不重新抽卡");
                    foreach (var ground in death.Drops)
                    {
                        if (!cardFullBagChecked && !ground.IsGold && ground.Core.ItemId is >= 10159145 and <= 10159168)
                        {
                            var template = items.Items.Get(ground.Core.ItemId);
                            var inventory = fixture.Read().Inventory;
                            var range = ItemSlotBoundService.Range(ItemKind.Profession, inventory.Expansion);
                            var slots = Enumerable.Range(range.Start, range.End - range.Start + 1)
                                .Concat(Enumerable.Range(ItemSlotBoundService.QuickSlotStart, ItemSlotBoundService.QuickSlotEnd - ItemSlotBoundService.QuickSlotStart + 1))
                                .Select(index => new InventorySlot(InventoryListType.Main, checked((short)index)))
                                .Where(slot => inventory.Find(slot) is null).ToHashSet();
                            That(fixture.Session.MutateInventory(current => new(true, 0, new(current.Inventory,
                                current.Inventory with { Items = current.Inventory.Items.AddRange(slots.Select(slot =>
                                    new InventoryItem(0, slot, ground.Core with { Value = template.StackLimit }))) }, "luke_card_full_fixture")),
                                requireTown: false).Accepted, "通过原库存事务建立满副职业背包与快捷栏夹具");
                            string fullBefore = JsonSerializer.Serialize(fixture.Read());
                            var denied = fixture.Session.PickFormalDungeonItem(ground.Reservation.GroundKey, items.Items, items.EpicPieces, items.GoldLimits);
                            That(denied.Outcome != DungeonRunOutcome.Accepted && JsonSerializer.Serialize(fixture.Read()) == fullBefore,
                                "卡片满包拾取失败不消费地面物或修改资产");
                            That(fixture.Session.MutateInventory(current => new(true, 0, new(current.Inventory,
                                current.Inventory with { Items = current.Inventory.Items.Where(item => !slots.Contains(item.Position)).ToImmutableArray() },
                                "luke_card_free_fixture")), requireTown: false).Accepted, "原库存事务释放夹具占位后可重试同一张卡");
                            var pickupBefore = JsonSerializer.Serialize(fixture.Read());
                            fixture.Sql("CREATE TRIGGER luke_card_pickup_fail AFTER INSERT ON inventory_audit_log BEGIN SELECT RAISE(ABORT,'card pickup rollback'); END");
                            Reject<SqliteException>(() => fixture.Session.PickFormalDungeonItem(ground.Reservation.GroundKey, items.Items, items.EpicPieces, items.GoldLimits),
                                "卡片拾取晚期SQL失败回滚实际资产");
                            That(JsonSerializer.Serialize(fixture.Read()) == pickupBefore, "拾取SQL失败保留原卡与原库存");
                            fixture.Sql("DROP TRIGGER luke_card_pickup_fail");
                            cardFullBagChecked = true;
                        }
                        var pickup = fixture.Session.PickFormalDungeonItem(ground.Reservation.GroundKey, items.Items, items.EpicPieces, items.GoldLimits);
                        That(pickup is { Outcome: DungeonRunOutcome.Accepted, Commit: not null }, "实际地面对象拾取入库");
                        after = JsonSerializer.Serialize(fixture.Read());
                        fixture.Session.PickFormalDungeonItem(ground.Reservation.GroundKey, items.Items, items.EpicPieces, items.GoldLimits);
                        That(JsonSerializer.Serialize(fixture.Read()) == after, "重复拾取不增发资产");
                    }
                }
                if (point == entry.Entry.BossMap.Position) break;
                var next = route[Array.IndexOf(route, point) + 1];
                That(fixture.Session.MoveFormalDungeon((byte)next.X, (byte)next.Y) is not null, $"{id}实际清房后过图");
            }
            That(last?.ClearFact is not null && fixture.Session.Phase == SessionPhase.Settlement, $"{id}真实终点生成通关事实");
            beforeSettlement?.Invoke();
            var progressBefore = fixture.Repository.LoadOrdinaryLukeProgress(fixture.Owner, fixture.Read().Id);
            var preyBefore = fixture.Repository.LoadPreyNormalProgress(fixture.Owner,fixture.Read().Id);
            var antonBefore = fixture.Repository.LoadAntonNormalProgress(fixture.Owner,fixture.Read().Id);
            if (definition.IsOrdinaryLuke || definition.IsOrdinaryPrey || definition.IsOrdinaryAnton)
            {
                fixture.Sql("CREATE TRIGGER luke_progress_fail BEFORE INSERT ON character_dungeon_card_rewards BEGIN SELECT RAISE(ABORT,'luke progress rollback'); END");
                string before = JsonSerializer.Serialize(fixture.Read());
                Reject<SqliteException>(() => fixture.Session.SettleFormalDungeon(fixture.Read().Id, 99, 99, [6, 0, 8], dungeons, clears, growth, cards),
                    "进度已规划但末段卡牌账本SQL失败");
                That(fixture.Repository.LoadOrdinaryLukeProgress(fixture.Owner, fixture.Read().Id) == progressBefore &&
                    fixture.Repository.LoadPreyNormalProgress(fixture.Owner,fixture.Read().Id) == preyBefore &&
                    fixture.Repository.LoadAntonNormalProgress(fixture.Owner,fixture.Read().Id) == antonBefore &&
                    JsonSerializer.Serialize(fixture.Read()) == before, "晚失败回滚经验、进度与冻结牌，不提前安装内存");
                fixture.Sql("DROP TRIGGER luke_progress_fail");
            }
            var result = fixture.Session.SettleFormalDungeon(fixture.Read().Id, 99, 99, [6, 0, 8], dungeons, clears, growth, cards);
            That(result is { Outcome: DungeonRunOutcome.Accepted, Card: not null }, $"{id}通关冻结奖励");
            if (definition.IsOrdinaryAnton)
                That(result.Card!.Item is { ItemId: 10093974, Value: 3 }, "安徒恩专用翻牌为资源声明的3个魔能石");
            if (definition.IsNormalFiendwar)
                That(result.Card!.Item is { ItemId: 490007849, Value: 180 } && result.GainedExperience == 0 && result.ProficiencyGained == 0 &&
                    result.Options is { PaidDisabled: true, BlackDiamond: null }, "普通超时空180个材料、禁经验、无黄金牌和黑钻加牌");
            var progressAfter = fixture.Repository.LoadOrdinaryLukeProgress(fixture.Owner, fixture.Read().Id);
            var preyAfter = fixture.Repository.LoadPreyNormalProgress(fixture.Owner,fixture.Read().Id);
            var antonAfter = fixture.Repository.LoadAntonNormalProgress(fixture.Owner,fixture.Read().Id);
            if (definition.IsOrdinaryAnton)
                That(antonAfter == antonBefore.Clear(entry!.Entry.AntonNormal!, (uint)id), "普通安徒恩进度与实际结算同事务提交");
            if (definition.IsOrdinaryPrey)
                That(preyAfter == preyBefore.Clear(entry!.Entry.PreyNormal!, (uint)id),"普通普雷真实结算与日进度同事务提交");
            if (definition.IsOrdinaryLuke)
                That(progressAfter == progressBefore.Clear(entry!.Entry.OrdinaryLukeDay!.Value, (uint)id), "进度与实际清图结算同事务提交");
            var settled = JsonSerializer.Serialize(fixture.Read());
            fixture.Session.SettleFormalDungeon(fixture.Read().Id, 99, 99, [6, 0, 8], dungeons, clears, growth, cards);
            That(JsonSerializer.Serialize(fixture.Read()) == settled &&
                fixture.Repository.LoadOrdinaryLukeProgress(fixture.Owner, fixture.Read().Id) == progressAfter &&
                fixture.Repository.LoadPreyNormalProgress(fixture.Owner,fixture.Read().Id) == preyAfter, "重复结算不增加经验、资产和进度");
            fixture.Session.AdvanceFormalDungeonCards(false);
            fixture.Session.AdvanceFormalDungeonCards(true);
            That(fixture.Session.SelectFormalDungeonFreeCard(0, 0) is not null, $"{id}免费牌实际领取");
            settled = JsonSerializer.Serialize(fixture.Read());
            fixture.Session.SelectFormalDungeonFreeCard(0, 0);
            That(JsonSerializer.Serialize(fixture.Read()) == settled, "重复翻牌不重复发放");
            fixture.Session.SetCharacterGold(fixture.Read().Name, 1000000);
            var goldBefore = fixture.Read().Inventory.Gold;
            if (definition.DisablePaidCard)
                That(fixture.Session.SelectFormalDungeonPaidCard(1, 0, cards) is null && fixture.Read().Inventory.Gold == goldBefore,
                    "禁止黄金牌的副本拒绝付费领取且不扣费");
            else
                That(fixture.Session.SelectFormalDungeonPaidCard(1, 0, cards) is not null &&
                    fixture.Read().Inventory.Gold == goldBefore - result.Options!.Price, $"{id}付费牌按冻结价格扣费");
            settled = JsonSerializer.Serialize(fixture.Read());
            fixture.Session.SelectFormalDungeonPaidCard(1, 0, cards);
            That(JsonSerializer.Serialize(fixture.Read()) == settled, "重复付费牌不扣费、不重抽");
            if (lukeRevival)
            {
                That(revivals == 12 && fixture.Session.ReportFormalCharacterDeath() is not null,
                    $"{id}跨房保留十二次复活后仍可报告死亡");
                string before = JsonSerializer.Serialize(fixture.Read());
                for (int repeat = 0; repeat < 2; repeat++)
                    That(fixture.Session.ReviveFormalCharacter(fixture.Read().Id) is { Accepted:false } &&
                        JsonSerializer.Serialize(fixture.Read()) == before,
                        $"{id}第十三次及重复复活被拒绝，余额仍有两币且资产不变");
            }
            That(fixture.Session.ReturnFromFormalDungeon() is not null, $"{id}领奖后回城");
            if (lukeRevival)
            {
                That(fixture.Session.EnterFormalDungeon((uint)id,0,2,dungeons,rooms,monsters,items.Items,
                    pieces:items.EpicPieces,prepareDrops:value => drops.PrepareEntry(value)) is not null &&
                    fixture.Session.CompleteFormalDungeonLoading() is not null,
                    $"{id}回城后新Run重新入场");
                ReviveOnce();
                That(fixture.Read().Inventory.ReviveCoins == 1,"新Run不继承旧复活次数，真实余额继续扣一币");
                That(fixture.Session.ReturnFromFormalDungeon() is not null,"新Run复活后正常回城");
            }

            void ReviveOnce()
            {
                That(fixture.Session.ReportFormalCharacterDeath() is not null,"已加载房间报告真实角色死亡");
                string before = JsonSerializer.Serialize(fixture.Read());
                int coins = fixture.Read().Inventory.ReviveCoins;
                if (revivals == 11)
                {
                    fixture.Sql("CREATE TRIGGER luke_revive_fail BEFORE INSERT ON currency_audit_log BEGIN SELECT RAISE(ABORT,'luke revive rollback'); END");
                    Reject<SqliteException>(() => fixture.Session.ReviveFormalCharacter(fixture.Read().Id),"第十二次复活晚审计失败");
                    That(JsonSerializer.Serialize(fixture.Read()) == before,"失败不扣币、不提前占用第十二次资格");
                    fixture.Sql("DROP TRIGGER luke_revive_fail");
                }
                That(fixture.Session.ReviveFormalCharacter(fixture.Read().Id) is { Accepted:true } &&
                    fixture.Read().Inventory.ReviveCoins == coins-1,"合法复活实际扣一币");
                revivals++;
                string saved = JsonSerializer.Serialize(fixture.Read());
                That(fixture.Session.ReviveFormalCharacter(fixture.Read().Id) is null &&
                    JsonSerializer.Serialize(fixture.Read()) == saved,"活角色重复复活不扣币或占次数");
            }
        }

        // 单图奖励夹具显式准备前序；自然零进度六图链由VerifyAntonRoute独立验证。
        void SeedAntonPredecessors(QuestSceneTests.Fixture fixture, int id, int day)
        {
            int branch = id is >= 9599 and <= 9602 ? id - 9598 : id is >= 9603 and <= 9606 ? id - 9602 : 1;
            int index = dungeons.AntonNormalSequence.Routes[branch - 1].IndexOf(id);
            fixture.Sql("INSERT INTO character_anton_normal_progress(character_id,account,day,branch,progress) SELECT id,account,$p0,$p1,$p2 FROM characters WHERE 1 " +
                "ON CONFLICT(character_id) DO UPDATE SET day=excluded.day,branch=excluded.branch,progress=excluded.progress",
                day, branch, index);
        }

        void VerifyAntonRoute()
        {
            var catalog = dungeons.AntonNormalSequence;
            for (int branch = 1; branch <= 4; branch++)
            {
                var admission = new AntonNormalAdmission(20261025, branch, catalog);
                var state = default(AntonNormalProgress);
                foreach (int id in catalog.Routes[branch - 1])
                {
                    That(state.Allows(admission, (uint)id), "安徒恩四分支仅真实前序开放下一项");
                    state = state.Clear(admission, (uint)id);
                    if (state.Completed == 4)
                        That(catalog.Routes[branch - 1].Take(4).All(previous => !state.Allows(admission, (uint)previous)) &&
                            state.Allows(admission, (uint)catalog.Routes[branch - 1][4]),
                            "能源结算后按资源关闭前三图及能源，保留当前火山入口");
                }
                That(state.Completed == 6 && state.Clear(admission, 9607) == state &&
                    state.Clear(admission with { Day = 20261024 }, 9607) == state &&
                    state.Information(20261026) == new AntonNormalSequenceInfo(0, 0, 1),
                    "终点重复不增量、迟到旧日不覆盖、跨日从首图重新开放");
                That(AntonNormalSequencePackets.Information(state.Information(20261025))[13] == 0 &&
                    AntonNormalSequencePackets.Information(state.Information(20261026))[13] == 1,
                    "四分支完成心脏耗尽当日讨伐资格，跨日恢复，不能恒定开放或恒定压暗卡片");
                That(!state.Allows(admission with { Day = 20261024 }, 9595) &&
                    !state.Allows(admission with { Branch = branch % 4 + 1 }, 9595), "拒绝回拨日与其他分支资格");
            }
            var (position, done) = Entrance(9595);
            foreach (int id in catalog.Routes[0]) done.UnionWith(Entrance(id).Completed);
            var clock = new DfoServer.Testing.Inventory.DailyBoosterTestClock(DateTimeOffset.Parse("2026-10-25T12:00:00Z"));
            using var fixture = new QuestSceneTests.Fixture(resources.Towns, resources.Characters, resources.Learning,
                growth, resources.Quests, false, 95, done.ToArray(), position:position, timeProvider:clock, items:[(3341,256)]);
            string initial = JsonSerializer.Serialize(fixture.Read());
            That(fixture.Session.CaptureAntonNormalSequence()!.Info == new AntonNormalSequenceInfo(0,0,1), "新角色序列28只开放黑雾之源");
            foreach (uint id in new uint[] {9596,9597,9599,9603,9607})
                That(fixture.Session.EnterFormalDungeon(id,0,2,dungeons,rooms,monsters,items.Items,pieces:items.EpicPieces,
                    prepareDrops:value => drops.PrepareEntry(value)) is null &&
                    fixture.Session.LastFormalDungeonRejection == "anton-normal-progress-locked" && JsonSerializer.Serialize(fixture.Read()) == initial,
                    "安徒恩跳图拒绝不消费材料、不扣疲劳或预留实例");
            fixture.Sql("CREATE TRIGGER anton_admit_fail BEFORE UPDATE OF revision ON characters BEGIN SELECT RAISE(ABORT,'anton admission rollback'); END");
            Reject<SqliteException>(() => fixture.Session.EnterFormalDungeon(9595,0,2,dungeons,rooms,monsters,items.Items,
                pieces:items.EpicPieces,prepareDrops:value => drops.PrepareEntry(value)), "入场进度与费用事务晚失败");
            That(fixture.Query("SELECT count(*) FROM character_anton_normal_progress") is 0L &&
                JsonSerializer.Serialize(fixture.Read()) == initial, "首次准入失败不留下进度或费用");
            fixture.Sql("DROP TRIGGER anton_admit_fail");
            for (int index = 0; index < 6; index++)
            {
                Play(fixture,catalog.Routes[0][index]);
                var receipt = fixture.Session.CaptureAntonNormalSequence()!;
                That(receipt.Info == new AntonNormalSequenceInfo((byte)(index+1),(1u << (index+1))-1,1), "真实六图逐次提交并开放后继");
                var body = AntonNormalSequencePackets.Information(receipt.Info);
                That(body[13] == (index < 5 ? 1 : 0) && body.AsSpan(14).SequenceEqual(new byte[2]),
                    "真实结算后的可继续资格随六图进度投影，周次数不伪造");
                fixture.Session.ReturnToRoster(); fixture.Session.Select(0,resumeTown:true); fixture.Session.MarkReady();
                That(!fixture.Session.CanPublishAntonNormalSequence(receipt) && fixture.Session.CaptureAntonNormalSequence()!.Info == receipt.Info,
                    "重登读取持久进度并拒绝旧会话投影");
            }
            var old = fixture.Session.CaptureAntonNormalSequence()!;
            clock.Advance(TimeSpan.FromDays(1));
            That(!fixture.Session.CanPublishAntonNormalSequence(old) && fixture.Session.CaptureAntonNormalSequence()!.Info == new AntonNormalSequenceInfo(0,0,1),
                "跨06:00废弃旧投影和上一日通关位");
            That(fixture.Session.EnterFormalDungeon(9596,0,2,dungeons,rooms,monsters,items.Items,pieces:items.EpicPieces,
                prepareDrops:value => drops.PrepareEntry(value)) is null,"次日不能借昨日完成跳过黑雾之源");
        }

        void VerifyPreyRoutes()
        {
            var box = items.Boosters.Get(490011518);
            That(box.Available && box.Pools is [{ Draws:1 }] && box.Pools[0].Rewards.SequenceEqual(new BoosterReward[]
                { new(10158964,4500,8),new(10158964,3300,9),new(10158964,2100,10),new(10158964,100,20) }),
                "每日宝箱按原45%/33%/21%/1%给8/9/10/20个精炼时空石，不改为均匀区间");
            var covered = new HashSet<int>();
            for (int branch = 1; branch <= 6; branch++)
            {
                var clock = new DfoServer.Testing.Inventory.DailyBoosterTestClock(
                    new DateTimeOffset(2026,10,25,12,0,0,TimeSpan.FromHours(8)));
                while (DfoServer.GameWorld.Dungeons.PreyNormalCatalog.BranchForDay(QuestCycle.Day(clock.GetUtcNow())) != branch)
                    clock.Advance(TimeSpan.FromDays(1));
                var route = dungeons.PreyNormal.Route(branch);
                var (position, done) = Entrance(route[0]);
                done.Add(8925);
                using var fixture = new QuestSceneTests.Fixture(resources.Towns,resources.Characters,resources.Learning,
                    growth,resources.Quests,false,95,done.ToArray(),position:position,timeProvider:clock);
                That(fixture.Session.ChangeQuest(8926,QuestLifecycleAction.Accept,resources.Quests,
                    items.Items,items.GoldLimits,items.EpicPieces)?.Commit is not null,"接取原普通普雷每日考验");
                for (int index = 0; index < 5; index++)
                {
                    int id = route[index]; covered.Add(id);
                    var definition = dungeons.Get(id);
                    That(definition.IsOrdinaryPrey && definition.AllowsDifficulty(2,false) && !definition.AllowsDifficulty(0,false) &&
                        definition.StartFatigue == 10,"十五张普通普雷仅接通原声明难度2与入场10疲劳");
                    var information = fixture.Session.CapturePreyNormalSequence()!;
                    That(information.Info == new PreyNormalSequenceInfo((byte)index,(1u << index)-1,(uint)branch),
                        "同一角色六条五图路线从无进度开始，不预造通关");
                    string before = JsonSerializer.Serialize(fixture.Read());
                    if (index < 4)
                        That(fixture.Session.EnterFormalDungeon((uint)route[index+1],0,2,dungeons,rooms,monsters,items.Items,
                            pieces:items.EpicPieces,prepareDrops:value => drops.PrepareEntry(value)) is null &&
                            fixture.Session.LastFormalDungeonRejection == "prey-normal-progress-locked" &&
                            JsonSerializer.Serialize(fixture.Read()) == before,"跳关拒绝且资产不变");
                    Play(fixture,id,id == 400000046 ? 8926u : 0);
                    That(fixture.Repository.LoadPreyNormalProgress(fixture.Owner,fixture.Read().Id).Completed == index+1,
                        "真实死亡、拾取、结算与翻牌后推进当日一图");
                }
                var finish = fixture.Session.FinishQuest(8926,ushort.MaxValue,resources.Quests,
                    SelfTestCatalogs.QuestRewardParameters(pvf),items.Items,items.GoldLimits,items.EpicPieces,growth);
                That(finish.Outcome == QuestFinishOutcome.Committed &&
                    fixture.Read().Inventory.Items.Any(row => row.Core.ItemId == 490011518 && row.Core.Count == 1),
                    "本体真实通关后领取每日原资源宝箱");
                string claimed = JsonSerializer.Serialize(fixture.Read());
                fixture.Session.FinishQuest(8926,ushort.MaxValue,resources.Quests,
                    SelfTestCatalogs.QuestRewardParameters(pvf),items.Items,items.GoldLimits,items.EpicPieces,growth);
                That(JsonSerializer.Serialize(fixture.Read()) == claimed,"每日奖励重复领取不增发");
                fixture.Session.ReturnToRoster(); fixture.Session.Select(0,resumeTown:true); fixture.Session.MarkReady();
                That(fixture.Session.CapturePreyNormalSequence()!.Info.Stage == 5,"重选恢复当日五图进度");
                clock.Advance(TimeSpan.FromDays(1));
                That(fixture.Session.CapturePreyNormalSequence()!.Info.Stage == 0,"跨业务日清除旧日显示");
            }
            That(covered.Count == 15,"六条路线的真实奖励链覆盖全部十五张图");
        }

        void VerifyPreyPrerequisite()
        {
            var quest = resources.Quests.Get(8925);
            var raidQuest = resources.Quests.Get(8935);
            That(resources.Quests.All.Where(row => row.Grade == "[mission]" && row.SubType == 6 &&
                QuestDungeonClearProgress.TryTarget(row,out _)).Select(row => row.Id).Order().SequenceEqual([8683,8925]),
                "本PVF普通使命sub6形状只有已支持每日累计8683与普通考验8925");
            var (position, done) = Entrance(400000046);
            done.UnionWith(quest.Prerequisites);
            // 真实角色先经灰色墓地的城镇前置再传送到普通等候区；夹具保留返回NPC的原准入。
            var gray = resources.Towns.Areas[((byte)60,(byte)1)];
            done.UnionWith(gray.Quests);
            if (gray.AnyQuests.Length != 0) done.Add(gray.AnyQuests[0]);
            That(!done.Contains(8925) && !done.Contains(8935),"普雷夹具不预完成普通考验或攻坚入场任务");
            using var fixture = new QuestSceneTests.Fixture(resources.Towns,resources.Characters,resources.Learning,
                growth,resources.Quests,false,95,done.ToArray(),position:position);
            That(QuestEligibility.Evaluate(fixture.Read(),quest,resources.Quests) is { Eligible:true,Visible:true } &&
                !QuestEligibility.Evaluate(fixture.Read(),raidQuest,resources.Quests).Eligible,"普雷普通考验可见但攻坚入场仍受前置限制");
            That(fixture.Session.ChangeQuest(8925,QuestLifecycleAction.Accept,resources.Quests,
                items.Items,items.GoldLimits,items.EpicPieces)?.Commit is not null,"接取真实亚瑟的考验");
            var accepted = fixture.Read().ActiveMainQuests.Single(row => row.QuestId == 8925);
            That(accepted.ConditionState == 1,"原任务要求一次真实通关");
            That(DungeonEntryPlan.TryCreate(dungeons,fixture.Read(),400000046,8925,2,out var explicitPlan,out _,quest:quest,quests:resources.Quests) &&
                explicitPlan is { IsOrdinary:true,RouteQuestId:0 } && explicitPlan.QuestAcceptanceId == accepted.AcceptanceId &&
                DungeonEntryPlan.TryCreate(dungeons,fixture.Read(),400000046,0,2,out var ordinaryPlan,out _,quests:resources.Quests) &&
                explicitPlan.BossMap == ordinaryPlan!.BossMap && explicitPlan.EntryItem == ordinaryPlan.EntryItem &&
                explicitPlan.StartFatigue == ordinaryPlan.StartFatigue,"考验显式任务保留普通路线原终点、费用与接取身份");
            foreach (var invalid in new[] { quest with { SubType=11 }, quest with { Grade="[auto daily mission]" },
                quest with { IntData=[400000016,-1] }, quest with { IntData=[400000046,4] },
                quest with { IntData=[400000046,-1,5] } })
                That(!DungeonEntryPlan.TryCreate(dungeons,fixture.Read(),400000046,8925,2,out _,out _,quest:invalid,quests:resources.Quests),
                    "未知子型/专用每日、目标不符、难度不足和未确认累计次数不能借普通考验路线");
            That(!DungeonEntryPlan.TryCreate(dungeons,fixture.Read() with { ActiveMainQuests=[accepted with { AcceptanceId=Guid.Empty }] },
                400000046,8925,2,out _,out _,quest:quest,quests:resources.Quests),"空接取身份不能进入考验路线");
            var foreign = quest with { Id=29999 };
            That(!DungeonEntryPlan.TryCreate(dungeons,fixture.Read() with { ActiveMainQuests=[accepted with { QuestId=29999 }] },
                400000046,29999,2,out _,out _,quest:foreign,quests:resources.Quests),"未知任务即使复制普雷单次考验形状也不开放路线");
            int preyDay = QuestCycle.Day(DateTimeOffset.UtcNow);
            foreach (int id in dungeons.PreyNormal.Route(DfoServer.GameWorld.Dungeons.PreyNormalCatalog.BranchForDay(preyDay)).Take(4))
                Play(fixture,id);
            Play(fixture,400000046,8925,2);
            That(fixture.Read().ActiveMainQuests.Single(row => row.QuestId == 8925) is { ConditionState:0 } active &&
                active.AcceptanceId == accepted.AcceptanceId,"普通普雷终末沿原接取身份推进考验");
            var result = fixture.Session.FinishQuest(8925,ushort.MaxValue,resources.Quests,
                SelfTestCatalogs.QuestRewardParameters(pvf),items.Items,items.GoldLimits,items.EpicPieces,growth);
            That(result.Outcome == QuestFinishOutcome.Committed && fixture.Read().CompletedQuests.Contains(8925) &&
                fixture.Read().Inventory.Items.Any(row => row.Core.ItemId == 10308559 && row.Core.Count == 8) &&
                QuestEligibility.Evaluate(fixture.Read(),raidQuest,resources.Quests) is { Eligible:true,Visible:true },
                "真实通关交付后开放前往夜之摩天楼任务：" + result.Reason);
            That(QuestEligibility.Candidates(fixture.Read(),resources.Quests).Contains(8935) &&
                fixture.Session.ChangeQuest(8935,QuestLifecycleAction.Accept,resources.Quests,
                    items.Items,items.GoldLimits,items.EpicPieces)?.Commit is not null,"后继进入真实任务候选并可接取");
            That(fixture.Session.Travel(new(60,1,1140,90,5,118,0,255,8001)),
                "沿真实亚瑟NPC反向传送返回灰色墓地："+fixture.Session.TravelReason);
            var meeting = fixture.Session.ChangeQuest(8935,QuestLifecycleAction.Condition,resources.Quests,
                items.Items,items.GoldLimits,items.EpicPieces,new(null,0,0));
            That(meeting?.Commit is not null,"在实际NPC8001场景会面："+meeting?.Reason);
            var raidFinish = fixture.Session.FinishQuest(8935,ushort.MaxValue,resources.Quests,
                SelfTestCatalogs.QuestRewardParameters(pvf),items.Items,items.GoldLimits,items.EpicPieces,growth);
            That(raidFinish.Outcome == QuestFinishOutcome.Committed && fixture.Read().CompletedQuests.Contains(8935),
                "普雷入场任务沿普通会面交付，不预写完成标志：" + raidFinish.Reason);
            var saved = JsonSerializer.Serialize(fixture.Read());
            That(fixture.Session.FinishQuest(8935,ushort.MaxValue,resources.Quests,
                SelfTestCatalogs.QuestRewardParameters(pvf),items.Items,items.GoldLimits,items.EpicPieces,growth).Outcome == QuestFinishOutcome.AlreadyCompleted &&
                JsonSerializer.Serialize(fixture.Read()) == saved,"重复交付不发第二份奖励");
            fixture.Session.ReturnToRoster(); fixture.Session.Select(0,resumeTown:true); fixture.Session.MarkReady();
            That(fixture.Read().CompletedQuests.Contains(8925) && fixture.Read().CompletedQuests.Contains(8935),"重选持久保存考验和入场任务完成");
        }

        void VerifyOtherRaidPrerequisite(ushort id)
        {
            var quest = resources.Quests.Get(id);
            var done = resources.Towns.AccessQuestIds.Concat(quest.Prerequisites).Where(value => value != id).ToHashSet();
            var position = resources.Towns.InitialReturn;
            if (id == 2903)
            {
                var area = resources.Towns.Areas.Values.First(row => row.Map(done).Npcs.Any(npc => npc.Id == 347));
                var bounds = area.Map(done).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains)).Bounds;
                position = new(area.Town,area.Area,(ushort)(bounds.X+bounds.Width/2),(ushort)(bounds.Y+bounds.Height/2));
            }
            using var fixture = new QuestSceneTests.Fixture(resources.Towns,resources.Characters,resources.Learning,
                growth,resources.Quests,false,(byte)quest.MinimumLevel,done.ToArray(),position:position,
                items:id == 2903 ? [(10158964,5)] : []);
            That(QuestEligibility.Evaluate(fixture.Read(),quest,resources.Quests) is { Eligible:true,Visible:true } &&
                fixture.Session.ChangeQuest(id,QuestLifecycleAction.Accept,resources.Quests,
                    items.Items,items.GoldLimits,items.EpicPieces)?.Commit is not null,$"攻坚入场任务{id}按原前置真实接取");
            if (id == 2903)
                That(fixture.Session.ChangeQuest(id,QuestLifecycleAction.Condition,resources.Quests,
                    items.Items,items.GoldLimits,items.EpicPieces,new(0x20,0,0))?.Commit is not null,"卢克入场材料足额并在NPC347所在地图会面");
            var result = fixture.Session.FinishQuest(id,ushort.MaxValue,resources.Quests,
                SelfTestCatalogs.QuestRewardParameters(pvf),items.Items,items.GoldLimits,items.EpicPieces,growth);
            That(result.Outcome == QuestFinishOutcome.Committed && fixture.Read().CompletedQuests.Contains(id),
                $"攻坚入场任务{id}实际交付：{result.Reason}");
            if (id == 2903) That(result.Receipt!.Commit.Consumed is [{ ItemId:10158964,Count:5 }],"卢克资格实际消费原资源材料");
            fixture.Session.ReturnToRoster(); fixture.Session.Select(0,resumeTown:true); fixture.Session.MarkReady();
            That(fixture.Read().CompletedQuests.Contains(id),"入场任务完成记录重选后仍在");
        }

        void VerifyLukeDailyDrops(bool firstEmpty)
        {
            var quest = resources.Quests.Get(2902);
            var (position, done) = Entrance(5101);
            done.UnionWith(quest.Prerequisites);
            var clock = new DfoServer.Testing.Inventory.DailyBoosterTestClock(DateTimeOffset.UtcNow);
            using var fixture = new QuestSceneTests.Fixture(resources.Towns, resources.Characters, resources.Learning,
                growth, resources.Quests, false, 95, done.ToArray(), position: position, timeProvider: clock);
            int firstDay = QuestCycle.Day(clock.GetUtcNow());
            var first = KillTarget(firstEmpty, rollback: true);
            int source = first.Monster.MonsterId;
            int cardId = lukeCards[source].ItemId;
            int questItem = quest.Definition!.Drops.First(row => row.DungeonId == 5101 && row.TemplateId == source).ItemId;
            var firstEvent = fixture.Repository.LoadOrdinaryLukeDropEvent(fixture.Owner, fixture.Read().Id, firstDay, source);
            That(firstEvent is not null && (firstEmpty ? first.Death.Drops.IsEmpty :
                first.Death.Drops.Any(row => !row.IsGold && row.Core.ItemId == cardId)),
                "首次合法击杀不论是否抽中都消费源怪日资格，命中时保留原卡片/正常地面计划");
            That(fixture.Session.ReturnFromFormalDungeon() is not null, "未拾取离场使用原Run地面过期路径，不补发");
            long CardCount() => fixture.Read().Inventory.Items.Where(row => row.Core.ItemId == cardId).Sum(row => (long)row.Core.Count);
            That(CardCount() == 0, "离场未拾取卡片没有暗中入包或补发");
            fixture.Session.ReturnToRoster(); fixture.Session.Select(0, resumeTown: true); fixture.Session.MarkReady();
            That(fixture.Session.ChangeQuest(2902, QuestLifecycleAction.Accept, resources.Quests,
                items.Items, items.GoldLimits, items.EpicPieces)?.Commit is not null, "消耗普通首掉资格后仍可独立接取2902");
            long QuestCount() => fixture.Read().Inventory.Items.Where(row => row.Core.ItemId == questItem).Sum(row => (long)row.Core.Count);
            long beforeQuest = QuestCount();
            var repeated = KillTarget(false, rollback: false);
            That(repeated.Monster.MonsterId == source && repeated.Death.Drops.IsEmpty && repeated.MillionDraws == 0 &&
                fixture.Repository.LoadOrdinaryLukeDropEvent(fixture.Owner, fixture.Read().Id, firstDay, source) == firstEvent,
                "重登/新Run同源怪不重抽卡片根源等正常掉落，不改首次资格事实");
            That(QuestCount() > beforeQuest && repeated.Death.Reward is not null,
                "已用普通首掉资格仍独立推进2902材料及原击杀经验合同");
            That(fixture.Session.ReturnFromFormalDungeon() is not null, "重复击杀仍可正常离场");
            clock.Advance(TimeSpan.FromDays(1));
            int nextDay = QuestCycle.Day(clock.GetUtcNow());
            var next = KillTarget(false, rollback: false, crossDay: true);
            That(next.Death.Drops.Any(row => !row.IsGold && row.Core.ItemId == cardId) && next.MillionDraws > 0 &&
                fixture.Repository.LoadOrdinaryLukeDropEvent(fixture.Owner, fixture.Read().Id, nextDay, source) is not null &&
                fixture.Repository.LoadOrdinaryLukeDropEvent(fixture.Owner, fixture.Read().Id, firstDay, source) == firstEvent &&
                fixture.Repository.LoadOrdinaryLukeDropEvent(fixture.Owner, fixture.Read().Id, QuestCycle.Day(clock.GetUtcNow()), source) is null,
                "新入场日恢复资格；运行中再次跨日仍记入场日，不污染新日或覆盖旧日");
            var cardGround = next.Death.Drops.Single(row => !row.IsGold && row.Core.ItemId == cardId);
            That(fixture.Session.PickFormalDungeonItem(cardGround.Reservation.GroundKey, items.Items, items.EpicPieces, items.GoldLimits)
                is { Outcome: DungeonRunOutcome.Accepted } && CardCount() == 1, "次日真实卡片仍沿原地面拾取到账");
            fixture.Session.PickFormalDungeonItem(cardGround.Reservation.GroundKey, items.Items, items.EpicPieces, items.GoldLimits);
            That(CardCount() == 1, "日资格不破坏原拾取幂等");
            That(fixture.Session.ReturnFromFormalDungeon() is not null, "次日副本正常收尾");

            (DungeonPlannedMonster Monster, FormalDungeonDeathResult Death, int MillionDraws) KillTarget(bool empty, bool rollback, bool crossDay = false)
            {
                int draws = 0, million = 0;
                var entered = fixture.Session.EnterFormalDungeon(5101, 0, 2, dungeons, rooms, monsters, items.Items,
                    draw: bound => { draws++; if (bound == 1000000) million++; return 0; }, pieces: items.EpicPieces,
                    prepareDrops: entry =>
                    {
                        var prepare = drops.PrepareEntry(entry);
                        return monster =>
                        {
                            var plan = prepare(monster);
                            if (plan.OrdinaryLukeDailySource is { } dailySource)
                                That(plan.WithGrowthContract(10).OrdinaryLukeDailySource == dailySource,
                                    "成长契约计划复制保留日资格源怪，不放大抽样次数");
                            // 合成零阈值只验证未命中也占资格，不改变生产资源概率；仍预留真实对象容量。
                            return empty && plan.OrdinaryLukeDailySource is not null
                                ? new DungeonDropGenerationPlan(plan.ResourceSource, plan.Trials.Select(trial => trial with { Threshold = 0 }),
                                    plan.ReferenceQuality, plan.Amplification) { OrdinaryLukeDailySource = plan.OrdinaryLukeDailySource }
                                : plan;
                        };
                    });
                That(entered is not null, "首掉回归沿正式首图入口：" + fixture.Session.LastFormalDungeonRejection);
                var route = QuestStoryRouteTests.Path(entered!.Entry.Maze.StaticTopology, entered.Entry.StartMap.Position, entered.Entry.BossMap.Position);
                foreach (var point in route)
                {
                    var loaded = fixture.Session.CompleteFormalDungeonLoading()!;
                    foreach (var monster in loaded.Monsters.Monsters.Where(row => row.IsBlocking && row.CanReportDeath && !row.IsFriendly)
                        .OrderBy(row => row.IsBoss))
                    {
                        bool target = lukeCards.ContainsKey(monster.MonsterId) && quest.Definition!.Drops.Any(row =>
                            row.DungeonId == 5101 && row.TemplateId == monster.MonsterId);
                        var observation = FormalDungeonSessionTests.Observation(loaded, monster);
                        if (target && crossDay) clock.Advance(TimeSpan.FromDays(1));
                        int millionBefore = million;
                        if (target && rollback)
                        {
                            string before = JsonSerializer.Serialize(fixture.Read());
                            fixture.Sql($"CREATE TRIGGER luke_daily_claim_fail AFTER INSERT ON character_luke_normal_drop_claims WHEN NEW.source_monster_id={monster.MonsterId} BEGIN SELECT RAISE(ABORT,'luke daily claim rollback'); END");
                            Reject<SqliteException>(() => fixture.Session.ObserveFormalDungeonDeath(observation, kills, growth), "首击资格写入后晚SQL失败");
                            int frozenDraws = draws;
                            That(JsonSerializer.Serialize(fixture.Read()) == before &&
                                fixture.Repository.LoadOrdinaryLukeDropEvent(fixture.Owner, fixture.Read().Id, entered.Entry.OrdinaryLukeDay!.Value, monster.MonsterId) is null,
                                "失败不扣首掉资格、不发布资产，保留原pending随机结果");
                            fixture.Sql("DROP TRIGGER luke_daily_claim_fail");
                            var retry = fixture.Session.ObserveFormalDungeonDeath(observation, kills, growth);
                            That(draws == frozenDraws && retry.Outcome == DungeonRunOutcome.Accepted, "SQL失败重试不重新抽样");
                            VerifyDuplicate(retry);
                            return (monster, retry, million - millionBefore);
                        }
                        var death = fixture.Session.ObserveFormalDungeonDeath(observation, kills, growth);
                        That(death.Outcome == DungeonRunOutcome.Accepted, "日资格不阻止合法死亡/经验/通关事实");
                        if (target)
                        {
                            VerifyDuplicate(death);
                            return (monster, death, million - millionBefore);
                        }

                        void VerifyDuplicate(FormalDungeonDeathResult original)
                        {
                            string after = JsonSerializer.Serialize(fixture.Read());
                            int afterDraws = draws;
                            fixture.Session.ObserveFormalDungeonDeath(observation, kills, growth);
                            That(JsonSerializer.Serialize(fixture.Read()) == after && draws == afterDraws,
                                "重复死亡通知不重抽、不发放或改写日资格");
                        }
                    }
                    if (point == route[^1]) break;
                    var next = route[Array.IndexOf(route, point) + 1];
                    That(fixture.Session.MoveFormalDungeon((byte)next.X, (byte)next.Y) is not null, "首掉回归正常清房过图");
                }
                throw new InvalidOperationException("资源首图没有找到同时属于24卡及2902的源怪");
            }
        }

        void VerifyLukeDayBoundary()
        {
            var (position, done) = Entrance(5101);
            var clock = new DfoServer.Testing.Inventory.DailyBoosterTestClock(
                new DateTimeOffset(2026, 10, 26, 5, 59, 59, TimeSpan.FromHours(8)));
            using var fixture = new QuestSceneTests.Fixture(resources.Towns, resources.Characters, resources.Learning,
                growth, resources.Quests, false, 95, done.ToArray(), position: position, timeProvider: clock);
            // questId=0的Play含限币回归；本例仍给足真实复活币，不跳过其检查。
            fixture.Session.ReturnToRoster(); fixture.Sql("UPDATE characters SET revive_coins=14");
            fixture.Session.Select(0, resumeTown: true); fixture.Session.MarkReady();
            string initial = JsonSerializer.Serialize(fixture.Read());
            DungeonEntryPlan? preparedAtBoundary = null;
            That(fixture.Session.EnterFormalDungeon(5101, 0, 2, dungeons, rooms, monsters, items.Items,
                pieces: items.EpicPieces, prepareDrops: value =>
                {
                    preparedAtBoundary = value;
                    clock.Advance(TimeSpan.FromSeconds(1));
                    return drops.PrepareEntry(value);
                }) is null && JsonSerializer.Serialize(fixture.Read()) == initial &&
                fixture.Query("SELECT count(*) FROM character_luke_normal_progress") is 0L,
                "准备完成到安装之间跨06:00，原准备拒绝且不扣疲劳/费用或占attempt");
            clock.Advance(TimeSpan.FromSeconds(-1));
            var role = fixture.Read();
            var forged = preparedAtBoundary! with { IsOrdinaryLuke = false, CharacterRevision = checked(role.Revision + 1) };
            var identity = new DungeonRunIdentity(role.Id, 0, role.DungeonState!.LastAttempt + 1, Guid.NewGuid(), 1);
            Reject<InvalidDataException>(() => fixture.Repository.CommitDungeonEntry(fixture.Owner, role,
                role.DungeonState.NextObjectKey + 1, new(null, 0, identity, forged.StartMap.Id)
                    { DungeonId = 5101, OrdinaryLukeEntry = forged }), "内部charge也不能用day借非卢克资源身份通过");
            That(JsonSerializer.Serialize(fixture.Read()) == initial && fixture.Query("SELECT count(*) FROM character_luke_normal_progress") is 0L,
                "内部错资源准入拒绝不占日、不改角色");
            fixture.Sql("CREATE TRIGGER luke_entry_fail BEFORE UPDATE OF revision ON characters BEGIN SELECT RAISE(ABORT,'luke admission rollback'); END");
            Reject<SqliteException>(() => fixture.Session.EnterFormalDungeon(5101, 0, 2, dungeons, rooms, monsters, items.Items,
                pieces: items.EpicPieces, prepareDrops: value => drops.PrepareEntry(value)), "准入日写入后原费用事务晚失败");
            That(JsonSerializer.Serialize(fixture.Read()) == initial && fixture.Query("SELECT count(*) FROM character_luke_normal_progress") is 0L,
                "首次准入失败不留下进度日、不扣费");
            fixture.Sql("DROP TRIGGER luke_entry_fail");
            Play(fixture, 5101, beforeSettlement: () =>
            {
                var sequence = fixture.Session.CaptureOrdinaryLukeSequence(42)!;
                clock.Advance(TimeSpan.FromSeconds(1));
                That(!fixture.Session.CanPublishOrdinaryLukeSequence(sequence) &&
                    fixture.Session.CaptureOrdinaryLukeSequence(42)!.Info == new OrdinaryLukeSequenceInfo(0, 0),
                    "06:00之后丢弃旧日待发序列，查询只显示新日进度");
                // 防御性构造已存在更新日行，验证迟到旧Run仍可结算但不得覆盖它；不冒充自然双Run并发。
                fixture.Sql("UPDATE character_luke_normal_progress SET day=20261026,progress=0");
            });
            // Play末尾再次入场验证复活次数重置；跨日该新Run已写新日progress0，但失败退出不完成首图。
            var progress = fixture.Repository.LoadOrdinaryLukeProgress(fixture.Owner, fixture.Read().Id);
            That(progress == new OrdinaryLukeProgress(20261026, 0), "跨日旧Run结算归旧日，新Run从首图零进度开始且退出不推进");
            string after = JsonSerializer.Serialize(fixture.Read());
            That(fixture.Session.EnterFormalDungeon(5102, 0, 2, dungeons, rooms, monsters, items.Items,
                pieces: items.EpicPieces, prepareDrops: value => drops.PrepareEntry(value)) is null &&
                JsonSerializer.Serialize(fixture.Read()) == after, "06:00新日不能继承旧首图解锁");
            clock.Advance(TimeSpan.FromSeconds(-1));
            That(fixture.Session.EnterFormalDungeon(5101, 0, 2, dungeons, rooms, monsters, items.Items,
                pieces: items.EpicPieces, prepareDrops: value => drops.PrepareEntry(value)) is null &&
                JsonSerializer.Serialize(fixture.Read()) == after, "墙钟回拨拒绝入场，不恢复旧日额度/进度");
        }

        void VerifyQuest(int questId, int[] route)
        {
            var quest = resources.Quests.Get(questId);
            bool dailyClear = QuestDungeonClearProgress.CountOncePerDay(quest);
            var (position, done) = Entrance(route[0]);
            done.UnionWith(quest.Prerequisites);
            foreach (int id in route) done.UnionWith(Entrance(id).Completed);
            That(!done.Contains(questId), "夹具不预先完成被测任务");
            var clock = new DfoServer.Testing.Inventory.DailyBoosterTestClock(DateTimeOffset.UtcNow);
            using var fixture = new QuestSceneTests.Fixture(resources.Towns, resources.Characters, resources.Learning,
                growth, resources.Quests, false, 95, done.ToArray(), position: position, timeProvider: clock,
                items: (dailyClear ? route.Append(route[0]) : route).Select(dungeons.Get).Where(row => row.OrdinaryEntryTicket is not null)
                    .Select(row => row.OrdinaryEntryTicket!.Value).GroupBy(row => row.ItemId)
                    .Select(group => (group.Key, group.Sum(row => row.Count))).ToArray());
            var parameters = SelfTestCatalogs.QuestRewardParameters(pvf);
            var accept = fixture.Session.ChangeQuest((ushort)questId, QuestLifecycleAction.Accept, resources.Quests,
                items.Items, items.GoldLimits, items.EpicPieces);
            That(accept?.Commit is not null, $"{questId}原资源接取：{QuestEligibility.Evaluate(fixture.Read(), quest, resources.Quests).Reason}");
            if (questId == 10019)
                That(fixture.Session.ChangeQuest(2683,QuestLifecycleAction.Accept,resources.Quests,
                    items.Items,items.GoldLimits,items.EpicPieces)?.Commit is not null,"神经细胞与高级塔尔坦材料任务可同时接取");
            var original = fixture.Read().ActiveMainQuests.Single(row => row.QuestId == questId);
            bool daily = QuestCycle.NormalDungeonMission(quest);
            That(original.ConditionState == (daily ? 1 : 5), "寻物或五次通关采用对应的初始条件");
            ushort selector = daily ? ushort.MaxValue : (ushort)0;
            QuestFinishResult Finish() => fixture.Session.FinishQuest((ushort)questId, selector, resources.Quests,
                parameters, items.Items, items.GoldLimits, items.EpicPieces, growth);
            That(Finish().Outcome == QuestFinishOutcome.Rejected, "条件未达成不能提前领奖");
            for (int index = 0; index < route.Length; index++)
            {
                if (dailyClear && index > 0)
                {
                    fixture.Session.ReturnToRoster();
                    fixture.Sql($"UPDATE character_quest_daily_clear SET day={QuestCycle.Day(DateTimeOffset.UtcNow.AddDays(-1))} WHERE quest_id={questId}; UPDATE character_fatigue SET used=0,remaining=maximum");
                    fixture.Session.Select(0, resumeTown: true); fixture.Session.MarkReady();
                }
                if (questId == 2902)
                {
                    var progress = fixture.Repository.LoadOrdinaryLukeProgress(fixture.Owner, fixture.Read().Id);
                    That(progress.Completed == index, "同一角色真实六图链按前序解锁，不预置进度");
                    var sequence = fixture.Session.CaptureOrdinaryLukeSequence(42);
                    That(sequence is not null && sequence.Info == new OrdinaryLukeSequenceInfo((byte)index, (1u << index) - 1) &&
                        fixture.Session.CanPublishOrdinaryLukeSequence(sequence) && fixture.Session.CaptureOrdinaryLukeSequence(203) is null,
                        "查询自己的真实前置；其它序列不得借用卢克记录");
                    if (index < 5)
                    {
                        string before = JsonSerializer.Serialize(fixture.Read());
                        That(fixture.Session.EnterFormalDungeon((uint)route[index + 1], 0, 2, dungeons, rooms, monsters, items.Items,
                            pieces: items.EpicPieces, prepareDrops: value => drops.PrepareEntry(value)) is null &&
                            fixture.Session.LastFormalDungeonRejection == "luke-normal-progress-locked" && JsonSerializer.Serialize(fixture.Read()) == before,
                            "跳图拒绝且不扣费/疲劳、不改尝试号");
                    }
                }
                if (dungeons.Get(route[index]).IsOrdinaryAnton) SeedAntonPredecessors(fixture, route[index], QuestCycle.Day(clock.GetUtcNow()));
                Play(fixture, route[index], (uint)questId);
                if (questId == 2902)
                {
                    That(fixture.Repository.LoadOrdinaryLukeProgress(fixture.Owner, fixture.Read().Id).Completed == index + 1,
                        "每个实际终点仅推进一图");
                    var sequence = fixture.Session.CaptureOrdinaryLukeSequence(42)!;
                    That(sequence.Info == new OrdinaryLukeSequenceInfo((byte)(index + 1), (1u << (index + 1)) - 1),
                        "实际结算提交后投影新进度");
                    fixture.Session.ReturnToRoster(); fixture.Session.Select(0, resumeTown: true); fixture.Session.MarkReady();
                    That(!fixture.Session.CanPublishOrdinaryLukeSequence(sequence) &&
                        fixture.Session.CaptureOrdinaryLukeSequence(42)!.Info == sequence.Info, "重登丢弃旧回执，重新读取保留进度");
                    That(fixture.Repository.LoadOrdinaryLukeProgress(fixture.Owner, fixture.Read().Id).Completed == index + 1,
                        "重登保留已提交每日进度");
                }
                var active = fixture.Read().ActiveMainQuests.Single(row => row.QuestId == questId);
                That(active.AcceptanceId == original.AcceptanceId, "多次入场保留同一真实接取身份");
                if (!daily) That(active.ConditionState == 4 - index, "每次真实通关只减一次，重放不额外计数");
                if (dailyClear && index == 0)
                {
                    fixture.Session.ReturnToRoster(); fixture.Session.Select(0, resumeTown: true); fixture.Session.MarkReady();
                    if (dungeons.Get(route[index]).IsOrdinaryAnton) SeedAntonPredecessors(fixture, route[index], QuestCycle.Day(clock.GetUtcNow()));
                    Play(fixture,route[index], (uint)questId);
                    That(fixture.Read().ActiveMainQuests.Single(row => row.QuestId == questId).ConditionState == 4,
                        "重登后同日第二个真实实例仍不累计；下一轮只把账本日期设为前日来复核跨日资格");
                }
                if (index < route.Length - 1) That(Finish().Outcome == QuestFinishOutcome.Rejected, "尚缺材料或次数仍拒绝交付");
            }
            That(fixture.Read().ActiveMainQuests.Single(row => row.QuestId == questId).ConditionState == 0,
                $"{questId}真实击杀掉落或完整通关后条件归零");
            if (daily)
            {
                That(!QuestCycle.NormalDungeonMission(quest with { Id = 29999 }) &&
                    !QuestCycle.NormalDungeonMission(quest with { Grade = "[auto daily mission]" }),
                    "本次普通材料任务支持不误放未知任务或自动每日分配");
                for (int at = 0; at < quest.IntData.Count; at += 2)
                    That(fixture.Read().Inventory.Items.Where(row => row.Core.ItemId == quest.IntData[at]).Sum(row => row.Core.Count) == quest.IntData[at + 1],
                        "任务材料按原掉落行和持有上限获得完整数量");
            }
            if (daily)
                That(DungeonEntryPlan.TryCreate(dungeons,fixture.Read(),(uint)route[^1],(uint)questId,2,out var ready,out _,
                    quest:quest,quests:resources.Quests) && ready is { IsOrdinary:true },
                    "材料收齐但未交付仍可携带原任务身份进入普通路线");
            if (questId == 10019)
                That(fixture.Read().ActiveMainQuests.Any(row => row.QuestId == 2683 && row.ConditionState == 1) &&
                    fixture.Read().Inventory.Items.Any(row => row.Core.ItemId == 10093923),
                    "同一次真实能源通关也推进另一材料任务，但缺少其余材料不能提前达成");
            var rewardPairs = daily ? quest.Rewards : quest.Definition!.SelectRewards.Take(2).ToArray();
            var expectedRewards = Enumerable.Range(0, rewardPairs.Count / 2)
                .Select(at => (Item: rewardPairs[at * 2].Value, Count: rewardPairs[at * 2 + 1].Value)).ToArray();
            long Held(int itemId) => fixture.Read().Inventory.Items.Where(row => row.Core.ItemId == itemId).Sum(row => (long)row.Core.Count);
            var beforeRewards = expectedRewards.Select(row => Held(row.Item)).ToArray();
            var finish = Finish();
            That(finish.Outcome == QuestFinishOutcome.Committed, $"{questId}完成及奖励原子提交：{finish}");
            var completed = fixture.Read();
            if (daily)
                That(quest.IntData.Where((_,index) => index % 2 == 0).All(id => completed.Inventory.Items.All(row => row.Core.ItemId != id)),
                    "任务交付实际消耗完整材料，不只写完成标志");
            var rewardIds = expectedRewards.Select(row => row.Item).ToArray();
            for (int at = 0; at < expectedRewards.Length; at++)
                That(Held(expectedRewards[at].Item) == beforeRewards[at] + expectedRewards[at].Count,
                    "固定或选择奖励按资源完整数量增加，不以已有同名物品代替领奖验收");
            string saved = JsonSerializer.Serialize(completed);
            Finish();
            That(JsonSerializer.Serialize(fixture.Read()) == saved, "重复交付不二次消耗材料或发任务奖励");
            fixture.Session.ReturnToRoster();
            fixture.Session.Select(0, resumeTown: true);
            fixture.Session.MarkReady();
            That(fixture.Read().CompletedQuests.Contains(questId) && rewardIds.All(id => fixture.Read().Inventory.Items.Any(row => row.Core.ItemId == id)),
                "重登保留任务完成与奖励资产");
            That(fixture.Session.ChangeQuest((ushort)questId, QuestLifecycleAction.Accept, resources.Quests,
                items.Items, items.GoldLimits, items.EpicPieces)?.Commit is null, "同日或一次性任务完成后不能重接刷奖");
            if (daily)
            {
                That(fixture.Read().QuestDailyClaims.ContainsKey(questId), "每日交付记录真实领取周期");
                That(fixture.Session.AdvanceQuestCycle(DateTimeOffset.UtcNow.AddDays(1)) is not null &&
                    !fixture.Read().CompletedQuests.Contains(questId) &&
                    QuestEligibility.Evaluate(fixture.Read(), quest, resources.Quests) is { Eligible: true, Visible: true },
                    "下一游戏日按既有周期恢复资格，永久前置与奖励保留");
            }
        }
    }
}
