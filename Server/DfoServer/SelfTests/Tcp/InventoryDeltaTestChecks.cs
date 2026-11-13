using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class InventoryDeltaTestChecks
{
    // 仅用于已有实例穿脱：严格消费一次清槽，以及需要时的一次同槽完整回填。
    internal static byte[] Read(TcpPeer peer, byte list, short slot, int itemId)
    {
        int size = list == 1 ? 165 : 123;
        var clear = peer.Expect(0, 1681).Body;
        Check(clear, uint.MaxValue);
        if (itemId == 0) return clear;
        var fill = peer.Expect(0, 1681).Body;
        Check(fill, checked((uint)itemId));
        return fill;

        void Check(byte[] body, uint expectedId) => That(body.Length == 3 + size && body[0] == list &&
            U16(body, 1) == 1 && U16(body, 3) == slot && U32(body, 5) == expectedId,
            $"已有实例差量严格匹配容器{list}、槽{slot}、模板{expectedId}和完整条目长度");
    }
}
