using DfoServer.Game.Characters;
using DfoServer.Network.Builders.Inventory;

namespace DfoServer.Network.Builders;

internal static class CharacterPackets
{
    // Client ACK692 错误码为 u16；20/24 都弹“名称重复”，当前采用24。
    // 错误码2只记录致命错误日志，不显示重名弹窗。
    public static byte[] NameCheck(bool exists) => exists ? [0, 24, 0] : [1];
    // ACK6成功额外读取u8和u16；123仅作为已确认的静默收尾，不表示某个业务错误。
    public static byte[] Deleted(CharacterDeletionResult result) => result.Receipt is { } receipt
        ? [1, 0, (byte)receipt.RosterIndex, (byte)(receipt.RosterIndex >> 8)] : [0, 123, 0];
    public static byte[] Created(Character character)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)1);
        writer.Write(character.Id);
        PacketEncoding.WriteString(writer, character.Name);
        return stream.ToArray();
    }

    public static byte[] Roster(CharacterRosterEntry[] rows)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(new byte[] { 2, (byte)rows.Length, 5 });
        writer.Write((ushort)32);
        writer.Write((ushort)32);
        writer.Write((ushort)rows.Length);
        writer.Write(0u);
        writer.Write((ushort)rows.Length);
        foreach (var character in rows)
        {
            writer.Write(character.Slot);
            PacketEncoding.WriteString(writer, character.Name);
            writer.Write(new byte[] { 0, 0, character.Job, character.GrowType, character.Level });
            writer.Write(new byte[10]);
            EquipmentProtocolWriter.Appearance(writer, character.Equipment);
            byte[] tail = new byte[48];
            tail[24] = 3;
            tail[27] = 4;
            writer.Write(tail);
        }

        writer.Write(new byte[] { 1, 0 });
        writer.Write(new byte[8]);
        return stream.ToArray();
    }
}
