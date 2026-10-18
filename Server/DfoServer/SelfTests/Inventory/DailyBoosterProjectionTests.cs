using System.Buffers.Binary;
using DfoServer.Network.Builders.Inventory;

namespace DfoServer.Testing.Inventory;

internal static partial class DailyBoosterProjectionTests
{
    public static void RunCore()
    {
        int checks = TestCheck.Count;
        foreach (int count in new[] { 0, 1, 255, 256, 511 })
        {
            var expected = Enumerable.Range(1, count).Select(id => (ItemId: 490000000 + id, Used: (byte)(id % 256))).ToArray();
            var decoded = new Dictionary<int, byte>();
            foreach (var chunk in count == 0 ? new[] { Array.Empty<(int ItemId, byte Used)>() } : expected.Chunk(255))
                foreach (var row in Decode(DailyBoosterPackets.Used(chunk))) decoded.Add(row.Key, row.Value);
            TestCheck.That(decoded.Count == count && expected.All(row => decoded[row.ItemId] == row.Used),
                "541完整分片保留模板i32与已用u8，不写剩余次数");
        }
        try
        {
            DailyBoosterPackets.Used(Enumerable.Range(1, 256).Select(id => (id, (byte)1)).ToArray());
            throw new InvalidOperationException("541没有拒绝超过255条的单包");
        }
        catch (ArgumentOutOfRangeException) { TestCheck.That(true, "541条数上限独立于次数上限"); }
        var consumer = new Dictionary<int, byte> { [5] = 1 };
        foreach (var row in Decode([0, 0, 0])) consumer[row.Key] = row.Value;
        TestCheck.That(consumer[5] == 1, "独立消费者不假定A0清空map");
        foreach (var row in Decode(DailyBoosterPackets.Used([(5, 0)]))) consumer[row.Key] = row.Value;
        TestCheck.That(consumer[5] == 0, "显式zero确实覆盖旧值");
        Console.WriteLine($"PASS {TestCheck.Count - checks} daily booster protocol assertions");
    }

    // 与生产 Builder 无共享编码逻辑，严格读取三组和精确边界。
    internal static Dictionary<int, byte> Decode(byte[] body)
    {
        TestCheck.That(body.Length >= 3 && body.Length == 3 + 5 * body[0], "541长度恰为3+5A");
        int count = body[0];
        TestCheck.That(body[1 + count * 5] == 0 && body[2 + count * 5] == 0, "541第二和第三组独立为0");
        var rows = new Dictionary<int, byte>();
        int previous = 0;
        for (int i = 0; i < count; i++)
        {
            int id = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(1 + i * 5));
            TestCheck.That(id > previous && rows.TryAdd(id, body[5 + i * 5]), "541正模板ID有序且没有重复");
            previous = id;
        }
        return rows;
    }
}
