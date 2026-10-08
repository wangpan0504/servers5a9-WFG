using System.Buffers.Binary;
using System.Collections;
using DfoServer.Network.Parsers;
using DfoServer.Network.Builders;

namespace DfoServer;

internal static class DungeonRequestTests
{
    public static void Run()
    {
        int checks = 0;
        void Check(bool condition, string name)
        {
            checks++;
            if (!condition)
                throw new Exception("FAIL: " + name);
        }

        Check(DungeonRequests.ParseEntry([0, 0, 0, 0]).DungeonIndex == 0, "zero entry remains an entrance request value");
        using (var rejection = new BinaryReader(new MemoryStream(DungeonPackets.SelectionRejected())))
            Check(rejection.ReadByte() == 0 && rejection.ReadUInt16() == 9 && rejection.ReadByte() == 0 &&
                rejection.BaseStream.Position == rejection.BaseStream.Length,
                "静默ACK16独立消费者完整读取失败、u16错误与单人成员槽，无额外字段");
        Check(DungeonRequests.ParseEntry([0x78, 0x56, 0x34, 0xF2]).DungeonIndex == 0xF2345678u, "entry preserves the unsigned 32-bit index");
        Check(DungeonRequests.ParseEntry([0xFF, 0xFF, 0xFF, 0xFF]).DungeonIndex == uint.MaxValue, "entry parsing does not impose an admission range");
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

            Check(rejected, "entry rejects width " + length);
        }

        // 独立字节样本包含非零高位及未知字段，避免只用全零包掩盖偏移或截断错误。
        byte[] body = Convert.FromHexString("785634F2 A5 EFBE 7D 91 57F3 490C0000 FE 214365F7 80 98BADCFE".Replace(" ", "", StringComparison.Ordinal));
        var expected = new DungeonSelectionRequest(0xF2345678, 0xA5, 0xBEEF, 0x7D, 0x91, 0xF357, 3145, 0xFE, 0xF7654321, 0x80, 0xFEDCBA98);
        Check(DungeonRequests.TryParseSelection(body, out var selection) && selection == expected, "selection preserves every field and unsigned width");
        Array.Fill(body, (byte)0);
        Check(selection == expected, "selection does not retain mutable input bytes");
        Check(DungeonRequests.TryParseSelection(body, out var zero) && zero == default, "selection leaves zero values for business validation");
        foreach (int length in Enumerable.Range(0, 25).Concat(new[] { 26, 27, 256 }))
        {
            selection = expected;
            Check(!DungeonRequests.TryParseSelection(new byte[length], out selection) && selection == default,
                "unconfirmed selection width stays unhandled and clears output: " + length);
        }

        // 100 字节路径的尾部仍是不透明数据；用独立模式及完整 u64 样本防止缩短统计尾部。
        byte[] moveBody = Enumerable.Range(0, 100).Select(index => (byte)(index * 37 + 11)).ToArray();
        moveBody[0] = 0xFE;
        moveBody[1] = 0xFD;
        Convert.FromHexString("785634F2214365F7A5").CopyTo(moveBody, 2);
        Convert.FromHexString("EFCDAB89674523F1").CopyTo(moveBody, 85);
        byte[] expectedTail = moveBody[11..];
        Check(DungeonRequests.TryParseMove(moveBody, out var move) && move is not null
            && move.X == 0xFE && move.Y == 0xFD && move.Unknown2 == 0xF2345678 && move.Unknown6 == 0xF7654321 && move.Unknown10 == 0xA5,
            "move parsing preserves target bytes and unsigned 32-bit fields without admission guesses");
        Check(move is not null && move.RemainingFields.Count == 89 && move.RemainingFields.SequenceEqual(expectedTail),
            "move retains every remaining byte through offset 99");
        Check(move is not null && BinaryPrimitives.ReadUInt64LittleEndian(move.RemainingFields.Skip(74).Take(8).ToArray()) == 0xF123456789ABCDEFUL,
            "move retains the complete unsigned 64-bit value at body offset 85");
        Check(move is not null && move.RemainingFields is IList tail && tail.IsReadOnly,
            "move tail cannot be mutated through the returned collection");
        Array.Fill(moveBody, (byte)0);
        Check(move is not null && move.RemainingFields.SequenceEqual(expectedTail) && move.Unknown2 == 0xF2345678,
            "changing the move input cannot alter the parsed request");
        foreach (int length in new[] { 0, 64, 92, 99, 101 })
        {
            Check(!DungeonRequests.TryParseMove(new byte[length], out var rejected) && rejected is null,
                "unconfirmed move width stays unhandled without a partial request: " + length);
        }

        Console.WriteLine($"PASS {checks} dungeon request assertions");
    }
}
