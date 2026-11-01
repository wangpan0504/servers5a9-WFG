using System.Buffers.Binary;

namespace DfoServer.Network.Builders;

internal readonly record struct FiendwarUserRow(uint Operation, byte Area, ushort Character, uint RecoveryEnd);
internal readonly record struct FiendwarUnitRow(byte Area, uint Monster, byte Kind, uint Operation, uint RawFlags = 2);
internal readonly record struct FiendwarTimerRow(byte Area, byte Kind, uint End, uint Duration);

// JP 00D31160登记1522–1530；各消费者按整行读取，禁止套用其它客户端的编号。
internal static class FiendwarPackets
{
    // 00D310D0：NOTI1554只读一个路线字节，并播放对应团长支援提示。
    internal static byte[] SupportNotice(byte lane) => lane <= 2 ? [lane] : throw new ArgumentOutOfRangeException(nameof(lane));

    // 00D307A0连续读取3B；状态语义由投影选择，构造器不推进战场。
    internal static byte[] State(byte operation, ushort argument)
    {
        byte[] body = new byte[3]; body[0] = operation;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(1), argument);
        return body;
    }
    // 00D30830：u8数量，每行区域与警告两个u8。
    internal static byte[] Warnings(IReadOnlyList<(byte Area, byte Warning)> rows)
    {
        var body = Counted(rows.Count, 2);
        for (int i = 0; i < rows.Count; i++) { body[1 + i * 2] = rows[i].Area; body[2 + i * 2] = rows[i].Warning; }
        return body;
    }
    // 00D309F0/00D2CC30：操作u32、区域u8、角色u32、恢复截止u32。
    internal static byte[] Users(IReadOnlyList<FiendwarUserRow> rows)
    {
        var body = Counted(rows.Count, 13);
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row.Operation is not (1 or 2)) throw new ArgumentOutOfRangeException(nameof(rows));
            var span = body.AsSpan(1 + i * 13, 13);
            BinaryPrimitives.WriteUInt32LittleEndian(span, row.Operation); span[4] = row.Area;
            BinaryPrimitives.WriteUInt32LittleEndian(span[5..], row.Character);
            BinaryPrimitives.WriteUInt32LittleEndian(span[9..], row.RecoveryEnd);
        }
        return body;
    }
    // 00D30B40读10B，00D30C90读14B；尾部额外u32只属于军团行。
    internal static byte[] Monsters(IReadOnlyList<FiendwarUnitRow> rows) => Units(rows, false);
    internal static byte[] Enemies(IReadOnlyList<FiendwarUnitRow> rows) => Units(rows, true);
    static byte[] Units(IReadOnlyList<FiendwarUnitRow> rows, bool enemy)
    {
        int width = enemy ? 14 : 10;
        var body = Counted(rows.Count, width);
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row.Operation is not (1 or 2)) throw new ArgumentOutOfRangeException(nameof(rows));
            var span = body.AsSpan(1 + i * width, width); span[0] = row.Area;
            BinaryPrimitives.WriteUInt32LittleEndian(span[1..], row.Monster); span[5] = row.Kind;
            BinaryPrimitives.WriteUInt32LittleEndian(span[6..], row.Operation);
            if (enemy) BinaryPrimitives.WriteUInt32LittleEndian(span[10..], row.RawFlags);
        }
        return body;
    }
    // 00D30DF0/00D2D160：区域u8、timer种类u8、截止u32、总长u32；清除也发送完整10B行。
    internal static byte[] Timers(IReadOnlyList<FiendwarTimerRow> rows)
    {
        var body = Counted(rows.Count, 10);
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i]; var span = body.AsSpan(1 + i * 10, 10);
            span[0] = row.Area; span[1] = row.Kind;
            BinaryPrimitives.WriteUInt32LittleEndian(span[2..], row.End);
            BinaryPrimitives.WriteUInt32LittleEndian(span[6..], row.Duration);
        }
        return body;
    }
    // 00D308F0读u8与u32；第二列经timeGetTime()+seconds*1000，不能只发区域字节。
    internal static byte[] Merchant(byte area, uint seconds)
    {
        byte[] body = new byte[5]; body[0] = area;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(1), seconds);
        return body;
    }
    // 00D30EB0：u8数量，逐行读取符号u32与值u32。
    internal static byte[] Symbols(IReadOnlyList<(uint Symbol, uint Value)> rows)
    {
        var body = Counted(rows.Count, 8);
        for (int i = 0; i < rows.Count; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(1 + i * 8), rows[i].Symbol);
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(5 + i * 8), rows[i].Value);
        }
        return body;
    }
    static byte[] Counted(int count, int width)
    {
        if (count is < 0 or > 255) throw new ArgumentOutOfRangeException(nameof(count));
        var body = new byte[1 + count * width]; body[0] = checked((byte)count); return body;
    }
}
