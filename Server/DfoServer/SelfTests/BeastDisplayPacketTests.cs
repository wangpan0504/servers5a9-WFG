using System.Buffers.Binary;
using DfoServer.Game.Dungeons;
using DfoServer.Network.Builders;
using DfoServer.Testing.Inventory;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class BeastDisplayPacketTests
{
    internal static void RunCore()
    {
        var binding = new Guid("00112233-4455-6677-8899-aabbccddeeff");
        var selected = BeastDisplayPackets.Selection(7, binding, 23, 20755, [(310, 0), (314, 2)]);
        That(selected.Length == 144 && BinaryPrimitives.ReadUInt32LittleEndian(selected) == 0x31565342 &&
            selected[4] == 1 && selected[6] == 255 && selected[8] == 23 && selected[12] == 7 &&
            selected[41] == 2 && BinaryPrimitives.ReadInt32LittleEndian(selected.AsSpan(44)) == 20755 &&
            BinaryPrimitives.ReadUInt16LittleEndian(selected.AsSpan(48)) == 310 && selected[50] == 0 &&
            BinaryPrimitives.ReadUInt16LittleEndian(selected.AsSpan(52)) == 314 && selected[54] == 2 &&
            new Guid(selected.AsSpan(128)) == binding, "魔兽选图显示固定144B，原图和独立剩余次数不冒充原生通知");
        That(selected.AsSpan(16,16).IndexOfAnyExcept((byte)0) < 0 && selected.AsSpan(56,72).IndexOfAnyExcept((byte)0) < 0,
            "选图不伪造运行实例或已获得的阶段奖励");
        var clock = new DailyBoosterTestClock(DateTimeOffset.UnixEpoch);
        var run = new BeastRunProgress(Guid.NewGuid(), clock);
        for (int stage = 0; stage < 6; stage++)
        {
            That(run.Enter(stage), "显示用例进入真实阶段");
            var loading = BeastDisplayPackets.Progress((uint)(8 + stage * 2), binding, 23, run.View());
            That(loading[6] == stage && loading[7] == stage && new Guid(loading.AsSpan(16,16)) == run.View().InstanceId,
                "激活阶段不会提前显示完成或奖励");
            if (stage == 2) That(BinaryPrimitives.ReadInt32LittleEndian(loading.AsSpan(32)) == 180000,
                "第三阶段首次显示180秒");
            if (stage == 3) That(BinaryPrimitives.ReadInt32LittleEndian(loading.AsSpan(32)) == 179000,
                "换到第四阶段不重置共同倒计时");
            clock.Advance(TimeSpan.FromSeconds(1));
            That(run.Confirm(run.Prepare(stage)!), "只在真实确认后生成奖励视图");
            var body = BeastDisplayPackets.Progress((uint)(9 + stage * 2), binding, 23, run.View());
            for (int i = 0; i < 6; i++)
            {
                var row = body.AsSpan(56 + 12 * i, 12);
                That(i <= stage ? BinaryPrimitives.ReadUInt32LittleEndian(row) == BeastDisplayPackets.Rewards[i] &&
                    BinaryPrimitives.ReadInt32LittleEndian(row[4..]) == 1 &&
                    BinaryPrimitives.ReadInt32LittleEndian(row[8..]) == (i == 0 ? 1300 : i == 1 ? 2600 : 4200) :
                    row.IndexOfAnyExcept((byte)0) < 0, "六罐视图仅包含已确认阶段的物品、耗时和得分");
            }
            if (stage == 5) That(body[5] == 1 && body[7] == 6 && body[40] == 0 &&
                BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(36)) == 20700, "真实六阶段终局为20700分及首档RANK");
        }
        var clear = BeastDisplayPackets.Clear(99, binding, 23);
        That(clear[4] == 0 && clear[6] == 255 && clear[40] == 6 &&
            clear.AsSpan(56,72).IndexOfAnyExcept((byte)0) < 0, "退出清理包不制造奖励或结果");
    }
}
