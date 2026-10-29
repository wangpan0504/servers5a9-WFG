using DfoServer.Game.Characters;
using DfoServer.Network.Builders.Inventory;

namespace DfoServer.Network.Builders;

internal static class CharacterPackets
{
    // 1408：u8地区数，逐地区u8地区/u16待收人数。本服只有已发NOTI2的本地区名单。
    public static byte[] AllServerRosterCounts() => [0];

    // 0233CDB0成功额外u8为可选提示；0继续命名。失败非150/151仅静默结束，沿既有拒绝码19不猜提示语义。
    public static byte[] PvpPrivateChecked(bool allowed) => allowed ? [1,0] : [0,19,0];
    public static byte[] HiddenRoster() => [1, 0];
    // Client ACK692 错误码为 u16；20/24 都弹“名称重复”，当前采用24。
    // 错误码2只记录致命错误日志，不显示重名弹窗。
    public static byte[] NameCheck(bool exists) => exists ? ClientFailurePackets.Create(ClientCharacterNameError.Duplicate).Body : [1];
    // ACK6成功额外读取u8和u16；123仅作为已确认的静默收尾，不表示某个业务错误。
    public static byte[] Deleted(CharacterDeletionResult result) => result.Receipt is { } receipt
        ? [1, 0, (byte)receipt.RosterIndex, (byte)(receipt.RosterIndex >> 8)] : [0, 123, 0];
    // ACK295成功只需单字节；90CN/S4A21同序：先ACK，再完整roster。失败正文在本版无匹配证据，不猜造。
    public static byte[] SlotChanged() => [1];
    public static byte[] Created(Character character)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)1);
        writer.Write(character.Id);
        PacketEncoding.WriteString(writer, character.Name);
        return stream.ToArray();
    }

    // 名册行 48 字节尾部 tail[37..41] 由 ARAD 02676A00 依次读到 +0x491/+0x492/+0x493/+0x499/+0x494（==1 为真）。
    internal const int AntonEligibleTailOffset = 37;
    internal const int LukeEligibleTailOffset = 38;
    // tail[39] -> +0x493 魔兽频道（type 0x28）：完成 5927 为 1。
    internal const int BeastEligibleTailOffset = 39;
    // 名册行 48 字节尾部中超时空漩涡资格字节的偏移（KB-20261021T015635-5c920e4e59）。
    internal const int FiendWarEligibleTailOffset = 40;
    internal const int PreyEligibleTailOffset = 41;
    // 02681030 读 u32 进 Data+49Ch；与 Actor 头 +6Eh 同一武器幻化字段。光环 u16 仍在 tail[46]。
    internal const int WeaponSkinTailOffset = 42;

    public static byte[] Roster(CharacterRosterEntry[] rows, ushort capacity = 32)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(new byte[] { 2, (byte)rows.Length, 5 });
        writer.Write(capacity);
        writer.Write(capacity);
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
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(tail, character.CloneTitleItemId);
            tail[24] = 3;
            // 02680FA3读取非零为真，02680FCE安装到Data+420，再投影展示角色+149C。
            tail[34] = character.IsPvpPrivate ? (byte)1 : (byte)0;
            tail[27] = 4;
            // 选角前资格位：为 0 时进入对应类型频道弹 1099。
            tail[AntonEligibleTailOffset] = character.AntonEligible ? (byte)1 : (byte)0;
            tail[LukeEligibleTailOffset] = character.LukeEligible ? (byte)1 : (byte)0;
            tail[BeastEligibleTailOffset] = character.BeastEligible ? (byte)1 : (byte)0;
            tail[FiendWarEligibleTailOffset] = character.FiendWarEligible ? (byte)1 : (byte)0;
            tail[PreyEligibleTailOffset] = character.PreyEligible ? (byte)1 : (byte)0;
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(tail.AsSpan(WeaponSkinTailOffset),
                character.WeaponSkin);
            // 0268104E读取末尾u16，只有1才经02A8DCF0启用预览光环；高字节由零初始化保留。
            tail[46] = 1;
            writer.Write(tail);
        }

        writer.Write(new byte[] { 1, 0 });
        writer.Write(new byte[8]);
        return stream.ToArray();
    }
}
