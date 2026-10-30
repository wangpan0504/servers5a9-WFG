using System.Collections.Immutable;
using System.Buffers.Binary;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;
using DfoServer.Network.Builders;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class PreyNormalProgressTests
{
    internal static void RunCore()
    {
        var route = ImmutableArray.Create(400000032,400000035,400000039,400000043,400000046);
        var catalog = new PreyNormalCatalog(Enumerable.Repeat(route,6).ToImmutableArray());
        var admission = new PreyNormalAdmission(20190606,1,catalog);
        PreyNormalProgress progress = default;
        for (int index = 0; index < 5; index++)
        {
            That(progress.Allows(admission,(uint)route[index]),"普通普雷只开放前序已完成的当日图");
            if (index < 4)
                Reject<InvalidDataException>(() => progress.Clear(admission,(uint)route[index+1]),"不能跳关推进日进度");
            progress = progress.Clear(admission,(uint)route[index]);
            That(progress.Completed == index+1 && progress.Clear(admission,(uint)route[0]) == progress,
                "真实通关推进，重打不降低或重复增加阶段");
        }
        That(!progress.Allows(admission,400000033) && !progress.Allows(admission with { Day=20190605 },400000032),
            "非当日路线及时间回拨不授权进入");
        var next = admission with { Day=20190607,Branch=2 };
        That(progress.Allows(next,400000032) && !progress.Allows(next,400000035) &&
            new PreyNormalProgress(20190607,2,0).Clear(admission,400000046) == new PreyNormalProgress(20190607,2,0),
            "新日仅首图开放，旧Run结算不污染新日");
        byte[] info = PreyNormalSequencePackets.Information(new(4,15,6));
        That(info.Length == 16 && BinaryPrimitives.ReadUInt32LittleEndian(info) == 203 && info[4] == 4 &&
            BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(5)) == 15 &&
            BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(9)) == 6 && info.AsSpan(13).SequenceEqual(new byte[3]),
            "203精确16B保留分支键和真实低位进度，不借用42的零分支");
        That(PreyNormalCatalog.BranchForDay(20190605) == 6 && PreyNormalCatalog.BranchForDay(20190612) == 1 &&
            PreyNormalCatalog.BranchForDay(20261101) is >= 1 and <= 6,"兼容六日历跨月及锚点之前仍在1至6");
    }
}
