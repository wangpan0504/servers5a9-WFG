using System.Buffers.Binary;
using DfoServer.GameWorld.Dungeons;
using DfoServer.Network.Parsers;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class NativePassiveObjectLifecycleTests
{
    public static void RunCore()
    {
        int before = Count;
        var room = new DungeonPoint(4, 5);
        byte[] header = new byte[36];
        header[3] = 0x11; header[4] = 1; header[5] = 7;
        header[7] = 0x21; header[9] = 8;
        header[25] = 4; header[27] = 5; header[29] = 9; header[34] = 1;
        byte[] row = new byte[51];
        BinaryPrimitives.WriteUInt32LittleEndian(row, 18865);
        row[9] = 0x21; row[11] = 23; row[21] = 1;
        BinaryPrimitives.WriteInt32LittleEndian(row.AsSpan(22), -123);
        BinaryPrimitives.WriteInt32LittleEndian(row.AsSpan(26), 456);
        byte[] plain = [.. header, .. row];
        var record = Frame(21, plain);
        byte[] original = record.Wire.ToArray();
        That(NativePassiveObjectSpawns.TryRead(record, room, out var decoded) &&
            decoded is { SourceKind: 0x111, SourceKey: 7, ParentKind: 0x21, ParentKey: 8, RoomX: 4, RoomY: 5, Tick: 9 } &&
            decoded.Rows.Single() is { Template: 18865, ObjectKind: 0x21, ObjectKey: 23, Direction: 1, X: -123, Y: 456, Z: 0 },
            "Native21区分资源模板、来源、父对象和真实实例身份");
        Bytes(record.Wire.Span, original, "对象生成解码保留中继原字节");
        That(NativePassiveObjectSpawns.TryRead(Frame(21, [4, 5, .. plain]), room, out var prefixed) &&
            prefixed!.HasRoomPrefix && !NativePassiveObjectSpawns.TryRead(Frame(21, [4, 6, .. plain]), room, out _),
            "Native21房间前缀必须匹配并具有唯一完整布局");
        byte[] variable = [..row[..16], ..new byte[116], ..row[16..36], 0xAA, 0xBB, ..row[36..39],
            ..new byte[4], ..row[39..]];
        variable[15] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(variable.AsSpan(150), 2);
        variable[156] = 1;
        That(NativePassiveObjectSpawns.TryRead(Frame(21, [..header, ..variable]), room, out var complex) &&
            complex!.Rows.Single().Body.Length == 173, "Native21完整消费116B结构、变长blob与可选4B尾部");
        byte[] modeOne = [.. row[..21], ..new byte[8], ..row[34..]];
        modeOne[20] = 1;
        That(NativePassiveObjectSpawns.TryRead(Frame(21, [..header, ..modeOne]), room, out var attached) &&
            attached!.Rows.Single() is { PositionMode: 1, Direction: null, X: null, Y: null, Z: null },
            "Native21附着位置分支保留不透明字段，不伪造绝对坐标");
        byte[] debugHeader = [..header[..25], ..new byte[40], ..header[25..]];
        debugHeader[24] = 1;
        byte[] debugRow = [..row[..5], ..new byte[24], ..row[5..]];
        debugRow[4] = 1;
        That(NativePassiveObjectSpawns.TryRead(Frame(21, [..debugHeader, ..debugRow]), room, out var debug) &&
            debug!.Rows.Single().ObjectKey == 23, "Native21两处诊断字段不偏移真实对象身份");
        foreach (byte[] malformed in new byte[][] { plain[..^1], [..plain, 0], [..header, ..row[..15]],
            [..header, ..variable[..^1]], [..header, ..modeOne[..^1]] })
            That(!NativePassiveObjectSpawns.TryRead(Frame(21, malformed), room, out _),
                "Native21拒绝截断、额外尾字节及不完整可选段");
        byte[] tooMany = plain.ToArray(); tooMany[34] = 2;
        That(!NativePassiveObjectSpawns.TryRead(Frame(21, tooMany), room, out _), "行数不能超出实际完整数据");
        byte[] destroyed = [0x21, 0, 23, 0, 4, 5];
        That(NativeObjectDestructions.TryRead(Frame(23, destroyed), room, out var death) &&
            death is { ObjectKind: 33, ObjectKey: 23, HasRoomPrefix: false } && death.Room == room,
            "Native23严格读取对象实例身份及所在房间");
        That(NativeObjectDestructions.TryRead(Frame(23, [4, 5, ..destroyed]), room, out var nested) && nested!.HasRoomPrefix,
            "Native23支持匹配房间前缀");
        That(!NativeObjectDestructions.TryRead(Frame(23, destroyed), new(4, 6), out _) &&
            !NativeObjectDestructions.TryRead(Frame(21, destroyed), room, out _) &&
            !NativeObjectDestructions.TryRead(Frame(23, [..destroyed, 0]), room, out _),
            "Native23拒绝旧房间、其它opcode和额外尾部");
        byte[] corrupt = Frame(23, destroyed).Wire.ToArray(); corrupt[^1] ^= 1;
        var invalid = NativeGameplayRecords.Read(corrupt).Single();
        That(!NativeObjectDestructions.TryRead(invalid, room, out _) &&
            !NativePassiveObjectSpawns.TryRead(invalid, room, out _), "对象生命周期解码拒绝CRC错误");
        byte[] batteryLast = [110, 0, 0, 0, 100, 0, 0, 0, 2];
        byte[] terminalLast = [110, 0, 0, 0, 184, 11, 0, 0, 2];
        That(RaidRequests.AntonBatteryLast(batteryLast) && !RaidRequests.AntonEnergyClear(batteryLast) &&
            RaidRequests.AntonEnergyClear(terminalLast) && !RaidRequests.AntonBatteryLast(terminalLast),
            "小电池100与终末3000是独立且不可互换的九字节Last合同");
        foreach (byte[] malformed in new byte[][] { [], batteryLast[..^1], [..batteryLast, 0],
            [111, 0, 0, 0, 100, 0, 0, 0, 2], [110, 0, 0, 0, 101, 0, 0, 0, 2],
            [110, 0, 0, 0, 100, 0, 0, 0, 1], [110, 0, 0, 0, 100, 0, 0, 0, 3] })
            That(!RaidRequests.AntonBatteryLast(malformed), "小电池Last拒绝错误长度、符号、减量和运算");
        var observation = new byte[36 + plain.Length];
        uint[] fields = [0x314F4E41, 7, 218, 19200, 1234, 0x0504, 11, 21, (uint)plain.Length];
        for (int index = 0; index < fields.Length; index++)
            BinaryPrimitives.WriteUInt32LittleEndian(observation.AsSpan(index * 4), fields[index]);
        plain.CopyTo(observation, 36);
        var observed = AntonNativeRequests.Parse(observation);
        That(observed is { Serial: 7, Dungeon: 218, Map: 19200, Seed: 1234, Position: 0x0504, Sequence: 11, NativeOpcode: 21 } &&
            NativePassiveObjectSpawns.TryReadPlain(observed.Plain, room, out var bridged) &&
            bridged!.Rows.Single() is { Template: 18865, ObjectKind: 0x21, ObjectKey: 23, X: -123, Y: 456 } &&
            bridged.Rows.Single().Body.AsSpan().SequenceEqual(decoded!.Rows.Single().Body.AsSpan()),
            "ANO1保留精确房间与原生生成正文，复用结构合同");
        That(NativeObjectDestructions.TryReadPlain(destroyed, room, out var bridgedDeath) && bridgedDeath == death,
            "只观测桥与Native23共用销毁结构及所在房间检查");
        foreach (byte[] malformed in new byte[][] { [], observation[..35], observation[..^1], [..observation, 0] })
            Reject<InvalidDataException>(() => AntonNativeRequests.Parse(malformed), "ANO1拒绝截断和额外尾字节");
        foreach (var change in new (int Offset, uint Value)[] { (0, 0), (4, 0), (8, 219), (12, 0),
            (20, 0x10000), (24, 0), (28, 39), (32, 0xFFFFFFFF) })
        {
            byte[] malformed = observation.ToArray();
            BinaryPrimitives.WriteUInt32LittleEndian(malformed.AsSpan(change.Offset), change.Value);
            Reject<InvalidDataException>(() => AntonNativeRequests.Parse(malformed), "ANO1拒绝未知事件、空身份和越界声明");
        }
        Console.WriteLine($"PASS {Count - before} 原生被动物件生成与销毁结构断言");
    }

    static NativeGameplayRecord Frame(byte opcode, byte[] plain)
    {
        var record = NativeObjectStateTests.Frame(plain);
        byte[] wire = record.Wire.ToArray(); wire[10] = opcode;
        return NativeGameplayRecords.Read(wire).Single();
    }
}
