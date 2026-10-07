using PvfLib;

namespace DfoServer.GameWorld.Dungeons;

// 本服规则显式采用怪物等级表的整数作为倍率1奖励；不声称是客户端还原的原版公式。
internal sealed class DungeonKillExperienceCatalog
{
    public const string RuleId = "local-monster-level-x1-v1";
    private readonly IReadOnlyDictionary<int, uint> values;
    private DungeonKillExperienceCatalog(string resourceSha256, IReadOnlyDictionary<int, uint> values)
        => (ResourceSha256, this.values) = (resourceSha256, values);
    public string ResourceSha256 { get; }

    public static DungeonKillExperienceCatalog Load(PvfArchive pvf) =>
        new(pvf.Sha256, Parse(pvf.ReadTokens("monster/monsterexp.tbl")));

    public uint GetExperience(int monsterLevel) => values.TryGetValue(monsterLevel, out var value)
        ? value : throw new NotSupportedException("怪物等级没有本服经验表记录");

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
