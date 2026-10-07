using PvfLib;

namespace DfoServer.GameWorld.Characters;

internal sealed class CharacterGrowthCatalog
{
    public const int MaximumSupportedLevel = 95;
    private readonly SkillCatalog points;

    private CharacterGrowthCatalog(ulong[] experience, SkillCatalog points)
    {
        CumulativeExperience = Array.AsReadOnly(experience);
        this.points = points;
    }

    // 0 是初始经验零，后续项是资源中的累计边界。
    public IReadOnlyList<ulong> CumulativeExperience { get; }

    public static CharacterGrowthCatalog Load(PvfArchive pvf)
    {
        ulong[] experience = ParseExperienceTable(pvf.ReadTokens("character/exptable.tbl"));
        if (experience.Length != 100)
            throw new InvalidDataException("匹配经验表的99项累计边界改变");
        return new CharacterGrowthCatalog(experience, SkillCatalog.Load(pvf));
    }

    public ulong GetLevelStartExperience(int level)
    {
        ValidateLevel(level);
        return CumulativeExperience[level - 1];
    }

    public ulong GetCumulativeExperienceBoundary(int level)
    {
        ValidateLevel(level);
        return CumulativeExperience[level];
    }

    public ulong GetLevelExperienceSpan(int level)
    {
        ValidateLevel(level);
        // 040C2851..040C2872 按 level 取边界，level>=2 时减前一边界。
        return CumulativeExperience[level] - CumulativeExperience[level - 1];
    }

    public int GetSpDelta(int fromLevel, int toLevel)
    {
        ValidateLevel(fromLevel);
        ValidateLevel(toLevel);
        if (toLevel < fromLevel)
            throw new ArgumentOutOfRangeException(nameof(toLevel), "正常成长的目标等级不能降低");
        // 只查询等级表贡献差值，不修改余额，不包含学习扣点、退点或任务来源。
        return checked(points.GetLevelSpTotal(toLevel) - points.GetLevelSpTotal(fromLevel));
    }

    internal static ulong[] ParseExperienceTable(ReadOnlySpan<PvfToken> tokens)
    {
        if (tokens.IsEmpty)
            throw new InvalidDataException("角色经验表缺失");
        var experience = new List<ulong> { 0 };
        for (int i = 0; i < tokens.Length; i++)
        {
            ulong value;
            if (tokens[i].Type == 0)
            {
                // 03DB0DAC..03DB0DB9 使用 CDQ 扩展有符号值，负数不是合法经验边界。
                if (tokens[i].Value < 0)
                    throw new InvalidDataException("角色经验表有负数累计边界");
                value = (ulong)tokens[i].Value;
            }
            else if (tokens[i].Type == 9)
            {
                if (i + 1 >= tokens.Length || tokens[i + 1].Type != 9)
                    throw new InvalidDataException("角色经验表的 type9 高低DWORD对不完整");
                // 03DB0CF6..03DB0D17、04380980：当前项为高DWORD，下一项为低DWORD，消费两项。
                uint high = unchecked((uint)tokens[i].Value);
                uint low = unchecked((uint)tokens[++i].Value);
                value = ((ulong)high << 32) | low;
            }
            else
            {
                throw new InvalidDataException("角色经验表包含未支持的 token 类型");
            }

            if (value <= experience[^1])
                throw new InvalidDataException("角色经验表累计边界必须严格递增");
            experience.Add(value);
        }

        return experience.ToArray();
    }

    private void ValidateLevel(int level)
    {
        // 040C2845..040C284B 以 level-1 的 unsigned 上限94限定这一消费范围。
        if (level is < 1 or > MaximumSupportedLevel || level >= CumulativeExperience.Count)
            throw new ArgumentOutOfRangeException(nameof(level), "等级超出成长支持范围或资源边界");
    }
}
