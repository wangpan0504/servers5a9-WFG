using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class InventoryTcpTests
{
    internal static void VerifyItemSpaceSynchronization(TcpTestFixture fixture, TcpPeer peer)
    {
        string before = Snapshot(fixture);
        for (int repeat = 0; repeat < 2; repeat++)
        {
            peer.Send(870, [255]);
            var rows = AntonTcpProbe.Barrier(peer, "inventory space synchronization");
            That(rows.Count(row => row is { Kind: 0, Opcode: 105 }) == 1 &&
                rows.Where(row => row is { Kind: 0, Opcode: 13 }).Select(row => row.Body[0]).SequenceEqual(new byte[] { 0, 1, 7, 3 }),
                "城镇和漩涡1/4/8人图内重复870均收到完整四容器及宠物详情");
            var ack = rows.Single(row => row is { Kind: 1, Opcode: 870 });
            Bytes(ack.Body, [1], "原生870成功无额外正文");
            That(rows.FindIndex(row => row == ack) > rows.FindLastIndex(row => row is { Kind: 0, Opcode: 13 or 1449 or 105 or 251 }),
                "完整同步后才解除原生等待，不把ACK当同步实现");
            That(before == Snapshot(fixture), "重复同步不写库存、revision或审计，不影响图内资格");
        }
        foreach (byte[] malformed in new byte[][] { [], [255, 0] })
        {
            peer.Send(870, malformed);
            var rows = AntonTcpProbe.Barrier(peer, "malformed inventory sync");
            That(!rows.Any(row => row.Opcode is 870 or 13 or 105), "错误宽度不伪造成功且连接仍可用");
        }
    }
}
