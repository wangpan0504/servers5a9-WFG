using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Settings;
using DfoServer.GameWorld.Items;
using DfoServer.Network.Parsers.Inventory;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static class LotteryTests
{
    internal const int SourceId = 490705163;
    public static void Run(TcpTestResources resources)
    {
        int checks = Count;
        var items = resources.ItemResources;
        LegacyExpiryFixture.Restore(items.Items, Enumerable.Range(490705157, 7).Select(id => (id, "2020-03-19 06:00:00")).ToArray());
        var random = new EdgeRandom();
        var settings = new GameDebugSettings();
        var service = new InventoryBoosterService(items.Items, items.Boosters, items.GoldLimits, items.EpicPieces,
            random, settings, items.Contracts);
        var now = DateTimeOffset.UtcNow;
        var role = new Character(1, 0, "Lottery", 0, resources.Towns.Room, resources.Towns.InitialReturn) { Job = 15 };
        var definition = items.Boosters.Get(SourceId);
        That(definition.Available && definition.Lottery && definition.Pools is [{ Draws: 1 }], "真实袖珍罐为独立单次抽取入口");
        var rows = definition.Pools[0].Rewards;
        That(rows.Select(row => row.ItemId).SequenceEqual(Enumerable.Range(490705157, 6)) &&
            rows.Select(row => row.Weight).SequenceEqual(new long[] { 3240, 100, 3230, 100, 3230, 100 }) &&
            rows.All(row => row.Count == 1) && rows.Sum(row => row.Weight) == 10000, "六项奖励、数量和总权重保持PVF原表");
        long start = 0;
        foreach (var row in rows)
        {
            foreach (long value in new[] { start, start + row.Weight - 1 })
            {
                random.Value = value;
                var source = role with { Inventory = new([new(1, Bag(117), Core(SourceId, 2)), new(2, Bag(65), Core(row.ItemId, 5))]) };
                var result = service.OpenLottery(source, new(0, 117), out var receipt, now);
                That(result.Accepted && receipt is { SourceSlot: 117, Count: 1 } && receipt.ItemId == row.ItemId &&
                    receipt.RewardSlot == Bag(65) && result.Plan!.After.Find(Bag(65))!.Core.Count == 6 &&
                    result.Plan.After.Find(Bag(117))!.Core.Count == 1 && source.Inventory.Find(Bag(117))!.Core.Count == 2,
                    "每个权重区间两端均产出真实一份奖励并合并到既有槽位");
            }
            start += row.Weight;
        }
        That(start == 10000, "逐行累计与独立求和一致");
        random.Value = 0;
        var normal = role with { Inventory = new([new(1, Bag(117), Core(SourceId, 2))]) };
        foreach (var request in new[] { new LotteryOpenRequest(1, 117), new(ushort.MaxValue, 117), new(0, -1), new(0, 116) })
            That(!service.OpenLottery(normal, request, out _, now).Accepted, "未知模式与无效源不消费");
        That(!service.Open(normal, new(117, 1), out _, now).Accepted, "袖珍罐不能绕入普通礼包入口");
        That(!service.OpenLottery(role with { Inventory = new([new(1, Bag(117), Core(7542))]) }, new(0, 117), out _, now).Accepted,
            "普通礼包不能绕入袖珍罐入口");
        foreach (var core in new[] { Core(SourceId) with { EquipmentLockId = 1 }, Core(SourceId) with { ExpireTime = checked((int)(now.ToUnixTimeSeconds() - 1)) } })
            That(!service.OpenLottery(role with { Inventory = new([new(1, Bag(117), core)]) }, new(0, 117), out _, now).Accepted,
                "锁定或到期源完整保留");
        var range = ItemSlotBoundService.Range(ItemKind.Consumable, normal.Inventory.Expansion);
        var full = Enumerable.Range(range.Start, range.End - range.Start + 1)
            .Select(slot => new InventoryItem(slot, Bag((short)slot), Core(slot == 117 ? SourceId : 7542, slot == 117 ? 2 : 1))).ToArray();
        var crowded = role with { Inventory = new([.. full]) };
        That(!service.OpenLottery(crowded, new(0, 117), out _, now).Accepted && crowded.Inventory.Find(Bag(117))!.Core.Count == 2,
            "源剩余堆叠占格时满包拒绝且不扣罐子");
        crowded = role with { Inventory = new([.. full.Select(item => item.Position == Bag(117) ? item with { Core = Core(SourceId) } : item)]) };
        var freed = service.OpenLottery(crowded, new(0, 117), out var freedReceipt, now);
        That(freed.Accepted && freedReceipt!.RewardSlot == Bag(117) && freed.Plan!.After.Find(Bag(117))!.Core.ItemId == rows[0].ItemId,
            "最后一只罐子释放源格后原格接收奖励");
        settings.IgnoreCashShopAbsoluteExpiration = false;
        int calls = random.Calls;
        That(!service.OpenLottery(normal, new(0, 117), out _, now).Accepted && random.Calls == calls,
            "遵守既有期限开关并在抽取前拒绝过期候选");
        for (int length = 0; length <= 8; length++)
        {
            if (length == 4) continue;
            bool rejected = false;
            try { BoosterRequests.ReadLottery(new byte[length]); }
            catch (InvalidDataException) { rejected = true; }
            That(rejected, "袖珍罐完整4B请求边界 " + length);
        }
        That(BoosterRequests.ReadLottery(Convert.FromHexString("00007500")) == new LotteryOpenRequest(0, 117), "实机请求源槽位解码");
        const string header = "[stackable type]\n`[upgradable legacy]` 1\n[int data]\n";
        foreach (string invalid in new[] { "", "1 1", "1 0 1", "1 -1 1", "0 1 1", "1 1 0", "1 1 1 2" })
            That(!BoosterCatalog.Parse(header + invalid + "\n[/int data]").Available, "无效或零权重奖池不启用 " + invalid);
        foreach (string tail in new[] { "\n[int data]\n2 1 1", "\n[/int data]" })
            That(!BoosterCatalog.Parse(header + "10 1 2\n[/int data]" + tail).Available,
                "袖珍罐不忽略额外未闭合奖池或孤立结束标记");
        var duplicate = BoosterCatalog.Parse(header + "10 1 2 10 2 3\n[/int data]");
        That(duplicate.Available && duplicate.Pools[0].Rewards.Length == 2, "重复编号的不同行保留各自权重和数量");
        Console.WriteLine($"PASS {Count - checks} 袖珍罐资源、权重与原子规划断言");
        ItemCore Core(int id, int count = 1) => InventoryRewardGrantService.Create(items.Items.Get(id), count, now, shopExpiration: true);
    }

    // 仅使用旧生产已存在的API；同一测试文件先RED，再随生产候选GREEN。
    public static void RunMaterialCost(TcpTestResources resources)
    {
        const int pot = 490705287, material = 490705286;
        int checks = Count;
        var items = resources.ItemResources;
        var now = DateTimeOffset.UtcNow;
        var random = new EdgeRandom();
        var service = new InventoryBoosterService(items.Items, items.Boosters, items.GoldLimits, items.EpicPieces,
            random, contracts: items.Contracts);
        var role = new Character(1, 0, "PotCost", 0, resources.Towns.Room, resources.Towns.InitialReturn)
            { Job = 0, GrowType = 1, Level = 95 };
        var missingRole = role with { Inventory = new([new(1, Bag(117), Core(pot))]) };
        var fundedRole = role with { Inventory = new([new(1, Bag(117), Core(pot)), new(2, Bag(3), Core(material, 2))]) };
        string missingBefore = JsonSerializer.Serialize(missingRole), fundedBefore = JsonSerializer.Serialize(fundedRole);
        int calls = random.Calls;
        var missing = service.OpenLottery(missingRole, new(0, 117), out var missingReceipt, now);
        int missingDraws = random.Calls - calls;
        calls = random.Calls;
        var funded = service.OpenLottery(fundedRole, new(0, 117), out var fundedReceipt, now);
        int fundedDraws = random.Calls - calls;
        int fundedMaterial = funded.Plan?.After.Items.Where(item => item.Core.ItemId == material).Sum(item => item.Core.Count) ?? -1;
        bool missingPass = !missing.Accepted && missing.Plan is null && missingReceipt is null && missingDraws == 0 &&
            JsonSerializer.Serialize(missingRole) == missingBefore;
        bool fundedPass = funded.Accepted && fundedMaterial == 1 && fundedDraws == 1 &&
            fundedReceipt is { ItemId: 490705288, Count: 1 } && funded.Plan!.After.Find(Bag(117)) is null &&
            JsonSerializer.Serialize(fundedRole) == fundedBefore;
        // 两个结果都先独立计算并保存；第一条断言失败也不会吞掉第二条RED观察。
        Console.WriteLine(JsonSerializer.Serialize(new { Target = "lottery-material-two-independent-observations",
            Missing = new { Passed = missingPass, missing.Accepted, HasPlan = missing.Plan is not null, Draws = missingDraws },
            Funded = new { Passed = fundedPass, funded.Accepted, MaterialBefore = 2, MaterialAfter = fundedMaterial,
                Draws = fundedDraws, Reward = fundedReceipt }, ExpectedCost = 1 }));
        That(missingPass, "287无286材料必须不抽取、不扣罐、不产生奖励计划");
        That(fundedPass, "287合法开罐必须同计划扣1罐和1份286，完整发一份奖励");

        var definition = items.Boosters.Get(pot);
        var rows = definition.Pools.Single().Rewards;
        That(definition.Available && definition.Lottery && definition.Pools.Single().Draws == 1 &&
            rows.Select(row => row.ItemId).SequenceEqual(Enumerable.Range(490705288, 5)) &&
            rows.Select(row => row.Weight).SequenceEqual(new long[] { 75000, 15000, 4000, 4000, 2000 }) &&
            rows.All(row => row.Count == 1) && rows.Sum(row => row.Weight) == 100000,
            "287五项奖励、权重和单次数量逐行保持原资源");
        long start = 0;
        foreach (var row in rows)
        {
            foreach (long value in new[] { start, start + row.Weight - 1 })
            {
                random.Value = value;
                calls = random.Calls;
                var opened = service.OpenLottery(fundedRole, new(0, 117), out var receipt, now);
                That(opened.Accepted && receipt is { Count: 1 } && receipt.ItemId == row.ItemId &&
                    random.Calls == calls + 1 && random.LastMaximum == 100000 &&
                    opened.Plan!.After.Find(Bag(3))!.Core.Count == 1 && opened.Plan.After.Find(Bag(117)) is null &&
                    opened.Plan.After.Items.Where(item => item.Core.ItemId == row.ItemId).Sum(item => item.Core.Count) == 1 &&
                    JsonSerializer.Serialize(fundedRole) == fundedBefore,
                    "287每个权重区间两端完整扣材并发原行奖励 " + value);
            }
            start += row.Weight;
        }
        random.Value = 0;
        foreach (var invalidRole in new[]
        {
            fundedRole with { Level = 84 },
            fundedRole with { Inventory = fundedRole.Inventory with { Items = fundedRole.Inventory.Items.SetItem(0,
                fundedRole.Inventory.Items[0] with { Core = Core(pot) with { EquipmentLockId = 1 } }) } },
            fundedRole with { Inventory = fundedRole.Inventory with { Items = fundedRole.Inventory.Items.SetItem(0,
                fundedRole.Inventory.Items[0] with { Core = Core(pot) with { ExpireTime = (int)now.ToUnixTimeSeconds() } }) } }
        })
        {
            calls = random.Calls;
            string before = JsonSerializer.Serialize(invalidRole);
            var rejected = service.OpenLottery(invalidRole, new(0, 117), out var receipt, now);
            That(!rejected.Accepted && rejected.Plan is null && receipt is null && random.Calls == calls &&
                before == JsonSerializer.Serialize(invalidRole), "原等级、源锁和源期限资格在扣材及抽样前拒绝");
        }
        foreach (var badMaterial in new[]
        {
            new InventoryItem(2, Bag(3), Core(material) with { EquipmentLockId = 1 }),
            new InventoryItem(2, Bag(3), Core(material) with { ExpireTime = (int)now.ToUnixTimeSeconds() }),
            new InventoryItem(2, new(InventoryListType.PersonalCargo, 0), Core(material))
        })
        {
            calls = random.Calls;
            var rejected = service.OpenLottery(role with { Inventory = new([new(1, Bag(117), Core(pot)), badMaterial]) },
                new(0, 117), out _, now);
            That(!rejected.Accepted && rejected.Plan is null && random.Calls == calls,
                "锁定、恰到期与仓库材料不能抵扣且不抽样");
        }
        var mixed = role with { Inventory = new([new(1, Bag(117), Core(pot)),
            new(2, Bag(3), Core(material) with { ExpireTime = (int)now.ToUnixTimeSeconds() }),
            new(3, Bag(4), Core(material) with { ExpireTime = (int)now.AddSeconds(5).ToUnixTimeSeconds() })]) };
        var mixedOpen = service.OpenLottery(mixed, new(0, 117), out _, now);
        That(mixedOpen.Accepted && mixedOpen.Plan!.After.Find(Bag(3)) == mixed.Inventory.Find(Bag(3)) &&
            mixedOpen.Plan.After.Find(Bag(4)) is null &&
            mixedOpen.Plan.CharacterPrecondition!.Matches(mixed, now.AddSeconds(4).ToUnixTimeSeconds()) &&
            !mixedOpen.Plan.CharacterPrecondition.Matches(mixed, now.AddSeconds(5).ToUnixTimeSeconds()),
            "跳过过期材料，只扣合法槽且被扣材料期限进入提交资格");
        var unusedSoon = role with { Inventory = new([new(1, Bag(117), Core(pot)),
            new(2, Bag(3), Core(material) with { ExpireTime = (int)now.AddSeconds(10).ToUnixTimeSeconds() }),
            new(3, Bag(4), Core(material) with { ExpireTime = (int)now.AddSeconds(2).ToUnixTimeSeconds() })]) };
        var unusedSoonOpen = service.OpenLottery(unusedSoon, new(0, 117), out _, now);
        That(unusedSoonOpen.Accepted && unusedSoonOpen.Plan!.After.Find(Bag(3)) is null &&
            unusedSoonOpen.Plan.After.Find(Bag(4)) == unusedSoon.Inventory.Find(Bag(4)) &&
            unusedSoonOpen.Plan.CharacterPrecondition!.Matches(unusedSoon, now.AddSeconds(5).ToUnixTimeSeconds()) &&
            !unusedSoonOpen.Plan.CharacterPrecondition.Matches(unusedSoon, now.AddSeconds(10).ToUnixTimeSeconds()),
            "仅实际按Main槽序扣的材料进入deadline，不强加未消耗材料的临近期限");
        var shortSource = mixed with { Inventory = mixed.Inventory with { Items = mixed.Inventory.Items.SetItem(0,
            mixed.Inventory.Items[0] with { Core = Core(pot) with { ExpireTime = (int)now.AddSeconds(2).ToUnixTimeSeconds() } }) } };
        var shortPlan = service.OpenLottery(shortSource, new(0, 117), out _, now).Plan!;
        That(shortPlan.CharacterPrecondition!.Matches(shortSource, now.AddSeconds(1).ToUnixTimeSeconds()) &&
            !shortPlan.CharacterPrecondition.Matches(shortSource, now.AddSeconds(2).ToUnixTimeSeconds()),
            "材料期限只取更早截止，不能覆盖原源物品期限");

        const string header = "[stackable type]\n`[upgradable legacy]` 1\n";
        const string pool = "[int data]\n490705288 75000 1 490705289 15000 1 490705290 4000 1 490705291 4000 1 490705292 2000 1\n[/int data]\n";
        string Cost(string body) => header + "[lottery use need item]\n" + body + "\n" + pool;
        foreach (string invalid in new[] { "", "490705286", "490705286 1 2", "0 1", "490705286 0", "-1 1",
            "490705286 -1", "2147483648 1", "490705286 2147483648", "`490705286` 1" })
            That(!BoosterCatalog.Parse(Cost(invalid)).Available, "材料必须是完整两个正整数 " + invalid);
        foreach (string invalid in new[]
        {
            Cost("490705286 1") + "[lottery use need item]\n490705286 1\n",
            header + "[/lottery use need item]\n490705286 1\n" + pool,
            header + "[etc]\n[lottery use need item]\n490705286 1\n[/etc]\n" + pool,
            header + "[lottery use need item\n490705286 1\n" + pool,
            Cost("490705286 1").Replace("[upgradable legacy]", "[booster]")
        }) That(!BoosterCatalog.Parse(invalid).Available, "重复、孤立结束、嵌套、残缺及不适用入口材料声明整罐拒绝");

        var archive = (PvfLib.PvfArchive)typeof(ItemCatalog).GetField("pvf", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(items.Items)!;
        var isolated = new BoosterCatalog(archive, new Dictionary<int, string>());
        var definitions = (ConcurrentDictionary<int, BoosterDefinition>)typeof(BoosterCatalog)
            .GetField("definitions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(isolated)!;
        var isolatedService = new InventoryBoosterService(items.Items, isolated, items.GoldLimits, items.EpicPieces,
            random, contracts: items.Contracts);
        definitions[pot] = BoosterCatalog.Parse(header + pool);
        That(isolatedService.OpenLottery(missingRole, new(0, 117), out _, now).Accepted,
            "无材料声明的罐保持原免费材料合同，不按源ID添加费用");
        definitions[pot] = BoosterCatalog.Parse(header + "[explain]\n`[lottery use need item] 490705286 1`\n" + pool);
        That(isolatedService.OpenLottery(missingRole, new(0, 117), out _, now).Accepted,
            "说明文字里的同名字串不是材料声明");
        definitions[pot] = BoosterCatalog.Parse(Cost("490705287 1"));
        var sameId = role with { Inventory = new([new(1, Bag(117), Core(pot, 2))]) };
        var sameIdOpen = isolatedService.OpenLottery(sameId, new(0, 117), out _, now);
        That(sameIdOpen.Accepted && sameIdOpen.Plan!.After.Find(Bag(117)) is null,
            "源和材料同ID时合计扣2，不能把同一份源计两次");
        calls = random.Calls;
        That(!isolatedService.OpenLottery(missingRole, new(0, 117), out _, now).Accepted && random.Calls == calls,
            "源和材料同ID仅一件时拒绝且不抽样");
        definitions[pot] = BoosterCatalog.Parse(Cost("490705286 3"));
        var split = role with { Inventory = new([new(1, Bag(117), Core(pot)), new(2, Bag(3), Core(material)), new(3, Bag(4), Core(material, 2))]) };
        var splitOpen = isolatedService.OpenLottery(split, new(0, 117), out _, now);
        That(splitOpen.Accepted && splitOpen.Plan!.After.Find(Bag(3)) is null && splitOpen.Plan.After.Find(Bag(4)) is null,
            "一条资源cost可跨多个合法堆叠足额扣材");

        definitions[pot] = BoosterCatalog.Parse(Cost("490705286 1").Replace(pool, "[int data]\n490705286 1 1\n[/int data]\n"));
        var materialReward = isolatedService.OpenLottery(fundedRole, new(0, 117), out var materialRewardReceipt, now);
        That(materialReward.Accepted && materialRewardReceipt is { ItemId: material, Count: 1 } &&
            materialRewardReceipt.RewardSlot == Bag(3) && materialReward.Plan!.After.Find(Bag(3))!.Core.Count == 2,
            "材料ID等于奖励ID时按扣材后的grant增量回执，不能被净零资产抵消");
        definitions[pot] = BoosterCatalog.Parse(Cost("490705286 1").Replace(pool, "[int data]\n490705287 1 1\n[/int data]\n"));
        var sourceRewardRole = role with { Inventory = new([new(1, Bag(117), Core(pot, 2)), new(2, Bag(3), Core(material))]) };
        var sourceReward = isolatedService.OpenLottery(sourceRewardRole, new(0, 117), out var sourceRewardReceipt, now);
        That(sourceReward.Accepted && sourceRewardReceipt is { ItemId: pot, Count: 1 } &&
            sourceRewardReceipt.RewardSlot == Bag(117) && sourceReward.Plan!.After.Find(Bag(117))!.Core.Count == 2 &&
            sourceReward.Plan.After.Find(Bag(3)) is null, "源ID等于奖励ID时保留源扣除并正确回本次一份grant");
        definitions[pot] = BoosterCatalog.Parse(Cost("490705287 1").Replace(pool, "[int data]\n490705287 1 1\n[/int data]\n"));
        var allSameRole = role with { Inventory = new([new(1, Bag(117), Core(pot, 3))]) };
        var allSame = isolatedService.OpenLottery(allSameRole, new(0, 117), out var allSameReceipt, now);
        That(allSame.Accepted && allSameReceipt is { ItemId: pot, Count: 1 } && allSameReceipt.RewardSlot == Bag(117) &&
            allSame.Plan!.After.Find(Bag(117))!.Core.Count == 2, "源材料奖励同ID仍扣两份并发一份，ACK不按净减少误拒绝");

        var range = ItemSlotBoundService.Range(items.Items.Get(rows[0].ItemId).Kind, role.Inventory.Expansion);
        var filled = Enumerable.Range(range.Start, range.End - range.Start + 1)
            .Select(slot => new InventoryItem(slot + 1000, Bag((short)slot), Core(pot, 2))).ToArray();
        var crowded = role with { Inventory = new([.. filled, new(2, Bag(3), Core(material, 2))]) };
        string crowdedBefore = JsonSerializer.Serialize(crowded);
        That(!service.OpenLottery(crowded, new(0, 117), out _, now).Accepted &&
            JsonSerializer.Serialize(crowded) == crowdedBefore, "满容量失败不提交已规划的扣罐、扣材和奖励");
        var freed = crowded with { Inventory = crowded.Inventory with { Items = crowded.Inventory.Items.Select(item =>
            item.Position == Bag(117) ? item with { Core = Core(pot) } : item).ToImmutableArray() } };
        var freedOpen = service.OpenLottery(freed, new(0, 117), out var freedReceipt, now);
        That(freedOpen.Accepted && freedReceipt!.RewardSlot == Bag(117) && freedOpen.Plan!.After.Find(Bag(3))!.Core.Count == 1,
            "最后一罐释放原格后合法发奖且仍精确扣材");
        Console.WriteLine($"PASS {Count - checks} 袖珍罐材料解析、资格、权重与冻结规划断言");
        ItemCore Core(int id, int count = 1) => InventoryRewardGrantService.Create(items.Items.Get(id), count, now);
    }

    // 符文箱([legacy] 0 + [rune random box] 族 档)：PVF 声明、[random choose info] 表、
    // 抽取顺序与真实资源开箱/失败路径。用户报告的六件在前，PVF 内同声明的其余箱子全部一并断言。
    public static void RunRuneBoxes(TcpTestResources resources)
    {
        int checks = Count;
        var items = resources.ItemResources;
        var catalog = items.Items;
        var now = DateTimeOffset.UtcNow;
        (int Id, int Family, int Tier)[] boxes =
        [
            (10310783, 1, 0), (10310784, 0, 0), (10310786, 0, 0), (10310946, 0, 0), (10311365, 1, 0), (490014161, 1, 3),
            (10310785, 1, 1), (10310787, 0, 1), (10311013, 1, 0), (10311014, 0, 0),
            (10313591, 0, 3), (10313592, 1, 3), (10313593, 0, 5), (10313594, 1, 5),
            (10313595, 0, 2), (10313596, 1, 2), (10313597, 0, 4), (10313598, 1, 4)
        ];
        foreach (var (id, family, tier) in boxes)
        {
            var definition = items.Boosters.Get(id);
            That(definition.Available && definition.Lottery && definition.Pools.IsEmpty && definition.RuneBox == (family, tier),
                "符文箱按PVF族档声明启用 " + id);
        }
        // 未带 [rune random box] 的 legacy、越界族档、未闭合或重复动作声明一律继续拒绝。
        const string header = "[stackable type]\n`[legacy]` 0\n[action type]\n";
        const string footer = "\n[/action type]\n";
        That(BoosterCatalog.Parse(header + "`[rune random box]` 0 0" + footer) is
            { Available: true, Lottery: true, RuneBox: (0, 0) }, "最小合法符文箱声明启用");
        foreach (string body in new[]
        {
            "", "`[rune random box]` 0", "`[rune random box]` 2 0", "`[rune random box]` 0 256",
            "`[rune random box]` 0 0 0", "`[other box]` 0 0"
        })
            That(!BoosterCatalog.Parse(header + body + footer).Available, "非法符文箱动作声明拒绝 " + body);
        That(!BoosterCatalog.Parse("[stackable type]\n`[legacy]` 1\n[action type]\n`[rune random box]` 0 0" + footer).Available,
            "legacy 标记非0时拒绝");
        That(!BoosterCatalog.Parse(header + "`[rune random box]` 0 0\n[/action type]\n[action type]\n`[rune random box]` 0 0" +
            footer).Available, "重复动作块拒绝");

        var table = catalog.RuneRandomChoices();
        That(table.Rows.Count == 12, "随机抽取表恰好十二行");
        (int Family, int Tier, int Own, (byte Grade, int Weight)[] Grades)[] expected =
        [
            (0, 0, 3500, [((byte)0, 7000), ((byte)1, 2500), ((byte)2, 500)]),
            (0, 1, 3500, [((byte)0, 7000), ((byte)1, 2500), ((byte)2, 500)]),
            (0, 2, 164, [((byte)1, 10000)]),
            (0, 3, 10000, [((byte)1, 10000)]),
            (0, 4, 164, [((byte)2, 10000)]),
            (0, 5, 10000, [((byte)2, 10000)]),
            (1, 0, 500, [((byte)2, 9700), ((byte)3, 300)]),
            (1, 1, 500, [((byte)2, 9700), ((byte)3, 300)]),
            (1, 2, 164, [((byte)2, 10000)]),
            (1, 3, 10000, [((byte)2, 10000)]),
            (1, 4, 164, [((byte)3, 10000)]),
            (1, 5, 10000, [((byte)3, 10000)])
        ];
        foreach (var row in expected)
            That(table.TryGet(row.Family, row.Tier, out var entry) && entry.OwnWeight == row.Own &&
                entry.Grades.Select(grade => (grade.Grade, grade.Weight)).SequenceEqual(row.Grades),
                "随机抽取表族档与权重保持PVF原表 " + row.Family + "/" + row.Tier);
        That(!table.TryGet(2, 0, out _) && !table.TryGet(0, 6, out _), "表外族档不命中");

        var sample = new RuneRandomChooseRow(3500, [((byte)0, 7000), ((byte)1, 2500), ((byte)2, 500)]);
        IReadOnlyList<int> Pool(int grade) => grade switch
        {
            0 => [10, 11],
            1 => [20],
            2 => [30, 31, 32],
            _ => []
        };
        bool Own(int id) => id is 10 or 20;
        var queue = new Queue<(long Value, long Max)>();
        long Next(long max)
        {
            That(queue.Count > 0, "抽取次数不超过表要求");
            var (value, bound) = queue.Dequeue();
            // 越界取值由 Select 自己拒绝并断言，这里只核对调用方请求的上界。
            That(bound == max, "抽取上界");
            return value;
        }
        void Seed(params (long Value, long Max)[] draws)
        {
            queue.Clear();
            foreach (var draw in draws) queue.Enqueue(draw);
        }
        string? Draw(long own, long grade, long pick, long pickMax, out int chosen, out int product,
            RuneRandomChooseRow? source = null)
        {
            var entry = source ?? sample;
            Seed((own, 10000), (grade, entry.TotalWeight));
            if (pick >= 0) queue.Enqueue((pick, pickMax));
            var reason = RuneBoxSelection.Select(entry, Pool, Own, Next, out chosen, out product);
            That(queue.Count == 0, "抽取次数与表一致");
            return reason;
        }
        That(Draw(0, 0, 0, 1, out int drawnGrade, out int drawnProduct) is null && drawnGrade == 0 && drawnProduct == 10,
            "本职业骰命中时只在同职业产物中抽取");
        That(Draw(3500, 0, 1, 2, out drawnGrade, out drawnProduct) is null && drawnProduct == 11,
            "本职业骰边界3500转入全体池");
        That(Draw(9999, 6999, 0, 2, out drawnGrade, out drawnProduct) is null && drawnGrade == 0,
            "品级权重首段末值仍属0档");
        That(Draw(9999, 7000, 0, 1, out drawnGrade, out drawnProduct) is null && drawnGrade == 1 && drawnProduct == 20,
            "品级权重第二段起点进入1档");
        That(Draw(9999, 9999, 2, 3, out drawnGrade, out drawnProduct) is null && drawnProduct == 32,
            "品级权重末值落在2档末件");
        That(Draw(0, 9500, 2, 3, out drawnGrade, out drawnProduct) is null && drawnProduct == 32,
            "本职业在该档无产物时退回全体池");
        Seed((10000, 10000), (0, 10000));
        That(RuneBoxSelection.Select(sample, Pool, Own, Next, out _, out _) == "rune_box_roll" && queue.Count == 0,
            "本职业骰越界整单拒绝");
        Seed((9999, 10000), (10000, 10000));
        That(RuneBoxSelection.Select(sample, Pool, Own, Next, out _, out _) == "rune_box_roll" && queue.Count == 0,
            "档权重越界整单拒绝");
        That(Draw(9999, 7000, 1, 1, out _, out _) == "rune_box_roll", "池内抽取越界整单拒绝");
        var empty = new RuneRandomChooseRow(0, [((byte)5, 10000)]);
        That(Draw(9999, 0, -1, 0, out drawnGrade, out drawnProduct, empty) == "rune_box_empty" &&
            drawnGrade == 5 && drawnProduct == 0, "品级池为空时拒绝且不再抽取");

        var random = new EdgeRandom();
        var service = new InventoryBoosterService(catalog, items.Boosters, items.GoldLimits, items.EpicPieces,
            random, new GameDebugSettings(), items.Contracts);
        var role = new Character(1, 0, "RuneBox", 0, resources.Towns.Room, resources.Towns.InitialReturn)
            { Job = 0, Level = 95, GrowType = 1 };
        var ownRunes = catalog.RuneCandidates(0).Where(id => catalog.Rune(id)!.AllowsUse(95, 0, 1)).ToArray();
        That(ownRunes.Length > 0, "匹配资源提供本职业可用的0级符文");
        random.Values = new([0, 0, 0]);
        random.Bounds.Clear();
        var runeSource = role with { Inventory = new([new(1, Bag(117), Core(10310784))]) };
        var runeOpen = service.OpenLottery(runeSource, new(0, 117), out var runeReceipt, now);
        var rune = runeReceipt!;
        That(runeOpen.Accepted && runeOpen.Plan!.Reason == "rune_box_open" &&
            rune is { SourceSlot: 117, Count: 1 } && rune.ItemId == ownRunes[0] &&
            rune.RewardSlot.List == InventoryListType.Talisman && rune.RewardSlot.Index is >= 70 and < 210 &&
            runeOpen.Plan.After.Find(Bag(117)) is null &&
            runeOpen.Plan.After.Find(rune.RewardSlot)!.Core.ItemId == ownRunes[0] &&
            runeSource.Inventory.Find(Bag(117)) is not null,
            "符文箱按本职业骰与0档抽取并原子扣减源物品");
        That(random.Bounds.SequenceEqual((long[])[10000, 10000, ownRunes.Length]),
            "符文箱抽取上界依次为本职业万分比、档权重与池大小");

        InventoryQualificationTests.CheckInputs(runeSource, value =>
        {
            random.Values = new([0, 0, 0]);
            return service.OpenLottery(value, new(0, 117), out _, now);
        }, now.ToUnixTimeSeconds(), Bag(117));
        var ownTalismans = catalog.TalismanCandidates(2).Where(id => catalog.Talisman(id)!.AllowsUse(95, 0, 1)).ToArray();
        That(ownTalismans.Length >= 2, "匹配资源提供至少两枚本职业可用的2级护石");
        random.Values = new([0, 0, 0]);
        random.Bounds.Clear();
        var stoneSource = role with { Inventory = new([new(1, Bag(118), Core(10310783))]) };
        var stoneOpen = service.OpenLottery(stoneSource, new(0, 118), out var stoneReceipt, now);
        var stone = stoneReceipt!;
        That(stoneOpen.Accepted && stoneOpen.Plan!.Reason == "rune_box_open" && stone.ItemId == ownTalismans[0] &&
            stone.RewardSlot.List == InventoryListType.Talisman && stone.RewardSlot.Index is >= 0 and < 70 &&
            stoneOpen.Plan.After.Find(Bag(118)) is null,
            "护石箱按本职业骰与2档抽取并进入护石页槽位0..69");
        That(random.Bounds.SequenceEqual((long[])[10000, 10000, ownTalismans.Length]), "护石箱抽取上界与护石池大小一致");

        random.Values = new([9999, 9999, 1]);
        random.Bounds.Clear();
        var chnSource = role with { Inventory = new([new(1, Bag(119), Core(490014161))]) };
        var chnOpen = service.OpenLottery(chnSource, new(0, 119), out var chnReceipt, now);
        var chn = chnReceipt!;
        That(chnOpen.Accepted && chn.ItemId == ownTalismans[1] && chn.RewardSlot.List == InventoryListType.Talisman &&
            chnOpen.Plan!.After.Find(Bag(119)) is null, "百分之百本职业的3档箱子抽取第二枚本职业护石");
        That(random.Bounds.SequenceEqual((long[])[10000, 10000, ownTalismans.Length]), "全员档位仍先掷本职业骰再取池");

        // 18件同族声明都必须能在真实资源上开出对应容器和品级；新增同类箱子不需要改编号白名单。
        bool Opens(int id, int family, int tier)
        {
            random.Values = new([0, 0, 0]);
            random.Bounds.Clear();
            var open = service.OpenLottery(role with { Inventory = new([new(1, Bag(117), Core(id))]) }, new(0, 117), out var got, now);
            if (!open.Accepted || open.Plan!.Reason != "rune_box_open" || got is null ||
                got.RewardSlot.List != InventoryListType.Talisman || !table.TryGet(family, tier, out var declared))
                return false;
            if (family == 0)
                return catalog.Rune(got.ItemId) is { } rune && declared.Grades.Any(entry => entry.Grade == rune.Grade) &&
                    got.RewardSlot.Index is >= 70 and < 210;
            int rarity = catalog.Get(got.ItemId).Rarity;
            return catalog.Talisman(got.ItemId) is not null && declared.Grades.Any(entry => entry.Grade == rarity) &&
                got.RewardSlot.Index is >= 0 and < 70;
        }
        foreach (var (id, family, tier) in boxes)
            That(Opens(id, family, tier), "同类符文箱在真实资源上开出对应容器与品级 " + id);

        foreach (var core in new[]
        {
            Core(10310784) with { EquipmentLockId = 1 },
            Core(10310784) with { ExpireTime = checked((int)(now.ToUnixTimeSeconds() - 1)) }
        })
        {
            random.Values = new();
            random.Bounds.Clear();
            var rejected = service.OpenLottery(role with { Inventory = new([new(1, Bag(117), core)]) }, new(0, 117), out _, now);
            That(!rejected.Accepted && rejected.Error == 19 && rejected.RejectReason == "rune_box_source" &&
                random.Bounds.Count == 0, "锁定或到期的符文箱拒绝且不抽取");
        }
        var boxTemplate = catalog.Get(10310784);
        foreach (var invalid in new[]
        {
            boxTemplate with { MinimumLevel = 96 }, boxTemplate with { MaximumLevel = 94 },
            boxTemplate with { Jobs = 2 }, boxTemplate with { RequiredSkill = ushort.MaxValue },
            boxTemplate with { UseForbidden = true }, boxTemplate with { Kind = ItemKind.Material },
            boxTemplate with { Id = boxTemplate.Id + 1 }
        })
        {
            var isolated = new ItemCatalog(null!, new Dictionary<int, string>(), new Dictionary<int, string>(), 0);
            isolated.PrefillTemplates([new(boxTemplate.Id, invalid)]);
            var guarded = new InventoryBoosterService(isolated, items.Boosters, items.GoldLimits, items.EpicPieces, random);
            random.Values = new();
            random.Bounds.Clear();
            var denied = guarded.OpenLottery(runeSource, new(0, 117), out var deniedReceipt, now);
            That(!denied.Accepted && denied.Error == 19 && denied.RejectReason == "rune_box_source" &&
                deniedReceipt is null && random.Bounds.Count == 0,
                "符文盒在读取产物池及抽取前复验源模板身份、种类、等级、职业、技能与禁用资格");
        }
        random.Values = new();
        random.Bounds.Clear();
        That(!service.Open(role with { Inventory = new([new(1, Bag(117), Core(10310784))]) }, new(117, 1), out _, now)
            .Accepted && random.Bounds.Count == 0, "符文箱不能绕入普通礼包入口");

        var filler = catalog.RuneCandidates(0).First(id => id != ownRunes[0]);
        var crowded = role with { Inventory = new([
            new(1, Bag(117), Core(10310784)),
            .. Enumerable.Range(70, 140).Select(slot => new InventoryItem(1000 + slot,
                new(InventoryListType.Talisman, (short)slot), Core(filler)))
        ]) };
        random.Values = new([0, 0, 0]);
        random.Bounds.Clear();
        var fullOpen = service.OpenLottery(crowded, new(0, 117), out _, now);
        That(!fullOpen.Accepted && fullOpen.Error == 4 && fullOpen.RejectReason == "rune_box_capacity" &&
            crowded.Inventory.Find(Bag(117))!.Core.Count == 1, "符文页无空位时拒绝且源物品保留");
        Console.WriteLine($"PASS {Count - checks} 符文箱声明、抽取表与真实资源断言");
        ItemCore Core(int id, int count = 1) => InventoryRewardGrantService.Create(items.Items.Get(id), count, now, shopExpiration: true);
    }
    static InventorySlot Bag(short slot) => new(InventoryListType.Main, slot);
    sealed class EdgeRandom : Random
    {
        public long Value { get; set; }
        // 队列模式按预置序列回答；空队列表示该路径不应发生任何抽取。
        public Queue<long>? Values { get; set; }
        public List<long> Bounds { get; } = [];
        public int Calls { get; private set; }
        public long LastMaximum { get; private set; }
        public override long NextInt64(long maxValue)
        {
            Calls++;
            LastMaximum = maxValue;
            if (Values is not null)
            {
                That(Values.Count > 0, "预置抽取序列覆盖全部抽取");
                long value = Values.Dequeue();
                Bounds.Add(maxValue);
                That(value >= 0 && value < maxValue, "预置抽取值在范围内");
                return value;
            }
            That(Value >= 0 && Value < maxValue, "权重端点有效");
            return Value;
        }
    }
}
