using DfoServer.Game.Characters;

namespace DfoServer.Network.Parsers;

internal readonly record struct ExpertJobCompoundRequest(uint First, ushort Second, ushort Third);
internal readonly record struct ExpertJobEnchantStoreRequest(ushort First, uint Second, byte Third, byte Fourth, ushort Fifth, byte Sixth, ushort Seventh);
internal readonly record struct ExpertJobDisjointRequest(ushort First, ushort Second, byte Third);
internal readonly record struct ExpertJobCreateStoreRequest(byte Mode, string Name, uint First, ushort Second, ushort Third, ushort Fourth);
internal readonly record struct ExpertJobExtractionRequest(byte ExtractorType, short ExtractorSlot, byte TargetList, short TargetSlot);

internal static class ExpertJobRequests
{
    // C2S 238 038BDAF7：u32 esi+0x210、u16 esi+0x234、u16 esi+0x254。
    public static ExpertJobCompoundRequest ReadCompound(byte[] body)
    {
        using var reader = Open(body, 8);
        var request = new ExpertJobCompoundRequest(reader.ReadUInt32(), reader.ReadUInt16(), reader.ReadUInt16());
        RequireEnd(reader);
        return request;
    }

    // C2S 601 021329E4/02132ABA：u16 店主演员（02D82BD0）、u32 edi+0x294 配方、
    // 常量 u8=2（不是列表类型）、u8 目标列表硬编码 0、u16 edi+0x298 目标槽、
    // u8 卡片列表硬编码 0、u16 edi+0x29C 卡/珠槽。
    public static ExpertJobEnchantStoreRequest ReadEnchantStore(byte[] body)
    {
        using var reader = Open(body, 13);
        var request = new ExpertJobEnchantStoreRequest(reader.ReadUInt16(), reader.ReadUInt32(), reader.ReadByte(),
            reader.ReadByte(), reader.ReadUInt16(), reader.ReadByte(), reader.ReadUInt16());
        RequireEnd(reader);
        return request;
    }

    // C2S 416 029D2F20：u8 萃取器类型、u16 萃取器槽、u8 目标列表、u16 目标槽。与 90 六字节同形。
    public static ExpertJobExtractionRequest ReadExtraction(byte[] body)
    {
        using var reader = Open(body, 6);
        var request = new ExpertJobExtractionRequest(reader.ReadByte(), reader.ReadInt16(), reader.ReadByte(),
            reader.ReadInt16());
        RequireEnd(reader);
        return request;
    }

    // C2S 599 0209F040：u16 店主演员。
    public static ushort ReadEnterStore(byte[] body)
    {
        using var reader = Open(body, 2);
        ushort actor = reader.ReadUInt16();
        RequireEnd(reader);
        return actor;
    }

    // C2S 235 029D5A85：u16 ebp+0xC、u16 ebp+8、对齐后的 u8。
    public static ExpertJobDisjointRequest ReadDisjoint(byte[] body)
    {
        using var reader = Open(body, 5);
        var request = new ExpertJobDisjointRequest(reader.ReadUInt16(), reader.ReadUInt16(), reader.ReadByte());
        RequireEnd(reader);
        return request;
    }

    // C2S 598 0209FB0A：u8 模式 0/3，03BDF850 的 i32 长度+字节，u32、三个 u16。
    public static ExpertJobCreateStoreRequest ReadCreateStore(byte[] body)
    {
        using var reader = Open(body, 15);
        byte mode = reader.ReadByte();
        if (mode is not (0 or 3)) throw new InvalidDataException("开设副职业商店模式不是 0 或 3");
        int length = reader.ReadInt32();
        if (length is < 0 or > 64) throw new InvalidDataException("开设副职业商店名称长度非法");
        string name = length == 0 ? "" : CharacterName.Encoding.GetString(reader.ReadBytes(length));
        var request = new ExpertJobCreateStoreRequest(mode, name, reader.ReadUInt32(), reader.ReadUInt16(),
            reader.ReadUInt16(), reader.ReadUInt16());
        RequireEnd(reader);
        return request;
    }

    public static void ReadEmpty(byte[] body)
    {
        if (body.Length != 0) throw new InvalidDataException("该副职业命令正文必须为空");
    }

    static BinaryReader Open(byte[] body, int minimum)
    {
        if (body.Length < minimum) throw new InvalidDataException("副职业命令正文过短");
        return new(new MemoryStream(body, writable: false));
    }

    static void RequireEnd(BinaryReader reader)
    {
        if (reader.BaseStream.Position != reader.BaseStream.Length)
            throw new InvalidDataException("副职业命令正文有多余字节");
    }
}
