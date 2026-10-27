using System.Collections.Immutable;
using System.Buffers.Binary;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Network.Builders.Inventory;

namespace DfoServer.Testing.Inventory;

internal static class InventoryProjectionTests
{
    public static void Run()
    {
        int start = TestCheck.Count;
        var random = new Random(1702);
        foreach (ushort expansion in new ushort[] { 0, 8, 16, 24 })
            for (int sample = 0; sample < 30; sample++)
            {
                var items = new List<InventoryItem>();
                foreach (var kind in new[] { ItemKind.Equipment, ItemKind.Consumable, ItemKind.Material, ItemKind.Quest, ItemKind.Profession, ItemKind.Emblem })
                {
                    var range = ItemSlotBoundService.Range(kind, expansion);
                    for (short slot = range.Start; slot <= range.End; slot++)
                        if (sample == 1 || sample > 1 && random.Next(3) == 0)
                            items.Add(new(slot + 1, new(InventoryListType.Main, slot), new(50000 + slot, kind, 7)
                            { Rune = 3, EnchantCardId = slot, EquipmentLockId = 1, SortLockFlag = 1 }));
                }
                if (sample > 0)
                    for (short slot = 3; slot < 9; slot++)
                        items.Add(new(slot + 1, new(InventoryListType.Main, slot), new(51000 + slot, ItemKind.Material, slot)));
                // 其他容器与 Main 槽号重叠，不能进入本列表或影响其取值。
                for (short slot = 0; slot < 64; slot++)
                {
                    items.Add(new(1000 + slot, new(InventoryListType.PersonalCargo, slot), new(52000, ItemKind.Material, 2)));
                    items.Add(new(2000 + slot, new(InventoryListType.AccountCargo, slot), new(53000, ItemKind.Equipment, 0)));
                }
                var wallet = AccountWallet.Empty;
                foreach (var kind in Enum.GetValues<CurrencyKind>().Where(kind => kind >= CurrencyKind.BlackCube && kind <= CurrencyKind.EpicSoul))
                    wallet = wallet.Set(kind, sample == 0 ? 0 : (int)kind * 13);
                var inventory = new InventorySnapshot(items.OrderBy(_ => random.Next()).ToImmutableArray(), expansion, (uint)(sample * 123))
                { AccountWallet = wallet, ReviveCoins = sample, WinPoints = sample * 3, CargoCapacity = 64, AccountCargo = new(0, 64, 0) };
                inventory.Validate();
                byte[] actual = InventoryPackets.Full(inventory);
                TestCheck.That(actual.SequenceEqual(ReferenceFull(inventory)), "无序、多容器、全部扩展档的 Main 全量与逐槽参考算法一致");
                var expectedSlots = inventory.Items.Where(item => item.Position.List == InventoryListType.Main).Select(item => item.Position.Index)
                    .Concat(new short[] { 0, 1, 2, 354, 355, 356, 357, 358, 359, 360, 361, 362, 363, 364, 365 }).Order().ToArray();
                TestCheck.That(actual[0] == 0 && BinaryPrimitives.ReadUInt16LittleEndian(actual.AsSpan(1)) == expansion &&
                    BinaryPrimitives.ReadUInt16LittleEndian(actual.AsSpan(3)) == expectedSlots.Length &&
                    actual.Length == 5 + expectedSlots.Length * ItemListProtocolWriter.EntrySize, "列表头、虚拟零值记录及全部实体长度不变");
                TestCheck.That(expectedSlots.Select((slot, index) => slot == BinaryPrimitives.ReadInt16LittleEndian(actual.AsSpan(5 + index * ItemListProtocolWriter.EntrySize))).All(value => value), "所有输出槽位按 wire 顺序有序且无遗漏");
            }
        var wornCore = new ItemCore(18400, ItemKind.Equipment, 999999998) { SortLockFlag = 1, EquipmentLockId = 2 };
        foreach (var list in new[] { InventoryListType.Main, InventoryListType.Equipment })
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            var item = new InventoryItem(7, new(list, 12), wornCore);
            ItemListProtocolWriter.Write(writer, item.Position, item, 0);
            TestCheck.That(stream.ToArray()[95] == (list == InventoryListType.Equipment ? 0 : 1) &&
                stream.ToArray()[96] == 2 && item.Core == wornCore, "穿戴蓝锁投影隐藏，背包和安全锁保留，实例不解锁");
        }
        using (var stream = new MemoryStream())
        {
            using var writer = new BinaryWriter(stream);
            EquipmentProtocolWriter.Player(writer, new([new(7, new(InventoryListType.Equipment, 12), wornCore)]), 0);
            TestCheck.That(stream.ToArray()[44] == 0 && stream.ToArray()[45] == 2,
                "Player穿戴尾部独立隐藏排序锁，保留装备安全锁");
        }
        var duplicate = new InventorySnapshot([new(1, new(InventoryListType.Main, 121), new(50001, ItemKind.Material, 1)),
            new(2, new(InventoryListType.Main, 121), new(50002, ItemKind.Material, 1))]);
        try { InventoryPackets.Full(duplicate); throw new Exception("重复槽位未拒绝"); }
        catch (ArgumentException) { TestCheck.That(true, "投影仍拒绝重复实体槽位"); }
        Console.WriteLine($"PASS {TestCheck.Count - start} 全量背包投影与独立槽位断言");
    }

    static byte[] ReferenceFull(InventorySnapshot inventory)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        var slots = inventory.Items.Where(item => item.Position.List == InventoryListType.Main).Select(item => item.Position.Index)
            .Concat(CurrencyPackets.VirtualSlots).Order().ToArray();
        writer.Write((byte)InventoryListType.Main);
        writer.Write(inventory.Expansion);
        writer.Write(checked((ushort)slots.Length));
        foreach (short slot in slots)
            if (!CurrencyPackets.WriteVirtual(writer, inventory, slot))
                ItemListProtocolWriter.Write(writer, slot, inventory.Find(new(InventoryListType.Main, slot))!.Core);
        return stream.ToArray();
    }
}
