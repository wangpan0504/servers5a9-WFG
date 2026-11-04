using PvfLib;

namespace DfoServer.GameWorld.Dungeons;

// 基础表与倍率来自匹配PVF，组合和等级差规则由Game按external参考实现。
internal sealed class DungeonKillExperienceCatalog
{
    public const string RuleId = "monster-level-modifiers-v2";
    public const string ResourcePath = "monster/monsterexp.tbl";
    public const string ServerParameterPath = "etc/(r)serverparameter.etc";
    private readonly IReadOnlyDictionary<int, uint> values;
    private readonly float[] difficultyRates, monsterRates, partyRates;
    private DungeonKillExperienceCatalog(IReadOnlyDictionary<int, uint> values, float[] difficulty, float[] monster, float[] party)
        => (this.values, difficultyRates, monsterRates, partyRates) = (values, difficulty, monster, party);

    public static DungeonKillExperienceCatalog Load(PvfArchive pvf) =>
        Parse(pvf.ReadTokens(ResourcePath), pvf.ReadTokens(ServerParameterPath), pvf.ResolveString);

    public uint GetExperience(int monsterLevel) => values.TryGetValue(monsterLevel, out var value)
        ? value : throw new NotSupportedException("怪物等级没有本服经验表记录");

    public float GetDifficultyRate(byte difficulty) => difficulty < difficultyRates.Length ? difficultyRates[difficulty] : 1;
    // 可选声明缺失时不虚构组队分量，也不阻断原有单人及组队经验。
    public float GetPartyRate(int members) => members >= 1 && members <= partyRates.Length ? partyRates[members - 1] : 1;
    public float GetMonsterRate(byte type, bool named) =>
        monsterRates[type < monsterRates.Length ? type : 0] * (named && type != 3 ? 3 : 1);

    internal static DungeonKillExperienceCatalog Parse(ReadOnlySpan<PvfToken> table,
        IReadOnlyList<PvfToken> server, Func<int, string> resolve) => new(Parse(table),
        ReadRates(server, resolve, "dungeon difficulty exp bonusrate", 5), ReadRates(server, resolve, "monster exp bonusrate", null),
        ReadRates(server, resolve, "party user number exp bonusrate", 4, required: false));

    private static float[] ReadRates(IReadOnlyList<PvfToken> server, Func<int, string> resolve, string tag, int? count, bool required = true)
    {
        float[]? result = null;
        for (int i = 0; i < server.Count; i++)
        {
            if (server[i].Type != 3 || !resolve(server[i].Value).Equals("[" + tag + "]", StringComparison.OrdinalIgnoreCase))
                continue;
            if (result is not null)
                throw new InvalidDataException("重复击杀经验倍率: " + tag);
            var rates = new List<float>();
            while (++i < server.Count && server[i].Type != 3)
            {
                float rate = Number(server[i]);
                if (rate < 0) throw new InvalidDataException("击杀经验倍率不能为负: " + tag);
                rates.Add(rate);
            }
            i--;
            if (rates.Count == 0 || count is int expected && rates.Count != expected)
                throw new InvalidDataException("击杀经验倍率数量无效: " + tag);
            result = rates.ToArray();
        }
        return result ?? (required ? throw new InvalidDataException("缺少击杀经验倍率: " + tag) : []);
    }

    public static float GetDungeonWeight(DungeonDefinition dungeon)
    {
        if (dungeon.DisableMonsterAndClearExperience) return 0;
        var sections = dungeon.RawSections.Where(section => section.Tag == "experience increasing point").ToArray();
        if (sections.Length == 0) return 1;
        if (sections.Length != 1 || sections[0].Tokens.Count != 1)
            throw new InvalidDataException("副本击杀经验权重无效或重复");
        float weight = Number(sections[0].Tokens[0]);
        return weight < 0 ? 1 : weight;
    }

    public static IReadOnlyList<int> GetNamedMonsters(DungeonDefinition dungeon)
    {
        var ids = new List<int>();
        foreach (var section in dungeon.RawSections.Where(section => section.Tag == "named monster"))
        foreach (var token in section.Tokens)
        {
            if (token.Type != 0 || token.Value <= 0) throw new InvalidDataException("副本精英怪物ID无效");
            ids.Add(token.Value);
        }
        return Array.AsReadOnly(ids.ToArray());
    }

    private static float Number(PvfToken token)
    {
        float value = token.Type switch { 0 => token.Value, 2 => BitConverter.Int32BitsToSingle(token.Value), _ => float.NaN };
        return float.IsFinite(value) ? value : throw new InvalidDataException("击杀经验字段必须为有限数");
    }

    internal static IReadOnlyDictionary<int, uint> Parse(ReadOnlySpan<PvfToken> tokens)
    {
        if (tokens.IsEmpty || tokens.Length % 2 != 0)
            throw new InvalidDataException("怪物经验表必须是完整等级与经验整数对");
        var rows = new Dictionary<int, uint>();
        int previous = 0;
        for (int i = 0; i < tokens.Length; i += 2)
        {
            if (tokens[i].Type != 0 || tokens[i + 1].Type != 0 || tokens[i].Value <= previous || tokens[i + 1].Value <= 0)
                throw new InvalidDataException("怪物经验表等级须递增，奖励须为正整数");
            rows.Add(tokens[i].Value, checked((uint)tokens[i + 1].Value));
            previous = tokens[i].Value;
        }
        return new System.Collections.ObjectModel.ReadOnlyDictionary<int, uint>(rows);
    }
}
