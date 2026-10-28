using static DfoServer.Testing.TestCheck;
using System.Buffers.Binary;
using System.Collections;
using DfoServer.Network.Parsers;
using DfoServer.Network.Builders;

namespace DfoServer;

internal static class DungeonRequestTests
{
    public static void Run()
    {
        int checks = Count;

        foreach (byte operation in new byte[] { 1,2,3 })
            That(DungeonRequests.TryParseRidableOperation([operation,0x78,0x56,0x34,0xF2,0xAB,0xCD],out var riding) &&
                riding == new DungeonRidableOperationRequest(operation,0xF2345678,0xAB,0xCD),
                "316保留7B操作、完整无符号资源编号及两个地图上下文值");
        That(DungeonRequests.TryParseRidableOperation([2,0,0,0,0,255,255],out var noMap) &&
            noMap is { ResourceId:0,MapX:255,MapY:255 },"316解析保留空场景哨兵，资格由领域校验");
        foreach (int length in new[] { 0,1,6,8,11,100 })
            That(!DungeonRequests.TryParseRidableOperation(new byte[length],out _),"316拒绝未证实宽度："+length);
        foreach (byte operation in new byte[] { 0,4,255 })
            That(!DungeonRequests.TryParseRidableOperation([operation,1,0,0,0,0,0],out _),"316拒绝未知操作："+operation);

        That(DungeonRequests.TryParseCharacterDeath([0x34, 0xF2, 0xFF, 0xFF], out var dead) &&
            dead == new DungeonCharacterDeathRequest(0xF234, ushort.MaxValue), "角色死亡保留两个完整u16坐标");
        That(DungeonRequests.TryParseUseCoin([0xCD, 0xAB], out ushort reviveKey) && reviveKey == 0xABCD,
            "复活请求保留完整u16角色key");
        That(DungeonRequests.TryParseCharacterDeath([0x74, 0, 4, 1, 0, 0], out dead) &&
            dead == new DungeonCharacterDeathRequest(116, 260, 0), "现场普通角色6B死亡正文完整解析");
        That(DungeonRequests.TryParseCharacterDeath([0, 0, 0, 0, 0xFE, 0xAB], out dead) && dead.Context == 0xABFE,
            "死亡扩展u16保留原值，不推断额外字段枚举");
        foreach (int width in new[] { 0, 1, 2, 3, 5, 8 })
            if (width != 4) That(!DungeonRequests.TryParseCharacterDeath(new byte[width], out _), "拒绝角色死亡宽度 " + width);
        foreach (int width in new[] { 0, 1, 3, 4, 6 })
            That(!DungeonRequests.TryParseUseCoin(new byte[width], out _), "拒绝复活请求宽度 " + width);
        That(DungeonPackets.CoinUsed(0xABCD).SequenceEqual(new byte[] { 1, 0xCD, 0xAB, 0 }),
            "本版复活ACK完整编码成功、u16角色key及标志");
        That(DungeonPackets.CharacterLife(0xABCD, false).SequenceEqual(new byte[] { 0xCD, 0xAB, 0, 0 }) &&
            DungeonPackets.CharacterLife(0xABCD, true).SequenceEqual(new byte[] { 0xCD, 0xAB, 1, 0 }), "死亡及复活32状态字节独立核对");

        That(DungeonRequests.ParseEntry([0, 0, 0, 0]).DungeonIndex == 0, "zero entry remains an entrance request value");
        using (var rejection = new BinaryReader(new MemoryStream(DungeonPackets.SelectionRejected())))
            That(rejection.ReadByte() == 0 && rejection.ReadUInt16() == 9 && rejection.ReadByte() == 0 &&
                rejection.BaseStream.Position == rejection.BaseStream.Length,
                "静默ACK16独立消费者完整读取失败、u16错误与单人成员槽，无额外字段");
        foreach (byte code in new byte[] { 10, 246 })
        {
            using var rejection = new BinaryReader(new MemoryStream(DungeonPackets.SelectionRejected(code)));
            That(rejection.ReadByte() == 0 && rejection.ReadUInt16() == code && rejection.ReadByte() == 0 &&
                rejection.BaseStream.Position == rejection.BaseStream.Length,
                "祭坛失败ACK16完整编码u16提示码和本人槽");
        }
        That(DungeonRequests.ParseEntry([0x78, 0x56, 0x34, 0xF2]).DungeonIndex == 0xF2345678u, "entry preserves the unsigned 32-bit index");
        That(DungeonRequests.ParseEntry([0xFF, 0xFF, 0xFF, 0xFF]).DungeonIndex == uint.MaxValue, "entry parsing does not impose an admission range");
        foreach (int length in new[] { 0, 1, 2, 3, 5, 25 })
        {
            bool rejected = false;
            try
            {
                DungeonRequests.ParseEntry(new byte[length]);
            }
            catch (InvalidDataException)
            {
                rejected = true;
            }

            That(rejected, "entry rejects width " + length);
        }

        // 独立字节样本包含非零高位及未知字段，避免只用全零包掩盖偏移或截断错误。
        byte[] body = Convert.FromHexString("785634F2 A5 EFBE 7D 91 57F3 490C0000 FE 214365F7 80 98BADCFE".Replace(" ", "", StringComparison.Ordinal));
        var expected = new DungeonSelectionRequest(0xF2345678, 0xA5, 0xBEEF, 0x7D, 0x91, 0xF357, 3145, 0xFE, 0xF7654321, 0x80, 0xFEDCBA98);
        That(DungeonRequests.TryParseSelection(body, out var selection) && selection == expected, "selection preserves every field and unsigned width");
        Array.Fill(body, (byte)0);
        That(selection == expected, "selection does not retain mutable input bytes");
        That(DungeonRequests.TryParseSelection(body, out var zero) && zero == default, "selection leaves zero values for business validation");
        foreach (int length in Enumerable.Range(0, 25).Concat(new[] { 26, 27, 256 }))
        {
            selection = expected;
            That(!DungeonRequests.TryParseSelection(new byte[length], out selection) && selection == default,
                "unconfirmed selection width stays unhandled and clears output: " + length);
        }

        // 100 字节路径的尾部仍是不透明数据；用独立模式及完整 u64 样本防止缩短统计尾部。
        byte[] moveBody = Enumerable.Range(0, 100).Select(index => (byte)(index * 37 + 11)).ToArray();
        moveBody[0] = 0xFE;
        moveBody[1] = 0xFD;
        Convert.FromHexString("785634F2214365F7A5").CopyTo(moveBody, 2);
        Convert.FromHexString("EFCDAB89674523F1").CopyTo(moveBody, 85);
        byte[] expectedTail = moveBody[11..];
        That(DungeonRequests.TryParseMove(moveBody, out var move) && move is not null
            && move.X == 0xFE && move.Y == 0xFD && move.Unknown2 == 0xF2345678 && move.Unknown6 == 0xF7654321 && move.Unknown10 == 0xA5,
            "move parsing preserves target bytes and unsigned 32-bit fields without admission guesses");
        That(move is not null && move.RemainingFields.Count == 89 && move.RemainingFields.SequenceEqual(expectedTail),
            "move retains every remaining byte through offset 99");
        That(move is not null && BinaryPrimitives.ReadUInt64LittleEndian(move.RemainingFields.Skip(74).Take(8).ToArray()) == 0xF123456789ABCDEFUL,
            "move retains the complete unsigned 64-bit value at body offset 85");
        That(move is not null && move.RemainingFields is IList tail && tail.IsReadOnly,
            "move tail cannot be mutated through the returned collection");
        Array.Fill(moveBody, (byte)0);
        That(move is not null && move.RemainingFields.SequenceEqual(expectedTail) && move.Unknown2 == 0xF2345678,
            "changing the move input cannot alter the parsed request");
        foreach (int length in new[] { 0, 64, 92, 99, 101 })
        {
            That(!DungeonRequests.TryParseMove(new byte[length], out var rejected) && rejected is null,
                "unconfirmed move width stays unhandled without a partial request: " + length);
        }

        Console.WriteLine($"PASS {Count - checks} dungeon request assertions");
    }
}
