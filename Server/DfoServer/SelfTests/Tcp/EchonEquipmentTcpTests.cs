using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class EchonEquipmentTcpTests
{
    public static async Task RunAsync(ServerOptions options, string root, TcpTestResources resources)
    {
        var assets = resources.ItemResources;
        var rules = assets.Echon;
        That(rules.Options.Length == 16 && rules.UpgradeCosts[3] is [{ ItemId: 10161536, Count: 34 }] &&
            rules.Sets[1].Points[(3, 6)] == 600 && rules.Sets[1].Points[(0, 3)] == 105,
            "纯净PVF提供16项能力、A档升级材料、四档三至六件预算");
        int[] ids = [100120561, 100070568, 100170536, 100220536];
        var templates = ids.Select(assets.Items.Get).Where(template => template.Echon is not null)
            .DistinctBy(template => template.EquipmentSlot).Take(4).ToArray();
        That(templates.Length == 4, "真实艾肯装备样本覆盖不同穿戴部位");
        CheckResources(assets);
        await using var fixture = new TcpTestFixture(options, Path.Combine(root, "echon-equipment"), resources: resources);
        var repository = new CharacterRepository(fixture.Database);
        var inventory = templates.Select((template, index) => new InventoryItem(0, Bag((short)(9 + index)),
            new EchonEquipmentState(1, 11, 0, 0, index == 0 ? (byte)2 : (byte)3).Apply(
                InventoryRewardGrantService.Create(template) with { Attr = 11, GenuineUpgrade = 5, EnchantCardId = 3168 }))).ToList();
        int[] materials = [10161533, 10161534, 10161535, 10161536, 10161537];
        for (int at = 0; at < materials.Length; at++)
            inventory.Add(new(0, Bag((short)(121 + at)), InventoryRewardGrantService.Create(assets.Items.Get(materials[at]), 1000)));
        inventory.Add(new(0, Bag(13), InventoryRewardGrantService.Create(assets.Items.Get(100120611))));
        inventory.Add(new(0, Bag(126), InventoryRewardGrantService.Create(assets.Items.Get(10161538), 100)));
        inventory.Add(new(0, Bag(14), new EchonEquipmentState(1, 11, 0, 0, 3).Apply(
            InventoryRewardGrantService.Create(templates[0]))));
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            repository.Create(owner, new("EchonTcp", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Level = 95, Skills = resources.Characters.Get(0).Skills, AcceptedStoryLevel = 1,
                DungeonState = CharacterDungeonState.NewOrdinary, Inventory = new(inventory.ToImmutableArray())
            });
            repository.Create(owner, new("EchonPoor", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Level = 95, Skills = resources.Characters.Get(0).Skills, AcceptedStoryLevel = 1,
                DungeonState = CharacterDungeonState.NewOrdinary,
                Inventory = new(inventory.Where(item => item.Core.Kind == ItemKind.Equipment)
                    .Select(item => item with { Uid = 0 }).ToImmutableArray())
            });
        }
        await fixture.StartAsync();
        var original = Reload().Inventory.Find(Bag(9))!;
        byte[]? setWire = null;
        using (var peer = Open(fixture))
        {
            peer.Select();
            string before = Snapshot();
            peer.Send(1344, [9]); peer.Expect(1, 1344, [0, 11, 0]); peer.Barrier();
            That(Snapshot() == before, "畸形升级请求拒绝且不改持久状态，连接可继续使用");
            peer.Send(1344, [9, 0]); peer.Expect(0, 14); peer.Expect(1, 1344, [1]); peer.Barrier();
            var upgraded = Reload().Inventory.Find(Bag(9))!;
            That(upgraded.Uid == original.Uid && upgraded.Core == original.Core with { Extension114 = new(3, 0x0B01) } &&
                Reload().Inventory.Find(Bag(124))!.Core.Count == 966, "升级真实扣34材料并完整保留原UID和打造");
            peer.Send(1342, [9, 0, 0, 2]); peer.Expect(0, 14); peer.Expect(1, 1342, [1]); peer.Barrier();
            That(Reload().Inventory.Find(Bag(121))!.Core.Count == 904 &&
                EchonEquipmentState.Read(Reload().Inventory.Find(Bag(9))!.Core).First == 2,
                "A档属性变更按所选能力扣96材料并落库");
            peer.Send(1342, [13, 0, 0, 1]); peer.Expect(0, 14); peer.Expect(1, 1342, [1]); peer.Barrier();
            That(EchonEquipmentState.Read(Reload().Inventory.Find(Bag(13))!.Core) == new EchonEquipmentState(1, 21, 0, 0, 0) &&
                Reload().Inventory.Find(Bag(126))!.Core.Count == 60, "新版属性改造走同一1342入口并真实扣40材料");
            peer.Send(1342, [13, 0, 1, 2]); peer.Expect(1, 1342, [0, 1, 0]);
            peer.Send(1344, [13, 0]); peer.Expect(1, 1344, [0, 11, 0]);
            peer.Send(1344, [9, 0]); peer.Expect(1, 1344, [0, 7, 0]);
            peer.Send(1342, [9, 0, 0, 2]); peer.Expect(1, 1342, [0, 1, 0]);
            peer.Send(1344, [99, 0]); peer.Expect(1, 1344, [0, 2, 0]); peer.Barrier();
            before = Snapshot();
            peer.Send(1343, [1, 1, 3, 9, 0, 10, 0, 14, 0]); peer.Expect(1, 1343, [0, 9, 0]);
            peer.Send(1343, [1, 1, 3, 13, 0, 10, 0, 11, 0]); peer.Expect(1, 1343, [0, 5, 0]); peer.Barrier();
            That(Snapshot() == before, "专用错误拒绝重复部位和新版套装，拒绝后连接及资产保持");
            peer.Send(1343, [1, 1, 4, 9, 0, 10, 0, 11, 0, 12, 0]);
            var delta = peer.Expect(0, 14).Body;
            var saved = Reload();
            setWire = [.. BitConverter.GetBytes((uint)saved.Id), 1, 4, 1, 0, 0, 0, 0, 3];
            peer.Expect(0, 1282, setWire); peer.Expect(1, 1343, [1]); peer.Barrier();
            That(delta[0] == 0 && saved.Inventory.Items.Count(item => item.Core.EchonSet is not null) == 4 &&
                saved.Inventory.Find(Bag(125))!.Core.Count == 871,
                "成功按14物品差量→1282套装定义→1343确认发布，129材料与四件定义共事务");
            before = Snapshot();
            fixture.Sql("CREATE TRIGGER fail_echon AFTER INSERT ON inventory_audit_log BEGIN SELECT RAISE(ABORT,'echon rollback'); END");
            peer.Send(1342, [9, 0, 0, 11]); peer.Closed();
            That(Snapshot() == before, "末段SQL失败完整回滚能力、套装、材料和审计");
            fixture.Sql("DROP TRIGGER fail_echon");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(echonSets: [setWire!]);
            That(Reload().Inventory.Find(Bag(9))!.Uid == original.Uid &&
                Reload().Inventory.Find(Bag(9))!.Core.EchonSet is { Grade: 3, Parts: 4, First: 1 },
                "重连从已提交完整实例恢复相同套装定义，不重新随机或扣费");
            string before = Snapshot();
            peer.Send(1343, [2, 1, 2, 4, 9, 0, 10, 0, 11, 0, 12, 0]); peer.Expect(1, 1343, [0, 4, 0]); peer.Barrier();
            That(Snapshot() == before, "不同选项同组请求整批拒绝，不污染现有套装");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(1);
            string before = Snapshot();
            peer.Send(1344, [9, 0]); peer.Expect(1, 1344, [0, 8, 0]);
            peer.Send(1342, [9, 0, 0, 2]); peer.Expect(1, 1342, [0, 8, 0]);
            peer.Send(1343, [1, 1, 4, 9, 0, 10, 0, 11, 0, 12, 0]); peer.Expect(1, 1343, [0, 8, 0]); peer.Barrier();
            That(Snapshot() == before, "三条请求缺料均回客户端材料不足错误8，不改库存、材料和审计");
        }
        await fixture.StopAsync();
        Character Reload()
        {
            using var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { });
            return repository.List(owner).Single(character => character.Name == "EchonTcp");
        }
        string Snapshot() => fixture.Snapshot() + fixture.Snapshot("SELECT item_uid,item_core FROM character_inventory_items ORDER BY item_uid") +
            fixture.Snapshot("SELECT count(*) FROM inventory_audit_log");
    }

    static void CheckResources(Infrastructure.ItemResources assets)
    {
        var character = new Character(1, 0, "EchonResource", 0, new(1, 0, 10, 10), null) { Level = 95 };
        foreach (int book in new[] { 10305808, 10305809, 10305810 })
        {
            var definition = assets.UpgradeChronicles.Get(book)!;
            That(definition.Recipes.Length == 31, "每类斗气转换书覆盖31种部位与材质");
            foreach (var recipe in definition.Recipes)
            {
                var source = assets.Items.Get(recipe.Accepts.Single());
                var target = assets.Items.Get(recipe.OutputItemId);
                That(source.Echon is { Rules.IsModern: false } && target.Echon is { Rules.IsModern: true } &&
                    source.EquipmentSlot == target.EquipmentSlot && target.Echon.InitialFirst != 0 && target.Echon.InitialSecond != 0 &&
                    InventoryCargoService.AllowsInsert(InventoryRewardGrantService.Create(source), InventoryListType.AccountCargo, source) == false,
                    $"转换映射旧版→新版同部位且旧装备不可进账号仓库：{source.Id}/{target.Id}，" +
                    $"类型{source.Echon?.Rules.IsModern}/{target.Echon?.Rules.IsModern}，部位{source.EquipmentSlot}/{target.EquipmentSlot}，初值{target.Echon?.InitialFirst}/{target.Echon?.InitialSecond}，绑定{source.AttachType}");
            }
        }
        var original = new InventoryItem(1, Bag(9), new EchonEquipmentState(2, 11, 0, 1, 3).Apply(
            InventoryRewardGrantService.Create(assets.Items.Get(100120561))) with
        { Attr = 12, GenuineUpgrade = 5, EnchantCardId = 3168, EchonSet = new(1, 4, 1, 0, 0, 0, 3) });
        var withBook = character with { Inventory = new([original,
            new(2, Bag(3), InventoryRewardGrantService.Create(assets.Items.Get(10305808)))]) };
        var conversion = new InventoryUpgradeChronicleService(assets.UpgradeChronicles, assets.Items, assets.EpicPieces)
            .Upgrade(withBook, Bag(3), 10305808, Bag(9), 100120561);
        var output = conversion.Plan!.After.Find(Bag(9))!;
        That(conversion.Accepted && output.Uid == original.Uid && output.Core == original.Core with
            { ItemId = 100120612, Extension114 = new(0, 0x0101), EchonSet = null },
            "转换书保留完整打造/UID，按模板应用守护初始能力1和1并清除旧套装，不扣商店购买价");
        var booster = character with { Inventory = new([new(1, Bag(3),
            InventoryRewardGrantService.Create(assets.Items.Get(490008596)))]) };
        var opened = new InventoryBoosterService(assets.Items, assets.Boosters, assets.GoldLimits, assets.EpicPieces)
            .Open(booster, new(3, 1, new(2, 0, [100120561])), out _);
        That(opened.Accepted && EchonEquipmentState.Read(opened.Plan!.After.Items.Single().Core).Grade == 3,
            "真实3星自选礼盒按根级品级声明生成A级斗气装备");
        foreach (var template in new[] { assets.Items.Get(100120561), assets.Items.Get(100120611) })
        {
            var core = InventoryRewardGrantService.Create(template);
            if (!template.Echon!.Rules.IsModern) core = new EchonEquipmentState(1, 1, 0, 0, 3).Apply(core);
            var role = character with { Inventory = new([new(1, Bag(9), core)]) };
            var broken = new InventoryDisjointService(assets.Items, assets.Disjoint, assets.GoldLimits, assets.EpicPieces,
                new Random(1)).Disjoint(role, new(Bag(9), -1, 0), out _);
            That(broken.Accepted && !broken.Plan!.After.Items.Any(item => item.Core.ItemId == 10099775) &&
                broken.Plan.After.Items.Single(item => item.Core.ItemId == 10161538).Core.Count == (template.Echon.Rules.IsModern ? 8 : 28),
                "真实分解追加两项材料并禁止产出传说灵魂");
        }
    }
    static InventorySlot Bag(short slot) => new(InventoryListType.Main, slot);
}
