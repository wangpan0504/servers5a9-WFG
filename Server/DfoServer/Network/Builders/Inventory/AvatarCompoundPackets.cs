using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Network.Builders.Inventory;

internal static class AvatarCompoundPackets
{
    public const int SuccessSize = 131;

    public static byte[] Success(AvatarCompoundRequest request, IReadOnlyList<InventoryItem> outputs,
        long? utcSeconds = null)
    {
        if (outputs.Count is < 1 or > 2 || outputs.Select(output => output.Position).Distinct().Count() != outputs.Count)
            throw new InvalidDataException("装扮合成成功回执必须包含一至两个不同槽位的产出");
        using var stream = new MemoryStream(SuccessSize);
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)1);
        writer.Write((byte)3);
        Consume(writer, InventoryListType.Avatar, request.SourceSlot0);
        Consume(writer, InventoryListType.Avatar, request.SourceSlot1);
        Consume(writer, InventoryListType.Main, request.MaterialSlot);
        long now = utcSeconds ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        for (int index = 0; index < 2; index++)
            Output(writer, index < outputs.Count ? outputs[index] : null, request.Ability, now);
        byte[] body = stream.ToArray();
        if (body.Length != SuccessSize)
            throw new InvalidDataException("装扮合成成功回执宽度变化");
        return body;
    }

    public static byte[] Failure() => [0, 22, 0];

    static void Consume(BinaryWriter writer, InventoryListType list, short slot)
    {
        writer.Write((byte)list);
        writer.Write(slot);
        writer.Write(1);
    }

    static void Output(BinaryWriter writer, InventoryItem? item, ushort fallbackAbility, long utcSeconds)
    {
        if (item is null)
        {
            writer.Write((short)-1);
            writer.Write(0);
            writer.Write(0);
            writer.Write(fallbackAbility);
            ItemListProtocolWriter.WriteAvatarSockets(writer, null);
            ItemListProtocolWriter.WriteAvatarColors(writer, null);
            return;
        }
        if (item.Position.List != InventoryListType.Avatar || item.Core.Kind != ItemKind.Avatar || item.Avatar is null)
            throw new InvalidDataException("装扮合成回执产出不是完整时装实例");
        writer.Write(item.Position.Index);
        writer.Write(item.Core.ItemId);
        writer.Write(item.RemainingSeconds(utcSeconds));
        writer.Write(item.Core.Durability);
        ItemListProtocolWriter.WriteAvatarSockets(writer, item.Avatar);
        ItemListProtocolWriter.WriteAvatarColors(writer, item.Avatar);
    }
}
