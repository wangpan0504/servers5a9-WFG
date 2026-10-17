using static DfoServer.Testing.TestCheck;
using DfoServer.Network.Builders;

namespace DfoServer;

// 魂·黄龙/青龙大会（#259）结算后卡在“胜利了”的回归：大会副本必须发 NOTI388 并应答 CMD457/458。实机未验证。
internal static class TournamentSettlementTests
{
    public static void Run()
    {
        var reward = DungeonSettlementPackets.TournamentReward(4, 1289457);
        // 1 + 4 + 1 + 2 行 × (1 + 2 卡 × 10B)
        That(reward.Length == 6 + 2 * (1 + 2 * 10) && reward[0] == 4 &&
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(reward.AsSpan(1)) == 1289457 && reward[5] == 0 &&
            reward[6] == 2 && System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(reward.AsSpan(7)) == -1 &&
            reward[6 + 21] == 2,
            "大会结算 NOTI388 布局：轮数/经验/两行各两张空卡");
        var state = DungeonSettlementPackets.TournamentSelectState(0);
        That(state.SequenceEqual(new byte[] { 1, 1, 255, 255, 255, 1, 255, 255, 255 }), "CMD457 回复为 9 字节，本人位=1其余 0xFF");
        byte[] none = [255, 255];
        That(DungeonSettlementPackets.TournamentSelection(0, none).SequenceEqual(new byte[] { 1, 2, 255, 255, 2, 255, 255 }),
            "CMD458 回复未选时为 7 字节全 0xFF");
        byte[] picked = [1, 0];
        That(DungeonSettlementPackets.TournamentSelection(0, picked).SequenceEqual(new byte[] { 1, 2, 255, 0, 2, 0, 255 }),
            "CMD458 回复在已选卡位写入本人位");
    }
}