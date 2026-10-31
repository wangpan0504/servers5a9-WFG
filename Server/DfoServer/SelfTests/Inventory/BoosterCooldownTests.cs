using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static class BoosterCooldownTests
{
    const int SourceId = 10006409, SharedSourceId = 10006279, Milliseconds = 604800000;
    static readonly DateTimeOffset SeedNow = new(2026, 10, 21, 0, 0, 0, TimeSpan.FromHours(8));
    // 保存源第476行的完整raw，UTF-8 SHA256=97EB36EEC376AF4861D32E87740B7858A3F0B91BD1EAD1B3D6D8C86523D69C32。
    const string SavedSource = "\n[name]\n`冒险家征集自选礼盒` \n[explain]\n`开启后， 可以从以下两种奖励中选择一种获得。 A类奖励： 追忆之史诗武器袖珍罐、 传承防具、 49张升级券； B类奖励： 追忆之史诗武器袖珍罐、 5种裂魂之异次元防具、 裂魂之异次元辅助装备、 裂魂之异次元魔法石。` \n[flavor text]\n`<冒险家征集令>` \n[grade]\n1 \n[rarity]\n3 \n[usable job]\n`[all]` \n[/usable job]\n\n[attach type]\n`[account]` \n[minimum level]\n50 \n[weight]\n1 \n[cool time]\n604800000 \n[icon]\n`Item/stackable/consumption_cn.img` 66 \n[stackable type]\n`[booster selection]` 0 \n[move wav]\n`BONE_TOUCH` \n[stack limit]\n1 \n[cooltime group]\n71 \n[cooltime maintenance]\n1 1 1 \n[booster category num]\n1 2 \n[booster selection num]\n0 \n[booster select category]\n0 0 \n[stackable]\n10006279 1 10006309 1 10006429 1 \n[/stackable]\n\n[/booster select category]\n\n[booster select category]\n1 0 \n[stackable]\n10006279 1 10006311 1 10006312 1 10006313 1 \n[/stackable]\n\n[/booster select category]\n\n[booster category name]\n`请选择职业` `A类型` `B类型` \n[/booster category name]\n";

    public static void RunCore()
    {
        int checks = Count;
        AssertDefinition(BoosterCatalog.Parse(SavedSource), "保存10006409冷却声明必须准入并保留完整A/B奖励");
        foreach (string text in new[]
        {
            SavedSource.Replace("1 1 1 ", "1 1 "), SavedSource.Replace("1 1 1 ", "1 1 1 1 "),
            SavedSource.Replace("1 1 1 ", "1 -1 1 "), SavedSource.Replace("1 1 1 ", "1 2 1 "),
            SavedSource.Replace("1 1 1 ", "`1` 1 1 "),
            SavedSource + "[cooltime maintenance]\n1 1 1\n",
            SavedSource.Replace("[cooltime maintenance]\n1 1 1 ", "[outer]\n[cooltime maintenance]\n1 1 1\n[/outer]"),
            SavedSource + "[/cooltime maintenance]\n",
            SavedSource.Replace("604800000 ", "-1 "), SavedSource.Replace("604800000 ", "2147483648 "),
            SavedSource.Replace("604800000 ", "604800000 1 "), SavedSource + "[cool time]\n604800000 1\n",
            SavedSource.Replace("[cooltime group]\n71 ", "[cooltime group]\n-1 "),
            SavedSource.Replace("[cooltime group]\n71 ", "[cooltime group]\n2147483648 "),
            SavedSource.Replace("[cooltime group]\n71 ", "[cooltime group]\n71 72 "),
            SavedSource + "[cooltime group]\n71 72\n", SavedSource + "[effect maintenance]\n1 1 1 1\n"
        })
            That(!BoosterCatalog.Parse(text).Available, "冷却字段损坏、重复维护位、嵌套或额外未实现效果仍整盒拒绝");
        That(BoosterCatalog.Parse(SavedSource + "[cool time]\n1000\n[cooltime group]\n0\n") is
            { Available: true, CoolTime: 1000, CoolGroup: 0, CooltimeMaintenance: 7 },
            "冷却标量按完整声明的最后值读取，保留位仍唯一且不变");
        string plain = SavedSource.Replace("[cooltime maintenance]\n1 1 1 \n", "");
        That(BoosterCatalog.Parse(plain) is { Available: true, CoolTime: Milliseconds, CoolGroup: 71, CooltimeMaintenance: null },
            "普通礼包不依赖维护位读取冷却时长及组");
        foreach (string field in new[] { "cool time", "cooltime group" })
            foreach (string value in new[] { "", "-1", "2147483648", "`1000`", "1 2", "1\n[outer]\n[" + field + "]\n1\n[/outer]" })
                That(!BoosterCatalog.Parse(plain + "[" + field + "]\n" + value + "\n").Available,
                    "无维护位仍逐份拒绝损坏冷却标量，不绕过字段验证");
        Console.WriteLine($"PASS {Count - checks} booster cooldown declaration checks");
    }

    public static void RunPlainResources(TownCatalog towns, ItemResources resources)
    {
        int checks = Count;
        const int sourceId = 490011382;
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var definition = resources.Boosters.Get(sourceId);
        That(definition is { Available: true, CoolTime: 1000, CooltimeMaintenance: null, CoolGroup: null },
            $"普通礼包无maintenance仍须读取原文1000毫秒冷却，缺省组独立且不补保留位：{definition.PendingRule}/{definition.CoolTime}/{definition.CoolGroup}/{definition.CooltimeMaintenance}");
        var core = InventoryRewardGrantService.Create(resources.Items.Get(sourceId), utcNow: now);
        var role = new Character(1, 0, "PlainBox", 0, towns.Room, towns.InitialReturn)
            { Inventory = new([new(1, Bag(3), core), new(2, Bag(4), core)]) };
        var service = new InventoryBoosterService(resources.Items, resources.Boosters, resources.GoldLimits, resources.EpicPieces);
        var first = service.Open(role, new(3, 1), out var receipt, now);
        That(first.Accepted && first.Plan is not null && receipt is not null &&
            first.Plan.After.ConsumableStates.SequenceEqual(new[]
            { new ConsumableState(ConsumableStateKind.Cooldown, sourceId, now.ToUnixTimeMilliseconds() + 1000, 0, -sourceId) }),
            "普通会话冷却与源及两个子盒发奖同一计划，维护位为0而非跨重选保留");
        var active = role with { Inventory = first.Plan!.After };
        string before = JsonSerializer.Serialize(active);
        var denied = service.Open(active, new(4, 1), out var deniedReceipt, now.AddMilliseconds(999));
        That(!denied.Accepted && denied.Plan is null && deniedReceipt is null && JsonSerializer.Serialize(active) == before,
            "同模板不同UID/槽第二源在截止前1ms拒绝，全部资产和已提交截止不变");
        That(service.Open(active, new(4, 1), out _, now.AddMilliseconds(1000)).Accepted,
            "恰到普通冷却截止可再次使用，不向后续请求追加旧时长");
        foreach (var boundary in new[] { ConsumableMaintenance.Death, ConsumableMaintenance.Town, ConsumableMaintenance.Reselect })
        {
            var cleared = active with { Inventory = ConsumableStatePolicy.CrossBoundary(active.Inventory, boundary, now.ToUnixTimeMilliseconds()) };
            That(cleared.Inventory.ConsumableStates.IsEmpty && service.Open(cleared, new(4, 1), out _, now).Accepted,
                "无保留声明的普通冷却在既有生命周期边界清理 " + boundary);
        }
        Console.WriteLine($"PASS {Count - checks} plain booster session cooldown resource checks");
    }

    public static void RunResources(TownCatalog towns, ItemResources resources)
    {
        int checks = Count;
        RunPlainResources(towns, resources);
        var clock = new ConsumableUseSessionTests.FakeClock();
        clock.Advance(SeedNow - clock.GetUtcNow());
        var service = new InventoryBoosterService(resources.Items, resources.Boosters, resources.GoldLimits,
            resources.EpicPieces, clock: clock);
        AssertDefinition(resources.Boosters.Get(SourceId), "真实10006409冷却声明与保存原A/B合同一致");
        That(resources.Items.Get(SourceId) is { MinimumLevel: 50, StackLimit: 1, Jobs: ushort.MaxValue },
            "真实源保持50级、全职业及每堆一件，不因冷却改变资格");
        for (byte job = 0; job < 16; job++)
            for (byte category = 0; category < 2; category++)
            {
                var role = Role(towns, resources.Items, job);
                string before = JsonSerializer.Serialize(role);
                var result = service.Open(role, Select(65, category), out var receipt, SeedNow);
                AssertOpened(role, result, receipt, category, SeedNow);
                That(JsonSerializer.Serialize(role) == before, "各职业A/B首用只规划，不修改输入角色");
            }

        var firstRole = Role(towns, resources.Items);
        var first = service.Open(firstRole, Select(65), out _, SeedNow).Plan!.After;
        var activeRole = firstRole with { Inventory = first };
        RejectUnchanged(service, activeRole, Select(66), SeedNow.AddMilliseconds(Milliseconds - 1),
            "第二实例在同组截止前1ms仍拒绝，不因源UID或槽变化绕过");
        var renewed = service.Open(activeRole, Select(66), out _, SeedNow.AddMilliseconds(Milliseconds));
        That(renewed.Accepted && renewed.Plan!.After.Items.All(item => item.Uid != activeRole.Inventory.Find(Bag(66))!.Uid) &&
            renewed.Plan.After.Items.All(item => item.Core.ItemId != SourceId) &&
            Expected(0).All(id => renewed.Plan.After.Items.Where(item => item.Core.ItemId == id).Sum(item => item.Core.Count) == 2) &&
            renewed.Plan.After.ConsumableStates.SequenceEqual(new[]
            { new ConsumableState(ConsumableStateKind.Cooldown, SourceId, SeedNow.ToUnixTimeMilliseconds() + 2L * Milliseconds, 7, 71) }),
            "恰到7天允许第二件，新的唯一冷却从本次使用起算");

        var shared = resources.Boosters.Get(SharedSourceId);
        That(shared.Available && shared.Selection is not null, "10006279同组真实源保留其原分类");
        var group = shared.Selection!.Categories.First(category => category.PendingRule is null && !category.Rewards.IsEmpty);
        var otherRole = firstRole with { Inventory = firstRole.Inventory with
        {
            Items = [new(10003, Bag(67), InventoryRewardGrantService.Create(resources.Items.Get(SharedSourceId), utcNow: SeedNow))],
            ConsumableStates = first.ConsumableStates
        } };
        var sharedRequest = new BoosterOpenRequest(67, 1, new(group.Category, group.Subcategory, [group.Rewards[0].ItemId]));
        That(service.Open(otherRole with { Inventory = otherRole.Inventory with { ConsumableStates = [] } },
            sharedRequest, out _, SeedNow.AddSeconds(1)).Accepted,
            "10006279原选择数量1的首个奖励请求在无冷却时完整合法，避免无关选择错误遮蔽同组拒绝");
        RejectUnchanged(service, otherRole, sharedRequest,
            SeedNow.AddSeconds(1), "10006409冷却同时阻止不同物品10006279的共享组71");
        foreach (var boundary in new[] { ConsumableMaintenance.Death, ConsumableMaintenance.Town, ConsumableMaintenance.Reselect })
            That(ConsumableStatePolicy.CrossBoundary(first, boundary, SeedNow.ToUnixTimeMilliseconds() + 1000).ConsumableStates.SequenceEqual(first.ConsumableStates),
                "真实礼盒111跨死亡、回城和重选保留原截止，不续期或补奖");
        Bytes(ConsumableStatePackets.Restore(first, ConsumableStateKind.Cooldown, SeedNow.ToUnixTimeMilliseconds() + 1000)!,
            [1, 0x89, 0xAF, 0x98, 0, 0x7F, 0x3A, 0x09, 0], "172恢复10006409及604799剩余秒，独立字节预期");
        That(ConsumableStatePackets.Restore(first, ConsumableStateKind.Effect, SeedNow.ToUnixTimeMilliseconds()) is null &&
            ConsumableStatePackets.Restore(first, ConsumableStateKind.Cooldown, SeedNow.ToUnixTimeMilliseconds() + Milliseconds - 500) is null,
            "礼盒不生成效果174，剩余不足1秒不重新启动完整冷却");

        var range = ItemSlotBoundService.Range(ItemKind.Consumable, firstRole.Inventory);
        var full = firstRole with { Inventory = firstRole.Inventory with { Items = firstRole.Inventory.Items.AddRange(
            Enumerable.Range(range.Start, range.End - range.Start + 1).Where(slot => firstRole.Inventory.Find(Bag((short)slot)) is null)
                .Select(slot => new InventoryItem(20000 + slot, Bag((short)slot), new(1113, ItemKind.Consumable, int.MaxValue)))) } };
        RejectUnchanged(service, full, Select(65), SeedNow, "消费一个源空格不足完整A类三件，失败不写冷却");
        var saturated = firstRole with { Inventory = firstRole.Inventory with { ConsumableStates = Enumerable.Range(1, 255)
            .Select(index => new ConsumableState(ConsumableStateKind.Cooldown, 200000 + index,
                SeedNow.ToUnixTimeMilliseconds() + Milliseconds, 7, 1000 + index)).ToImmutableArray() } };
        RejectUnchanged(service, saturated, Select(65), SeedNow, "255条其他冷却已满时源、完整奖励与状态一起保持");
        RejectUnchanged(service, firstRole with { Level = 49 }, Select(65), SeedNow, "维护位不绕过原50级资格");
        RejectUnchanged(service, firstRole, new(65, 1, new(0, 0, [10006279])), SeedNow, "A类全领不能截成一个奖励");
        RunGroups(towns, resources);
        RunPersistence(towns, resources, service, clock);
        Console.WriteLine($"PASS {Count - checks} booster cooldown resource and transaction checks");
    }

    static void RunGroups(TownCatalog towns, ItemResources resources)
    {
        var archive = (PvfLib.PvfArchive)typeof(ItemCatalog).GetField("pvf", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(resources.Items)!;
        var isolated = new BoosterCatalog(archive, new Dictionary<int, string>());
        var definitions = (ConcurrentDictionary<int, BoosterDefinition>)typeof(BoosterCatalog)
            .GetField("definitions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(isolated)!;
        var service = new InventoryBoosterService(resources.Items, isolated, resources.GoldLimits, resources.EpicPieces);
        foreach (var (text, activeGroup, accepted) in new[]
        {
            (SavedSource.Replace("[cooltime group]\n71 ", "[cooltime group]\n0 "), 0, false),
            (SavedSource.Replace("[cooltime group]\n71 \n", ""), 0, true),
            (SavedSource.Replace("[cooltime group]\n71 \n", ""), -SourceId, false),
            (SavedSource, 72, true),
            (SavedSource.Replace("[cooltime maintenance]\n1 1 1 \n", "").Replace("604800000 ", "1000 "), 71, false),
            (SavedSource.Replace("[cooltime maintenance]\n1 1 1 \n", "").Replace("604800000 ", "1000 "), 72, true)
        })
        {
            definitions[SourceId] = BoosterCatalog.Parse(text);
            var role = Role(towns, resources.Items);
            role = role with { Inventory = role.Inventory with { ConsumableStates =
                [new(ConsumableStateKind.Cooldown, SharedSourceId, SeedNow.ToUnixTimeMilliseconds() + Milliseconds, 7, activeGroup)] } };
            string before = JsonSerializer.Serialize(role);
            var result = service.Open(role, Select(65), out var receipt, SeedNow);
            That(result.Accepted == accepted && JsonSerializer.Serialize(role) == before &&
                (accepted || result.Plan is null && receipt is null),
                "显式组0共享、缺省组按物品独立；普通冷却同组封锁、不同组允许");
            if (accepted && !text.Contains("[cooltime maintenance]", StringComparison.Ordinal))
                That(result.Plan!.After.ConsumableStates.Any(row => row.ItemId == SourceId && row.Maintenance == 0 &&
                        row.CoolGroup == 71 && row.UntilMilliseconds == SeedNow.ToUnixTimeMilliseconds() + 1000) &&
                    result.Plan.After.ConsumableStates.Contains(role.Inventory.ConsumableStates[0]),
                    "普通冷却新增独立维护位0状态，既有不同组冷却保持原截止");
        }

        foreach (bool transient in new[] { true, false })
        {
            definitions[SourceId] = BoosterCatalog.Parse(transient
                ? SavedSource.Replace("1 1 1 ", "0 0 0 ")
                : SavedSource.Replace("604800000 ", "0 "));
            var role = Role(towns, resources.Items);
            var result = service.Open(role, Select(65), out _, SeedNow);
            That(result.Accepted && (transient
                ? result.Plan!.After.ConsumableStates is [{ Kind: ConsumableStateKind.Cooldown, Maintenance: 0, CoolGroup: 71 }]
                : result.Plan!.After.ConsumableStates.IsEmpty),
                "显式000仍登记本会话冷却，零时长不登记冷却行");
            var active = role with { Inventory = result.Plan!.After };
            That(service.Open(active, Select(66), out _, SeedNow.AddMilliseconds(1)).Accepted == !transient,
                "显式000阻止本会话第二源，零时长允许第二源按原完整清单打开");
        }

        // 合成维护声明只放入独立定义缓存，实际九件装扮模板及选择规则复用现有518夹具。
        const int packageId = 2683533;
        var package = resources.Boosters.Get(packageId);
        var totals = package.PackageRewards.GroupBy(row => row.ItemId).ToDictionary(group => group.Key, group => group.Sum(row => row.Count));
        That(package.Available && totals.Values.Sum() == 9 && totals.Keys.All(id => resources.Items.Get(id).Kind == ItemKind.Avatar),
            "518冷却夹具保留真实2683533九件装扮，不猜或缩减原固定清单");
        definitions[packageId] = package with { CoolTime = 1000, CoolGroup = 71, CooltimeMaintenance = 7 };
        var choices = totals.Keys.Select(id => new CeraPackageChoice(id, resources.AvatarPackages.GetAvatar(id).Abilities.Min())).ToArray();
        var template = resources.Items.Get(packageId);
        byte level = Math.Max((byte)1, template.MinimumLevel);
        byte job = (byte)Enumerable.Range(0, 16).First(value => template.AllowsUse(level, (byte)value));
        var ceraRole = new Character(1, 0, "CooldownCera", 0, towns.Room, towns.InitialReturn)
        {
            Level = level, Job = job, Skills = template.RequiredSkill == 0 ? [] : [new(template.RequiredSkill, 1)],
            Inventory = new([new(10000, Bag(65), InventoryRewardGrantService.Create(template, utcNow: SeedNow, shopExpiration: true)),
                new(10001, Bag(66), InventoryRewardGrantService.Create(template, utcNow: SeedNow, shopExpiration: true))], Gold: 12345)
        };
        var ceraService = new InventoryBoosterService(resources.Items, isolated, resources.GoldLimits, resources.EpicPieces,
            debugSettings: new() { IgnoreCashShopAbsoluteExpiration = true }, avatars: resources.AvatarPackages);
        string ceraBefore = JsonSerializer.Serialize(ceraRole);
        var opened = ceraService.OpenCeraPackage(ceraRole, new(65, choices), out var ceraReceipt, SeedNow);
        That(opened.Accepted && ceraReceipt is not null && ceraReceipt.Rewards.Sum(row => row.Count) == 9 &&
            totals.All(pair => ceraReceipt.Rewards.Any(row => row.ItemId == pair.Key && row.Count == pair.Value)) &&
            opened.Plan!.After.Items.All(item => item.Uid != ceraRole.Inventory.Find(Bag(65))!.Uid) &&
            opened.Plan.After.Items.Where(item => item.Core.ItemId == packageId).Sum(item => item.Core.Count) == 1 &&
            opened.Plan.After.Find(Bag(66)) == ceraRole.Inventory.Find(Bag(66)) &&
            opened.Plan.After.ConsumableStates.SequenceEqual(new[]
            { new ConsumableState(ConsumableStateKind.Cooldown, packageId, SeedNow.ToUnixTimeMilliseconds() + 1000, 7, 71) }) &&
            JsonSerializer.Serialize(ceraRole) == ceraBefore,
            "非空518仍发完整九件及原选项，只消费一个源并把共享冷却合入同一计划");
        var actualAvatars = opened.Plan!.After.Items.Where(item => item.Core.Kind == ItemKind.Avatar)
            .Concat(opened.Plan.OverflowItems).ToArray();
        That(actualAvatars.Length == 9 && totals.All(pair =>
                actualAvatars.Where(item => item.Core.ItemId == pair.Key).Sum(item => item.Core.Count) == pair.Value) &&
            actualAvatars.All(item => item.Uid == 0 && item.Avatar is not null &&
                item.Core.Durability == choices.Single(choice => choice.ItemId == item.Core.ItemId).Option),
            "518实际计划的全部九件装扮及其原选择属性保持，不只核回执清单");
        var ceraActive = ceraRole with { Inventory = opened.Plan!.After };
        string activeBefore = JsonSerializer.Serialize(ceraActive);
        var denied = ceraService.OpenCeraPackage(ceraActive, new(66, choices), out var deniedReceipt, SeedNow.AddMilliseconds(999));
        That(!denied.Accepted && denied.Plan is null && deniedReceipt is null && JsonSerializer.Serialize(ceraActive) == activeBefore,
            "非空518在共享截止前拒绝且源、装扮和状态不变");
        That(ceraService.OpenCeraPackage(ceraActive, new(66, choices), out _, SeedNow.AddSeconds(1)).Accepted,
            "非空518共享冷却恰到期后可按原完整清单再开");
        RunRuneBoxes(towns, resources, isolated, definitions);
    }

    static void RunRuneBoxes(TownCatalog towns, ItemResources resources, BoosterCatalog isolated,
        ConcurrentDictionary<int, BoosterDefinition> definitions)
    {
        // 与现有RuneBox资源夹具共用真实源、职业与产物池；只在同一archive的独立缓存附加维护声明。
        var catalog = resources.Items;
        var runePool = catalog.RuneCandidates(0).Where(id => catalog.Rune(id)!.AllowsUse(95, 0, 1)).ToArray();
        var talismanPool = catalog.TalismanCandidates(2).Where(id => catalog.Talisman(id)!.AllowsUse(95, 0, 1)).ToArray();
        That(runePool.Length > 0 && talismanPool.Length > 0, "两族冷却夹具沿用真实0级符文与2级护石本职业池");
        (int Id, int Family, int Product, int Start, int Length)[] boxes =
        [
            (10310784, 0, runePool[0], 70, 140),
            (10310783, 1, talismanPool[0], 0, 70)
        ];
        foreach (var box in boxes)
        {
            var definition = resources.Boosters.Get(box.Id);
            That(definition.Available && definition.Lottery && definition.RuneBox == (box.Family, 0),
                "冷却夹具保留真实RuneBox族档及原抽奖声明 " + box.Id);
            definitions[box.Id] = definition with { CoolTime = 1000, CoolGroup = 71, CooltimeMaintenance = 7 };
        }
        foreach (var box in boxes)
        {
            var other = boxes.Single(row => row.Id != box.Id);
            var random = new CountingRuneRandom();
            var service = new InventoryBoosterService(catalog, isolated, resources.GoldLimits, resources.EpicPieces, random);
            var role = new Character(1, 0, "CooldownRuneBox", 0, towns.Room, towns.InitialReturn)
            {
                Job = 0, Level = 95, GrowType = 1,
                Inventory = new([new(1, Bag(117), Core(box.Id)), new(2, Bag(118), Core(other.Id)),
                    new(3, Bag(119), new(1113, ItemKind.Consumable, 7))], Gold: 12345)
            };
            string before = JsonSerializer.Serialize(role);
            var opened = service.OpenLottery(role, new(0, 117), out var receipt, SeedNow);
            That(opened.Accepted && opened.Plan!.Reason == "rune_box_open" &&
                receipt is { SourceSlot: 117, Count: 1 } && receipt.ItemId == box.Product &&
                receipt.RewardSlot.List == InventoryListType.Talisman &&
                receipt.RewardSlot.Index >= box.Start && receipt.RewardSlot.Index < box.Start + box.Length &&
                opened.Plan.After.Find(receipt.RewardSlot)!.Core.ItemId == box.Product &&
                opened.Plan.After.Items.Where(item => item.Core.ItemId == box.Product).Sum(item => item.Core.Count) == 1 &&
                opened.Plan.After.Items.All(item => item.Core.ItemId != box.Id) &&
                opened.Plan.After.Items.Sum(item => item.Core.Count) == role.Inventory.Items.Sum(item => item.Core.Count) &&
                opened.Plan.After.Items.All(item => item.Uid != 1) && opened.Plan.After.Find(Bag(118)) == role.Inventory.Find(Bag(118)) &&
                opened.Plan.After.Find(Bag(119)) == role.Inventory.Find(Bag(119)) && opened.Plan.OverflowItems.IsEmpty &&
                opened.Plan.After.ConsumableStates.SequenceEqual(new[]
                { new ConsumableState(ConsumableStateKind.Cooldown, box.Id, SeedNow.ToUnixTimeMilliseconds() + 1000, 7, 71) }) &&
                JsonSerializer.Serialize(opened.Plan.After with { Items = role.Inventory.Items, ConsumableStates = [] }) ==
                    JsonSerializer.Serialize(role.Inventory) && JsonSerializer.Serialize(role) == before && random.Calls == 3,
                "RuneBox首用完整发原族档奖励、扣一个源并在同计划登记共享冷却 " + box.Id);

            var active = role with { Inventory = opened.Plan!.After };
            string activeBefore = JsonSerializer.Serialize(active);
            var denied = service.OpenLottery(active, new(0, 118), out var deniedReceipt, SeedNow.AddMilliseconds(999));
            That(!denied.Accepted && denied.Plan is null && deniedReceipt is null && random.Calls == 3 &&
                JsonSerializer.Serialize(active) == activeBefore,
                "另一族RuneBox在同组截止前拒绝，不耗源、不发奖、不改状态且不抽取 " + box.Id);
            var renewed = service.OpenLottery(active, new(0, 118), out var renewedReceipt, SeedNow.AddMilliseconds(1000));
            That(renewed.Accepted && renewedReceipt is { Count: 1 } && renewedReceipt.ItemId == other.Product &&
                renewed.Plan!.After.Items.All(item => item.Uid is not (1 or 2)) &&
                renewed.Plan.After.Items.Where(item => item.Core.ItemId == box.Product).Sum(item => item.Core.Count) == 1 &&
                renewed.Plan.After.Items.Where(item => item.Core.ItemId == other.Product).Sum(item => item.Core.Count) == 1 &&
                renewed.Plan.After.ConsumableStates.SequenceEqual(new[]
                { new ConsumableState(ConsumableStateKind.Cooldown, other.Id, SeedNow.ToUnixTimeMilliseconds() + 2000, 7, 71) }) &&
                JsonSerializer.Serialize(active) == activeBefore && random.Calls == 6,
                "RuneBox共享冷却恰到期可开另一族并从本次使用重算唯一截止 " + box.Id);

            int filler = (box.Family == 0 ? catalog.RuneCandidates(0) : catalog.TalismanCandidates(2)).First(id => id != box.Product);
            var crowded = role with { Inventory = role.Inventory with { Items = role.Inventory.Items.AddRange(
                Enumerable.Range(box.Start, box.Length).Select(slot => new InventoryItem(1000 + slot,
                    new(InventoryListType.Talisman, (short)slot), Core(filler)))) } };
            string crowdedBefore = JsonSerializer.Serialize(crowded);
            var full = service.OpenLottery(crowded, new(0, 117), out var fullReceipt, SeedNow);
            That(!full.Accepted && full.Error == 4 && full.RejectReason == "rune_box_capacity" &&
                full.Plan is null && fullReceipt is null && crowded.Inventory.ConsumableStates.IsEmpty &&
                JsonSerializer.Serialize(crowded) == crowdedBefore,
                "RuneBox对应护石或符文页满时整单拒绝，不消费源或留下冷却 " + box.Id);
        }
        ItemCore Core(int id) => InventoryRewardGrantService.Create(catalog.Get(id), 1, SeedNow, shopExpiration: true);
    }

    sealed class CountingRuneRandom : Random
    {
        public int Calls { get; private set; }
        public override long NextInt64(long maxValue)
        {
            Calls++;
            return 0;
        }
    }

    static void RunPersistence(TownCatalog towns, ItemResources resources, InventoryBoosterService service,
        ConsumableUseSessionTests.FakeClock clock)
    {
        string root = Path.Combine(Path.GetTempPath(), "s5a9-booster-cooldown-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "game.db");
            var repository = new CharacterRepository(path);
            var owners = new AccountOwners();
            using var owner = owners.Acquire("booster-cooldown", () => { });
            foreach (string name in new[] { "CooldownA", "CooldownB" })
            {
                var inventory = Role(towns, resources.Items).Inventory;
                repository.Create(owner, new(name, towns.Room, towns.InitialReturn)
                { Level = 50, Inventory = inventory with { Items = inventory.Items.Select(item => item with { Uid = 0 }).ToImmutableArray() } });
            }
            var session = new GameSession(repository, towns, owner, timeProvider: clock);
            session.Select(0);
            session.MarkReady();
            InventoryResult Open(short slot) => session.PlanAndCommitBooster(role => service.Open(role, Select(slot), out _, SeedNow),
                service.UsePlaces(SourceId), SourceId);
            var saved = session.Selected!;
            var stale = service.Open(saved, Select(65), out _, SeedNow).Plan!;
            string before = Snapshot(owner);
            Query("CREATE TRIGGER fail_booster_cooldown AFTER INSERT ON character_consumable_states BEGIN SELECT RAISE(ABORT,'booster cooldown rollback'); END");
            Reject<SqliteException>(() => Open(65), "奖励、审计已写后冷却行失败必须传播");
            That(ReferenceEquals(session.Selected, saved) && Snapshot(owner) == before,
                "冷却写入失败回滚源、全部奖励、UID、revision与审计");
            Query("DROP TRIGGER fail_booster_cooldown");
            var result = Open(65);
            That(result.Accepted && result.Receipt is not null && ReferenceEquals(result.Receipt.Character, session.Selected),
                "源、完整奖励与冷却同一Session事务提交后才有回执");
            var committed = session.Selected!;
            That(committed.Inventory.ConsumableStates.SequenceEqual(new[]
                { new ConsumableState(ConsumableStateKind.Cooldown, SourceId, SeedNow.ToUnixTimeMilliseconds() + Milliseconds, 7, 71) }) &&
                committed.Inventory.Items.All(item => item.Uid > 0) &&
                JsonSerializer.Serialize(new CharacterRepository(path).List(owner).Single(role => role.Name == "CooldownA")) == JsonSerializer.Serialize(committed),
                "重开Repository保留唯一角色冷却、原截止、完整奖励和持久UID");
            session.ReturnToRoster();
            session.Select(0);
            session.MarkReady();
            before = Snapshot(owner);
            That(!Open(66).Accepted && Snapshot(owner) == before &&
                session.Selected!.Inventory.ConsumableStates.SequenceEqual(committed.Inventory.ConsumableStates),
                "真实重选保留111冷却，不重复扣源、续期或补奖");
            Reject<InvalidOperationException>(() => repository.CommitInventory(owner, saved, stale),
                "旧revision冷却计划不得重复提交");
            That(Snapshot(owner) == before, "旧计划失败不改变冷却、奖励、审计和UID");
            var other = repository.List(owner).Single(role => role.Name == "CooldownB");
            var independent = service.Open(other, Select(65, 1), out var receipt, SeedNow);
            AssertOpened(other, independent, receipt, 1, SeedNow);
            That(other.Inventory.ConsumableStates.IsEmpty, "同账号另一角色首用仍合法，不把account attach改成账号冷却");
            const int plainSource = 490011382;
            var plainCore = InventoryRewardGrantService.Create(resources.Items.Get(plainSource), utcNow: SeedNow);
            repository.Create(owner, new("PlainBox", towns.Room, towns.InitialReturn)
            { Inventory = new([new(0, Bag(3), plainCore), new(0, Bag(4), plainCore)]) });
            session.ReturnToRoster();
            session.Select(2);
            session.MarkReady();
            InventoryResult OpenPlain(short slot) => session.PlanAndCommitBooster(
                role => service.Open(role, new(slot, 1), out _, SeedNow), service.UsePlaces(plainSource), plainSource);
            var plainBefore = session.Selected!;
            before = Snapshot(owner);
            Query("CREATE TRIGGER fail_plain_cooldown AFTER INSERT ON character_consumable_states BEGIN SELECT RAISE(ABORT,'plain cooldown rollback'); END");
            Reject<SqliteException>(() => OpenPlain(3), "普通礼包冷却行晚SQL失败传播，不提前发布奖励");
            That(ReferenceEquals(session.Selected, plainBefore) && Snapshot(owner) == before,
                "普通冷却晚失败完整回滚源、两个子盒奖励、revision、审计和UID");
            Query("DROP TRIGGER fail_plain_cooldown");
            That(OpenPlain(3).Accepted && session.Selected!.Inventory.ConsumableStates is
                [{ ItemId: plainSource, Maintenance: 0, CoolGroup: -plainSource }],
                "普通礼包完整奖励和维护位0冷却同事务提交");
            var plainCommitted = session.Selected!;
            That(JsonSerializer.Serialize(new CharacterRepository(path).List(owner).Single(role => role.Name == "PlainBox")) ==
                JsonSerializer.Serialize(plainCommitted), "普通冷却提交后的源、完整奖励及唯一截止可重新读取");
            before = Snapshot(owner);
            That(!OpenPlain(4).Accepted && Snapshot(owner) == before,
                "普通冷却中第二源拒绝不修改持久化状态");
            session.ReturnToRoster();
            session.Select(2);
            session.MarkReady();
            That(session.Selected!.Inventory.ConsumableStates.IsEmpty && OpenPlain(4).Accepted &&
                session.Selected!.Inventory.Items.All(item => item.Core.ItemId != plainSource),
                "普通维护位0冷却真实重选后清除，第二源沿原完整奖励流程提交");
            before = Snapshot(owner);
            using var replacement = owners.Acquire(owner.Account, () => { });
            Reject<InvalidOperationException>(() => repository.CommitInventory(owner, other, independent.Plan!),
                "旧generation不能提交另一角色的合法冷却计划");
            That(Snapshot(replacement) == before, "旧owner失败没有写入新冷却或奖励");
        }
        finally { Directory.Delete(root, true); }

        // SQL只在原有资源自测入口被root执行，测试源码不新增产品CLI或访问玩家库。
        object? Query(string sql)
        {
            using var connection = new SqliteConnection("Data Source=" + Path.Combine(root, "game.db") + ";Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return command.ExecuteScalar();
        }
        string Snapshot(AccountOwners.Lease lease) => JsonSerializer.Serialize(new CharacterRepository(Path.Combine(root, "game.db")).List(lease)) + ":" +
            Query("SELECT count(*) FROM inventory_audit_log") + ":" +
            Query("SELECT seq FROM sqlite_sequence WHERE name='character_inventory_items'");
    }

    static void AssertDefinition(BoosterDefinition definition, string message)
    {
        That(definition.Available && definition.Selection is { SelectionCount: 0, Categories.Length: 2 }, message);
        foreach (byte category in new byte[] { 0, 1 })
        {
            var group = definition.Selection!.Categories.Single(row => row.Category == category && row.Subcategory == 0);
            That(group.PendingRule is null && group.Rewards.Select(row => (row.ItemId, row.Count)).SequenceEqual(
                Expected(category).Select(id => (id, 1))), "A/B原类别各行、顺序和每件数量保持");
        }
    }

    static void AssertOpened(Character before, InventoryResult result, BoosterReceipt? receipt, byte category, DateTimeOffset now)
    {
        That(result.Accepted && result.Plan is not null && receipt is not null, "原合法A/B分类完整开盒成功");
        var after = result.Plan!.After;
        That(receipt!.Rewards.Select(row => (row.ItemId, row.Count)).SequenceEqual(Expected(category).Select(id => (id, 1))) &&
            after.Items.All(item => item.Uid != before.Inventory.Find(Bag(65))!.Uid) &&
            after.Items.Where(item => item.Core.ItemId == SourceId).Sum(item => item.Core.Count) ==
                before.Inventory.Items.Where(item => item.Core.ItemId == SourceId).Sum(item => item.Core.Count) - 1 &&
            after.Items.Sum(item => item.Core.Count) == before.Inventory.Items.Sum(item => item.Core.Count) - 1 + Expected(category).Length &&
            result.Plan.OverflowItems.IsEmpty && after.Find(Bag(66)) == before.Inventory.Find(Bag(66)) &&
            after.Find(Bag(68)) == before.Inventory.Find(Bag(68)), "只消费Main65原源UID及数量1，允许完整奖励复用释放源格，另一源及哨兵保持");
        foreach (int id in Expected(category))
            That(after.Items.Single(item => item.Core.ItemId == id).Core == new ItemCore(id, ItemKind.Consumable, 1),
                "每个原奖励完整普通核心为数量1，不改成子盒内奖励或缩减发奖");
        That(after.ConsumableStates.SequenceEqual(new[]
            { new ConsumableState(ConsumableStateKind.Cooldown, SourceId, now.ToUnixTimeMilliseconds() + Milliseconds, 7, 71) }) &&
            JsonSerializer.Serialize(after with { Items = before.Inventory.Items, ConsumableStates = before.Inventory.ConsumableStates }) ==
                JsonSerializer.Serialize(before.Inventory), "唯一group71/mask7冷却与发奖合入计划，钱包及其余库存状态保持");
    }

    static void RejectUnchanged(InventoryBoosterService service, Character role, BoosterOpenRequest request,
        DateTimeOffset now, string message)
    {
        string before = JsonSerializer.Serialize(role);
        var result = service.Open(role, request, out var receipt, now);
        That(!result.Accepted && result.Plan is null && receipt is null && JsonSerializer.Serialize(role) == before, message);
    }

    static Character Role(TownCatalog towns, ItemCatalog items, byte job = 0) => new(1, 0, "Cooldown", 0, towns.Room, towns.InitialReturn)
    {
        Level = 50, Job = job,
        Inventory = new([new(10000, Bag(65), InventoryRewardGrantService.Create(items.Get(SourceId), utcNow: SeedNow)),
            new(10001, Bag(66), InventoryRewardGrantService.Create(items.Get(SourceId), utcNow: SeedNow)),
            new(10002, Bag(68), new(1113, ItemKind.Consumable, 7))], Gold: 12345)
    };

    static int[] Expected(byte category) => category == 0 ? [10006279, 10006309, 10006429] : [10006279, 10006311, 10006312, 10006313];
    static InventorySlot Bag(short slot) => new(InventoryListType.Main, slot);
    static BoosterOpenRequest Select(short slot, byte category = 0) => new(slot, 1, new(category, 0, Expected(category)));
}
