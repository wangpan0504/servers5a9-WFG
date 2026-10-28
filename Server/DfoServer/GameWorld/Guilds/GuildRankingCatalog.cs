using System.Globalization;
using PvfLib;

namespace DfoServer.GameWorld.Guilds;

internal sealed record GuildRankingBand(int Minimum, int Maximum, float Normal, float Bonus);
internal sealed record GuildRankingConquest(uint First, uint Second, float Points);

internal sealed class GuildRankingCatalog
{
    public const string ResourcePath = "etc/guild/guildranking.etc";
    public float MemberMaximum { get; }
    public float MemberRate { get; }
    public float UvMaximum { get; }
    public float UvRate { get; }
    public float DungeonMaximum { get; }
    public float DungeonMinimum { get; }
    public float PurifyRate { get; }
    public float ContentMaximum { get; }
    public float ContentRate { get; }
    public float ContributionDivisor { get; }
    public float RaidPoint { get; }
    public GuildRankingConquest[] Conquests { get; }
    public GuildRankingBand[] Adventures { get; }
    public GuildRankingBand[] Pvp { get; }
    readonly Dictionary<uint, float> party = [];

    public GuildRankingCatalog(PvfArchive pvf) : this(pvf.ReadText(ResourcePath)) { }

    internal GuildRankingCatalog(string text)
    {
        string guild = PvfScript.Block(text, "guild factor"), member = PvfScript.Block(text, "guild member factor");
        static float Number(string block, string field)
        {
            string value = PvfScript.Section(block, field).Trim();
            return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float number) &&
                float.IsFinite(number) && number >= 0 ? number : throw new InvalidDataException("公会排行字段无效: " + field);
        }
        static double[] Values(string block, string field, int width, bool closed = false)
        {
            string value = closed ? PvfScript.Block(block, field) : PvfScript.Section(block, field);
            var values = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(value =>
                double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) &&
                double.IsFinite(number) && number >= 0 && number <= float.MaxValue ? number : throw new InvalidDataException("公会排行表含非法数字: " + field)).ToArray();
            if (values.Length == 0 || values.Length % width != 0) throw new InvalidDataException("公会排行表宽度无效: " + field);
            return values;
        }
        string count = PvfScript.Block(guild, "member count"), uv = PvfScript.Block(guild, "weekly uv");
        string dungeon = PvfScript.Block(guild, "guild dungeon"), contents = PvfScript.Block(guild, "guild contents");
        MemberMaximum = Number(count, "max point"); MemberRate = Number(count, "rate");
        UvMaximum = Number(uv, "max point"); UvRate = Number(uv, "rate");
        DungeonMaximum = Number(dungeon, "max point"); DungeonMinimum = Number(dungeon, "min conquest point");
        PurifyRate = Number(dungeon, "purify point rate");
        ContentMaximum = Number(contents, "max point"); ContentRate = Number(contents, "rate");
        ContributionDivisor = Number(member, "contrib");
        if (ContributionDivisor <= 0 || MemberMaximum <= 0 || UvMaximum <= 0 || DungeonMaximum <= 0 || ContentMaximum <= 0)
            throw new InvalidDataException("公会排行上限或贡献除数无效");
        var conquest = Values(dungeon, "dungeon conquest", 3, true);
        Conquests = Enumerable.Range(0, conquest.Length / 3).Select(index => new GuildRankingConquest(
            Integer(conquest[index * 3]), Integer(conquest[index * 3 + 1]), (float)conquest[index * 3 + 2])).ToArray();
        GuildRankingBand[] Bands(string field)
        {
            var data = Values(member, field, 4, true);
            var rows = Enumerable.Range(0, data.Length / 4).Select(index => new GuildRankingBand(
                checked((int)Integer(data[index * 4])), checked((int)Integer(data[index * 4 + 1])),
                (float)data[index * 4 + 2], (float)data[index * 4 + 3])).ToArray();
            if (rows.Any(row => row.Minimum > row.Maximum) || rows.Zip(rows.Skip(1)).Any(pair => pair.First.Maximum >= pair.Second.Minimum))
                throw new InvalidDataException("公会排行分档重叠或倒置");
            return rows;
        }
        Adventures = Bands("adventure level"); Pvp = Bands("pvp");
        var raids = Values(PvfScript.Block(member, "raid clear"), "point", 3);
        RaidPoint = (float)raids[1]; // 本服统一终局回执使用首档，不能把奖励kind当团本类型。
        var partyRows = Values(guild, "guild party clear", 4, true);
        for (int at = 0; at < partyRows.Length; at += 4)
            if (!party.TryAdd(Integer(partyRows[at]), (float)partyRows[at + 2])) throw new InvalidDataException("公会排行组队地图重复");
    }

    static uint Integer(double number) => number <= uint.MaxValue && number == Math.Truncate(number)
        ? checked((uint)number) : throw new InvalidDataException("公会排行整数列无效");
    public float Adventure(int level) => Band(Adventures, level);
    public float Duel(int grade) => Band(Pvp, grade);
    public float Party(uint dungeon) => party.GetValueOrDefault(dungeon);
    static float Band(GuildRankingBand[] rows, int value) => rows.FirstOrDefault(row => value >= row.Minimum && value <= row.Maximum)?.Normal ??
        (value > rows[^1].Maximum ? rows[^1].Normal : 0);
}
