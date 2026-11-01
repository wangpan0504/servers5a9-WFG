using System.Buffers.Binary;
using DfoServer.Network.Parsers;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class NativeMonsterSpawnsTests
{
    internal static void RunCore()
    {
        int before = Count;
        byte[] plain = Body(true, true);
        var record = Frame(plain);
        byte[] original = record.Wire.ToArray();
        That(NativeMonsterSpawns.TryRead(record, new(4, 5), out var batch) &&
            batch is { ReportFlag: 1, Mode: 1, ModeValue: 3, SourceKind: 0x211, SourceKey: 41,
                SourceAltA: ushort.MaxValue, SourceAltB: ushort.MaxValue, FlagA: 2, FlagB: 3, FlagC: 4,
                OpaqueInheritedValue: 0xDEADBEEF, Team: 100, Tick: 0x87654321, HasRoomPrefix: false } &&
            batch.Rows.Length == 2, "原生22完整读取可选模式和来源头，不将FFFF转换为有效父对象");
        var first = batch!.Rows[0];
        That(first is { MonsterId: 107013169, Level: 95, ObjectKind: 0x211, ObjectKey: 501,
            Direction: 1, X: -120, Y: 450, Z: 17, OpaqueFlag: 7, OpaqueStructureFlag: 1,
            OpaqueValue: 0x11223344, OpaqueWord: 0x1234, TrailingA: 8, TrailingB: 9 } &&
            first.OpaqueBlob.SequenceEqual(new byte[] { 0, 255, 11 }) &&
            first.OpaqueStructure.SequenceEqual(Enumerable.Range(1, 73).Select(value => (byte)value)) &&
            first.Statuses.SequenceEqual(new byte[] { 0, 128, 255 }),
            "生成实例键和有符号坐标不与模板或行尾字段混用，所有可变字节保留");
        That(batch.Rows[1] is { MonsterId: 107013170, ObjectKey: 502, OpaqueStructureFlag: 0 } second &&
            second.OpaqueBlob.IsEmpty && second.OpaqueStructure.IsEmpty && second.Statuses.IsEmpty,
            "多行在前一行状态列表之后对齐，允许第二行的空可变区");
        Bytes(record.Wire.Span, original, "解析不改Native中继原字节");
        byte[] mutable = record.Wire.ToArray();
        That(NativeMonsterSpawns.TryRead(record with { Wire = mutable }, new(4, 5), out var frozen),
            "解析调用者拥有的缓冲");
        Array.Fill(mutable, (byte)0);
        That(frozen!.Rows[0].OpaqueBlob.SequenceEqual(first.OpaqueBlob) &&
            frozen.Rows[0].OpaqueStructure.SequenceEqual(first.OpaqueStructure) && frozen.Rows[0].Statuses.SequenceEqual(first.Statuses),
            "解析结果的可变区与接收缓冲分离");

        byte[] minimal = Body(false, false);
        That(minimal.Length == 59 && NativeMonsterSpawns.TryRead(Frame(minimal), new(4, 5), out var direct) &&
            direct is { Mode: 0, ModeValue: null, HasRoomPrefix: false } && direct.Rows.Length == 1,
            "最小单行正文为59字节，不凭固定尾字段误读实例键");
        foreach (byte kind in new byte[] { 1, 2, 8 })
        {
            byte[] wire = Frame(minimal).Wire.ToArray(); wire[0] = kind;
            That(NativeMonsterSpawns.TryRead(NativeGameplayRecords.Read(wire).Single(), new(4, 5), out _),
                "所有已支持原生数据记录种类均可承载生成结构");
        }
        That(NativeMonsterSpawns.TryRead(Frame([4, 5, ..plain]), new(4, 5), out var prefixed) &&
            prefixed!.HasRoomPrefix && prefixed.Rows.Length == 2, "唯一且匹配当前房间的XY前缀可解析");
        That(!NativeMonsterSpawns.TryRead(Frame([4, 5, ..plain]), new(4, 6), out _), "错误房间前缀不能登记当前房间对象");
        // 两种布局均有一行：无前缀解释多2字节blob，有前缀解释的blob为空。
        byte[] ambiguous = new byte[61]; ambiguous[23] = 1; ambiguous[25] = 1; ambiguous[47] = 2;
        That(!NativeMonsterSpawns.TryRead(Frame(ambiguous), new(0, 0), out _), "完整布局存在两种解释时拒绝猜测");

        foreach (byte[] body in new[] { minimal, plain })
            for (int length = 0; length < body.Length; length++)
                That(!NativeMonsterSpawns.TryRead(Frame(body[..length]), new(4, 5), out _),
                    "任意字段、附加结构和状态列表截断均拒绝，不能部分返回前面行");
        foreach (byte[] malformed in new byte[][] { [..minimal, 0], [..plain, 0] })
            That(!NativeMonsterSpawns.TryRead(Frame(malformed), new(4, 5), out _), "完整行之后的多余字节拒绝");
        byte[] wrongCount = minimal.ToArray(); wrongCount[23] = 2;
        That(!NativeMonsterSpawns.TryRead(Frame(wrongCount), new(4, 5), out _), "头声明超过实际行数拒绝");
        byte[] wrongBlob = minimal.ToArray(); wrongBlob[47] = 255; wrongBlob[48] = 255;
        That(!NativeMonsterSpawns.TryRead(Frame(wrongBlob), new(4, 5), out _), "blob长度越界拒绝");
        byte[] missingStructure = minimal.ToArray(); missingStructure[49] = 1;
        That(!NativeMonsterSpawns.TryRead(Frame(missingStructure), new(4, 5), out _), "非零结构标志必须带齐73字节");
        byte[] wrongStatuses = minimal.ToArray(); wrongStatuses[^1] = 255;
        That(!NativeMonsterSpawns.TryRead(Frame(wrongStatuses), new(4, 5), out _), "状态数量必须有对应字节");
        byte[] unknownSource = minimal.ToArray(); Array.Fill(unknownSource, (byte)255, 2, 4);
        That(NativeMonsterSpawns.TryRead(Frame(unknownSource), new(4, 5), out var unknown) &&
            unknown is { SourceKind: ushort.MaxValue, SourceKey: ushort.MaxValue },
            "来源FFFF保留为数据，不能由Parser伪造有效归属");
        byte[] corrupt = original.ToArray(); corrupt[^1] ^= 1;
        That(!NativeMonsterSpawns.TryRead(NativeGameplayRecords.Read(corrupt).Single(), new(4, 5), out _), "CRC错误拒绝");
        byte[] wrongOpcode = original.ToArray(); wrongOpcode[10] = 21;
        That(!NativeMonsterSpawns.TryRead(NativeGameplayRecords.Read(wrongOpcode).Single(), new(4, 5), out _),
            "原生21被动对象不能当成22怪物生成");
        byte[] wrongType = original.ToArray(); wrongType[9] = 3;
        That(!NativeMonsterSpawns.TryRead(NativeGameplayRecords.Read(wrongType).Single(), new(4, 5), out _), "原生数据头种类越界拒绝");
        byte[] wrongLength = original.ToArray(); wrongLength[5]--;
        That(!NativeMonsterSpawns.TryRead(record with { Wire = wrongLength }, new(4, 5), out _) &&
            !NativeMonsterSpawns.TryRead(record with { Kind = 0 }, new(4, 5), out _) &&
            !NativeMonsterSpawns.TryRead(record with { Wire = new byte[15] }, new(4, 5), out _) &&
            !NativeMonsterSpawns.TryRead(record with { Wire = new byte[NativeGameplayRecords.MaximumLength + 1] }, new(4, 5), out _),
            "拒绝错误记录长度、确认种类、短头和超出客户端上限的缓冲");
        Console.WriteLine($"PASS {Count - before} Native22生成正文严格解析断言");
    }

    // 使用已有独立旋转/XOR测试帧，仅替换未参与正文CRC的opcode。
    static NativeGameplayRecord Frame(byte[] plain)
    {
        byte[] wire = NativeObjectStateTests.Frame(plain).Wire.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(wire.AsSpan(10), 22);
        return NativeGameplayRecords.Read(wire).Single();
    }

    static byte[] Body(bool mode, bool variable)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)1); writer.Write((byte)(mode ? 1 : 0));
        if (mode) writer.Write((ushort)3);
        writer.Write((ushort)0x211); writer.Write((ushort)41);
        writer.Write(ushort.MaxValue); writer.Write(ushort.MaxValue);
        writer.Write((byte)2); writer.Write((byte)3); writer.Write((byte)4);
        writer.Write(0xDEADBEEFu); writer.Write((ushort)100); writer.Write(0x87654321u);
        writer.Write((byte)(variable ? 2 : 1));
        WriteRow(107013169, 501, variable ? [0, 255, 11] : [], variable, variable ? [0, 128, 255] : []);
        if (variable) WriteRow(107013170, 502, [], false, []);
        return stream.ToArray();

        void WriteRow(uint template, ushort key, byte[] blob, bool structure, byte[] statuses)
        {
            writer.Write(template); writer.Write((byte)95); writer.Write((ushort)0x211); writer.Write(key);
            writer.Write((byte)1); writer.Write(-120); writer.Write(450); writer.Write(17); writer.Write((byte)7);
            writer.Write(checked((ushort)blob.Length)); writer.Write(blob); writer.Write((byte)(structure ? 1 : 0));
            if (structure) writer.Write(Enumerable.Range(1, 73).Select(value => (byte)value).ToArray());
            writer.Write(0x11223344u); writer.Write((ushort)0x1234); writer.Write((byte)8); writer.Write((byte)9);
            writer.Write(checked((byte)statuses.Length)); writer.Write(statuses);
        }
    }
}
