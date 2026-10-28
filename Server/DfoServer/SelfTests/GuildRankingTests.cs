using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Guild;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Guilds;
using DfoServer.GameWorld.Towns;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class GuildRankingTests
{
    sealed class Clock(long unix) : TimeProvider
    {
        public long Unix = unix;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(Unix);
    }

    const string Rules = """
        [guild factor]
        [member count]
        [max point]
        100
        [rate]
        0.1
        [/member count]
        [weekly uv]
        [max point]
        90
        [rate]
        0.1
        [/weekly uv]
        [guild dungeon]
        [max point]
        120
        [min conquest point]
        10
        [dungeon conquest]
        101 102 50
        [/dungeon conquest]
        [purify point rate]
        0.1
        [/guild dungeon]
        [guild contents]
        [max point]
        60
        [rate]
        10
        [/guild contents]
        [guild party clear]
        400000016 0 5 7.5 291100320 0 3 4.5
        [/guild party clear]
        [/guild factor]
        [guild member factor]
        [adventure level]
        1 10 1 1.5 11 15 2 3
        [/adventure level]
        [raid clear]
        [point]
        1 2 3 2 3 4.5
        [/point]
        [/raid clear]
        [pvp]
        0 0 1 1 1 4 2 2
        [/pvp]
        [contrib]
        100000
        [/guild member factor]
        """;

    public static void RunCore()
    {
        int before = Count;
        var rules = new GuildRankingCatalog(Rules);
        That(rules.Party(400000016) == 5 && rules.Party(400000000) == 0 && rules.Party(291100320) == 3,
            "排行地图整数不经过float截断，大ID不发生精度合并");
        Reject<InvalidDataException>(() => new GuildRankingCatalog(Rules.Replace("400000016", "4294967296")), "超u32地图拒绝");
        Reject<InvalidDataException>(() => new GuildRankingCatalog(Rules.Replace("1 10 1 1.5 11 15", "1 11 1 1.5 11 15")), "重叠分档拒绝");
        That(GuildRankingRequests.TrySummary([0x34, 0x12], out ushort reference) && reference == 0x1234 &&
            !GuildRankingRequests.TrySummary([], out _) && !GuildRankingRequests.TrySummary([0, 0, 0], out _),
            "1506必须为2B Actor UID引用，拒绝旧空正文推测");
        That(GuildRankingRequests.TryPage([0, 7, 2, 0], out byte mode, out byte sort, out ushort page) &&
            mode == 0 && sort == 7 && page == 2 && GuildRankingRequests.TryPage([1, 0, 1, 0], out _, out _, out _) &&
            !GuildRankingRequests.TryPage([2, 0, 1, 0], out _, out _, out _) &&
            !GuildRankingRequests.TryPage([0, 8, 1, 0], out _, out _, out _) &&
            !GuildRankingRequests.TryPage([0, 0, 0, 0], out _, out _, out _) &&
            !GuildRankingRequests.TryPage([1, 0, 2, 0], out _, out _, out _), "1507固定4B与模式、分项、一基页码边界");
        long Now(int month, int day = 1, int hour = 6) => new DateTimeOffset(2026, month, day, hour, 0, 0, TimeSpan.FromHours(8)).ToUnixTimeSeconds();
        That(GuildRankingService.Month(Now(11) - 1) == 202610 && GuildRankingService.Month(Now(11)) == 202611 &&
            GuildRankingService.PreviousMonth(202601) == 202512 && GuildRankingService.MonthStart(Now(11, 20)) == Now(11),
            "排行榜月界固定东八区一日六点，跨年和月初六点前取上期");
        RunRepository(rules, new Clock(Now(10, 27)), Now(9, 30), Now(8, 31));
        Console.WriteLine($"PASS guild ranking protocol/facts/archive assertions {Count - before}");
    }

    static void RunRepository(GuildRankingCatalog rules, Clock clock, long september, long august)
    {
        string root = Path.Combine(Path.GetTempPath(), "guild-ranking-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "game.db");
            var repository = new CharacterRepository(path);
            var owners = new AccountOwners();
            using var owner = owners.Acquire("guild-ranking", static () => { });
            var room = new Location(0, 0, 100, 100);
            var character = repository.Create(owner, new("RankCore", room, room));
            var towns = new TownCatalog(new Dictionary<(byte, byte), TownArea>(), room, room, new(0, [], []), [], new Dictionary<uint, uint[]>());
            var actor = new GuildManagementIdentity(new GameSession(repository, towns, owner), owner, character.Id, character.Name,
                1, new(2, 10), () => owner.IsCurrent);
            using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False;Foreign Keys=True"); db.Open();
            void Sql(string sql) { using var cmd = db.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
            long Number(string sql) { using var cmd = db.CreateCommand(); cmd.CommandText = sql; return Convert.ToInt64(cmd.ExecuteScalar()); }
            // 公会表为空时榜面也恒为空：不能让维护循环在没有业务写入的存档里每分钟改一行。
            var vacant = new GuildRankingService(repository, rules, GuildDungeonProgressTests.Fixture(), PvpRatingTests.Fixture(), clock);
            vacant.Tick(); vacant.Tick();
            That(Number("SELECT count(*) FROM guild_ranking_months") == 0, "一个公会都没有时不写空榜历史");
            Sql($"INSERT INTO guilds(guild_id,name,address,master_character_id,created_at_unix) VALUES(1,'排行','ranking',{character.Id},1000); " +
                $"INSERT INTO guild_members(guild_id,character_id,grade,joined_at_unix) VALUES(1,{character.Id},1,1000)");
            var service = new GuildRankingService(repository, rules, GuildDungeonProgressTests.Fixture(), PvpRatingTests.Fixture(), clock);
            var empty = service.Summary(actor)!;
            That(empty.Current is { Rank: 1, Members: 1 } && empty.Current.Factors.Total == 0.1f && empty.Official is null && empty.Previous is null,
                "新库从真实会籍计算，不给没有评分/冒险团状态的角色赠分，不伪造历史榜");
            long now = clock.Unix, week = GuildContributionRules.WeekStart(now);
            Sql($"""
                INSERT INTO guild_contribution_events VALUES('rank-login',{character.Id},1,1000,1,{GuildAttendanceDay.Start(now)},{week},100,{now});
                INSERT INTO guild_contribution_events VALUES('rank-points',{character.Id},1,1000,3,{GuildAttendanceDay.Start(now)},{week},200000,{now});
                INSERT INTO guild_contribution_events VALUES('rank-old',{character.Id},1,999,3,{GuildAttendanceDay.Start(now)},{week},999999,{now});
                INSERT INTO guild_ranking_party_clears VALUES(1,'11111111111111111111111111111111',400000016,2,{now});
                INSERT INTO guild_ranking_party_clears VALUES(1,'22222222222222222222222222222222',400000016,2,{now});
                INSERT INTO guild_dungeon_progress VALUES(1,101,67,{now});
                INSERT INTO guild_dungeon_progress VALUES(1,102,10000,{now});
                """);
            var current = service.Summary(actor)!.Current;
            That(current.Factors is { WeeklyUv: 0.1f, Contents: 10, Party: 10 } && Math.Abs(current.Factors.Contribution - 2.001f) < 0.00001 &&
                current.Factors.Dungeon > 50 && current.Factors.War == 0 && current.Factors.Event == 0,
                "真实出勤角色去重，组队按实例计分而内容按地图去重，旧会籍事件不混入当月贡献");
            var old = current with { Factors = current.Factors with { Contribution = 77 }, Rank = 3 };
            repository.ObserveGuildRanking(new(202609, september, [old]));
            repository.ObserveGuildRanking(new(202609, september - 1, [current]));
            repository.ObserveGuildRanking(new(202608, august, [current with { Rank = 9 }]));
            var reopened = new GuildRankingService(new CharacterRepository(path), rules, GuildDungeonProgressTests.Fixture(), PvpRatingTests.Fixture(), clock);
            var history = reopened.Summary(actor)!;
            That(history.Official is { Rank: 3 } && history.Official.Factors.Contribution == 77 && history.Previous is { Rank: 9 },
                "重开恢复独立上月及前期快照，过时采样不能覆盖较新历史");
            That(reopened.OfficialRank(1) == 3 && reopened.OfficialRank(99) == 0,
                "详情排名只取真实上月封存榜，缺失公会保留未排名");
            Reject<InvalidDataException>(() => repository.ObserveGuildRanking(new(202610, september, [])), "不能把当前事实写成别的月份");
            byte[] summary = GuildRankingPackets.Summary(history);
            That(summary.Length == 116 && BitConverter.ToInt32(summary, 0) == 0 && BitConverter.ToInt32(summary, 4) == 1 &&
                BitConverter.ToInt32(summary, 12) == 3 && BitConverter.ToInt32(summary, 20) == 9 &&
                BitConverter.ToSingle(summary, 112) == 77 && BitConverter.ToSingle(summary, 92) == current.Factors.Contribution,
                "116B原生summary当前、上月、前期及11项因子独立排列，首DWORD零初始化不做标志位，绝不镜像上月");
            var sentinel = current with { Factors = current.Factors with { Adventure = 78, Pvp = 56, Raid = 1234, Event = 9, Contribution = 10 } };
            byte[] probe = GuildRankingPackets.Summary(new(sentinel, null, null));
            That(BitConverter.ToSingle(probe, 0x4C) == 78 && BitConverter.ToSingle(probe, 0x50) == 56 &&
                BitConverter.ToSingle(probe, 0x54) == 1234 && BitConverter.ToSingle(probe, 0x58) == 9 &&
                BitConverter.ToSingle(probe, 0x5C) == 10,
                "0x50按显示侧分类是决斗等级(PvP)，0x54本版不渲染，冒险团/贡献各归其位");
            var entries = Enumerable.Range(1, 12).Select(id => current with { Id = (uint)id, Name = "榜" + id, Master = "主" + id }).ToArray();
            byte[] list = GuildRankingPackets.Page(new(0, 12, entries[..5]));
            That(list.Length == 385 && list[0] == 0 && BitConverter.ToUInt16(list, 1) == 12 && BitConverter.ToUInt16(list, 3) == 5 &&
                BitConverter.ToUInt32(list, 5) == 1 && list[5 + 12] == 0 && list[5 + 23] == 0 &&
                BitConverter.ToUInt16(list, 5 + 74) == 1 && BitConverter.ToUInt32(list, 81) == 2,
                "1507头5B(mode/u16/count)与76B行缓冲、NUL与成员u16按独立偏移核验，正文不前置标志位");
            // 行内20起23B是公会名、43起31B是会长名：两列顺序由实机截图钉死，不得再对调。
            var encoding = CharacterName.Encoding;
            That(encoding.GetString(list, 5 + 20, 23).TrimEnd('\0') == "榜1" &&
                encoding.GetString(list, 5 + 43, 31).TrimEnd('\0') == "主1",
                "1507行20起写公会名、43起写会长名");
            for (int i = 2; i <= 12; i++) Sql($"INSERT INTO guilds(guild_id,name,address,master_character_id,created_at_unix) VALUES({i},'榜{i}','rank-{i}',{character.Id},1000)");
            That(service.Page(0, 0, 1).Entries.Length == 5 && service.Page(0, 0, 3).Entries.Length == 2 &&
                service.Page(0, 0, 4).Entries.Length == 0 && service.Page(1, 0, 1).Entries.Length == 3,
                "真实存储分页5/5/2/0与前三名模式，稳定同分以公会ID排序");
            Sql($"INSERT INTO guild_dungeon_effective_progress VALUES(1,101,67,{now - 7 * 86400L})");
            That(service.Summary(actor)!.Current.Factors.Dungeon < current.Factors.Dungeon &&
                Number("SELECT clear_count FROM guild_dungeon_progress WHERE dungeon_id=101") == 67,
                "排行净化和征服组合使用相同闲置有效进度，历史满值不会继续提供满净化分数");
            service.Tick(); service.Tick();
            That(Number("SELECT count(*) FROM guild_ranking_months WHERE month=202610") == 1, "同一维护循环分钟内不反复写榜");
            Sql("DROP TABLE guild_ranking_months; DROP TABLE guild_ranking_party_clears; PRAGMA user_version=140");
            _ = new CharacterRepository(path);
            That(Number("PRAGMA user_version") == SqliteMigrations.CurrentVersion && Number("SELECT count(*) FROM guild_ranking_months") == 0,
                "138真实升级139并重开，缺失历史保持空");
            Sql("DROP TABLE guild_ranking_months; DROP TABLE guild_ranking_party_clears; CREATE TABLE guild_ranking_months(month TEXT); PRAGMA user_version=140");
            Reject<Exception>(() => new CharacterRepository(path), "未知同名历史结构拒绝，不覆盖");
            That(Number("PRAGMA user_version") == 140 && Number("SELECT count(*) FROM sqlite_schema WHERE name='guild_ranking_party_clears'") == 0,
                "榜表碰撞整段回滚，不留下半套升级或版本前移");
        }
        finally { Directory.Delete(root, true); }
    }

    public static void RunResources(PvfArchive pvf)
    {
        var rules = new GuildRankingCatalog(pvf);
        That(rules.Conquests.Length == 5 && rules.Adventure(35) == 10 && rules.Duel(21) == 7 &&
            rules.Party(400000016) == 5 && rules.ContributionDivisor == 100000,
            "正式排行资源的档位、地图整数与贡献除数加载");
    }
}
