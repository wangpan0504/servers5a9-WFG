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
using DfoServer.Testing;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class SpecialNormalDungeonRewardTests
{
    public static void Run(TcpTestResources resources)
    {
        int before = Count;
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
        try { VerifyPreyPrerequisite(); }
        catch (Exception error) { errors.Add($"quest 8925: {error}"); }
        foreach (ushort id in new ushort[] { 2903,8672 })
        {
            try { VerifyOtherRaidPrerequisite(id); }
            catch (Exception error) { errors.Add($"quest {id}: {error}"); }
        }
        That(checkedCards.Count == 24 && cardFullBagChecked, "六图实际路线覆盖全部24种卡片源怪");
        That(errors.Count == 0, "普通特殊副本完整奖励链：" + string.Join("\n", errors));
        Console.WriteLine($"PASS {Count - before} 普通安徒恩、卢克和超时空正式死亡、拾取、通关与翻牌断言");

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
                fixture.Session.Select(0,resumeTown:true);
                fixture.Session.MarkReady();
            }
            Play(fixture, id);
        }

        void Play(QuestSceneTests.Fixture fixture, int id, uint questId = 0, byte difficulty = 2)
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
                    .OrderBy(row => row.IsBoss).ToArray())
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
            var result = fixture.Session.SettleFormalDungeon(fixture.Read().Id, 99, 99, [6, 0, 8], dungeons, clears, growth, cards);
            That(result is { Outcome: DungeonRunOutcome.Accepted, Card: not null }, $"{id}通关冻结奖励");
            if (definition.IsOrdinaryAnton)
                That(result.Card!.Item is { ItemId: 10093974, Value: 3 }, "安徒恩专用翻牌为资源声明的3个魔能石");
            if (definition.IsNormalFiendwar)
                That(result.Card!.Item is { ItemId: 490007849, Value: 180 } && result.GainedExperience == 0 && result.ProficiencyGained == 0 &&
                    result.Options is { PaidDisabled: true, BlackDiamond: null }, "普通超时空180个材料、禁经验、无黄金牌和黑钻加牌");
            var settled = JsonSerializer.Serialize(fixture.Read());
            fixture.Session.SettleFormalDungeon(fixture.Read().Id, 99, 99, [6, 0, 8], dungeons, clears, growth, cards);
            That(JsonSerializer.Serialize(fixture.Read()) == settled, "重复结算不增加经验和资产");
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
            That(DungeonEntryPlan.TryCreate(dungeons,fixture.Read(),400000046,8925,0,out var explicitPlan,out _,quest:quest,quests:resources.Quests) &&
                explicitPlan is { IsOrdinary:true,RouteQuestId:0 } && explicitPlan.QuestAcceptanceId == accepted.AcceptanceId &&
                DungeonEntryPlan.TryCreate(dungeons,fixture.Read(),400000046,0,0,out var ordinaryPlan,out _,quests:resources.Quests) &&
                explicitPlan.BossMap == ordinaryPlan!.BossMap && explicitPlan.EntryItem == ordinaryPlan.EntryItem &&
                explicitPlan.StartFatigue == ordinaryPlan.StartFatigue,"考验显式任务保留普通路线原终点、费用与接取身份");
            foreach (var invalid in new[] { quest with { SubType=11 }, quest with { Grade="[auto daily mission]" },
                quest with { IntData=[400000016,-1] }, quest with { IntData=[400000046,4] },
                quest with { IntData=[400000046,-1,5] } })
                That(!DungeonEntryPlan.TryCreate(dungeons,fixture.Read(),400000046,8925,0,out _,out _,quest:invalid,quests:resources.Quests),
                    "未知子型/专用每日、目标不符、难度不足和未确认累计次数不能借普通考验路线");
            That(!DungeonEntryPlan.TryCreate(dungeons,fixture.Read() with { ActiveMainQuests=[accepted with { AcceptanceId=Guid.Empty }] },
                400000046,8925,0,out _,out _,quest:quest,quests:resources.Quests),"空接取身份不能进入考验路线");
            var foreign = quest with { Id=29999 };
            That(!DungeonEntryPlan.TryCreate(dungeons,fixture.Read() with { ActiveMainQuests=[accepted with { QuestId=29999 }] },
                400000046,29999,0,out _,out _,quest:foreign,quests:resources.Quests),"未知任务即使复制普雷单次考验形状也不开放路线");
            Play(fixture,400000046,8925,0);
            That(fixture.Read().ActiveMainQuests.Single(row => row.QuestId == 8925) is { ConditionState:0 } active &&
                active.AcceptanceId == accepted.AcceptanceId,"普通普雷终末沿原接取身份推进考验");
            var result = fixture.Session.FinishQuest(8925,ushort.MaxValue,resources.Quests,
                SelfTestCatalogs.QuestRewardParameters(pvf),items.Items,items.GoldLimits,items.EpicPieces,growth);
            That(result.Outcome == QuestFinishOutcome.Committed && fixture.Read().CompletedQuests.Contains(8925) &&
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

        void VerifyQuest(int questId, int[] route)
        {
            var quest = resources.Quests.Get(questId);
            bool dailyClear = QuestDungeonClearProgress.CountOncePerDay(quest);
            var (position, done) = Entrance(route[0]);
            done.UnionWith(quest.Prerequisites);
            foreach (int id in route) done.UnionWith(Entrance(id).Completed);
            That(!done.Contains(questId), "夹具不预先完成被测任务");
            using var fixture = new QuestSceneTests.Fixture(resources.Towns, resources.Characters, resources.Learning,
                growth, resources.Quests, false, 95, done.ToArray(), position: position,
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
                Play(fixture, route[index], (uint)questId);
                var active = fixture.Read().ActiveMainQuests.Single(row => row.QuestId == questId);
                That(active.AcceptanceId == original.AcceptanceId, "多次入场保留同一真实接取身份");
                if (!daily) That(active.ConditionState == 4 - index, "每次真实通关只减一次，重放不额外计数");
                if (dailyClear && index == 0)
                {
                    fixture.Session.ReturnToRoster(); fixture.Session.Select(0, resumeTown: true); fixture.Session.MarkReady();
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
