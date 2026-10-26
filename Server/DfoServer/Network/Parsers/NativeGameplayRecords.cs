using System.Buffers.Binary;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Network.Parsers;

internal sealed record NativeGameplayRecord(byte Kind, uint Sequence, byte SourceSlot, ReadOnlyMemory<byte> Wire,
    NpcFavorAppearance? Appearance = null, DungeonPoint? Position = null);

internal static class NativeGameplayRecords
{
    public const int MaximumLength = 8192 - 13;

    // 03DA1DA0逐条读取：0为位图确认，1/8为可靠数据，2为无序数据；不把整个载荷当单个UDP包。
    public static NativeGameplayRecord[] Read(byte[] body)
    {
        if (body.Length > MaximumLength) throw new InvalidDataException("原生传输记录超过客户端缓冲");
        var rows = new List<NativeGameplayRecord>();
        int at = 0;
        while (at < body.Length)
        {
            if (rows.Count == 256) throw new InvalidDataException("原生传输记录数量越界");
            int start = at;
            byte kind = body[at++];
            if (kind == 0)
            {
                if (body.Length - at < 7) throw new InvalidDataException("原生确认记录截断");
                byte slot = body[at];
                uint sequence = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(at + 1));
                int length = (body[at + 5] >> 3) + 7;
                if (slot > 7 || body.Length - at < length) throw new InvalidDataException("原生确认位图无效");
                at += length;
                rows.Add(new(kind, sequence, slot, body.AsMemory(start, at - start)));
                continue;
            }
            if (kind is not (1 or 2 or 8) || body.Length - at < 8)
                throw new InvalidDataException("原生数据记录种类或头长无效");
            uint serial = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(at));
            int count = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(at + 4));
            byte source = body[at + 6];
            at += 8;
            if (source > 7 || count > body.Length - at)
                throw new InvalidDataException("原生数据记录槽位或长度无效");
            NpcFavorAppearance? appearance = null;
            DungeonPoint? position = null;
            var data = body.AsSpan(at, count);
            if (count >= 3 && BinaryPrimitives.ReadUInt16LittleEndian(data[1..]) == 140)
            {
                if (count is not (19 or 21) || data[0] > 2) throw new InvalidDataException("UDP140头或正文宽度无效");
                byte[] plain = data[7..].ToArray();
                Transform(plain, false);
                if (GameFrame.Crc(plain) != BinaryPrimitives.ReadUInt32LittleEndian(data[3..]))
                    throw new InvalidDataException("UDP140正文校验失败");
                int offset = plain.Length == 14 ? 2 : 0;
                if (offset != 0) position = new(plain[0], plain[1]);
                appearance = new(BinaryPrimitives.ReadInt32LittleEndian(plain.AsSpan(offset)),
                    BinaryPrimitives.ReadInt32LittleEndian(plain.AsSpan(offset + 4)),
                    BinaryPrimitives.ReadUInt32LittleEndian(plain.AsSpan(offset + 8)));
            }
            at += count;
            rows.Add(new(kind, serial, source, body.AsMemory(start, at - start), appearance, position));
        }
        return rows.ToArray();
    }

    // 036B4110写入固定基值+本构建UDP数量；0369FA30用同一次三步LCG结果解码。
    public static byte[] WithAppearance(NativeGameplayRecord record, NpcFavorAppearance appearance)
    {
        if (record.Appearance is null) throw new InvalidDataException("非UDP140记录不能替换好感NPC");
        var result = record.Wire.ToArray();
        int offset = record.Position is null ? 0 : 2;
        var plain = new byte[12 + offset];
        if (record.Position is { } position) { plain[0] = checked((byte)position.X); plain[1] = checked((byte)position.Y); }
        BinaryPrimitives.WriteInt32LittleEndian(plain.AsSpan(offset), appearance.NpcId);
        BinaryPrimitives.WriteInt32LittleEndian(plain.AsSpan(offset + 4), appearance.NameColor);
        BinaryPrimitives.WriteUInt32LittleEndian(plain.AsSpan(offset + 8), appearance.Colors);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(12), GameFrame.Crc(plain));
        Transform(plain, true);
        plain.CopyTo(result.AsSpan(16));
        return result;
    }

    static void Transform(Span<byte> bytes, bool sending)
    {
        uint seed = 3084777u + (uint)UdpPacketType.END;
        uint first = unchecked(seed * 1103515245u + 12345u);
        uint second = unchecked(first * 1103515245u + 12345u);
        uint third = unchecked(second * 1103515245u + 12345u);
        uint key = (((first >> 16 & 0x7FF) << 10) ^ (second >> 16 & 0x3FF)) << 10 ^ (third >> 16 & 0x3FF);
        int shift = (int)(key >> 8 & 7);
        for (int index = 0; index < bytes.Length; index++)
        {
            if (sending)
            {
                byte value = (byte)(bytes[index] ^ key);
                bytes[index] = (byte)((value >> shift) | (value << ((8 - shift) & 7)));
            }
            else bytes[index] = (byte)(((bytes[index] << shift) | (bytes[index] >> ((8 - shift) & 7))) ^ key);
        }
    }
}
