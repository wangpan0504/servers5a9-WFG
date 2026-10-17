using System.Buffers.Binary;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Network.Builders;

internal static class LevelUpTicketPackets
{
    // ARAD 0235BFF0：ACK先消费源并增加经验/SP/TP；绝对成长通知必须在其后。
    public static byte[] Used(LevelUpTicketSource source)
    {
        if (source.Kind == LevelUpConsumableKind.JumpingTable)
            return QuestTicketPackets.Used(new(checked((ushort)source.Item.Position.Index), (byte)source.Item.Position.List,
                0, LevelUpByTableDefinition.JumpingAction, 0, 0));
        byte[] body = new byte[12];
        body[0] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(1), checked((ushort)source.Item.Position.Index));
        body[3] = source.Kind == LevelUpConsumableKind.StatusTable ? (byte)1 : (byte)source.Item.Position.List;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), checked((uint)source.Growth.GainedExperience));
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(8), checked((ushort)source.Growth.LevelSpDelta));
        int tp = source.Growth.Learning.Trees.Single(tree=>tree.Context==0).Tp -
            source.Growth.Before.Trees.Single(tree=>tree.Context==0).Tp;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(10), checked((ushort)tp));
        return body;
    }

    public static byte[] Rejected() => [0,99,0];

    public static byte[] Rejected(LevelUpConsumableKind kind, InventorySlot slot) => kind switch
    {
        LevelUpConsumableKind.Ticket => Rejected(),
        LevelUpConsumableKind.StatusTable => ExperienceConsumablePackets.Rejected(),
        LevelUpConsumableKind.JumpingTable => QuestTicketPackets.Rejected(new(unchecked((ushort)slot.Index), (byte)slot.List,
            0, LevelUpByTableDefinition.JumpingAction, 0, 0), 19),
        _ => throw new InvalidDataException("未知升级道具回复入口")
    };
}
