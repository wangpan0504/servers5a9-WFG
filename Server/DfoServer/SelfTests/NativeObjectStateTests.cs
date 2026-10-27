using System.Buffers.Binary;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;
using DfoServer.Network;
using DfoServer.Network.Parsers;
using DfoServer.Network.Protocol;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class NativeObjectStateTests
{
    public static void RunCore()
    {
        int before = Count;
        // 0、128、-1位模式；独立大端7位编码，不经生产编码器。
        byte[] vector = [0, 0x81, 0, 0x8F, 0xFF, 0xFF, 0xFF, 0x7F];
        byte[] body = new byte[21 + vector.Length];
        body[0] = 0x21; body[2] = 9; body[4] = 1; body[5] = 7; body[9] = (byte)vector.Length;
        vector.CopyTo(body, 11);
        var row = Frame(body);
        byte[] original = row.Wire.ToArray();
        That(NativeObjectStates.TryRead(row, new(4, 5), out var state) &&
            state is { ObjectKind: 33, Instance: 9, State: 1, Tick: 7, HasRoomPrefix: false } &&
            state.Values.SequenceEqual(new[] { 0, 128, -1 }), "Native5字段与有符号整数位模式按独立向量解码");
        Bytes(row.Wire.Span, original, "只读解码不改中继记录或序号");
        That(NativeObjectStates.TryRead(Frame([4, 5, ..body]), new(4, 5), out var prefixed) && prefixed!.HasRoomPrefix,
            "房间XY前缀仅在唯一布局且匹配当前房间时观测");
        That(!NativeObjectStates.TryRead(Frame([4, 5, ..body]), new(4, 6), out _), "拒绝错误房间前缀");
        byte[] ambiguous = new byte[23]; ambiguous[9] = 2;
        That(!NativeObjectStates.TryRead(Frame(ambiguous), new(0, 0), out _), "两种正文布局同时成立不猜测");
        foreach (byte[] invalid in new byte[][] { [0x80, 0], [0x81], [0x90, 0x80, 0x80, 0x80, 0],
            [0x81, 0x80, 0x80, 0x80, 0x80, 0] })
            That(!NativeObjectStates.TryIntegers(invalid, out _), "拒绝非最短、截断、溢出或超过五组整数");
        That(NativeObjectStates.TryIntegers([0x7F, 0x81, 0, 0x87, 0xFF, 0xFF, 0xFF, 0x7F], out var limits) &&
            limits.SequenceEqual(new[] { 127, 128, int.MaxValue }), "整数边界不混为小端varint或ZigZag");
        foreach (byte[] malformed in new byte[][] { body[..^1], [..body, 0], new byte[20] })
        {
            var candidate = Frame(malformed);
            That(!NativeObjectStates.TryRead(candidate, new(4, 5), out _) &&
                NativeGameplayRecords.Read(candidate.Wire.ToArray()).Single().Wire.Span.SequenceEqual(candidate.Wire.Span),
                "坏Native5仅退出观测，原生记录转发接受合同不变");
        }
        byte[] corrupted = original.ToArray(); corrupted[^1] ^= 1;
        That(!NativeObjectStates.TryRead(NativeGameplayRecords.Read(corrupted).Single(), new(4, 5), out _), "CRC坏包不进入观测");
        byte[] extra = [..body[..^7], 10, 11, ..body[^7..]];
        BinaryPrimitives.WriteUInt16LittleEndian(extra.AsSpan(12 + vector.Length), 2);
        That(NativeObjectStates.TryRead(Frame(extra), new(4, 5), out var opaque) && opaque!.ExtraLength == 2,
            "变长附加区严格跳过，不擅自解释为对象权威");
        Ledger(row, state!);
        Console.WriteLine($"PASS {Count - before} Native5只读解码与有界观测断言");
    }

    static void Ledger(NativeGameplayRecord row, NativeObjectState state)
    {
        var clock = new Clock();
        var ledger = new NativeStateObservation(clock);
        var run = new DungeonRunIdentity(1, 0, 1, Guid.NewGuid(), 1);
        var room = new DungeonMap("normal", new(0, 0), 1, "fixture.map");
        That(ledger.Observe(run, room, row, state) == NativeObservationResult.Added &&
            ledger.Observe(run, room, row, state) == NativeObservationResult.Duplicate, "同来源向多接收者中继只登记一次");
        var changed = row with { Wire = new byte[row.Wire.Length] };
        That(ledger.Observe(run, room, changed, state) == NativeObservationResult.Conflict && ledger.Snapshot().Retained == 1,
            "同序号异文仅记冲突，不覆盖首条或产生游戏写入");
        That(ledger.Observe(run with { Epoch = 2 }, room, row, state) == NativeObservationResult.Added,
            "换房代次不与旧场景混为同一观测");
        That(new NativeStateObservation(clock).Observe(run, room, row, state) == NativeObservationResult.Added,
            "新连接租约不共享旧账本");
        clock.Advance(60);
        That(ledger.Snapshot().Retained == 0, "读取摘要同样惰性清除过期记录，不需要后台计时任务");
        That(ledger.Observe(run, room, row, state) == NativeObservationResult.Added && ledger.Snapshot().Retained == 1,
            "单调60秒淘汰只为诊断，不冒充永久去重权限");
        for (uint index = 1; index <= 300; index++) ledger.Observe(run, room, row with { Sequence = index }, state);
        That(ledger.Snapshot().Retained == NativeStateObservation.Capacity, "持续输入仍严格限定256条摘要");
    }

    // 独立按已核旋转/XOR常数构造，仅复用CRC算法，未调用生产Transform。
    internal static NativeGameplayRecord Frame(byte[] plain)
    {
        byte[] wire = new byte[16 + plain.Length]; wire[0] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(wire.AsSpan(1), 7);
        BinaryPrimitives.WriteUInt16LittleEndian(wire.AsSpan(5), checked((ushort)(7 + plain.Length)));
        wire[9] = 2; wire[10] = 5;
        BinaryPrimitives.WriteUInt32LittleEndian(wire.AsSpan(12), GameFrame.Crc(plain));
        for (int i = 0; i < plain.Length; i++) { byte value = (byte)(plain[i] ^ 0x6E); wire[16 + i] = (byte)((value >> 3) | (value << 5)); }
        return NativeGameplayRecords.Read(wire).Single();
    }

    sealed class Clock : TimeProvider
    {
        long ticks;
        public override long TimestampFrequency => 1;
        public override long GetTimestamp() => ticks;
        public void Advance(int seconds) => ticks += seconds;
    }
}
