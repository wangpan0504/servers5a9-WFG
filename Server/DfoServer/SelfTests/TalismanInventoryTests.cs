using System.Buffers.Binary;
using System.Collections.Frozen;
using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Network.Builders.Inventory;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class TalismanInventoryTests
{
    const int StoneId = 100400002, RuneId = 10605203, OtherRuneId = 10605204, MaterialId = 490014022;
    const long Now = 1_900_000_000;
    static readonly ItemTemplate Stone = new(StoneId, ItemKind.Talisman, 34, 95, 0, 1, 0, 3, 97, "[trade]", 1, 0, "stone");
    static readonly ItemTemplate Rune = new(RuneId, ItemKind.Rune, 0, 95, 0, 999, 0, 2, 1, "[trade]", 1, 0, "rune");
    static readonly ItemTemplate Material = new(MaterialId, ItemKind.Material, 0, 0, 0, 9999, 0, 0, 0, "[trade]", ushort.MaxValue, 0, "material");
    static readonly GoldLimitCatalog Limits = new("[gold limit from level]\n0 1000000000\n[/gold limit from level]");
    static readonly EpicPieceCatalog Pieces = new("[piece list]\n50000 50001\n[/piece list]");

    public static void RunCore()
    {
        int start = Count;
        var catalog = Catalog();
        var empty = Character();
        var granted = InventoryRewardGrantService.TryGrantBatch(empty, [new(StoneId, 1), new(RuneId, 12)], catalog, Limits, Pieces,
            DateTimeOffset.FromUnixTimeSeconds(Now));
        That(granted.Accepted && granted.Plan!.After.Find(new(InventoryListType.Talisman, 0))?.Core.Kind == ItemKind.Talisman &&
            granted.Plan.After.Find(new(InventoryListType.Talisman, 70))?.Core.Count == 12 &&
            granted.Plan.After.Items.All(item => item.Position.List == InventoryListType.Talisman), "护石与符文奖励自动进入各自专用分页");
        var newStone = granted.Plan!.After.Find(new(InventoryListType.Talisman, 0))!.Core;
        var colors = TalismanSocketColors.Read(newStone);
        That(colors.IsComplete && new[] { colors.First, colors.Second, colors.Third }.Distinct().Count() == 3,
            "新护石一次生成三种有效孔色");
        That(ItemSlotBoundService.Range(ItemKind.Talisman, 0) == ((short)0, (short)69) &&
            ItemSlotBoundService.Range(ItemKind.Rune, 0) == ((short)70, (short)209) &&
            ItemSlotBoundService.Allows(0, new(InventoryListType.Equipment, 35), ItemKind.Talisman) &&
            !ItemSlotBoundService.Allows(0, new(InventoryListType.Main, 65), ItemKind.Rune), "专用分页容量与第二护石槽不受普通背包扩容影响");

        var originalCore = new ItemCore(StoneId, ItemKind.Equipment, 999999998)
        { Rune = 0x4321, SortLockFlag = 1, EquipmentLockId = 3, Extension114 = new(0xAA, 0xFE030201), Extension119 = 9 };
        var legacyStone = new InventoryItem(10, new(InventoryListType.Main, 9), originalCore);
        var legacyRune = new InventoryItem(20, new(InventoryListType.Main, 65), new(RuneId, ItemKind.Consumable, 2001)
        { SortLockFlag = 1, EquipmentLockId = 4, Extension97 = 123 });
        var old = Character(legacyStone, legacyRune);
        var migrated = InventoryTalismanCompatibility.Plan(old, catalog);
        var migratedRunes = migrated.Plan!.After.Items.Where(item => item.Core.Kind == ItemKind.Rune).ToArray();
        That(migrated.Accepted && migrated.Plan.Before == old.Inventory && old.Inventory.Items[0] == legacyStone &&
            old.Inventory.Items[1] == legacyRune && migrated.Plan.After.Find(new(InventoryListType.Talisman, 0)) is { Uid: 10 } migratedStone &&
            migratedStone.Core == originalCore with { Kind = ItemKind.Talisman }, "历史护石迁移保留UID、孔色、未知位与锁，不改原快照");
        That(migratedRunes.Select(item => item.Core.Count).SequenceEqual([999, 999, 3]) &&
            migratedRunes.Count(item => item.Uid == 20) == 1 && migratedRunes.Count(item => item.Uid == 0) == 2 &&
            migratedRunes.All(item => item.Core.EquipmentLockId == 4 && item.Core.Extension97 == 123), "历史超限符文守恒拆分，额外堆叠不复用原UID");
        var cargoRune = legacyRune with { Position = new(InventoryListType.AccountCargo, 2) };
        var cargo = Character(cargoRune) with { Inventory = new([cargoRune]) { AccountCargo = new(Capacity: 4) } };
        var cargoResult = InventoryTalismanCompatibility.Plan(cargo, catalog);
        That(cargoResult.Accepted && cargoResult.Plan!.After.Items.All(item => item.Position.List == InventoryListType.AccountCargo) &&
            cargoResult.Plan.After.Find(cargoRune.Position)?.Uid == cargoRune.Uid && cargoResult.Plan.AccountCargoChanged,
            "账号仓库旧符文保持账号所有权及原格，拆分参与共享版本提交");
        var fullStones = Enumerable.Range(0, 70).Select(slot => StoneItem(slot + 100, (short)slot)).Append(legacyStone).ToArray();
        var noSpace = InventoryTalismanCompatibility.Plan(Character(fullStones), catalog);
        That(!noSpace.Accepted && noSpace.Plan is null && fullStones[^1] == legacyStone, "专用背包不足时迁移整批拒绝，不丢旧资产");
        var incoming = InventoryRewardGrantService.TryGrantItems(empty, [legacyStone with { Core = originalCore with { EquipmentLockId = 0 } },
            legacyRune with { Core = legacyRune.Core with { EquipmentLockId = 0 } }], catalog, Limits, Pieces, DateTimeOffset.FromUnixTimeSeconds(Now));
        That(incoming.Accepted && incoming.Plan!.After.Items.Count(item => item.Uid == 20) == 1 &&
            incoming.Plan.After.Items.Where(item => item.Core.Kind == ItemKind.Rune).Sum(item => item.Core.Count) == 2001,
            "旧邮件附件在领取输出归一分类并守恒拆分，不重写原附件身份");

        var service = new InventoryTalismanService(catalog, _ => 0);
        var worn = StoneItem(1, 34, InventoryListType.Equipment);
        var source = new InventoryItem(2, new(InventoryListType.Talisman, 70), new(RuneId, ItemKind.Rune, 3));
        var wearer = Character(worn, source);
        var equipped = service.Equip(wearer, new(210, 70, RuneId), Now);
        That(equipped.Accepted && equipped.Plan!.After.Find(source.Position)?.Core.Count == 2 &&
            equipped.Plan.After.Find(new(InventoryListType.Talisman, 210)) is { Uid: 0, Core.Count: 1 } &&
            TalismanSocketColors.Read(worn.Core).First != catalog.Rune(RuneId)!.SocketColor, "装载从堆叠分出一枚且不按孔色拒绝");
        var actual = equipped.Plan!.After.Find(new(InventoryListType.Talisman, 210))! with { Uid = 3 };
        var afterEquip = wearer with { Inventory = equipped.Plan.After with { Items = equipped.Plan.After.Items.Replace(equipped.Plan.After.Find(actual.Position)!, actual) } };
        var removed = service.Remove(afterEquip, 210, Now);
        That(removed.Accepted && removed.Plan!.After.Find(source.Position)?.Core.Count == 3 && removed.Plan.After.Find(actual.Position) is null,
            "卸除符文完整合回背包，已装载位置清空");
        var onlyLoaded = Character(worn, actual);
        var whileLoaded = InventoryRewardGrantService.TryGrantBatch(onlyLoaded, [new(RuneId, 2)], catalog, Limits, Pieces,
            DateTimeOffset.FromUnixTimeSeconds(Now));
        That(whileLoaded.Accepted && whileLoaded.Plan!.After.Find(actual.Position)?.Core.Count == 1 &&
            whileLoaded.Plan.After.Find(source.Position)?.Core.Count == 2,
            "发放同名符文只能合入背包，不能把装载槽增加为多枚");
        That(!service.Equip(afterEquip, new(211, 70, RuneId), Now).Accepted, "同一护石三孔拒绝同阵营同技能重复");
        var secondStone = StoneItem(4, 35, InventoryListType.Equipment);
        That(service.Equip(afterEquip with { Inventory = afterEquip.Inventory with { Items = afterEquip.Inventory.Items.Add(secondStone) } },
            new(213, 70, RuneId), Now).Accepted, "两个护石分别校验三孔，可各装相同阵营同技能符文");
        That(!service.Equip(wearer with { Inventory = new([worn with { Core = worn.Core with { EquipmentLockId = 1 } }, source]) },
            new(210, 70, RuneId), Now).Accepted, "锁定护石拒绝装载，材料不变");
        var allSlots = Enumerable.Range(70, 140).Select(slot => new InventoryItem(slot + 100,
            new(InventoryListType.Talisman, (short)slot), new(OtherRuneId, ItemKind.Rune, 999))).Append(actual).ToArray();
        var refused = service.Remove(Character(allSlots), 210, Now);
        That(!refused.Accepted && refused.Plan is null, "满符文分页卸除失败，不删除已装载实例");
        That(!InventoryDeleteService.TryDelete(actual, actual.Core.Value, 1, out var deleted) && deleted is null,
            "已装载符文拒绝通用删除与出售，资产只能经卸除命令归还");
        That(!InventoryLockService.Plan(Character(worn, actual).Inventory, actual.Position,
            InventoryLockOperation.EquipmentLock, catalog, Now).Accepted, "已装载符文不可加锁，避免卸除死锁");

        var target = StoneItem(11, 0) with { Core = TalismanSocketColors.Read(originalCore).Apply(originalCore with { Kind = ItemKind.Talisman, EquipmentLockId = 0 }) };
        var material = new InventoryItem(12, new(InventoryListType.Main, 121), new(MaterialId, ItemKind.Material, 5));
        var costs = new TalismanRefineCost(MaterialId, new Dictionary<int, int> { [3] = 2 }.ToFrozenDictionary());
        var refined = service.Refine(Character(target, material), new(121, 0), costs, Now);
        var refinedCore = refined.Plan!.After.Find(target.Position)!.Core;
        That(refined.Accepted && refined.Plan.After.Find(material.Position)?.Core.Count == 3 &&
            refinedCore.Rune == originalCore.Rune && refinedCore.Extension119 == originalCore.Extension119 &&
            refinedCore.Extension114.Flag == originalCore.Extension114.Flag && (refinedCore.Extension114.Value >> 24) == 0xFE,
            "重锻按品级足额扣料，仅替换三孔颜色，其他完整实例字段保留");
        That(!service.Refine(Character(target, material with { Core = material.Core with { Value = 1 } }), new(121, 0), costs, Now).Accepted,
            "重锻材料不足不返回扣料计划");

        var list = InventoryPackets.SpecialFull(afterEquip.Inventory, InventoryListType.Talisman, Now);
        That(list[0] == 46 && BinaryPrimitives.ReadUInt16LittleEndian(list.AsSpan(1)) == 2 && list.Length == 3 + 2 * 123,
            "list46全量直接使用123B普通物品行，并包含装载符文真实槽");
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        EquipmentProtocolWriter.Runes(writer, afterEquip.Inventory);
        var vector = stream.ToArray();
        That(vector.Length == 25 && vector[0] == 6 && BinaryPrimitives.ReadInt32LittleEndian(vector.AsSpan(1)) == RuneId &&
            vector.AsSpan(5).IndexOfAnyExcept((byte)0) < 0, "本人装备尾向量固定六位置，未装孔不压缩错位");
        Console.WriteLine($"PASS {Count - start} talisman inventory checks");
    }

    static ItemCatalog Catalog()
    {
        var catalog = new ItemCatalog(null!, new Dictionary<int, string>(), new Dictionary<int, string>(), 0);
        var other = Rune with { Id = OtherRuneId };
        catalog.PrefillTemplates(new[] { Stone, Rune, other, Material }.ToDictionary(item => item.Id));
        catalog.PrefillTalismans([new(Stone, [1], 9)]);
        catalog.PrefillRunes([new(Rune, [1], 9, RuneCamp.SecondPact, 2), new(other, [1], 10, RuneCamp.SecondPact, 2)]);
        return catalog;
    }

    static InventoryItem StoneItem(long uid, short slot, InventoryListType list = InventoryListType.Talisman) =>
        new(uid, new(list, slot), new TalismanSocketColors(1, 2, 3).Apply(new(StoneId, ItemKind.Talisman, 999999998)));
    static Character Character(params InventoryItem[] items) => new(1, 0, "Talisman", 0, new(1, 0, 10, 10), null)
    { Job = 0, GrowType = 1, Level = 95, Inventory = new(items.ToImmutableArray()) };
}
