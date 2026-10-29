using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;
using DfoServer.Network.Builders;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class CreatureProjectionTests
{
    public static void RunCore()
    {
        var role = new Character(1, 0, "宠物测试", 1, new(38, 1, 450, 234), null);
        var first = new InventoryItem(91, new(InventoryListType.Pet, 0),
            new(63000, ItemKind.Creature, 91))
            { Creature = new() { Name = "小龙", Level = 9, Satiety = 77, Experience = 1234 } };
        var second = first with { Uid = 92, Position = new(InventoryListType.Pet, 1),
            Core = first.Core with { ItemId = 63001, Value = 92 }, Creature = new() { Name = "Second" } };
        Check(role, 0, "", "无宠物保持原空Actor字段");
        Check(role with { Inventory = new([first, second]) }, 0, "", "背包中的宠物不创建穿戴实体");
        var worn = first with { Position = new(InventoryListType.Equipment, 26) };
        var current = role with { Inventory = new([second, worn]) };
        Check(current, 63000, "小龙", "Actor发送穿戴模板而非UID，并保留CP936名称");
        Check(current with { Inventory = new([second with { Position = worn.Position }, first]) },
            63001, "Second", "交换后投影新的已穿戴实例");
        Check(current with { Inventory = new([first, second]) }, 0, "", "脱下后清除实体模板及名称");
        var victoria = worn with { Core = worn.Core with { ItemId = 400990444, EnchantCardId = 490705461 },
            Creature = new() { Satiety = 100, Level = 1, Experience = 0 } };
        var victoriaBefore = System.Text.Json.JsonSerializer.Serialize(victoria);
        Check(role with { Inventory = new([victoria]) }, 400990444, "", "维多利亚空自定义名称仍启用城镇实体");
        That(System.Text.Json.JsonSerializer.Serialize(victoria) == victoriaBefore, "投影保留真实模板的完整实例、附魔和详情");
        That(worn.Creature == first.Creature && worn.Core.Value == 91 && current.Revision == 1,
            "Actor投影不改成长、饱食度、UID或revision");

        static void Check(Character character, uint itemId, string name, string message)
        {
            byte[] body = TownPackets.Actor(character, 0);
            int nameWidth = CharacterName.Encoding.GetByteCount(character.Name);
            int countOffset = 133 + nameWidth;
            // 按0267DF0B的独立读取顺序定位，状态在外观列表之前，不是NameTag之后。
            int stateOffset = 132 + nameWidth;
            byte[] dungeon = TownPackets.Actor(character, 0, sceneState: ActorSceneState.Dungeon);
            That(body[stateOffset] == 0 && dungeon[stateOffset] == 1 &&
                body[stateOffset - 2] == 0 && body[stateOffset - 1] == 0 &&
                body.AsSpan(0, stateOffset).SequenceEqual(dungeon.AsSpan(0, stateOffset)) &&
                body.AsSpan(stateOffset + 1).SequenceEqual(dungeon.AsSpan(stateOffset + 1)),
                "Actor默认城镇状态0，副本状态1仅改变Level后第三字节，保留其余报文");
            // 既有Actor头、25B穿戴摘要和22B前置尾字段；独立定位0267B3B0的三个读取值。
            int offset = countOffset + 1 + body[countOffset] * 25 + 22;
            That(body[offset - 6] == 0 && dungeon[offset - 6] == 0,
                "NameTag后guildId前的独立字节保持0，不误当作过房门禁");
            byte[] expectedName = CharacterName.Encoding.GetBytes(name);
            That(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(offset)) == itemId &&
                System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(offset + 4)) == expectedName.Length &&
                body.AsSpan(offset + 8, expectedName.Length).SequenceEqual(expectedName) &&
                body[offset + 8 + expectedName.Length] == (itemId == 0 ? 0 : 1), message + "；有实例时明确启用，无实例时关闭");
            That(body.Length == 243 + nameWidth + body[countOffset] * 25 + expectedName.Length,
                "宠物名称只扩展对应DSTR，后续Actor尾字段完整");
        }
    }
}
