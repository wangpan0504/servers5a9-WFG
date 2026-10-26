using System.Text.Json;
using DfoServer.Game.Characters;
using DfoServer.Game.Accounts;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static class RandomOptionTests
{
    public static void RunCore()
    {
        int checks = Count;
        That(RandomOptionRequests.Read([9, 0, 255, 255]) == new UnsealRandomOptionRequest(9, ushort.MaxValue),
            "解封4字节请求保留不透明背包状态");
        foreach (var body in new byte[][] { [], new byte[3], new byte[5], [255, 255, 0, 0] })
            Reject<InvalidDataException>(() => RandomOptionRequests.Read(body), "畸形解封请求拒绝");
        That(RandomOptionPackets.Status(true).SequenceEqual(new byte[] { 1 }) &&
            RandomOptionPackets.Status(false).SequenceEqual(new byte[] { 0 }), "解封状态ACK不伪造错误码");
        var texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["etc/randomoption/randomoption.lst"] = "1 `options/one.etc` 2 `options/two.etc`",
            ["etc/randomoption/optionquantity.etc"] = "[common]\n1 10 2 2 3 0\n[/common]\n[unique]\n1 2 2 3 3 1\n[/unique]",
            ["etc/randomoption/optiongroupselection.etc"] = "[choose option group]\n2 -1 `lt shoes` 1 3 1000 1000 1000 0 0 1000\n3 -1 `lswd` 1 3 1000 1000 1000 0 0 1000\n[/choose option group]",
            ["etc/randomoption/optiongrouping.etc"] = "[option group]\n1000 1 10 2 1\n[/option group]\n[option group]\n1001 1 2\n[/option group]",
            ["etc/randomoption/randomizedoptionoverall2.etc"] = "[break seal cost]\n2 1 1 0 2 15 1 100 2 15 2 0 3 1 1 10\n[/break seal cost]",
            ["equipment/common/shoes/leather/test.equ"] = "[random option]\n1\n[equipment type]\n`[shoes]` 0\n[item group name]\n`lt shoes`",
            ["equipment/swordman/weapon/hsword/test.equ"] = "[random option]\n1\n[equipment type]\n`[weapon]` 0",
            ["etc/randomoption/options/one.etc"] = "[dungeon]\n[option]\n15\n[level]\n1\n[physical attack]\n1 2\n[/level]\n[level]\n15\n[physical attack]\n-1.9 300\n[/level]\n[/dungeon]",
            ["etc/randomoption/options/two.etc"] = "[dungeon]\n[option]\n244\n[level]\n1\n[elemental property]\n`[fire element]`\n[/elemental property]\n`[fire element]`\n[/elemental property]\n[/level]\n[/dungeon]"
        };
        texts["etc/randomoption/optiongroupselection.etc"] = "[choose option group]\n" +
            string.Join("\n", Enumerable.Range(1, 3).SelectMany(count => new[]
            {
                $"2 -1 `lt shoes` {count} 3 {(count == 2 ? 1001 : 1000)} 1000 1000 0 0 1000",
                $"3 -1 `lswd` {count} 3 1000 1000 1000 0 0 1000"
            })) + "\n[/choose option group]";
        texts["etc/randomoption/options/one.etc"] += "\n[pvp]\n[dungeon]\n[option]\n15\n[level]\n15\n[physical attack]\n1 2\n[/level]\n[/dungeon]\n[/pvp]";
        var catalog = new RandomOptionCatalog(path => texts[path], new Dictionary<int, string>
        {
            [1] = "common/shoes/leather/test.equ", [2] = "swordman/weapon/hsword/test.equ"
        });
        var template = new ItemTemplate(1, ItemKind.Equipment, 17, 19, 10, 1, 0, 2, 1, "[sealing]", 65535, 0, "test");
        var definition = catalog.Resolve(template)!;
        That(definition.GoldCost == 100 && definition.Pools.Select(row => row.Count).SequenceEqual(new byte[] { 1, 2, 3 }),
            "等级费用向下匹配且保留零权重数量");
        That(definition.Pools[2].Groups.Length == 3 && definition.Pools[0].Groups[0][0] == new RandomOptionCandidate(15, 255, 255, 10) &&
            definition.Pools[0].Groups[0][1] == new RandomOptionCandidate(244, 1, 1, 1), "词条等级、补码、小数、饱和与元素端点");
        That(definition.Pools[1].Groups[0].Single().Weight == 2 && definition.Pools[0].Groups[0][0].Weight == 10,
            "不同数量的分组不相互覆盖，且不读取嵌套pvp参数");
        That(catalog.Resolve(template with { Id = 2, Rarity = 3, EquipmentSlot = 12 })?.GoldCost == 10,
            "巨剑路径别名匹配稀有度分组");
        That(catalog.Resolve(template with { Rarity = 0 }) is null && catalog.Resolve(template with { Kind = ItemKind.Material }) is null,
            "普通装备和堆叠物没有魔法封印资格");
        var freeTemplate = template with { AttachType = "`[free]`" };
        That(catalog.ResolveBoosterReward(freeTemplate) is not null && catalog.ResolveBoosterReward(template) is null,
            "礼盒标准池严格限定free，不解除普通封装");
        string equipmentText = texts["equipment/common/shoes/leather/test.equ"];
        foreach (string bad in new[] { "`1`", "junk1", "1 1", "1\n[random option]\n1", "1\n[/random option]" })
        {
            texts["equipment/common/shoes/leather/test.equ"] = "[random option]\n" + bad + "\n[equipment type]\n`[shoes]` 0";
            Reject<InvalidDataException>(() => catalog.ResolveBoosterReward(freeTemplate), "礼盒不以正则提取数字吞掉畸形或重复根声明");
        }
        texts["equipment/common/shoes/leather/test.equ"] = "[pvp]\n[random option]\n1\n[/pvp]";
        Reject<InvalidDataException>(() => catalog.ResolveBoosterReward(freeTemplate), "嵌套random option不作为普通礼盒根资格");
        texts["equipment/common/shoes/leather/test.equ"] = equipmentText;
        var itemCatalog = new ItemCatalog(null!, new Dictionary<int, string>(), new Dictionary<int, string>(), 0);
        itemCatalog.PrefillTemplates([new(template.Id, template), new(15, template with { Id = 15, Kind = ItemKind.Consumable,
            EquipmentSlot = 0, StackLimit = 999, QualityBox = new(80, 99, []) })]);
        Character role = new(1, 0, "SealRule", 0, new(1, 0, 10, 10), null)
        {
            Level = 95, Inventory = new([new(1, new(InventoryListType.Main, 9), new(1, ItemKind.Equipment, 123)),
                new(2, new(InventoryListType.Main, 65), new(15, ItemKind.Consumable, 2))], Gold: 1000)
        };
        var service = new InventoryRandomOptionService(itemCatalog, catalog, _ => 0);
        InventoryQualificationTests.CheckInputs(role, value => service.Unseal(value, new(9, 0)),
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(), new InventorySlot(InventoryListType.Main, 9));
        InventoryQualificationTests.CheckInputs(role, value => service.ResetQuality(value, new(9, 1, 65, 0, false)),
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(), new(InventoryListType.Main, 9), new(InventoryListType.Main, 65));
        EquipmentRandomOptionChangeTests.RunCatalog((readTokens, resolve) => new RandomOptionCatalog(path => texts[path],
            new Dictionary<int, string> { [1] = "common/shoes/leather/test.equ" }, readTokens, resolve), template);
        texts["equipment/common/shoes/leather/test.equ"] = "[random option]\n0";
        That(catalog.Resolve(template) is null, "显式零random option不解封");
        texts["etc/randomoption/optionquantity.etc"] = "[common]\n1 -1\n[/common]\n[unique]\n1 1\n[/unique]";
        Reject<InvalidDataException>(() => new RandomOptionCatalog(path => texts[path], new Dictionary<int, string>()),
            "无效权重不会静默过滤奖池");
        Console.WriteLine($"PASS {Count - checks} random option core assertions");
    }

    public static void Run(TownCatalog towns, ItemResources resources)
    {
        int checks = Count;
        var template = resources.Items.Get(100260138);
        var definition = resources.RandomOptions.Resolve(template)!;
        That(definition is { GoldCost: 100, Pools.Length: 3 }, "匹配PVF的15级皮鞋魔法封印费用和完整分组");
        That(resources.RandomOptions.Resolve(resources.Items.Get(101030264))?.Pools.SelectMany(pool => pool.Groups)
            .SelectMany(group => group).Any(option => option.Type is >= 244 and <= 247 && option.Value1 == 1 && option.Value2 == 1) == true,
            "匹配PVF武器分组完整保留元素词条端点");
        string root = Path.Combine(Path.GetTempPath(), "s5a9-random-option-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string database = Path.Combine(root, "game.db");
            var repository = new CharacterRepository(database);
            var owners = new AccountOwners();
            using var owner = owners.Acquire("random-option", () => { });
            var core = InventoryRewardGrantService.Create(template) with
            {
                Attr = 7, EnchantCardId = 3175, AmplifyType = 2, AmplifyValue = 21, SealFlag = 1,
                SortLockFlag = 1, Rune = 8, Extension27 = 1234
            };
            var character = repository.Create(owner, new("SealTest", towns.Room, towns.InitialReturn)
            {
                Inventory = new([new(0, new(InventoryListType.Main, 9), core)], Gold: 10000)
            });
            var service = new InventoryRandomOptionService(resources.Items, resources.RandomOptions, _ => 0);
            var request = new UnsealRandomOptionRequest(9, ushort.MaxValue);
            var result = service.Unseal(character, request);
            var before = character.Inventory.Items.Single();
            var after = result.Plan!.After.Items.Single();
            That(result.Accepted && result.Plan.After.Gold == 9900 && after.Core.Random0.Type > 0 && after.Core.Random1.Type == 0,
                "解封按权重生成词条并按PVF扣费");
            That(after.Uid == before.Uid && after.Position == before.Position &&
                (after.Core with { Random0 = default, RandomChangedIndex = 0 }) == before.Core,
                "解封仅改随机词条，完整核心、封装、UID和排序锁保留");
            That(!service.Unseal(character with { Inventory = character.Inventory with { Gold = 99 } }, request).Accepted,
                "金币不足保留封印");
            foreach (var invalid in new[] { core with { EquipmentLockId = 1 }, core with { ExpireTime = 1 }, core with { Random0 = new(1, 1, 1) } })
                That(!service.Unseal(character with { Inventory = character.Inventory with
                {
                    Items = [before with { Core = invalid }]
                } }, request).Accepted, "安全锁、到期和已解封目标拒绝");
            var last = new InventoryRandomOptionService(resources.Items, resources.RandomOptions, bound => bound - 1).Unseal(character, request);
            That(last.Accepted && last.Plan!.After.Items.Single().Core is { Random0.Type: > 0, Random1.Type: > 0, Random2.Type: 0 } two &&
                two.Random0.Type != two.Random1.Type, "数量权重上界与词条去重保留随机语义");
            var session = new GameSession(repository, towns, owner);
            session.Select(0);
            session.MarkReady();
            var selected = session.Selected!;
            var committed = session.PlanAndCommitInventory(value => service.Unseal(value, request), requireTown: false);
            That(committed.Accepted && session.CanPublishInventory(committed.Receipt!) &&
                new CharacterRepository(database).LoadCharacter(owner, 0).Inventory.Items.Single().Core == after.Core,
                "解封提交回执与重新打开数据库一致");
            That(!session.PlanAndCommitInventory(value => service.Unseal(value, request), requireTown: false).Accepted &&
                session.Selected!.Revision == selected.Revision + 1, "重复解封不扣第二次费用或增加revision");
            Reject<InvalidOperationException>(() => repository.CommitInventory(owner, character, result.Plan!), "旧revision不重复解封");
            session.MutateInventory(value => new(true, 0, new(value.Inventory, value.Inventory with
            {
                Items = [value.Inventory.Items.Single() with { Core = core }]
            }, "random_option_fixture")));
            var phaseCharacter = repository.Create(owner, new("SealPhase", new(38, 2, 1085, 249), towns.InitialReturn)
            {
                Inventory = new([new(0, new(InventoryListType.Main, 9), core)], Gold: 10000)
            });
            var phaseSession = new GameSession(repository, towns, owner);
            phaseSession.Select(phaseCharacter.Slot);
            phaseSession.MarkReady();
            var phaseBefore = phaseSession.Selected;
            var phaseConflict = phaseSession.PlanAndCommitInventory(value =>
            {
                var planned = service.Unseal(value, request);
                phaseSession.BeginDungeonSelection();
                return planned;
            }, requireTown: false);
            That(!phaseConflict.Accepted && ReferenceEquals(phaseBefore, phaseSession.Selected) &&
                repository.LoadCharacter(owner, phaseCharacter.Slot).Inventory.Gold == 10000,
                "锁外规划期间阶段改变但角色快照相同时，旧解封计划仍不得提交");
            phaseSession.End();
            string saved = JsonSerializer.Serialize(session.Selected);
            using (var db = new SqliteConnection("Data Source=" + database + ";Pooling=False"))
            {
                db.Open();
                using var sql = db.CreateCommand();
                sql.CommandText = "CREATE TRIGGER fail_random_option BEFORE UPDATE ON character_inventory_items BEGIN SELECT RAISE(ABORT,'seal failure'); END";
                sql.ExecuteNonQuery();
            }
            Reject<SqliteException>(() => session.PlanAndCommitInventory(value => service.Unseal(value, request), requireTown: false),
                "保存失败不提交半份解封");
            That(saved == JsonSerializer.Serialize(session.Selected) &&
                saved == JsonSerializer.Serialize(repository.LoadCharacter(owner, 0)), "回滚保留金币、核心和在线快照");
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine($"PASS {Count - checks} random option resource/commit assertions");
    }
}
