using DfoServer.GameWorld.Guilds;
using DfoServer.GameWorld.Pvp;

namespace DfoServer.Game.Guild;

internal sealed record GuildRankingMember(ushort Id, string Account, int AdventureLevel, int PvpExperience);
internal sealed record GuildRankingParty(uint Dungeon, int Members, long Clears = 1);
internal sealed record GuildRankingFact(uint Id, string Name, string Master, uint Mark, byte JoinMode,
    GuildRankingMember[] Members, int WeeklyUv, long Contribution, int Raids,
    GuildDungeonClearCount[] Dungeons, GuildRankingParty[] Parties, int ActiveContents = 0);
internal sealed record GuildRankingFactors(float Members, float WeeklyUv, float Dungeon, float Contents, float War,
    float Party, float Adventure, float Raid, float Pvp, float Event, float Contribution)
{
    public float Total => Members + WeeklyUv + Dungeon + Contents + War + Party + Adventure + Raid + Pvp + Event + Contribution;
    public static GuildRankingFactors Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
}
internal sealed record GuildRankingEntry(uint Id, string Name, string Master, uint Mark, byte JoinMode,
    ushort Members, GuildRankingFactors Factors, int Rank = 0);
internal sealed record GuildRankingArchive(int Month, long ObservedAt, GuildRankingEntry[] Entries);
internal sealed record GuildRankingData(GuildRankingFact[] Facts, GuildRankingArchive[] Archives);
internal sealed record GuildRankingSummary(GuildRankingEntry Current, GuildRankingEntry? Official, GuildRankingEntry? Previous);
internal sealed record GuildRankingPage(byte Mode, ushort Total, GuildRankingEntry[] Entries);

internal interface IGuildRankingStore
{
    GuildRankingData ReadGuildRanking(long now);
    uint ReadOfficialGuildRank(uint guild, int month);
    void ObserveGuildRanking(GuildRankingArchive archive);
}

internal sealed class GuildRankingService(IGuildRankingStore store, GuildRankingCatalog rules,
    GuildDungeonCatalog dungeons, PvpRatingCatalog ratings, TimeProvider? clock = null)
{
    readonly TimeProvider time = clock ?? TimeProvider.System;
    long nextObservation;
    public const string RuleVersion = "guild-ranking-compatible-v1";

    public uint OfficialRank(uint guild) => store.ReadOfficialGuildRank(guild,
        PreviousMonth(Month(time.GetUtcNow().ToUnixTimeSeconds())));

    public GuildRankingSummary? Summary(GuildManagementIdentity actor)
    {
        if (!actor.IsCurrent()) return null;
        long now = time.GetUtcNow().ToUnixTimeSeconds();
        var data = store.ReadGuildRanking(now);
        uint? guild = data.Facts.FirstOrDefault(row => row.Members.Any(member =>
            member.Id == actor.CharacterId && member.Account == actor.Owner.Account))?.Id;
        if (guild is null || !actor.IsCurrent()) return null;
        var current = Calculate(data.Facts).Single(row => row.Id == guild);
        int previous = PreviousMonth(Month(now));
        return new(current, data.Archives.SingleOrDefault(row => row.Month == previous)?.Entries.SingleOrDefault(row => row.Id == guild),
            data.Archives.SingleOrDefault(row => row.Month == PreviousMonth(previous))?.Entries.SingleOrDefault(row => row.Id == guild));
    }

    public GuildRankingPage Page(byte mode, byte sort, ushort page)
    {
        if (mode > 1 || sort > 7 || page == 0 || mode == 1 && page != 1)
            throw new ArgumentOutOfRangeException(nameof(page));
        var current = Calculate(store.ReadGuildRanking(time.GetUtcNow().ToUnixTimeSeconds()).Facts);
        if (current.Length > ushort.MaxValue) throw new InvalidDataException("公会排行总数超过原生u16范围");
        float Score(GuildRankingEntry row) => sort switch
        {
            2 => row.Factors.WeeklyUv, 3 => row.Factors.Adventure, 4 => row.Factors.Members,
            5 => row.Factors.Party, 6 => row.Factors.Raid, 7 => row.Factors.Pvp, _ => row.Factors.Total
        };
        var ordered = current.OrderByDescending(Score).ThenBy(row => row.Id);
        int size = mode == 1 ? 3 : 5;
        var selected = ordered.Skip((page - 1) * size).Take(size).ToArray();
        return new(mode, checked((ushort)current.Length), selected);
    }

    public GuildRankingEntry[] Calculate(IEnumerable<GuildRankingFact> facts)
    {
        long now = time.GetUtcNow().ToUnixTimeSeconds();
        int Purification(GuildDungeonClearCount row) => dungeons.Find(row.DungeonId)?
            .Purification(row.Effective(dungeons, now).Units) ?? 0;
        var rows = facts.Select(fact =>
        {
            float purification = fact.Dungeons.Sum(Purification);
            float dungeon = purification * rules.PurifyRate;
            foreach (var conquest in rules.Conquests)
                if (fact.Dungeons.Any(row => row.DungeonId == conquest.First && Purification(row) == 100) &&
                    fact.Dungeons.Any(row => row.DungeonId == conquest.Second && Purification(row) == 100))
                    dungeon += conquest.Points;
            if (purification > 0) dungeon = Math.Max(dungeon, rules.DungeonMinimum);
            var factors = new GuildRankingFactors(Math.Min(rules.MemberMaximum, fact.Members.Length * rules.MemberRate),
                Math.Min(rules.UvMaximum, fact.WeeklyUv * rules.UvRate), Math.Min(rules.DungeonMaximum, dungeon),
                Math.Min(rules.ContentMaximum, fact.ActiveContents * rules.ContentRate),
                0, fact.Parties.Sum(row => rules.Party(row.Dungeon) * row.Clears),
                fact.Members.DistinctBy(member => member.Account).Sum(member => rules.Adventure(member.AdventureLevel)),
                fact.Raids * rules.RaidPoint, fact.Members.Where(member => member.PvpExperience >= 0).Sum(member => rules.Duel(ratings.Grade(member.PvpExperience))),
                0, fact.Contribution / rules.ContributionDivisor);
            if (!float.IsFinite(factors.Total)) throw new InvalidDataException("公会排行分数超出原生float范围");
            return new GuildRankingEntry(fact.Id, fact.Name, fact.Master, fact.Mark, fact.JoinMode,
                checked((ushort)fact.Members.Length), factors);
        }).OrderByDescending(row => row.Factors.Total).ThenBy(row => row.Id).ToArray();
        return rows.Select((row, index) => row with { Rank = index + 1 }).ToArray();
    }

    // 复用已有公会维护循环，每分钟保存同一时点的完整榜。没有采样的历史月份保持缺省。
    public void Tick()
    {
        long now = time.GetUtcNow().ToUnixTimeSeconds();
        if (now < nextObservation) return;
        nextObservation = now + 60;
        // 一个公会都没有时榜面恒为空，写下的历史既无内容也会让存档在没有业务写入时持续变化。
        var data = store.ReadGuildRanking(now);
        if (data.Facts.Length == 0) return;
        store.ObserveGuildRanking(new(Month(now), now, Calculate(data.Facts)));
    }

    public static int Month(long unix)
    {
        var day = DateTimeOffset.FromUnixTimeSeconds(GuildAttendanceDay.Start(unix)).ToOffset(TimeSpan.FromHours(8));
        return day.Year * 100 + day.Month;
    }
    public static int PreviousMonth(int month) => month % 100 == 1 ? (month / 100 - 1) * 100 + 12 : month - 1;
    public static long MonthStart(long unix)
    {
        int month = Month(unix);
        return new DateTimeOffset(month / 100, month % 100, 1, 6, 0, 0, TimeSpan.FromHours(8)).ToUnixTimeSeconds();
    }
}
