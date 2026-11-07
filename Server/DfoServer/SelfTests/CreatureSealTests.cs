using System.Buffers.Binary;
using DfoServer.Game.Characters;
using DfoServer.Game.Creatures;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class CreatureSealTests
{
    public static void RunCore()
    {
        That(CreatureSealRequests.Read(Convert.FromHexString("160220006A8A5F0744E3D9581608002FF600006D00")) ==
            new CreatureSealRequest(8, 63023, 109), "真实1014抓包解析为Pet8波比63023与Main109工具，不把前13B当外层帧头");
        var wire = Enumerable.Repeat((byte)0xCD, 21).ToArray();
        BinaryPrimitives.WriteInt16LittleEndian(wire.AsSpan(13), 139);
        BinaryPrimitives.WriteInt32LittleEndian(wire.AsSpan(15), 63023);
        BinaryPrimitives.WriteInt16LittleEndian(wire.AsSpan(19), 65);
        That(CreatureSealRequests.Read(wire) == new CreatureSealRequest(139, 63023, 65), "封印字段固定尾部，前13字节不决定身份");
        foreach (int length in new[] { 0, 8, 20, 22 })
            Reject<InvalidDataException>(() => CreatureSealRequests.Read(new byte[length]), "拒绝非完整21B请求");
        var map = ItemCatalog.ReadCreatureCapsules("[creature capsule]\n63022 10002402 63023 10002402\n[/creature capsule]");
        That(map[63022] == 10002402 && map[63023] == 10002402, "蛋与成宠分别按表映射同一胶囊");
        Reject<InvalidDataException>(() => ItemCatalog.ReadCreatureCapsules("[creature capsule]\n1 2 1 3"), "不接受冲突源映射");
        var tool = new ItemTemplate(2683241, ItemKind.Consumable, 0, 1, 0, 1, 0, 1, 1, "[trade]", ushort.MaxValue, 0, "tool");
        var capsule = tool with { Id = 10002402, AttachType = "[trade limit]", AccountCargoForbidden = true };
        var pieces = new EpicPieceCatalog("[piece list]\n50000 50001\n[/piece list]");
        var pet = new InventoryItem(2, new(InventoryListType.Pet, 0), new(63023, ItemKind.Creature, 2)) { Creature = new() { Name = "old" } };
        var source = new InventoryItem(1, new(InventoryListType.Main, 65), new(tool.Id, ItemKind.Consumable, 1));
        Character character = new(1, 0, "Seal", 5, new(1, 0, 10, 10), null) { Inventory = new([source, pet]) };
        InventoryResult Plan(Character value) => CreatureSealService.Plan(value, 0, 63023, 65, tool, capsule, pieces, TimeProvider.System);
        var result = Plan(character);
        That(result.Accepted && result.Plan!.After.Find(pet.Position) is null && result.Plan.After.Find(source.Position) is
            { Uid: 0, Core.ItemId: 10002402, Core.Attr: 32, Creature: null }, "一次计划移除宠物与最后工具，在释放的消耗品槽创建一次交易礼包");
        That(!Plan(character with { Inventory = result.Plan!.After }).Accepted, "重复请求不重复发放胶囊");
        foreach (var invalid in new[] { pet with { Core = pet.Core with { EquipmentLockId = 1 } },
            pet with { Creature = pet.Creature! with { ExpireTime = int.MaxValue } }, pet with { Core = pet.Core with { Value = 3 } } })
            That(!Plan(character with { Inventory = new([source, invalid]) }).Accepted, "安全锁、限时宠物和UID不匹配均拒绝");
        var stacked = character with { Inventory = new([source with { Core = source.Core with { Value = 2 } }, pet]) };
        var full = stacked with { Inventory = stacked.Inventory with { Items = stacked.Inventory.Items.AddRange(
            Enumerable.Range(66, 55).Select(slot => new InventoryItem(slot, new(InventoryListType.Main, (short)slot), new(tool.Id, ItemKind.Consumable, 1)))) } };
        That(!Plan(full).Accepted && full.Inventory.Find(pet.Position) == pet, "产物分页满时整笔拒绝，不吞宠物和工具");
        Bytes(CreatureSealPackets.Ack(new(0, 63023, 65), result), [1, 0,0,0,0, 0,0, 0,0, 65,0, 0,0,0,0], "成功ACK为1+14B，工具槽复用产物不被当成工具剩余量");
        That(!result.Plan.CharacterPrecondition!.Matches(character with { Revision = 6 }), "过期revision不能重新绑定计划");
    }
}
