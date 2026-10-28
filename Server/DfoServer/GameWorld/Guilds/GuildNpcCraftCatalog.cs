using System.Collections.Frozen;
using PvfLib;

namespace DfoServer.GameWorld.Guilds;

internal sealed record GuildNpcCraftDefinition(uint BenefitId, int Level, int PublicFunds, int DurationSeconds,
    int ItemId, int Amount);

internal sealed class GuildNpcCraftCatalog
{
    readonly FrozenDictionary<uint, GuildNpcCraftDefinition> definitions;
    public IReadOnlyCollection<GuildNpcCraftDefinition> Definitions => definitions.Values;
    public int CrystalMaximum { get; }
    public GuildNpcCraftCatalog(PvfArchive pvf) : this(pvf.ReadTokens("etc/guildreform.etc"), pvf.ResolveString) { }
    internal GuildNpcCraftCatalog(IEnumerable<GuildNpcCraftDefinition> rows, int maximum)
    {
        var array = rows.ToArray();
        if (maximum <= 0 || array.Length == 0 || array.Any(row => row.BenefitId == 0 || row.Level is < 1 or > 5 ||
            row.PublicFunds <= 0 || row.DurationSeconds <= 0 || row.ItemId != 490013121 || row.Amount <= 0 || row.Amount > maximum) ||
            array.Select(row => row.Level).Distinct().Count() != array.Length)
            throw new InvalidDataException("守护石生产资源无效");
        definitions = array.ToFrozenDictionary(row => row.BenefitId); CrystalMaximum = maximum;
    }
    internal GuildNpcCraftCatalog(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve)
    {
        PvfToken[] Section(string tag, bool closed)
        {
            var starts = Enumerable.Range(0, tokens.Count).Where(at => tokens[at].Type == 3 && resolve(tokens[at].Value) == "[" + tag + "]").ToArray();
            if (starts.Length != 1) throw new InvalidDataException("守护石生产资源段缺失或重复: " + tag);
            int end = starts[0] + 1;
            while (end < tokens.Count && tokens[end].Type != 3) end++;
            if (closed && (end == tokens.Count || resolve(tokens[end].Value) != "[/" + tag + "]"))
                throw new InvalidDataException("守护石生产资源段闭合无效");
            return tokens.Skip(starts[0] + 1).Take(end - starts[0] - 1).ToArray();
        }
        int Number(PvfToken token) => token.Type == 0 ? token.Value : throw new InvalidDataException("守护石生产资源需要整数");
        string Text(PvfToken token) => token.Type is 6 or 7 ? resolve(token.Value) : throw new InvalidDataException("守护石生产资源需要字符串");
        var cap = Section("guild crystal max", false);
        if (cap.Length != 1 || Number(cap[0]) <= 0) throw new InvalidDataException("公会结晶上限声明无效");
        CrystalMaximum = Number(cap[0]);
        var values = Section("npc benefit info", true);
        var rows = new List<GuildNpcCraftDefinition>();
        var ids = new HashSet<int>();
        for (int at = 0; at < values.Length;)
        {
            if (values.Length - at < 11) throw new InvalidDataException("公会NPC福利行截断");
            int id = Number(values[at]), npc = Number(values[at + 1]), level = Number(values[at + 2]);
            int count = Number(values[at + 10]);
            if (id <= 0 || !ids.Add(id) || count < 0 || count > 32 || values.Length - at - 11 < count)
                throw new InvalidDataException("公会NPC福利索引或参数数量无效");
            if (Text(values[at + 4]) == "pure will crystal craft")
            {
                int minutes = Number(values[at + 9]);
                if (npc != GuildNpcCatalog.Guardian || Text(values[at + 3]) != "common" ||
                    Text(values[at + 5]) != "public funds" || Text(values[at + 7]) != "none" ||
                    Number(values[at + 8]) != 0 || count != 2 || minutes <= 0 || minutes > int.MaxValue / 60)
                    throw new InvalidDataException("守护石生产费用、时间或产物声明无效");
                rows.Add(new((uint)id, level, Number(values[at + 6]), minutes * 60,
                    Number(values[at + 11]), Number(values[at + 12])));
            }
            at += 11 + count;
        }
        var validated = new GuildNpcCraftCatalog(rows, CrystalMaximum);
        definitions = validated.definitions;
    }
    public GuildNpcCraftDefinition? Find(uint benefitId) => definitions.GetValueOrDefault(benefitId);
}
