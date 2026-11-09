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
        foreach (string invalid in new[] { "{\"mode\":\"unknown\"}", "{\"tiers\":{\"2\":{\"named\":{\"equip\":101}}}}", "{\"tiers\":{\"0\":{\"named\":{\"memories\":101}}}}", "{\"tiers\":{\"0\":{\"named\":{\"holyAuraPercent\":101}}}}", "{\"tiers\":{\"0\":{\"boss\":{\"shardStackSize\":-1}}}}",
            "{\"tiers\":{\"0\":{\"boss\":{\"count\":-1}}}}", "{\"setRateSource\":\"unknown\"}" })
            Reject<InvalidDataException>(() => DimensionDropRules.Parse(invalid), "R7 非法规则启动解析拒绝");
        var patch = DimensionDropRules.Parse("{\"tiers\":{\"1\":{\"named\":{\"set\":12}}}}");
        That(patch.Tiers[1].Named is { Equip: 80, Set: 12, Shards: 7, Memories: 5, HolyAuraPercent: 5 } && patch.Tiers[2].Boss is { Count: 2, ShardStacks: 3, ShardStackSize: 7, MemoryStacks: 5, MemoryStackSize: 4 },
            "配置局部覆盖保留各档其余默认值");
    }

    static void RunEntryRegression(PvfArchive pvf, ItemResources items)
    {
        var incounts = DungeonIncountCatalog.Load(pvf);
        var dimension = incounts.Dimension!;
        That(dimension.DailyLimit == 10 && dimension.ExtraLimit == 5 && dimension.BaseDailyLimit == 5 && dimension.DailyRow.Limit == 5,
            "异界资源合计十次拆为基础五次与追加五次");
        int[] tierZero = [.. dimension.Tiers.Where(row => row.Value == 0).Select(row => row.Key).Order()];
        That(tierZero.Length == 5 && dimension.RetryTarget(tierZero[^1], _ => 0) == tierZero[0] &&
            dimension.RetryTarget(tierZero[0], bound => bound - 1) == tierZero[^1],
            "再次挑战随机目标限定当前档位五图并覆盖首尾抽样");
        foreach (var group in dimension.Tiers.GroupBy(row => row.Value).OrderBy(group => group.Key))
        {
            int[] candidates = [.. group.Select(row => row.Key).Order()];
            That(candidates.Length == 5, "每个异界档位都有五张真实随机候选图：" + group.Key);
            int[] selected = [.. Enumerable.Range(0, candidates.Length).Select(index =>
                dimension.RetryTarget(candidates[^1], bound =>
                {
                    That(bound == candidates.Length, "随机抽样分母覆盖当前档位完整候选集合");
                    return index;
                }))];
            That(selected.SequenceEqual(candidates) && selected.Contains(candidates[^1]) &&
                selected.All(id => dimension.Tiers[id] == group.Key),
                "每档逐一抽取全部五图，包含当前图且不跨档位：" + group.Key);
        }

        using var f = new Fixture(pvf, TownCatalog.Load(pvf), items, incounts, 2);
        for (int used = 0; used < dimension.BaseDailyLimit; used++)
        {
            var opening = f.Enter(tierZero[used], 0, _ => 0);
            That(opening.Entry.Dimension is not null, "基础次数内可正常进入：" + used);
            That(f.Session.ReturnFromFormalDungeon() is not null, "基础次数内正常返城：" + used);
        }
        That(f.Usage(dimension.DailyRow.CountKey).DailyUsed == dimension.BaseDailyLimit &&
            f.Session.BeginDungeonSelection() is not null &&
            f.Session.EnterFormalDungeon((uint)tierZero[0], 0, 0, SelfTestCatalogs.Dungeons(pvf), SelfTestCatalogs.Rooms(pvf), pieces: items.EpicPieces) is null,
            "基础五次后无追加券不能进入第六次");
        f.Session.ReturnToRoster(); f.Session.Select(0, resumeTown: true); f.Session.MarkReady();
        That(f.Session.DimensionEntranceInfo() is (5, 0, 0), "基础五次用尽后815显示5/5、追加0、隐藏剩余0");

        var granted = f.Session.PlanAndCommitInventory(character => new InventoryService(items.Items, items.GoldLimits, items.EpicPieces)
            .Grant(character, [new(10161066, 2)]));
        That(granted.Accepted, "发放两张真实五次追加券");
        var source = f.Session.Selected!.Inventory.Items.Single(item => item.Core.ItemId == 10161066).Position;
        var tickets = new DimensionEntryTicketService(items.Items);
        That(f.Session.UseDimensionEntryTicket(source, tickets).Accepted && f.Session.DimensionEntranceInfo() is (5, 5, 0) &&
            f.ItemCount(10161066) == 1 && DimensionEntryTicketService.Extra(f.Read().Inventory, tickets.Clock.GetUtcNow()) == 5,
            "第一张五次券消耗一张，持久追加五次且基础上限不变");
        var capped = f.Read();
        That(!f.Session.UseDimensionEntryTicket(source, tickets).Accepted &&
            QuestCompletionRepositoryTests.Same(capped, f.Read()) && f.ItemCount(10161066) == 1,
            "每日已追加五次后第二张整拒且保留原券");
        f.Session.ReturnToRoster(); f.Session.Select(0, resumeTown: true); f.Session.MarkReady();
        That(f.Session.DimensionEntranceInfo() is (5, 5, 0) && f.ItemCount(10161066) == 1,
            "五次额度及未消费第二张券重选后保持");
        const int extraCapacity = 5;
        for (int extra = 0; extra < extraCapacity; extra++)
        {
            var opening = f.Enter(tierZero[extra % tierZero.Length], 0, _ => 0);
            var info = f.Session.DimensionEntranceInfo();
            That(opening.Entry.Dimension is { HiddenBoss: false } && info is (5, var remaining, 0) &&
                remaining == (uint)(extraCapacity - extra - 1),
                "追加入场逐次扣减额外剩余：" + extra);
            That(f.Session.ReturnFromFormalDungeon() is not null, "追加次数内正常返城：" + extra);
        }
        That(f.Usage(dimension.DailyRow.CountKey).DailyUsed == dimension.BaseDailyLimit + extraCapacity &&
            f.Session.BeginDungeonSelection() is not null && f.Session.EnterFormalDungeon((uint)tierZero[0], 0, 0, SelfTestCatalogs.Dungeons(pvf), SelfTestCatalogs.Rooms(pvf), pieces: items.EpicPieces) is null,
            "基础五次加五次追加合计十次后不能进入第十一次");
        That(f.Session.DimensionEntranceInfo() is (5, 0, 0), "追加次数用尽后815仍显示基础5/5、追加0、隐藏剩余0");
        // 旧版本把资源合计值当基础上限，可能留下 daily_used=10 的历史行。
        // 新规则只在超出“基础+已购追加”时把旧口径遗留降回基础，随后追加券仍可正常使用。
        using (var legacy = new Fixture(pvf, TownCatalog.Load(pvf), items, incounts, 2))
        {
            var grantedLegacy = legacy.Session.PlanAndCommitInventory(character => new InventoryService(items.Items, items.GoldLimits, items.EpicPieces)
                .Grant(character, [new(10158125, 1), new(10158126, 1), new(490008217, 1)]));
            That(grantedLegacy.Accepted, "旧口径修复夹具发放三张一次追加券");
            var initialPeriod = DungeonIncountPolicy.Period(DateTimeOffset.UtcNow);
            legacy.Sql($"INSERT INTO character_dungeon_incount(character_id,account,dungeon,day,daily_used,week,weekly_used) SELECT id,account,{dimension.DailyRow.CountKey},{initialPeriod.Day},5,{initialPeriod.Week},0 FROM characters");
            var legacyTickets = new DimensionEntryTicketService(items.Items);
            foreach (int ticketId in new[] { 10158125, 10158126, 490008217 })
            {
                var slot = legacy.Session.Selected!.Inventory.Items.Single(item => item.Core.ItemId == ticketId).Position;
                That(legacy.Session.UseDimensionEntryTicket(slot, legacyTickets).Accepted, "旧口径修复夹具追加券生效：" + ticketId);
            }
            var period = DungeonIncountPolicy.Period(DateTimeOffset.UtcNow);
            int legacyId = legacy.Read().Id;
            legacy.Sql($"DELETE FROM character_dungeon_incount WHERE character_id={legacyId} AND dungeon={dimension.DailyRow.CountKey}");
            legacy.Sql($"INSERT INTO character_dungeon_incount(character_id,account,dungeon,day,daily_used,week,weekly_used) SELECT id,account,{dimension.DailyRow.CountKey},{period.Day},10,{period.Week},0 FROM characters WHERE id={legacyId}");
            That(legacy.Session.DimensionEntranceInfo() is (5, 3, 2), "旧口径daily_used=10在超上限时降为基础5并恢复三张追加券");
            var legacyOpening = legacy.Enter(7165, 0, _ => 0);
            That(legacyOpening.Entry.Dimension is not null && legacy.Usage(dimension.DailyRow.CountKey).DailyUsed == 6,
                "旧口径修复后首张追加券实际进入并把持久计数从5推进到6");
        }
        Console.WriteLine("PASS 异界基础/追加次数与随机再挑战目标断言");
    }
    // 与TCP异界组共享只读资源，先核对真实MOB标记，再测试专用计划，避免另起服务。
    internal static void RunDropRegression(PvfArchive pvf, ItemResources items)
    {
        RunDropRulesCore();
        RunEntryRegression(pvf, items);
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
        var baseDrops = new DungeonResourceDropPolicy(SelfTestCatalogs.DropRules(pvf), items.DungeonDropItems,
            id => new(id, "monster/synthetic.mob", null, null), items.Items.Get);
        var baseNamedPlan = baseDrops.Prepare(named.MonsterId, named.CandidateLevel, named.RewardType, 2);
        That(baseNamedPlan.Trials.Any(t => t.Category is DungeonDropCategory.Equipment or DungeonDropCategory.Materials),
            "基础PVF计划包含装备或材料骰，必须由异界专用规则替换而不叠加");
        var replacedNamed = policy.Extend(baseNamedPlan, entry, named);
        That(!replacedNamed.Trials.Any(t => t.Category is DungeonDropCategory.Equipment or DungeonDropCategory.Materials),
            "异界专用规则替换基础装备/材料骰，避免双份掉落");
        var baseLotusPlan = baseDrops.Prepare(107005028, 85, 0, 0);
        var lotusBoss = named with { MonsterId = 107005028, Source = named.Source! with { RankKind = "[boss]" }, SourceMap = entry.BossMap };
        var replacedLotus = policy.Extend(baseLotusPlan, entry, lotusBoss);
        That(!replacedLotus.Trials.Any(t => t.Category is DungeonDropCategory.Equipment or DungeonDropCategory.Materials) &&
            replacedLotus.Generate(_ => 0, DateTimeOffset.UtcNow).Any(row => row.Item is { Kind: ItemKind.Equipment }),
            "真实长脚罗特斯107005028基础装备/材料被专用掉落替换且仍产出一套装备");
        var lotusDummy = named with
        {
            MonsterId = 107005037,
            Source = named.Source! with { RankKind = "[boss]" },
            SourceMap = entry.BossMap,
            IsDummyMapBoss = true
        };
        var replacedLotusDummy = policy.Extend(baseDrops.Prepare(107005037, 85, 0, 0), entry, lotusDummy);
        That(replacedLotusDummy.Trials.All(t => t.Category == DungeonDropCategory.Gold),
            "长脚罗特斯[dummy]阶段演员107005037只保留金币，不产生第二套装备或材料");
        var empty = new DungeonDropGenerationPlan("regression", []);
        var plan = policy.Extend(empty, entry, named);
        That(plan.ResourceSource.Contains("otherverse-rewards-server-v2") && plan.ResourceSource.Contains(DimensionRewardPolicy.AuraRule) && plan.ResourceSource.Contains(DimensionRewardPolicy.HolyAuraRule), "v2计划标记实际规则及本人拍板普通/神圣气息规则");
        That(plan.Trials.Count(t => !t.RarityPools.IsDefaultOrEmpty) == 1, "R1 勇士绿名本人有一次职业装备判定");
        That(MaterialCount(plan, 3311) == 7 && MaterialCount(plan, 10303569) == 5, "R1 勇士绿名固定发放异次元碎片7个、荒古的记忆碎片5个");
        var generatedNamed = plan.Generate(_ => 0, DateTimeOffset.UtcNow);
        That(generatedNamed.Single(row => row.Item?.ItemId == 3311).Item is { Count: 7 } &&
            generatedNamed.Single(row => row.Item?.ItemId == 10303569).Item is { Count: 5 }, "R1 绿名材料实际生成堆叠数量为7与5");
        var aura = plan.Trials.Single(t => t.Items?.Entries.Any(r => r.Template.Id == 1256) == true);
        That(aura is { Threshold: 1, ProbabilityDenominator: 1, MinimumCount: 1, MaximumCount: 1 } &&
            aura.Items!.Entries.Select(r => r.Template.Id).SequenceEqual([1254, 1255, 1256]),
            "R1 绿名三色气息为100%单次掉落且三色等权");
        var auraOnly = new DungeonDropGenerationPlan("aura-test", [aura]);
        for (int color = 0; color < 3; color++)
        {
            int expected = 1254 + color;
            That(auraOnly.Generate(bound => bound == 3 ? color : 0, DateTimeOffset.UtcNow) is [{ Item.ItemId: var itemId, Item.Count: 1 }] &&
                itemId == expected, "R1 三色气息等概率实际抽样颜色=" + expected);
        }
        var holy = plan.Trials.Single(t => t.Items?.Entries.Any(r => r.Template.Id == 10090760) == true);
        That(holy is { Threshold: 10, ProbabilityDenominator: 100, MinimumCount: 1, MaximumCount: 1 } &&
            holy.Items!.Entries.Select(r => r.Template.Id).SequenceEqual([10090758, 10090759, 10090760]),
            "R1 勇士绿名神圣气息追加概率10%且三色等权");
        var holyOnly = new DungeonDropGenerationPlan("holy-aura-test", [holy]);
        for (int color = 0; color < 3; color++)
        {
            int expected = 10090758 + color;
            That(holyOnly.Generate(bound => bound == 3 ? color : 0, DateTimeOffset.UtcNow) is [{ Item.ItemId: var itemId, Item.Count: 1 }] &&
                itemId == expected, "R1 神圣气息等概率实际抽样颜色=" + expected);
        }
        var boss = named with { Source = source.Monster with { RankKind = "[boss]", Modifiers = [] }, SourceMap = entry.BossMap };
        var bossPlan = policy.Extend(empty, entry, boss);
        That(bossPlan.ResourceSource.Contains(DimensionRewardPolicy.BossMaterialRule), "领主整组材料计划标记本人拍板规则");
        That(!bossPlan.Trials.Any(t => t.Items?.Entries.Any(r => r.Template.Id is 1254 or 1255 or 1256 or 10090758 or 10090759 or 10090760) == true), "R1 终点Boss不额外触发绿名普通或神圣气息");
        int equipment = bossPlan.Generate(_ => 0, DateTimeOffset.UtcNow).Count(row => row.Item is { } item &&
            catalog.Professions[(entry.DimensionRewardProfession!.Value.Job, entry.DimensionRewardProfession.Value.Grow & 15)].Set.Contains(item.ItemId));
        That(equipment == 2, "R2 勇士Boss两次判定可命中套装池");
        var bossDrops = bossPlan.Generate(_ => 0, DateTimeOffset.UtcNow).Where(r => r.Item is not null).Select(r => r.Item!).ToArray();
        var shardStacks = bossDrops.Where(row => row.ItemId == 3311).ToArray();
        var memoryStacks = bossDrops.Where(row => row.ItemId == 10303569).ToArray();
        That(shardStacks.Length == 3 && shardStacks.All(row => row.Count == 7) &&
            memoryStacks.Length == 5 && memoryStacks.All(row => row.Count == 4),
            "R2 领主材料为3311三堆各7、10303569五堆各4，不保留基础1份");
        var hiddenEntry = entry with { Dimension = entry.Dimension! with { HiddenBoss = true } };
        var terminalHiddenPlan = policy.Extend(empty, hiddenEntry, boss);
        var hiddenTableIds = dimension.HiddenRewards.Where(r => r.Tier == 2).Select(r => r.Item).ToHashSet();
        That(!terminalHiddenPlan.Trials.Any(t => t.Items?.Entries.Any(r => r.Template.Id == 10304874 || hiddenTableIds.Contains(r.Template.Id)) == true),
            "R2 v2不把依普利乌斯直接奖励或旧隐藏表错挂到普通终点Boss");

        using var hiddenFixture = new Fixture(pvf, TownCatalog.Load(pvf), items, DungeonIncountCatalog.Load(pvf), 2);
        var hiddenOpening = hiddenFixture.Enter(7167, 2, _ => 0);
        var iflius = hiddenOpening.Entry.Maze.MapSpecification!
            .Select(map => DungeonMonsterPlan.FromRoom(rooms.Get(map, requirePassiveDefinitions: false), 1, 100,
                hiddenOpening.Entry.BasisLevel ?? 95))
            .SelectMany(plan => plan.Monsters)
            .FirstOrDefault(monster => monster.MonsterId is 107000256 or 107000257 or 107000258);
        That(hiddenOpening.Entry.Dimension is { HiddenBoss: true } && iflius is not null,
            "R2 真实隐藏迷宫包含依普利乌斯随机奖励怪物");
        if (iflius is not null)
        {
            var ifliusPlan = policy.Extend(empty, hiddenOpening.Entry, iflius);
            var ifliusDrops = ifliusPlan.Generate(_ => 0, DateTimeOffset.UtcNow)
                .Where(row => row.Item is not null).Select(row => row.Item!).ToArray();
            var ifliusShards = ifliusDrops.Where(row => row.ItemId == 3311).ToArray();
            var ifliusMemories = ifliusDrops.Where(row => row.ItemId == 10303569).ToArray();
            var ifliusInvites = ifliusDrops.Where(row => row.ItemId == 3330).ToArray();
            var ifliusStones = ifliusDrops.Where(row => row.ItemId == 10000204).ToArray();
            That(ifliusPlan.ResourceSource.Contains(DimensionRewardPolicy.IfliusRule) &&
                ifliusShards.Length == 3 && ifliusShards.All(row => row.Count == 10) &&
                ifliusMemories.Length == 4 && ifliusMemories.All(row => row.Count == 6) &&
                ifliusInvites.Length == 4 && ifliusInvites.All(row => row.Count == 5) &&
                ifliusStones.Length == 2 && ifliusStones.All(row => row.Count == 8),
                "R2 依普利乌斯按独立堆叠掉3311×3堆10个、10303569×4堆6个、3330×4堆5个、10000204×2堆8个");
            That(ifliusDrops.Count(row => hiddenTableIds.Contains(row.ItemId)) == 1 &&
                !ifliusDrops.Any(row => row.ItemId == 10304874),
                "R2 依普利乌斯另掉1件远古稀有奖励且不掉10304874礼盒");
        }
        var setOnlyCard = policy.Card(7167, 0, 1, _ => 0, DateTimeOffset.UtcNow);
        That(setOnlyCard is not null && catalog.Professions[(0, 1)].Set.Contains(setOnlyCard.ItemId) &&
            !catalog.Professions[(0, 1)].Normal.Contains(setOnlyCard.ItemId),
            "R2 定向回归确认翻牌只从套装池产出");
        var now = DateTimeOffset.UtcNow;
        foreach (int tier in Enumerable.Range(0, 3))
        {
            var current = entry with { Dimension = entry.Dimension! with { Tier = tier } };
            var namedPlan = policy.Extend(empty, current, named);
            var n = namedPlan.Trials.Single(t => !t.RarityPools.IsDefaultOrEmpty);
            That(n.Threshold == new[] { 60, 80, 100 }[tier] && n.ProbabilityDenominator == 100,
                "绿名三档掉装概率不借翻牌字段");
            var holyTrial = namedPlan.Trials.Single(t => t.Items?.Entries.Any(r => r.Template.Id == 10090760) == true);
            int holyThreshold = new[] { 1, 5, 10 }[tier];
            That(holyTrial.Threshold == holyThreshold && holyTrial.ProbabilityDenominator == 100,
                "神圣气息难度概率为1%/5%/10%");
            var holyOnlyTier = new DungeonDropGenerationPlan("holy-tier", [holyTrial]);
            That(holyOnlyTier.Generate(bound => bound == 3 ? 0 : holyThreshold - 1, now).Length == 1 &&
                holyOnlyTier.Generate(bound => bound == 3 ? 0 : holyThreshold, now).Length == 0,
                "神圣气息概率命中与未命中边界");
            foreach (var actor in new[] { named, boss })
            {
                var selected = policy.Extend(empty, current, actor);
                if (tier == 0) That(!selected.Trials.Any(t => t.Items?.Entries.Any(r => r.Template.Id == 10303569) == true), "R1 普通档材料表不含荒古的记忆碎片");
                if (tier == 0)
                    That(selected.Generate(_ => 0, now).Where(r => r.Item is { Kind: ItemKind.Equipment })
                        .All(r => catalog.Professions[(0, 1)].Normal.Contains(r.Item!.ItemId)), "R3 普通档从不进入套装池");
                var extra = current with { Dimension = current.Dimension! with { DailyBefore = new(dimension.BaseDailyLimit, 0) } };
                var extraEquipment = policy.Extend(empty, extra, actor);
                That(extraEquipment.Generate(_ => 0, now).Any(r => r.Item is { } item && catalog.Professions.Values.Any(p => p.Normal.Contains(item.ItemId) || p.Set.Contains(item.ItemId))), "R4 追加次数保留职业装备");
                if (actor.IsBoss) That(extraEquipment.Trials.Any(t => t.Items?.Entries.Any(r => r.Template.Id == 3311) == true), "R4 材料保留");
                var disabled = new DimensionRewardPolicy(dimension, catalog, items.Items, DimensionDropRules.Parse("{\"extraEntryEquipment\":false}"));
                That(!disabled.Extend(empty, extra, actor).Generate(_ => 0, now).Any(r => r.Item is { } item && catalog.Professions.Values.Any(p => p.Normal.Contains(item.ItemId) || p.Set.Contains(item.ItemId))), "R4 显式关闭追加装备仍生效");
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
        That(!policy.Extend(empty, entry with { DimensionRewardProfession = (255, 15) }, named).Trials.Any(t => !t.RarityPools.IsDefaultOrEmpty),
            "R8 两池都空跳过职业装备，材料仍按资源发放");
        foreach (var actor in new[] { named with { IsFriendly = true }, named with { IsBlocking = false },
            named with { AiCharacter = new(1, [], "[monster]", "[normal]", "synthetic", 95) } })
            That(ReferenceEquals(policy.Extend(empty, entry, actor), empty), "R9 友方、非法死亡、APC不掉");
        That(ReferenceEquals(policy.Extend(empty, entry with { IsPractice = true }, boss), empty), "R9 练习不掉");
        That(ReferenceEquals(policy.Extend(empty, entry with { Dimension = null }, boss), empty), "R9 非异界不掉");
        var card = new DimensionRewardPolicy(dimension, catalog, items.Items, DimensionDropRules.Parse("{\"setRateSource\":\"card\"}"));
        That(card.Extend(empty, entry, named).Trials.Single(t => !t.RarityPools.IsDefaultOrEmpty).RarityPools.Single().AlternatePermille == catalog.CardRates[2].SetPercent * 10,
            "显式card来源沿翻牌套装率");
        That(policy.Extend(empty, entry with { Dimension = entry.Dimension! with { DailyBefore = new(dimension.BaseDailyLimit, 0) } }, named)
                .Trials.Count(t => !t.RarityPools.IsDefaultOrEmpty) == 1,
            "默认规则在追加券入场仍保留绿名职业装备判定");
        var extraPolicy = new DimensionRewardPolicy(dimension, catalog, items.Items, DimensionDropRules.Parse("{\"extraEntryEquipment\":true}"));
        That(extraPolicy.Extend(empty, entry with { Dimension = entry.Dimension! with { DailyBefore = new(10, 0) } }, named).Trials.Count(t => !t.RarityPools.IsDefaultOrEmpty) == 1,
            "显式追加开关允许装备");
        Console.WriteLine("PASS #899 R1-R9 资源及个人掉落计划（非TCP客户端验收）");
        DungeonItemDropPool Pool(IEnumerable<int> ids) => new(ids.Select(id => new DungeonWeightedDropItem(items.Items.Get(id), 1)));
        int MaterialCount(DungeonDropGenerationPlan generation, int id) => generation.Trials.Single(t => t.Items?.Entries.Any(r => r.Template.Id == id) == true).MinimumCount;

    }

    public static void Run(PvfArchive pvf, ItemResources items)
    {
        int before = Count;
        var baseDrops = new DungeonResourceDropPolicy(SelfTestCatalogs.DropRules(pvf), items.DungeonDropItems,
            id => new(id, "monster/synthetic.mob", null, null), items.Items.Get);

        var dungeons = SelfTestCatalogs.Dungeons(pvf);
        var rooms = SelfTestCatalogs.Rooms(pvf);
        var incounts = DungeonIncountCatalog.Load(pvf);
        var dimension = incounts.Dimension!;
        var rewardCatalog = new DimensionRewardCatalog(pvf, items.Items);
        var rewardPolicy = new DimensionRewardPolicy(dimension, rewardCatalog, items.Items);
        foreach (var row in rewardCatalog.Professions)
        {
            var card = rewardPolicy.Card(7167, (byte)row.Key.Job, (byte)row.Key.Grow, _ => 0, DateTimeOffset.UtcNow);
            if (row.Value.Set.IsEmpty)
                That(card is null, "异界空套装池不生成翻牌装备，不回退裂魂散件");
            else
                That(card is not null && row.Value.Set.Contains(card.ItemId) && !row.Value.Normal.Contains(card.ItemId),
                    "异界非空套装池翻牌必出套装，不回退裂魂散件");
        }
        foreach (int id in new[] { 10158125, 10158126, 490008217, 10158692, 10161066, 490003748, 490009451 })
            That(items.Items.ActionConsumable(id) is { Label: "[add dimension dungeon enter count]", Parameters: [1 or 5] },
                "七种正式追加券完整读取原动作、地点和冷却：" + id);
        var towns = TownCatalog.Load(pvf);
        That(dimension.Tiers.Count == 15 && dimension.DailyLimit == 10 && dimension.BaseDailyLimit == 5 && dimension.ExtraLimit == 5 && dimension.HiddenWeeklyLimit == 2,
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
            for (int used = 0; used < dimension.BaseDailyLimit; used++)
            {
                int dungeon = 7165 + used % 3;
                byte difficulty = checked((byte)dimension.Tiers[dungeon]);
                var opening = f.Enter(dungeon, difficulty, _ => 0);
                That(opening.Entry.Dimension is { Tier: var tier } && tier == difficulty &&
                    opening.Entry.Difficulty == difficulty && opening.Entry.Dimension.HiddenBoss == (used < 2),
                    "基础入场按实际图号档位选难度，隐藏领主每周最多两次，其后切普通路线");
                That(f.Session.ReturnFromFormalDungeon() is not null, "异界返城保留个人已用次数");
            }
            That(f.Session.BeginDungeonSelection() is not null && f.Session.EnterFormalDungeon(7165, 0, 0, dungeons, rooms, pieces: items.EpicPieces) is null,
                "基础五次用尽后不能换图绕过上限");
            That(f.Usage(dimension.DailyRow.CountKey).DailyUsed == dimension.BaseDailyLimit && f.Usage(7100).WeeklyUsed == 2,
                "基础日次数和隐藏周次数均持久保存");
            f.Session.ReturnToRoster(); f.Session.Select(0, resumeTown: true); f.Session.MarkReady();
            That(f.Session.DimensionEntranceInfo() is (5, 0, 0),
                "重选角色仍投影基础五次用尽、追加为零、隐藏剩余零");
            var granted = f.Session.PlanAndCommitInventory(character => new InventoryService(items.Items, items.GoldLimits, items.EpicPieces)
                .Grant(character, [new(10161066, 2)]));
            That(granted.Accepted, "合成夹具发放两张真实五次券");
            var source = f.Session.Selected!.Inventory.Items.Single(item => item.Core.ItemId == 10161066).Position;
            var tickets = new DimensionEntryTicketService(items.Items);
            var ticketBefore = f.Read();
            var onlineBefore = f.Session.Selected!;
            var usageBeforeTicket = f.Usage(dimension.DailyRow.CountKey);
            f.Sql("CREATE TRIGGER dimension_ticket_abort AFTER INSERT ON inventory_audit_log WHEN NEW.action_name='dimension_entry_ticket' BEGIN SELECT RAISE(ABORT,'dimension ticket rollback'); END");
            Reject<SqliteException>(() => f.Session.UseDimensionEntryTicket(source, tickets), "首张合法追加券库存审计末段SQL失败");
            That(QuestCompletionRepositoryTests.Same(ticketBefore, f.Read()) &&
                QuestCompletionRepositoryTests.Same(onlineBefore, f.Session.Selected!) &&
                f.ItemCount(10161066) == 2 && f.Session.DimensionEntranceInfo() is (5, 0, 0) &&
                f.Usage(dimension.DailyRow.CountKey) == usageBeforeTicket && f.Usage(7100).WeeklyUsed == 2,
                "失败完整回滚票数量、额度、revision和在线状态，日周次数不变");
            f.Sql("DROP TRIGGER dimension_ticket_abort");
            That(f.Session.UseDimensionEntryTicket(source, tickets).Accepted && f.Session.DimensionEntranceInfo() is (5, 5, 0) &&
                f.ItemCount(10161066) == 1 && DimensionEntryTicketService.Extra(f.Read().Inventory, tickets.Clock.GetUtcNow()) == 5,
                "移除故障后首张券只提交一次且增加五次");
            var capped = f.Read();
            That(!f.Session.UseDimensionEntryTicket(source, tickets).Accepted &&
                QuestCompletionRepositoryTests.Same(capped, f.Read()) && QuestCompletionRepositoryTests.Same(capped, f.Session.Selected!),
                "到上限后重复请求整拒，保留第二张券和全部资产");
            f.Session.ReturnToRoster(); f.Session.Select(0, resumeTown: true); f.Session.MarkReady();
            That(f.Session.DimensionEntranceInfo() is (5, 5, 0) && f.ItemCount(10161066) == 1,
                "重选角色保留五次追加与未消费的第二张券");
            const int extraCapacity = 5;
            for (int extra = 0; extra < extraCapacity; extra++)
            {
                int dungeon = 7165 + extra % 3;
                byte difficulty = checked((byte)dimension.Tiers[dungeon]);
                var extraOpening = f.Enter(dungeon, difficulty, _ => 0);
                var remaining = f.Session.DimensionEntranceInfo();
                That(extraOpening.Entry.Dimension is { HiddenBoss: false, Tier: var tier } && tier == difficulty &&
                    extraOpening.Entry.Difficulty == difficulty && remaining is (5, var extraRemaining, 0) &&
                    extraRemaining == (uint)(extraCapacity - extra - 1) &&
                    f.Usage(dimension.DailyRow.CountKey).DailyUsed == dimension.BaseDailyLimit + extra + 1 &&
                    f.Usage(7100).WeeklyUsed == 2 && f.ItemCount(10161066) == 1,
                    "跨图追加入场逐次扣额外剩余并持久共用日次数，不重置隐藏周额度");
                That(f.Session.ReturnFromFormalDungeon() is not null, "额外入场可正常返城");
            }
            var entriesExhausted = f.Read();
            That(f.Usage(dimension.DailyRow.CountKey).DailyUsed == dimension.BaseDailyLimit + extraCapacity &&
                f.Session.BeginDungeonSelection() is not null && f.Session.EnterFormalDungeon(7165, 0, 0, dungeons, rooms, pieces: items.EpicPieces) is null &&
                QuestCompletionRepositoryTests.Same(entriesExhausted, f.Read()) && f.Session.DungeonSnapshot is null,
                "基础五次加五次合计十次，第十一次被拒且不扣资产或安装实例");
            f.Session.ReturnToRoster(); f.Session.Select(0, resumeTown: true); f.Session.MarkReady();
            That(f.Session.DimensionEntranceInfo() is (5, 0, 0) &&
                f.Usage(dimension.DailyRow.CountKey).DailyUsed == 10 &&
                DimensionEntryTicketService.Extra(f.Read().Inventory, tickets.Clock.GetUtcNow()) == 5 &&
                f.ItemCount(10161066) == 1,
                "十次用尽后重选仍保留原五次额度，不因用尽恢复追加资格");
        }
        using (var f = new Fixture(pvf, towns, items, incounts, 2))
        {
            var opening = f.Enter(7177, 0, bound => bound == 10000 ? 9999 : 0);
            var settled = Clear(f, opening);
            // Clear已完成翻牌；按真实handler从当前结果焦点取新回执，不能复用领奖前角色快照。
            var current = f.Session.FormalDungeonResultFocus();
            That(current is not null && current.Snapshot.Identity == settled.Receipt!.Snapshot.Identity &&
                f.Session.CanPublishFormalDungeon(current), "翻牌后再次挑战使用当前结算实例和有效角色快照");
            int[] retryTargets = [.. dimension.Tiers.Where(row => row.Value == opening.Entry.Dimension!.Tier)
                .Select(row => row.Key).Order()];
            var beforeRetry = f.Usage(dimension.DailyRow.CountKey);
            var hiddenBefore = f.Usage(DimensionDungeonCatalog.Entrance);
            var retry = f.Session.RetryFormalDungeon(current!, dungeons, rooms, SelfTestCatalogs.MonsterDrops(pvf), items.Items, items.EpicPieces);
            That(retry is not null && retryTargets.Length == 5 && retryTargets.Contains(checked((int)retry.Entry.DungeonId)) &&
                retry.Entry.DungeonId == retryTargets[0] && retry.Entry.DungeonId != opening.Entry.DungeonId &&
                retry.Entry.Dimension is { HiddenBoss: false } && retry.Entry.Dimension.Tier == opening.Entry.Dimension!.Tier &&
                retry.Entry.Difficulty == opening.Entry.Difficulty && retry.Snapshot.Identity.InstanceId != current!.Snapshot.Identity.InstanceId &&
                f.Usage(dimension.DailyRow.CountKey).DailyUsed == beforeRetry.DailyUsed + 1 &&
                f.Usage(DimensionDungeonCatalog.Entrance).WeeklyUsed == hiddenBefore.WeeklyUsed,
                $"再次挑战按原注入抽样选当前档位首图、安装新实例并持久扣次：actual={retry?.Entry.DungeonId},reason={f.Session.LastFormalDungeonRejection}");
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

        FormalDungeonSettlementResult Clear(Fixture f, FormalDungeonReceipt opening, bool bonus = false)
        {
            var path = QuestStoryRouteTests.Path(opening.Entry.Maze.StaticTopology, opening.Entry.StartMap.Position, opening.Entry.BossMap.Position);
            if (opening.Entry.Dimension is { HiddenBoss: true })
            {
                var ifliusRoom = opening.Entry.Maze.MapSpecification!.Single(map => rooms.Get(map, requirePassiveDefinitions: false).Monsters
                    .Any(monster => monster.MonsterId is 107000256 or 107000257 or 107000258));
                var toIflius = QuestStoryRouteTests.Path(opening.Entry.Maze.StaticTopology, opening.Entry.StartMap.Position, ifliusRoom.Position);
                That(!toIflius.SkipLast(1).Contains(opening.Entry.BossMap.Position), "隐藏奖励支路可在终点领主之前实际到达");
                path = toIflius.Concat(QuestStoryRouteTests.Path(opening.Entry.Maze.StaticTopology, ifliusRoom.Position,
                    opening.Entry.BossMap.Position).Skip(1)).ToArray();
            }
            bool observedIflius = false;
            for (int at = 0; at < path.Length; at++)
            {
                var loaded = f.Session.CompleteFormalDungeonLoading()!;
                if (opening.Entry.Dimension is { HiddenBoss: true })
                {
                    foreach (var iflius in loaded.Monsters.Monsters.Where(monster => monster.MonsterId is 107000256 or 107000257 or 107000258))
                    {
                        observedIflius = true;
                        var hiddenDrops = new DungeonResourceDropPolicy(SelfTestCatalogs.DropRules(pvf), items.DungeonDropItems,
                            id => new(id, "monster/synthetic.mob", null, null), items.Items.Get, dimensionRewards: rewardPolicy);
                        var hiddenPlan = hiddenDrops.PrepareEntry(opening.Entry)(iflius);
                        That(hiddenPlan.ResourceSource.Contains(DimensionRewardPolicy.IfliusRule),
                            "真实隐藏路线依普利乌斯使用直接奖励合同：" + iflius.MonsterId);
                        foreach (var material in new[] { (Id: 3311, Stacks: 3, Count: 10), (Id: 10303569, Stacks: 4, Count: 6), (Id: 3330, Stacks: 4, Count: 5), (Id: 10000204, Stacks: 2, Count: 8) })
                        {
                            var trial = hiddenPlan.Trials.Single(t => t.Items?.Entries.Any(r => r.Template.Id == material.Id) == true);
                            That(trial.Threshold == 1 && trial.ProbabilityDenominator == 1 && trial.Attempts == material.Stacks &&
                                trial.MinimumCount == material.Count && trial.MaximumCount == material.Count,
                                "真实依普利乌斯直接材料保留原堆数和每堆数量：" + material.Id);
                        }
                        var rareIds = dimension.HiddenRewards.Where(row => row.Tier == opening.Entry.Dimension!.Tier).Select(row => row.Item).ToHashSet();
                        That(hiddenPlan.Trials.Count(t => t.Items is { } pool && pool.Entries.Length > 0 &&
                            pool.Entries.All(row => rareIds.Contains(row.Template.Id)) && t.Threshold == 1 &&
                            t.ProbabilityDenominator == 1 && t.Attempts == 1 && t.MinimumCount == 1 && t.MaximumCount == 1) == 1 &&
                            !hiddenPlan.Trials.Any(t => t.Items?.Entries.Any(row => row.Template.Id == 10304874) == true),
                            "真实依普利乌斯直接抽取本档一件稀有奖励，不再发10304874礼盒");
                    }
                }
                if (opening.Entry.Dimension is not null && loaded.Monsters.Monsters.FirstOrDefault(monster => monster.IsBoss &&
                    monster.SourceMap is { } map && opening.Entry.IsTerminalMap(map)) is { } boss)
                {
                    var drops = new DungeonResourceDropPolicy(SelfTestCatalogs.DropRules(pvf), items.DungeonDropItems,
                        id => new(id, "monster/synthetic.mob", null, null), items.Items.Get, dimensionRewards: rewardPolicy);
                    var middle = boss with { SourceMap = boss.SourceMap! with { Position = new(-1, -1) } };
                    var forward = drops.PrepareEntry(opening.Entry);
                    var middlePlan = forward(middle);
                    var endPlan = forward(boss);
                    var reverse = drops.PrepareEntry(opening.Entry);
                    // 逐只直接调用专用策略作为无缓存参照；v2替换基础骰，总骰数不具有奖励多少的含义。
                    var raw = baseDrops.Prepare(boss.MonsterId, boss.CandidateLevel, boss.RewardType,
                        opening.Entry.Difficulty, opening.Entry.BasisLevel);
                    var expectedMiddle = rewardPolicy.Extend(raw, opening.Entry, middle);
                    var expectedEnd = rewardPolicy.Extend(raw, opening.Entry, boss);
                    bool iflius = boss.MonsterId is 107000256 or 107000257 or 107000258;
                    string terminalRule = iflius ? DimensionRewardPolicy.IfliusRule :
                        boss.MonsterId == 107005037 && (boss.IsDummyMapBoss || boss.IsScriptedBoss)
                            ? DimensionRewardPolicy.LotusDummyRule : DimensionRewardPolicy.BossMaterialRule;
                    var reverseEnd = reverse(boss);
                    var reverseMiddle = reverse(middle);
                    // 专属规则替换基础装备/材料骰，终点骰数不一定更多；按真实奖励来源区别位置。
                    That((iflius ? SamePlan(expectedMiddle, expectedEnd) : !SamePlan(expectedMiddle, expectedEnd)) &&
                        SamePlan(middlePlan, expectedMiddle) && SamePlan(endPlan, expectedEnd) &&
                        SamePlan(reverseEnd, expectedEnd) && SamePlan(reverseMiddle, expectedMiddle) &&
                        endPlan.ResourceSource.Contains(terminalRule, StringComparison.Ordinal) &&
                        middlePlan.ResourceSource.Contains(terminalRule, StringComparison.Ordinal) == iflius &&
                        (!iflius || endPlan.Trials.Length == middlePlan.Trials.Length) &&
                        reverseEnd.ResourceSource == endPlan.ResourceSource && reverseEnd.Trials.Length == endPlan.Trials.Length &&
                        reverseMiddle.ResourceSource == middlePlan.ResourceSource && reverseMiddle.Trials.Length == middlePlan.Trials.Length,
                        $"同模板中途和终点Boss正反调用保留普通领主位置区别及依普利乌斯直接奖励：dungeon={opening.Entry.DungeonId},monster={boss.MonsterId},scripted={boss.IsScriptedBoss},friendly={boss.IsFriendly},apc={boss.AiCharacter is not null},trials={middlePlan.Trials.Length}/{endPlan.Trials.Length}");
                }
                foreach (var monster in loaded.Monsters.Monsters.Where(monster => monster.IsBlocking &&
                    loaded.Snapshot.LivingMonsters.Contains(monster.ObjectKey)).OrderBy(monster => monster.IsBoss))
                    That(f.Session.ObserveFormalDungeonDeath(FormalDungeonSessionTests.Observation(loaded, monster),
                        SelfTestCatalogs.KillExperience(pvf), SelfTestCatalogs.Growth(pvf)).Outcome == DungeonRunOutcome.Accepted,
                        "远古仅报告实际当前房间演员死亡");
                if (at + 1 < path.Length)
                    That(f.Session.MoveFormalDungeon((byte)path[at + 1].X, (byte)path[at + 1].Y) is not null, "远古沿真实门墙连接清房");
            }
            if (opening.Entry.Dimension is { HiddenBoss: true })
                That(observedIflius, "真实隐藏路线沿实际房间观察到依普利乌斯奖励怪物");
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
            return result;

        }
    }

    static bool SamePlan(DungeonDropGenerationPlan actual, DungeonDropGenerationPlan expected) =>
        actual.ResourceSource == expected.ResourceSource && actual.MaximumObjects == expected.MaximumObjects &&
        actual.ReferenceQuality == expected.ReferenceQuality && actual.OrdinaryLukeDailySource == expected.OrdinaryLukeDailySource &&
        ReferenceEquals(actual.Amplification, expected.Amplification) && actual.Trials.Length == expected.Trials.Length &&
        actual.Trials.Zip(expected.Trials).All(pair => SameTrial(pair.First, pair.Second));

    static bool SameTrial(DungeonDropTrial actual, DungeonDropTrial expected) =>
        actual with { Items = expected.Items, RarityPools = expected.RarityPools,
            EpicReplacementItems = expected.EpicReplacementItems, ItemCounts = expected.ItemCounts } == expected &&
        SamePool(actual.Items, expected.Items) && SamePool(actual.EpicReplacementItems, expected.EpicReplacementItems) &&
        (actual.ItemCounts is null ? expected.ItemCounts is null : expected.ItemCounts is not null &&
            actual.ItemCounts.OrderBy(row => row.Key).SequenceEqual(expected.ItemCounts.OrderBy(row => row.Key))) &&
        actual.RarityPools.IsDefault == expected.RarityPools.IsDefault &&
        (actual.RarityPools.IsDefault || actual.RarityPools.Length == expected.RarityPools.Length &&
            actual.RarityPools.Zip(expected.RarityPools).All(pair =>
                pair.First with { Items = pair.Second.Items, AlternateItems = pair.Second.AlternateItems,
                    SecondaryItems = pair.Second.SecondaryItems } == pair.Second &&
                SamePool(pair.First.Items, pair.Second.Items) &&
                SamePool(pair.First.AlternateItems, pair.Second.AlternateItems) &&
                SamePool(pair.First.SecondaryItems, pair.Second.SecondaryItems)));

    static bool SamePool(DungeonItemDropPool? actual, DungeonItemDropPool? expected) =>
        actual is null ? expected is null : expected is not null && actual.TotalWeight == expected.TotalWeight &&
            actual.Entries.SequenceEqual(expected.Entries);

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
        public void Dispose() { Session.End(); Owner.Dispose(); TestFiles.DeleteDirectory(root, true); }
    }
}
