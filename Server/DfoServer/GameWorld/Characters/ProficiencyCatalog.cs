using System.Collections.Immutable;
using System.Globalization;
using PvfLib;

namespace DfoServer.GameWorld.Characters;

// etc/honorlevel.etc 的 [honor expert exp table]：第 N 行是离开 N-1 级所需经验，
// 也就是 0 级用第 1 行、288 级用第 289 行；289 级没有下一行，经验恒为 0。
internal sealed class ProficiencyCatalog
{
    public const string ResourcePath = "etc/honorlevel.etc";
    public const int MaximumLevel = 289;

    ProficiencyCatalog(ImmutableArray<ulong> requirements) => Requirements = requirements;

    // Requirements[level] 是 level 升到 level+1 的所需经验。
    public ImmutableArray<ulong> Requirements { get; }

    public ulong Requirement(uint level) => level < Requirements.Length
        ? Requirements[(int)level]
        : throw new ArgumentOutOfRangeException(nameof(level), "熟练度已到上限");

    public static ProficiencyCatalog Load(PvfArchive pvf) =>
        Parse(new PvfTokenDocument(pvf.ReadTokens(ResourcePath), pvf.ResolveString).Sections, pvf.ResolveString);

    internal static ProficiencyCatalog Parse(ImmutableArray<PvfTokenSection> sections, Func<int, string> resolve)
    {
        var table = sections.SingleOrDefault(row => row.Name == "honor expert exp table") ??
            throw new InvalidDataException("熟练度经验表缺失");
        if (table.Values.Length == 0 || table.Values.Length % 2 != 0)
            throw new InvalidDataException("熟练度经验表不是等级/经验对");
        var requirements = new ulong[table.Values.Length / 2];
        for (int index = 0; index < requirements.Length; index++)
        {
            var level = table.Values[index * 2];
            if (level.Type != 0 || level.Value != index + 1)
                throw new InvalidDataException("熟练度经验表等级不连续");
            requirements[index] = ReadExperience(table.Values[index * 2 + 1], resolve);
        }
        if (requirements.Length != MaximumLevel)
            throw new InvalidDataException("熟练度经验表行数与客户端上限不同");
        return new(requirements.ToImmutableArray());
    }

    // 本表大数值以字符串 token 存放，小数值为整数 token；两种都必须是正数。
    static ulong ReadExperience(PvfToken token, Func<int, string> resolve)
    {
        ulong value;
        if (token.Type == 0)
        {
            if (token.Value <= 0) throw new InvalidDataException("熟练度经验表包含非正经验");
            value = (ulong)token.Value;
        }
        else if (token.Type is 6 or 7)
        {
            if (!ulong.TryParse(resolve(token.Value).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out value) || value == 0)
                throw new InvalidDataException("熟练度经验表字符串项不是正经验");
        }
        else throw new InvalidDataException("熟练度经验表包含未支持的 token 类型");
        return value;
    }
}
