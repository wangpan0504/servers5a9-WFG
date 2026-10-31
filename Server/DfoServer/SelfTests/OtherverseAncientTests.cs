using DfoServer.GameWorld.Items;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Persistence;
using DfoServer.Testing;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class OtherverseAncientTests
{
    public static void RunCore()
    {
        RunDropRulesCore();
        Bytes(DungeonEnterCountPackets.Dimension(3, 4, 1), [3, 0, 0, 0, 4, 0, 0, 0, 1], "815独立编码九字节金样");
        Bytes(DungeonEnterCountPackets.Ancient(500), [244, 1, 0, 0], "772独立编码四字节能量");
        var now = new DateTimeOffset(2026, 10, 27, 1, 0, 0, TimeSpan.Zero);
        var inventory = new InventorySnapshot([]);
        for (int clear = 1; clear <= 20; clear++)
        {
            var next = AncientGateProgression.Prepare(inventory, now);
            That(next.Bonus == (clear == 20) && next.Energy == (clear == 20 ? 0u : (uint)(clear * 500)),
                "远古本服进度二十次触发一次，不提前奖励或重复保留满条");
            inventory = inventory with { DailyBoosterUses = next.After };
        }
        That(AncientGateProgression.Energy(inventory, now) == 0, "领取后能量归零");
        inventory = inventory with { DailyBoosterUses = AncientGateProgression.Prepare(inventory, now).After };
        That(AncientGateProgression.Energy(inventory, now.AddDays(1)) == 0, "次日能量按既有六点业务日自然重置");
    }

    static void RunDropRulesCore()
    {
        That(DimensionDropRules.Parse("{}").Tiers[2] == DimensionDropRules.Default.Tiers[2], "默认空JSON保留v2表");
        foreach (string invalid in new[] { "{\"mode\":\"unknown\"}", "{\"tiers\":{\"2\":{\"named\":{\"equip\":101}}}}",
            "{\"tiers\":{\"0\":{\"boss\":{\"count\":-1}}}}", "{\"setRateSource\":\"unknown\"}" })
            Reject<InvalidDataException>(() => DimensionDropRules.Parse(invalid), "R7 非法规则启动解析拒绝");
        var patch = DimensionDropRules.Parse("{\"tiers\":{\"1\":{\"named\":{\"set\":12}}}}");
        That(patch.Tiers[1].Named is { Equip: 80, Set: 12, Shards: 0 } && patch.Tiers[2].Boss.Count == 2,
            "配置局部覆盖保留各档其余默认值");
    }

    // 与TCP异界组共享只读资源，先核对真实MOB标记，再测试专用计划，避免另起服务。
    internal static void RunDropRegression(PvfArchive pvf, ItemResources items)
    {
        RunDropRulesCore();
        var dimension = DungeonIncountCatalog.Load(pvf).Dimension!;
        var catalog = new DimensionRewardCatalog(pvf, items.Items);
        var policy = new DimensionRewardPolicy(dimension, catalog, items.Items);
        var dungeons = SelfTestCatalogs.Dungeons(pvf);
        var rooms = SelfTestCatalogs.Rooms(pvf);
        foreach (int tier in Enumerable.Range(0, 3))
        {
            int count = dimension.Tiers.Where(row => row.Value == tier).Sum(row =>
                dungeons.Get(row.Key).Mazes.SelectMany(maze => maze.MapSpecification ?? []).SelectMany(map => rooms.Get(map, requirePassiveDefinitions: false).Monsters)
                .Count(m => m.RankKind != "[boss]" && PvfScript.Integers(new PvfDocument(pvf.ReadText(m.Path)).Section("is named monster")) is [1]));
            That(count > 0, $"R1 真实PVF每档存在MOB命名声明 tier={tier}, count={count}");
        }
        using var f = new Fixture(pvf, TownCatalog.Load(pvf), items, DungeonIncountCatalog.Load(pvf), 2);
        var entry = f.Enter(7167, 2, bound => bound == 10000 ? 9999 : 0).Entry with { DimensionRewardProfession = (0, 1) };
        var source = dungeons.Get(7167).Mazes.SelectMany(maze => maze.MapSpecification ?? []).SelectMany(map => rooms.Get(map, requirePassiveDefinitions: false).Monsters
            .Select(m => (Map: map, Monster: m))).First(row => row.Monster.RankKind != "[boss]" &&
                PvfScript.Integers(new PvfDocument(pvf.ReadText(row.Monster.Path)).Section("is named monster")) is [1]);
        var named = new DungeonPlannedMonster(0, 500, source.Monster.MonsterId, 95, source.Monster)
            { SourceMap = source.Map };
        That(named.Source!.IsNamed, "R1 MOB命名标记冻结进入Source，未借用强化限制");
        var empty = new DungeonDropGenerationPlan("regression", []);
        var plan = policy.Extend(empty, entry, named);
        That(plan.ResourceSource.Contains("otherverse-rewards-server-v2"), "v2计划标记实际规则");
        That(plan.Trials.Length == 1, "R1 勇士绿名本人有一次职业装备判定");
        var boss = named with { Source = source.Monster with { RankKind = "[boss]", Modifiers = [] }, SourceMap = entry.BossMap };
        var bossPlan = policy.Extend(empty, entry, boss);
        int equipment = bossPlan.Generate(_ => 0, DateTimeOffset.UtcNow).Count(row => row.Item is { } item &&
            catalog.Professions[(entry.DimensionRewardProfession!.Value.Job, entry.DimensionRewardProfession.Value.Grow & 15)].Set.Contains(item.ItemId));
        That(equipment == 2, "R2 勇士Boss两次判定可命中套装池");
        var now = DateTimeOffset.UtcNow;
        foreach (int tier in Enumerable.Range(0, 3))
        {
            var current = entry with { Dimension = entry.Dimension! with { Tier = tier } };
            var n = policy.Extend(empty, current, named).Trials.Single();
            That(n.Threshold == new[] { 60, 80, 100 }[tier] && n.ProbabilityDenominator == 100,
                "绿名三档掉装概率不借翻牌字段");
            foreach (var actor in new[] { named, boss })
            {
                var selected = policy.Extend(empty, current, actor);
                if (tier == 0)
                    That(selected.Generate(_ => 0, now).Where(r => r.Item is { Kind: ItemKind.Equipment })
                        .All(r => catalog.Professions[(0, 1)].Normal.Contains(r.Item!.ItemId)), "R3 普通档从不进入套装池");
                var extra = current with { Dimension = current.Dimension! with { DailyBefore = new(dimension.DailyLimit, 0) } };
                var noEquipment = policy.Extend(empty, extra, actor);
                That(!noEquipment.Generate(_ => 0, now).Any(r => r.Item is { } item && catalog.Professions.Values.Any(p => p.Normal.Contains(item.ItemId) || p.Set.Contains(item.ItemId))), "R4 追加次数禁止职业装备");
                if (actor.IsBoss) That(noEquipment.Trials.Any(t => t.Items?.Entries.Any(r => r.Template.Id == 3311) == true), "R4 材料保留");
            }
        }
        var compatibility = new DimensionRewardPolicy(dimension, catalog, items.Items, DimensionDropRules.Parse("{\"mode\":\"compat-v1\"}"));
        That(ReferenceEquals(compatibility.Extend(empty, entry, named), empty), "R5 兼容绿名保持原计划");
        foreach (bool hidden in new[] { false, true })
        foreach (int tier in Enumerable.Range(0, 3))
        {
            var current = entry with { Dimension = entry.Dimension! with { Tier = tier, HiddenBoss = hidden, DailyBefore = new(10, 0) } };
            var actual = compatibility.Extend(empty, current, boss);
            var expected = new List<DungeonDropTrial>();
            foreach (var material in dimension.Materials.Where(row => row.Tier == tier))
                expected.Add(new(1, Pool([material.Item]), 1, 1, ProbabilityDenominator: 1));
            expected.Add(new(1, Pool(catalog.Professions[(0, 1)].Normal), 1, 1, ProbabilityDenominator: 1));
            if (tier == 2) expected.Add(new(1, Pool(catalog.Legendary), 1, 1, ProbabilityDenominator: 100));
            if (hidden) expected.Add(new(1, new DungeonItemDropPool(dimension.HiddenRewards.Where(r => r.Tier == tier)
                .Select(r => new DungeonWeightedDropItem(items.Items.Get(r.Item), r.Value))), 1, 1, ProbabilityDenominator: 1));
            That(actual.Trials.Length == expected.Count && actual.Trials.Zip(expected).All(pair =>
                pair.First with { Items = pair.Second.Items } == pair.Second &&
                pair.First.Items!.Entries.SequenceEqual(pair.Second.Items!.Entries)), "R5 compat-v1逐项保留旧顺序、池、权重、概率、数量");
            That(actual.ResourceSource == "regression;otherverse-rewards-server-compat-v1;" + DimensionRewardCatalog.PoolPath,
                "R5 兼容来源保持旧字符串");
        }
        foreach (var key in new[] { (Job: (byte)0, Grow: (byte)1), (Job: (byte)1, Grow: (byte)1) })
        {
            var own = policy.Extend(empty, entry with { DimensionRewardProfession = key }, boss);
            var allowed = catalog.Professions[(key.Job, key.Grow)];
            That(own.Trials.Where(t => !t.RarityPools.IsDefaultOrEmpty).SelectMany(t => t.RarityPools)
                .SelectMany(r => r.CandidatePools).SelectMany(p => p.Entries).All(r => allowed.Normal.Contains(r.Template.Id) || allowed.Set.Contains(r.Template.Id)),
                "R6 不同职业个人计划不串池");
        }
        // 当前PVF没有单空套装行，显式合成空池验证同一生产组装入口，不能靠空循环声称回退已覆盖。
        var normalPool = Pool(catalog.Professions[(0, 1)].Normal);
        var emptyPool = Pool([]);
        var fallbackTrial = DimensionRewardPolicy.EquipmentTrial(normalPool, emptyPool, 100, 100);
        That(fallbackTrial is not null && new DungeonDropGenerationPlan("synthetic-empty-set", [fallbackTrial])
            .Generate(_ => 0, now).Single().Item is { } fallback && catalog.Professions[(0, 1)].Normal.Contains(fallback.ItemId),
            "R8 强制套装命中但池空时实际生成散件");
        That(DimensionRewardPolicy.EquipmentTrial(emptyPool, emptyPool, 100, 100) is null, "R8 两池空不创建装备trial");
        That(policy.Extend(empty, entry with { DimensionRewardProfession = (255, 15) }, named).Trials.IsEmpty,
            "R8 两池都空跳过不抛");
        foreach (var actor in new[] { named with { IsFriendly = true }, named with { IsBlocking = false },
            named with { AiCharacter = new(1, [], "[monster]", "[normal]", "synthetic", 95) } })
            That(ReferenceEquals(policy.Extend(empty, entry, actor), empty), "R9 友方、非法死亡、APC不掉");
        That(ReferenceEquals(policy.Extend(empty, entry with { IsPractice = true }, boss), empty), "R9 练习不掉");
        That(ReferenceEquals(policy.Extend(empty, entry with { Dimension = null }, boss), empty), "R9 非异界不掉");
        var card = new DimensionRewardPolicy(dimension, catalog, items.Items, DimensionDropRules.Parse("{\"setRateSource\":\"card\"}"));
        That(card.Extend(empty, entry, named).Trials.Single().RarityPools.Single().AlternatePermille == catalog.CardRates[2].SetPercent * 10,
            "显式card来源沿翻牌套装率");
        var extraPolicy = new DimensionRewardPolicy(dimension, catalog, items.Items, DimensionDropRules.Parse("{\"extraEntryEquipment\":true}"));
        That(extraPolicy.Extend(empty, entry with { Dimension = entry.Dimension! with { DailyBefore = new(10, 0) } }, named).Trials.Length == 1,
            "显式追加开关允许装备");
        Console.WriteLine("PASS #899 R1-R9 资源及个人掉落计划（非TCP客户端验收）");
        DungeonItemDropPool Pool(IEnumerable<int> ids) => new(ids.Select(id => new DungeonWeightedDropItem(items.Items.Get(id), 1)));

    }

    public static void Run(PvfArchive pvf, ItemResources items)
    {
        int before = Count;
        var dungeons = SelfTestCatalogs.Dungeons(pvf);
        var rooms = SelfTestCatalogs.Rooms(pvf);
        var incounts = DungeonIncountCatalog.Load(pvf);
        var dimension = incounts.Dimension!;
        var rewardCatalog = new DimensionRewardCatalog(pvf, items.Items);
        var rewardPolicy = new DimensionRewardPolicy(dimension, rewardCatalog, items.Items);
        foreach (var row in rewardCatalog.Professions)
        foreach (bool set in new[] { false, true })
        {
            int draws = 0;
            int Draw(int bound) => draws++ < 2 ? (draws == 2 && !set ? 99 : 0) : 0;
            var card = rewardPolicy.Card(7167, (byte)row.Key.Job, (byte)row.Key.Grow, Draw, DateTimeOffset.UtcNow);
            var allowed = set ? row.Value.Set : row.Value.Normal;
            That(card is null ? allowed.IsEmpty : allowed.Contains(card.ItemId), "异界各职业单双装备池精确匹配，不回退通用池");
        }
        foreach (int id in new[] { 10158125, 10158126, 490008217, 10158692, 10161066, 490003748, 490009451 })
            That(items.Items.ActionConsumable(id) is { Label: "[add dimension dungeon enter count]", Parameters: [1 or 5] },
                "七种正式追加券完整读取原动作、地点和冷却：" + id);
        var towns = TownCatalog.Load(pvf);
        That(dimension.Tiers.Count == 15 && dimension.DailyLimit == 10 && dimension.ExtraLimit == 5 && dimension.HiddenWeeklyLimit == 2,
            "现代异界读取十五图及真实日周上限");
        foreach (int id in dimension.Tiers.Keys.Order())
        {
            using var f = new Fixture(pvf, towns, items, incounts, 2);
            byte difficulty = checked((byte)dimension.Tiers[id]);
            var opening = f.Enter(id, difficulty, bound => bound == 10000 ? 9999 : 0);
            That(opening.Entry.Dimension is { HiddenBoss: false } && !opening.Entry.Maze.IsDimensionHiddenBoss,
                "异界实际图号进入普通路线：" + id);
            That(f.Usage(dimension.DailyRow.CountKey).DailyUsed == 1 && f.ItemCount(3332) == 100 - (difficulty * 2 + 1),
                "十五图共用日次数并按档位扣真实门票");
            Clear(f, opening);
            That(f.Session.ReturnFromFormalDungeon() is not null, "十五图沿真实普通路线通关返城");
        }
        using (var f = new Fixture(pvf, towns, items, incounts, 2))
        {
            for (int used = 0; used < 10; used++)
            {
                var opening = f.Enter(7165 + used % 3, checked((byte)(used % 3)), _ => 0);
                That(opening.Entry.Dimension?.HiddenBoss == (used < 2), "隐藏领主每周最多两次，其后切普通路线");
                That(f.Session.ReturnFromFormalDungeon() is not null, "异界返城保留个人已用次数");
            }
            That(f.Session.BeginDungeonSelection() is not null && f.Session.EnterFormalDungeon(7165, 0, 0, dungeons, rooms, pieces: items.EpicPieces) is null,
                "十五图共享十次，不能换图绕过上限");
            That(f.Usage(dimension.DailyRow.CountKey).DailyUsed == 10 && f.Usage(7100).WeeklyUsed == 2,
                "日次数和隐藏周次数均持久保存");
            f.Session.ReturnToRoster(); f.Session.Select(0, resumeTown: true); f.Session.MarkReady();
            That(f.Session.DimensionEntranceInfo() is (10, 0, 2),
                "重选角色仍投影用尽状态");
            var granted = f.Session.PlanAndCommitInventory(character => new InventoryService(items.Items, items.GoldLimits, items.EpicPieces)
                .Grant(character, [new(10161066, 2)]));
            That(granted.Accepted, "合成夹具发放两张真实五次券");
            var source = f.Session.Selected!.Inventory.Items.Single(item => item.Core.ItemId == 10161066).Position;
            var tickets = new DimensionEntryTicketService(items.Items);
            That(f.Session.UseDimensionEntryTicket(source, tickets).Accepted && f.Session.DimensionEntranceInfo() is (10, 5, 2),
                "追加五次与一张券同事务保存，已耗尽基本次数可继续");
            var ticketUsed = f.Read();
            That(!f.Session.UseDimensionEntryTicket(source, tickets).Accepted && QuestCompletionRepositoryTests.Same(ticketUsed, f.Read()),
                "当日追加总上限五次，第二张券不消耗");
            var extraOpening = f.Enter(7165, 0, _ => 0);
            That(extraOpening.Entry.Dimension is { HiddenBoss: false } && f.Session.DimensionEntranceInfo() is (10, 4, 2),
                "额外入场扣额外剩余，不重置或绕过隐藏周额度");
            That(f.Session.ReturnFromFormalDungeon() is not null, "额外入场可正常返城");
        }
        foreach (int id in Enumerable.Range(7101, 7).Where(dungeons.ContainsDungeon))
        {
            That(dungeons.Get(id).IsAncientInfinite, "远古带值标签精确识别：" + id);
            using var f = new Fixture(pvf, towns, items, incounts, 5);
            var initial = f.Read();
            f.Sql("CREATE TRIGGER ancient_entry_fail AFTER UPDATE ON character_dungeon_state BEGIN SELECT RAISE(ABORT,'entry rollback'); END");
            Reject<SqliteException>(() => f.Enter(id, 2, _ => 0), "远古入场末段SQL失败整笔回滚");
            That(QuestCompletionRepositoryTests.Same(initial, f.Read()) && f.Session.DungeonSnapshot is null,
                "远古失败不扣双材料、疲劳、尝试号，也不安装实例");
            f.Sql("DROP TRIGGER ancient_entry_fail");
            var opening = f.Enter(id, 2, _ => 0);
            That(opening.Entry.StartFatigue == 6 && initial.Fatigue!.Remaining - f.Read().Fatigue!.Remaining == 6 &&
                f.ItemCount(3037) == 880 && f.ItemCount(3332) == 90, $"远古双材料及六点疲劳同次提交：{id}, start={opening.Entry.StartFatigue}, fatigue={initial.Fatigue!.Remaining-f.Read().Fatigue!.Remaining}, materials={f.ItemCount(3037)}/{f.ItemCount(3332)}");
            Clear(f, opening);
            That(f.Session.AncientGateEnergy() == 500, "远古真实通关事务增加能量，未选牌也已持久保存");
            That(f.Session.ReturnFromFormalDungeon() is not null, "远古沿正式实例返回原城镇");
        }
        if (dungeons.ContainsDungeon(7101))
        {
            using var f = new Fixture(pvf, towns, items, incounts, 5);
            That(f.Session.PlanAndCommitInventory(character => new(true, 0, new(character.Inventory,
                character.Inventory with { DailyBoosterUses = [new(AncientGateProgression.CounterItem,
                    ShopPurchaseCount.Today(DateTimeOffset.UtcNow), 19)] }, "ancient_bonus_fixture"))).Accepted,
                "合成夹具仅准备满能量前一步");
            Clear(f, f.Enter(7101, 2, _ => 0), bonus: true);
            That(f.Session.AncientGateEnergy() == 0, "第二十次通关触发加成箱并原子归零");
        }
        foreach (int id in new[] { 7167, 7170, 7173, 7176, 7179 })
        {
            using var f = new Fixture(pvf, towns, items, incounts, 2);
            var entry = f.Enter(id, 2, _ => 0);
            That(entry.Entry.Dimension is { HiddenBoss: true } && entry.Entry.Maze.IsDimensionHiddenBoss,
                "五家族真实隐藏路线可用：" + id);
            Clear(f, entry);
            That(f.Session.ReturnFromFormalDungeon() is not null, "隐藏路线沿完整实例通关返城");
        }
        Console.WriteLine($"PASS {Count-before} 异界日周路线、远古双材料及事务断言（合成入场，非实机）");

        void Clear(Fixture f, FormalDungeonReceipt opening, bool bonus = false)
        {
            var path = QuestStoryRouteTests.Path(opening.Entry.Maze.StaticTopology, opening.Entry.StartMap.Position, opening.Entry.BossMap.Position);
            for (int at = 0; at < path.Length; at++)
            {
                var loaded = f.Session.CompleteFormalDungeonLoading()!;
                if (opening.Entry.Dimension is not null && loaded.Monsters.Monsters.FirstOrDefault(monster => monster.IsBoss &&
                    monster.SourceMap is { } map && opening.Entry.IsTerminalMap(map)) is { } boss)
                {
                    var drops = new DungeonResourceDropPolicy(SelfTestCatalogs.DropRules(pvf), items.DungeonDropItems,
                        id => new(id, "monster/synthetic.mob", null, null), items.Items.Get, dimensionRewards: rewardPolicy);
                    var middle = boss with { SourceMap = boss.SourceMap! with { Position = new(-1, -1) } };
                    var forward = drops.PrepareEntry(opening.Entry);
                    int middleTrials = forward(middle).Trials.Length;
                    int endTrials = forward(boss).Trials.Length;
                    var reverse = drops.PrepareEntry(opening.Entry);
                    That(endTrials > middleTrials && reverse(boss).Trials.Length == endTrials && reverse(middle).Trials.Length == middleTrials,
                        $"同模板中途和终点Boss正反调用均使用各自掉落计划：dungeon={opening.Entry.DungeonId},monster={boss.MonsterId},scripted={boss.IsScriptedBoss},friendly={boss.IsFriendly},apc={boss.AiCharacter is not null},trials={middleTrials}/{endTrials}");
                }
                foreach (var monster in loaded.Monsters.Monsters.Where(monster => monster.IsBlocking).OrderBy(monster => monster.IsBoss))
                    That(f.Session.ObserveFormalDungeonDeath(FormalDungeonSessionTests.Observation(loaded, monster),
                        SelfTestCatalogs.KillExperience(pvf), SelfTestCatalogs.Growth(pvf)).Outcome == DungeonRunOutcome.Accepted,
                        "远古仅报告实际当前房间演员死亡");
                if (at + 1 < path.Length)
                    That(f.Session.MoveFormalDungeon((byte)path[at + 1].X, (byte)path[at + 1].Y) is not null, "远古沿真实门墙连接清房");
            }
            var enabled = f.Session.EnableFormalDungeonSettlement();
            That(enabled is not null, "真实Boss死亡后才可结算");
            f.Session.ConfirmFormalDungeonSettlementEnabled(enabled!.Value.Receipt);
            var report = DungeonSettlementProtocolTests.Report(f.Read().Id, 99);
            That(DungeonSettlementRequests.TryParsePlayResult(report, out var parsed), "使用正式结算报告");
            var cards = new DungeonCardRewardPolicy(SelfTestCatalogs.DropRules(pvf), items.DungeonDropItems, items.Items,
                items.GoldLimits, items.EpicPieces, prices: SelfTestCatalogs.CardPrices(pvf), dimensionRewards: rewardPolicy);
            FormalDungeonSettlementResult Settle() => f.Session.SettleFormalDungeon(parsed!.ActorId, parsed.RankPoint, parsed.MaximumRankPoint,
                report, dungeons, SelfTestCatalogs.ClearExperience(pvf), SelfTestCatalogs.Growth(pvf), cards: cards);
            if (opening.Entry.DungeonId == 7101)
            {
                var before = f.Read();
                f.Sql("CREATE TRIGGER ancient_clear_fail AFTER INSERT ON character_dungeon_settlement_receipts BEGIN SELECT RAISE(ABORT,'clear rollback'); END");
                Reject<SqliteException>(() => Settle(), "通关末段SQL失败回滚能量和冻结箱");
                That(QuestCompletionRepositoryTests.Same(before, f.Read()), "通关失败不留下能量、经验或库存半提交");
                f.Sql("DROP TRIGGER ancient_clear_fail");
            }
            var result = Settle();
            That(result.Outcome == DungeonRunOutcome.Accepted, "真实终点通关报告被正式结算接纳");
            if (opening.Entry.DungeonId is >= 7101 and <= 7107)
            {
                var boxes = rewardCatalog.AncientBoxes[(int)opening.Entry.DungeonId];
                That(result.Card?.Item?.ItemId == (bonus ? boxes.BonusBox : boxes.NormalBox),
                    "真实远古通关只冻结本图普通或满能量箱");
            }
            var committed = f.Read();
            That(Settle().Outcome == DungeonRunOutcome.Duplicate && QuestCompletionRepositoryTests.Same(committed, f.Read()),
                "同一结算报告不重复加能量、经验或冻结奖品");
            That(f.Session.AdvanceFormalDungeonCards(false) is not null && f.Session.AdvanceFormalDungeonCards(true) is not null,
                "正式结算可沿原滚分和翻牌流程领奖");
            That(f.Session.SelectFormalDungeonFreeCard(0, 0) is not null, "免费牌真实领取冻结产物");
            var claimed = f.Read();
            That(f.Session.SelectFormalDungeonFreeCard(0, 0) is { Duplicate: true } && QuestCompletionRepositoryTests.Same(claimed, f.Read()),
                "重复选牌不重复领取");
        }
    }

    sealed class Fixture : IDisposable
    {
        readonly string root = Path.Combine(Path.GetTempPath(), "S5A9-otherverse-" + Guid.NewGuid().ToString("N"));
        readonly string path;
        readonly DungeonCatalog dungeons;
        readonly DungeonRoomCatalog rooms;
        readonly ItemResources items;
        public AccountOwners.Lease Owner { get; }
        public CharacterRepository Repository { get; }
        public GameSession Session { get; }
        public Fixture(PvfArchive pvf, TownCatalog towns, ItemResources items, DungeonIncountCatalog incounts, byte areaId)
        {
            this.items = items; path = Path.Combine(root, "game.db");
            dungeons = SelfTestCatalogs.Dungeons(pvf); rooms = SelfTestCatalogs.Rooms(pvf);
            var quests = SelfTestCatalogs.Quests(pvf);
            var growth = SelfTestCatalogs.Growth(pvf);
            var area = towns.Areas[(17, areaId)];
            var done = area.Quests.Concat(area.AnyQuests).Concat(area.Default.DungeonGates.SelectMany(gate => gate.Quests)).Distinct().ToArray();
            var bounds = area.Map(done.ToHashSet()).DungeonGates.First(gate => gate.Conditions.Length == 0 && gate.Quests.All(done.Contains)).Bounds;
            var location = new Location(17, areaId, checked((ushort)(bounds.X + bounds.Width / 2)), checked((ushort)(bounds.Y + bounds.Height / 2)));
            Repository = new(path, quests); Owner = new AccountOwners().Acquire("otherverse-session", () => { });
            var created = Repository.Create(Owner, new("Otherverse", location, location) { Level = 95, AcceptedStoryLevel = 95,
                CompletedQuests = done, DungeonState = CharacterDungeonState.NewOrdinary,
                InitialLearning = new(growth.GetLevelStartExperience(95), [new(0, 10, 0, []), new(1, 20, 0, [])]) });
            var service = new InventoryService(items.Items, items.GoldLimits, items.EpicPieces);
            InventoryService.Commit(Repository, Owner, created, service.Grant(created, [new(3332, 100), new(3037, 1000)]));
            Session = new(Repository, towns, Owner, questCatalog: quests, incounts: incounts);
            Session.Select(0, resumeTown: true); Session.MarkReady();
        }
        public Character Read() => Repository.LoadCharacter(Owner, 0);
        public long ItemCount(int id) => CurrencyWallet.RewardKind(id) is { } currency
            ? CurrencyWallet.Get(Read().Inventory, currency)
            : Read().Inventory.Items.Where(item => item.Core.ItemId == id).Sum(item => item.Core.Count);
        public DungeonIncountUsage Usage(int key) => Repository.LoadDungeonIncount(Owner, Read().Id, key, DungeonIncountPolicy.Period(DateTimeOffset.UtcNow));
        public FormalDungeonReceipt Enter(int id, byte difficulty, Func<int, int> draw)
        {
            if (Session.Phase == SessionPhase.Town) That(Session.BeginDungeonSelection() is not null, "在真实入口选图");
            return Session.EnterFormalDungeon(checked((uint)id), 0, difficulty, dungeons, rooms, draw: draw, pieces: items.EpicPieces)
                ?? throw new InvalidOperationException("Otherverse entry rejected: " + id + ": " + Session.LastFormalDungeonRejection);
        }
        public void Sql(string text)
        {
            using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False"); db.Open();
            using var command = db.CreateCommand(); command.CommandText = text; command.ExecuteNonQuery();
        }
        public void Dispose() { Session.End(); Owner.Dispose(); Directory.Delete(root, true); }
    }
}
