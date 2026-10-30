using DfoServer.Game.Dungeons;
using DfoServer.Game.Quests;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class OrdinaryLukeProgressTests
{
    internal static void RunCore()
    {
        int day = QuestCycle.Day(new DateTimeOffset(2026, 10, 26, 5, 59, 59, TimeSpan.FromHours(8)));
        int next = QuestCycle.Day(new DateTimeOffset(2026, 10, 26, 6, 0, 0, TimeSpan.FromHours(8)));
        That(day == 20261025 && next == 20261026, "普通卢克复用北京时间06:00业务日");
        OrdinaryLukeProgress progress = default;
        That(progress.SequenceInfo(day) == new OrdinaryLukeSequenceInfo(0, 0), "无记录不补造前置通关");
        That(DungeonRequests.TryParseSequentialInfo([42,0,0,0], out uint sequence) && sequence == 42 &&
            !DungeonRequests.TryParseSequentialInfo([42,0,0], out _) &&
            !DungeonRequests.TryParseSequentialInfo([42,0,0,0,0], out _), "871严格读取一个u32序列号");
        for (uint id = 5101; id <= 5106; id++)
        {
            That(progress.Allows(day, id) && !progress.Allows(day, id + 1), "仅当前顺序图开放，不能跳关");
            if (id < 5106) Reject<InvalidDataException>(() => progress.Clear(day, id + 1), "未经前序不能max跳跃解锁");
            progress = progress.Clear(day, id);
            That(progress.SequenceInfo(day) == new OrdinaryLukeSequenceInfo((byte)(id - 5100), (1u << (int)(id - 5100)) - 1),
                "六图完成数投影为当前可进阶段及真实通关低位");
            That(progress.Completed == id - 5100 && progress.Clear(day, 5101) == progress, "真实清图推进，重复早图不降进度");
        }
        That(progress.Allows(next, 5101) && !progress.Allows(next, 5102), "新日仅首图开放");
        That(progress.SequenceInfo(next) == new OrdinaryLukeSequenceInfo(0, 0) &&
            progress.SequenceInfo(day - 1) == new OrdinaryLukeSequenceInfo(0, 0), "不同业务日不复述历史或未来进度");
        That(OrdinaryLukeSequencePackets.Information(new(5, 0x1f)).SequenceEqual(new byte[]
            {42,0,0,0,5,31,0,0,0,0,0,0,0,0,0,0}), "前五图完成的602精确16B：机械王座阶段5、通关位0x1f");
        That(!progress.Allows(day - 1, 5101), "墙钟回拨不得回到历史日重新准入");
        var newer = new OrdinaryLukeProgress(next, 0);
        That(newer.Clear(day, 5106) == newer, "旧Run晚结算不污染新日");
        That(!progress.Allows(day, 5100) && !progress.Allows(day, 5107), "进度不能授权范围外地图");
    }
}
