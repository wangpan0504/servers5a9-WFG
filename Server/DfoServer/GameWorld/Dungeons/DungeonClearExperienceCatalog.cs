using PvfLib;

namespace DfoServer.GameWorld.Dungeons;

internal sealed record DungeonClearExperienceReward(
    uint BaseExperience, uint RankExperience, uint TotalExperience,
    int RankBonusIndex, string RuleId, string ResourceSource)
{
    public uint GrowthContractExperience { get; init; }
    public uint PotionExperience { get; init; }
    public DungeonClearExperienceReward Applied(uint amount)
    {
        if (amount > TotalExperience) throw new InvalidDataException("通关实际经验超过冻结额度");
        uint basis = Math.Min(BaseExperience, amount);
        uint rank = Math.Min(RankExperience, amount - basis);
        uint contract = Math.Min(GrowthContractExperience, amount - basis - rank);
        return this with { BaseExperience = basis, RankExperience = rank, GrowthContractExperience = contract,
            PotionExperience = amount - basis - rank - contract, TotalExperience = amount };
    }
}

// 本服参考外部服务端的基础与评分公式，数值按PVF声明精度计算，不声称逐位还原客户端算法。
internal sealed class DungeonClearExperienceCatalog
{
    public const string RuleId = "dungeon-clear-base-rank-v1";
    public const string QuestParameterPath = "n_quest/questparameter.etc";
    public const string ServerParameterPath = "etc/(r)serverparameter.etc";
    public const string RankSystemPath = "etc/ranksysteminfo.etc";
    private readonly uint[] baseExperience;
    private readonly decimal[] difficultyRates, rankRates;
    private readonly int[] rankThresholds;

    private DungeonClearExperienceCatalog(uint[] experience, decimal[] difficulty, decimal[] rank, int[] thresholds)
        => (baseExperience, difficultyRates, rankRates, rankThresholds) = (experience, difficulty, rank, thresholds);

    public static DungeonClearExperienceCatalog Load(PvfArchive pvf) => Parse(
        pvf.ReadTokens(QuestParameterPath), pvf.ReadTokens(ServerParameterPath),
        pvf.ReadTokens(RankSystemPath), pvf.ResolveString);

    public byte GradeForPoint(byte rankPoint)
    {
        foreach (int threshold in rankThresholds)
        {
            if (rankPoint >= threshold)
                return (byte)threshold;
        }
        return 0;
    }

    public DungeonClearExperienceReward Calculate(DungeonDefinition dungeon, byte difficulty, uint score)
    {
        ArgumentNullException.ThrowIfNull(dungeon);
        if (dungeon.BasisLevel is not int level || level <= 0 || level > baseExperience.Length)
            throw new NotSupportedException("副本基准等级没有通关经验表记录");
        if (difficulty >= difficultyRates.Length)
            throw new NotSupportedException("副本难度没有通关经验倍率");
        decimal weight = DungeonWeight(dungeon);
        int bonusIndex = -1;
        // 资源另有三个低档阈值；五项经验倍率只对应前五档，最高档使用末项倍率。
        for (int i = 0; i < rankRates.Length; i++)
        {
            if (score >= rankThresholds[i])
            {
                bonusIndex = rankRates.Length - 1 - i;
                break;
            }
        }

        try
        {
            decimal basic = decimal.Floor(baseExperience[level - 1] * weight * difficultyRates[difficulty]);
            decimal rank = bonusIndex < 0 ? 0 : decimal.Floor(basic * rankRates[bonusIndex]);
            if (basic < 0 || basic == 0 && !dungeon.IsTutorial || basic > uint.MaxValue || rank > uint.MaxValue || basic + rank > uint.MaxValue)
                throw new InvalidDataException("通关经验超出正数u32奖励范围");
            return new((uint)basic, (uint)rank, (uint)(basic + rank), bonusIndex, RuleId,
                $"{QuestParameterPath};{ServerParameterPath};{RankSystemPath};{dungeon.Path}");
        }
        catch (OverflowException error)
        {
            throw new InvalidDataException("通关经验计算溢出", error);
        }
    }

    internal static DungeonClearExperienceCatalog Parse(
        IReadOnlyList<PvfToken> quest, IReadOnlyList<PvfToken> server,
        IReadOnlyList<PvfToken> ranks, Func<int, string> resolve)
    {
        var values = RequiredField(quest, resolve, "exp reward table");
        if (values.Length == 0 || values.Length % 2 != 0)
            throw new InvalidDataException("通关经验表必须是完整整数对");
        var experience = new uint[values.Length / 2];
        for (int i = 0; i < values.Length; i += 2)
        {
            if (values[i].Type != 0 || values[i + 1].Type != 0 || values[i].Value <= 0)
                throw new InvalidDataException("通关经验整数对的首项须为正整数");
            experience[i / 2] = (uint)values[i].Value;
        }

        decimal[] difficulty = Rates(server, resolve, "dungeon difficulty exp bonusrate", allowZero: false);
        decimal[] rank = Rates(server, resolve, "clear rank exp bonusrate", allowZero: true);
        var grades = RequiredField(ranks, resolve, "rank grade");
        if (grades.Length != 8)
            throw new InvalidDataException("通关评分表须包含八项下降阈值");
        var thresholds = new int[grades.Length];
        for (int i = 0; i < grades.Length; i++)
        {
            if (grades[i].Type != 0 || grades[i].Value < 0 || grades[i].Value > byte.MaxValue ||
                i > 0 && grades[i].Value >= thresholds[i - 1])
                throw new InvalidDataException("通关评分阈值须为严格下降的u8整数");
            thresholds[i] = grades[i].Value;
        }

        return new(experience, difficulty, rank, thresholds);
    }

    private static decimal DungeonWeight(DungeonDefinition dungeon)
    {
        decimal weight = 1;
        bool found = false;
        foreach (var section in dungeon.RawSections)
        {
            if (section.Tag != "experience increasing point")
                continue;
            if (found || section.Tokens.Count != 1)
                throw new InvalidDataException("副本通关经验权重无效或重复");
            found = true;
            var token = section.Tokens[0];
            float rawFloat = token.Type == 2 ? BitConverter.Int32BitsToSingle(token.Value) : 0;
            // 先识别有限负声明，避免极小负float转decimal变零，或极大负数转换溢出。
            if (token.Type == 0 && token.Value < 0 || token.Type == 2 && float.IsFinite(rawFloat) && rawFloat < 0)
                continue;
            decimal value = Number(token, "experience increasing point");
            if (value <= 0 && !dungeon.IsTutorial)
                throw new InvalidDataException("副本通关经验权重不能为零");
            // 与参考公式一致：缺失或负值采用中性权重，显式正值保留。
            weight = value;
        }
        return weight;
    }

    private static decimal[] Rates(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve, string tag, bool allowZero)
    {
        var values = RequiredField(tokens, resolve, tag);
        if (values.Length != 5)
            throw new InvalidDataException("通关经验倍率须包含五项: " + tag);
        var result = new decimal[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            result[i] = Number(values[i], tag);
            if (result[i] < 0 || !allowZero && result[i] == 0)
                throw new InvalidDataException("通关经验倍率不合法: " + tag);
        }
        return result;
    }

    private static decimal Number(PvfToken token, string tag)
    {
        if (token.Type == 0)
            return token.Value;
        if (token.Type != 2)
            throw new InvalidDataException("通关经验字段不是整数或浮点数: " + tag);
        float value = BitConverter.Int32BitsToSingle(token.Value);
        if (!float.IsFinite(value))
            throw new InvalidDataException("通关经验字段不是有限数: " + tag);
        try
        {
            // 避免将float32的1.30扩成double近似值后，100点基础经验向下取整成129。
            return (decimal)value;
        }
        catch (OverflowException error)
        {
            throw new InvalidDataException("通关经验字段超出计算范围: " + tag, error);
        }
    }

    private static PvfToken[] RequiredField(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve, string tag)
    {
        PvfToken[]? result = null;
        for (int i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Type != 3 || !resolve(tokens[i].Value).Equals("[" + tag + "]", StringComparison.OrdinalIgnoreCase))
                continue;
            if (result is not null)
                throw new InvalidDataException("重复通关经验字段: " + tag);
            int start = ++i;
            while (i < tokens.Count && tokens[i].Type != 3)
                i++;
            result = new PvfToken[i - start];
            for (int j = 0; j < result.Length; j++)
                result[j] = tokens[start + j];
            i--;
        }
        return result ?? throw new InvalidDataException("缺少通关经验字段: " + tag);
    }
}
