using DfoServer.Game.Dungeons;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class RaidChannelLandingTcpTests
{
    // 按独立字节合同验证真实wire，不调用产品Builder生成期望正文。
    sealed class BeastDisplayWire(TcpPeer peer)
    {
        uint serial;
        Guid? connection, instance;
        internal byte[] Selection(int day, (int First, int Second) pair, byte first = 2, byte second = 2)
        {
            byte[] body = Read(1);
            That(body[6] == 255 && body[7] == 0 && body[41] == 2 && U32(body, 44) == day &&
                U16(body, 48) == pair.First && body[50] == first && U16(body, 52) == pair.Second && body[54] == second,
                "选图显示包含当日两张原图的各自真实剩余次数");
            That(body.AsSpan(16,16).IndexOfAnyExcept((byte)0) < 0 && body.AsSpan(56,72).IndexOfAnyExcept((byte)0) < 0,
                "选图展示没有伪造阶段奖励");
            return body;
        }
        internal byte[] Progress(int stage, int completed, BeastTerminal terminal = BeastTerminal.Active)
        {
            byte[] body = Read(2);
            That(body[5] == (byte)terminal && body[6] == (stage < 0 ? 255 : stage) && body[7] == completed &&
                body[41] == 0 && U32(body,44) == 0, "真实阶段、完成数与终局显示不混用选图或第三版数据");
            Guid actual = new(body.AsSpan(16,16));
            That(actual != Guid.Empty && (instance is null || instance == actual), "换房保持同一个显示实例");
            instance = actual;
            int remaining = unchecked((int)U32(body,32));
            That(stage < 2 ? remaining == -1 : terminal == BeastTerminal.TimedOut ? remaining == 0 : remaining is > 0 and <= 180000,
                "前两阶段不限时，第三阶段沿共同deadline，自动超时为零");
            uint sum = 0;
            int[] rewards = [10303240,10303242,10303243,10303244,10303245,10303246];
            for (int i = 0; i < 6; i++)
            {
                int at = 56 + i * 12;
                That(i < completed ? U32(body,at) == rewards[i] && U32(body,at+4) > 0 && U32(body,at+8) > 0 :
                    body.AsSpan(at,12).IndexOfAnyExcept((byte)0) < 0, "展示只列已确认六罐，不提前发放或补造未完成阶段");
                sum += U32(body,at+8);
            }
            That(sum == U32(body,36) && body[40] == BeastRunProgress.Rank(checked((int)sum)), "结算显示得分等于阶段账本且RANK符合阈值");
            return body;
        }
        internal void Clear()
        {
            byte[] body = Read(0);
            That(body[6] == 255 && body[7] == 0 && body[40] == 6 && body.AsSpan(56,72).IndexOfAnyExcept((byte)0) < 0,
                "退场明确清除旧实例显示，不制造资产");
        }
        byte[] Read(byte kind)
        {
            byte[] body = peer.Expect(0, 0x7FF1).Body;
            That(body.Length == 144 && U32(body) == 0x31565342 && body[4] == kind && U16(body,8) == peer.Id &&
                U16(body,10) == 0 && U16(body,42) == 0 && body[51] == 0 && body[55] == 0,
                "首版显示固定144B与当前角色，保留字段为零");
            Guid actual = new(body.AsSpan(128,16));
            That(actual != Guid.Empty && (connection is null || connection == actual) && U32(body,12) > serial,
                "同一连接显示标识稳定，序号严格递增");
            connection = actual; serial = U32(body,12);return body;
        }
    }
}
