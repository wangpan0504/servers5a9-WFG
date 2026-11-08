using DfoServer.Game.Dungeons;
using DfoServer.Testing;
namespace DfoServer;
internal static class RevisedBeastSelectionTests
{
    public static void Run()
    {
        var now = new DateTimeOffset(2026,10,31,0,0,0,TimeSpan.Zero);
        var id = RevisedBeastSelection.DailyDungeon(now);
        TestCheck.That(id is >= 5335 and <= 5339, "改版魔兽日图发布锚点范围为5335-5339");
        TestCheck.That(RevisedBeastSelection.IsRequest(id,0,now) && RevisedBeastSelection.IsRequest(id,2,now), "改版魔兽支持三档难度");
        var old = Environment.GetEnvironmentVariable("DFO_REVISED_BEAST_ALL");
        try
        {
            Environment.SetEnvironmentVariable("DFO_REVISED_BEAST_ALL", "1");
            TestCheck.That(!RevisedBeastSelection.IsRequest(id,3,now) && !RevisedBeastSelection.IsRequest(id==5339?5335:5339,1,now), "单图模式拒绝非当日图");
            Environment.SetEnvironmentVariable("DFO_REVISED_BEAST_ALL", null);
            TestCheck.That(RevisedBeastSelection.AllDungeons.Count(value => RevisedBeastSelection.IsRequest(value, 0, now))==1 &&
                !RevisedBeastSelection.IsRequest(5334, 0, now), "清除旧全发环境后仍只发布一图");
            var seen=new HashSet<int>();
            for(int day=0;day<365;day++)
            {
                var time=now.AddDays(day);
                int published=RevisedBeastSelection.Published(time).DungeonId;
                seen.Add(published);
                TestCheck.That(RevisedBeastSelection.AllDungeons.Count(value=>RevisedBeastSelection.IsDaily(value,time))==1 &&
                    RevisedBeastSelection.DailyDungeon(time.AddHours(18))==published &&
                    RevisedBeastSelection.DailyDungeon(time.ToOffset(TimeSpan.FromHours(8)))==published,
                    "所有工作日和周末均发布唯一且业务日内稳定的图");
            }
            TestCheck.That(seen.Count==5,
                "业务日轮换覆盖全部五实体");
            var boundary=new DateTimeOffset(2026,11,2,6,0,0,TimeSpan.FromHours(8));
            TestCheck.That(DungeonIncountPolicy.Period(boundary.AddTicks(-1)).Day==20261101 &&
                DungeonIncountPolicy.Period(boundary).Day==20261102,"发布与次数使用同一北京时间06点日界");
            byte[] wire=DfoServer.Network.Builders.DungeonPackets.RevisedBeastNativeIncount(new(3,6,2,4));
            TestCheck.That(wire.SequenceEqual(new byte[]{3,0,0,0,6,0,0,0,2,4,2,4}),"1444头两字段发送已用入场次数，奖励尾部维持原投影");
            foreach (var used in new[]{new DungeonIncountUsage(0,0),new DungeonIncountUsage(1,2),new DungeonIncountUsage(3,6)})
            {
                byte[] countWire=DfoServer.Network.Builders.DungeonPackets.RevisedBeastNativeIncount(
                    new(checked((uint)used.DailyUsed),checked((uint)used.WeeklyUsed),2,4));
                TestCheck.That(3-System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(countWire)==3-used.DailyUsed &&
                    6-System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(countWire.AsSpan(4))==6-used.WeeklyUsed,
                    "客户端以上限减包内已用值，未用、部分使用和耗尽时均显示正确剩余");
            }
            var previous=new DungeonIncountPeriod(20261101,20261029);
            TestCheck.That(DungeonIncountPolicy.Current(new(3,6),previous,new(20261102,20261029))==new DungeonIncountUsage(0,6) &&
                DungeonIncountPolicy.Current(new(3,6),previous,new(20261105,20261105))==new DungeonIncountUsage(0,0),
                "日切只归零日已用，跨周同时归零日周已用");
            var channel=new DfoServer.GameWorld.Channels.ChannelEntry(new(2,46),40,"[beast]");
            var role=new DfoServer.Game.Characters.Character(1,0,"qualification",0,new(51,0,0,0),null){Level=95};
            TestCheck.That(!RevisedBeastAdmission.Check(channel,role,id,0,now).Accepted &&
                RevisedBeastAdmission.Check(channel,role with{CompletedQuests=[5927]},id,0,now).Accepted &&
                !RevisedBeastAdmission.Check(new(new(2,10),22,"[granfloris]"),role with{CompletedQuests=[5927]},id,0,now).Accepted,
                "5927完成资格与类型40隔离，不借普通频道开放改版");
        }
        finally { Environment.SetEnvironmentVariable("DFO_REVISED_BEAST_ALL", old); }
    }
}
