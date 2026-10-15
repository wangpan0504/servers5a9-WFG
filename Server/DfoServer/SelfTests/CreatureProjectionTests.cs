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
            byte[] body = TownPackets.Actor(character);
            int nameWidth = CharacterName.Encoding.GetByteCount(character.Name);
            int countOffset = 133 + nameWidth;
            // 既有Actor头、25B穿戴摘要和22B前置尾字段；独立定位0267B3B0的三个读取值。
            int offset = countOffset + 1 + body[countOffset] * 25 + 22;
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
