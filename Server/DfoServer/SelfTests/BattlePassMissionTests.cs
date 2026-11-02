using System.Collections.Immutable;
using DfoServer.Game.BattlePass;
using DfoServer.GameWorld.BattlePass;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class BattlePassMissionTests
{
    static readonly DateTimeOffset Start = new(2026, 10, 29, 6, 0, 0, TimeSpan.FromHours(8));
    public static void RunCore()
    {
        int before = Count;
        Periods();
        Persistence();
        DailyLoginPersistence();
        CompletionPersistence();
        WriterClockPersistence();
        Console.WriteLine($"PASS BattlePassMissionTests core ({Count - before} checks)");
    }

    static void Periods()
    {
        var policy = new BattlePassMissionPolicy("core-missions", Start, Catalog());
        That(policy.Days == 100 && policy.End == Start.AddDays(100) && policy.Active(Start.AddTicks(-1)).IsEmpty &&
            policy.Active(policy.End).IsEmpty, "显式开季和结束均有界，目录推导100日且不循环");
        var first = policy.Active(Start);
        That(first.Length == 26 && first.Take(4).Select(row => row.Key.Id).SequenceEqual(new[] { 1, 2, 3, 4 }) &&
            first.Skip(4).Take(4).Select(row => row.Key.Id).SequenceEqual(new[] { 25, 26, 27, 28 }) &&
            first.Skip(8).Select(row => row.Key.Id).SequenceEqual(Enumerable.Range(51, 18)),
            "完整26槽保每日四项、每周四项和赛季18项原序真key");
        var lastMoment = policy.Active(Start.AddDays(1).AddTicks(-1));
        var nextDay = policy.Active(Start.AddDays(1));
        That(lastMoment[0].Key.Period == Start.ToUnixTimeSeconds() && nextDay[0].Key.Period == Start.AddDays(1).ToUnixTimeSeconds() &&
            nextDay[4].Key.Period == Start.ToUnixTimeSeconds() && policy.Active(Start.AddDays(7))[4].Key.Period == Start.AddDays(7).ToUnixTimeSeconds(),
            "06:00日界推进日实例，周四06:00才推进周实例，显示重复key仍是不同期段");
        var last = policy.Active(policy.End.AddTicks(-1));
        That(last[0].Key.Period == Start.AddDays(99).ToUnixTimeSeconds() && last[4].Key.Period == Start.AddDays(98).ToUnixTimeSeconds(),
            "最后两日属于第15周段，结束后不复用最后组或回到第一组");
        That(new BattlePassMissionPolicy("short", Start, Catalog(weeklyGroups: 1)).Days == 7,
            "资源周组先耗尽时显式缩短结束，不扩写不存在的任务组");
        Reject<InvalidDataException>(() => new BattlePassMissionPolicy("bad", Start.AddDays(1), Catalog()),
            "未对齐周四06:00的显式本服开季拒绝");
        var season = new BattlePassSeasonState(policy.SeasonKey, 0, 0, ImmutableHashSet<BattlePassRewardKey>.Empty);
        var slots = first.Select(row => new BattlePassMissionSlot(row, row.Key.Id == 51 ? 2_000_000_000L : 0, false, row.Key.Id == 51)).ToImmutableArray();
        byte[] body = new BattlePassMissionState(season, slots).ToPayload();
        That(body.Length == 156 && BitConverter.ToUInt16(body, 48) == 51 && BitConverter.ToUInt16(body, 50) == ushort.MaxValue &&
            body[52] == 1 && body[53] == 0 && slots[8].Count == 2_000_000_000L &&
            BitConverter.ToUInt16(body, 150) == 68, "18槽尾有真实key和独立状态；u16显示封顶不窄化真实大目标计数");
        That(first[3].Premium && first[3].Experience == 300 && first[0].Experience == 100 &&
            policy.Active(Start.AddDays(58))[1].Key.Id == 12 && policy.Active(Start.AddDays(58))[6].Key.Id == 38,
            "本服高级日第4槽用资源3倍经验，真实远征日周定义在指定有界组激活");
    }

    static BattlePassCatalog Catalog(int weeklyGroups = 15, bool dailyLogin = false, bool completion = false)
    {
        var source = new List<object> { "[ui path]", "UI/MissionCoreFixture.xui" };
        for (int id = 1; id <= 70; id++)
        {
            bool complete = completion && id is (54 or 55);
            int target = complete ? id == 54 ? 150 : 30 : dailyLogin && id == 43 ? 3 : dailyLogin && id == 56 ? 50 : id == 38 ? 3 : id == 51 ? 2_000_000_000 : 1;
            int experience = complete ? 1600 : dailyLogin && id == 43 ? 1000 : dailyLogin && id == 56 ? 1600 : id == 38 ? 1000 : 100;
            source.AddRange(new object[] { "[mission]", id, target, experience, complete ? 36 : dailyLogin && id is (43 or 56) ? 30 : id is 12 or 38 ? 12 : 777,
                complete ? "累积完成日周任务" : dailyLogin && id is (43 or 56) ? "每日登录游戏" : id is 12 or 38 ? "派遣冒险团远征队" : "合成任务夹具", complete ? id - 54 : -1, "[/mission]" });
        }
        source.Add("[daily mission]");
        for (int day = 0; day < 100; day++) source.AddRange(day switch
        {
            14 => new object[] { 1, 2, 3, 12 },
            58 => new object[] { 1, 12, 3, 4 },
            59 => new object[] { 12, 2, 3, 4 },
            _ => new object[] { 1, 2, 3, 4 }
        });
        source.AddRange(new object[] { "[/daily mission]", "[weekly mission]" });
        for (int week = 0; week < weeklyGroups; week++) source.AddRange(dailyLogin ? new object[] { 25, 26, 43, 28 } : week == 8 ?
            new object[] { 25, 26, 38, 28 } : new object[] { 25, 26, 27, 28 });
        source.AddRange(new object[] { "[/weekly mission]", "[season mission]" });
        source.AddRange(Enumerable.Range(51, 18).Cast<object>());
        source.Add("[/season mission]");
        for (int level = 1; level <= 80; level++) source.AddRange(new object[] { "[level reward]", level, 999001, 1, 0, "[/level reward]" });
        source.Add("[exp table]");
        for (int level = 1; level <= 80; level++) source.AddRange(new object[] { level, level == 80 ? 0 : 1300 });
        source.AddRange(new object[] { "[/exp table]", "[tier table]", 1, 80, "[/tier table]", "[postal]", 1, 2, 3, 4, "[/postal]",
            "[package index]", 10, "[/package index]", "[certificate index]", 11, "[/certificate index]",
            "[buy restrict code]", 12, "[/buy restrict code]", "[golden mission exp ratio]", 3, "[cera consumption exp percentage]", 50 });
        var strings = new List<string>();
        PvfToken Token(object value)
        {
            if (value is int number) return new(0, number);
            string text = (string)value; strings.Add(text);
            return new(text.StartsWith('[') ? (byte)3 : (byte)6, strings.Count - 1);
        }
        return BattlePassCatalog.Parse(source.Select(Token).ToArray(), index => strings[index]);
    }
}
