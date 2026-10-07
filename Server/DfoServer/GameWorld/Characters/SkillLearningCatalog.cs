using System.Collections.Concurrent;
using PvfLib;

namespace DfoServer.GameWorld.Characters;

internal readonly record struct SkillPrerequisite(ushort SkillId, int Level);

internal sealed class SkillLearningDefinition
{
    private readonly IReadOnlyList<int>? costs;
    private readonly IReadOnlyList<int>? maximumLevels;
    private readonly IReadOnlyList<int>? growtypeMaximumLevels;
    private readonly int? requiredLevel;
    private readonly int? requiredLevelRange;
    private readonly bool supportedType;
    private readonly IReadOnlyList<int>? growtypeFitness;

    internal SkillLearningDefinition(byte job, ushort skillId, string path, bool supportedType,
        int[]? costs, int[]? maximumLevels, int[]? growtypeMaximumLevels, int? requiredLevel,
        int? requiredLevelRange, SkillPrerequisite[]? prerequisites, int[]? fitness, bool featureSkill, bool comboDeclared, string[] unsupported)
    {
        growtypeFitness = fitness is null ? null : Array.AsReadOnly(fitness);
        IsFeatureSkill = featureSkill;
        HasComboDeclaration = comboDeclared;
        Job = job;
        SkillId = skillId;
        Path = path;
        this.supportedType = supportedType;
        this.costs = costs is null ? null : Array.AsReadOnly(costs);
        this.maximumLevels = maximumLevels is null ? null : Array.AsReadOnly(maximumLevels);
        this.growtypeMaximumLevels = growtypeMaximumLevels is null ? null : Array.AsReadOnly(growtypeMaximumLevels);
        this.requiredLevel = requiredLevel;
        this.requiredLevelRange = requiredLevelRange;
        Prerequisites = prerequisites is null ? null : Array.AsReadOnly(prerequisites);
        UnsupportedFields = Array.AsReadOnly(unsupported);
    }

    public byte Job { get; }
    public ushort SkillId { get; }
    public string Path { get; }
    public IReadOnlyList<int>? DeclaredPointCosts => costs;
    public IReadOnlyList<int>? DeclaredMaximumLevels => maximumLevels;
    public IReadOnlyList<int>? DeclaredGrowtypeMaximumLevels => growtypeMaximumLevels;
    public IReadOnlyList<SkillPrerequisite>? Prerequisites { get; }
    public IReadOnlyList<string> UnsupportedFields { get; }
    public bool IsFeatureSkill { get; }
    public bool HasComboDeclaration { get; }
    public IReadOnlyList<int>? DeclaredGrowtypeFitness => growtypeFitness;
    // 04234A70、04234ACD 初始化 feature/combo 为零；特殊职业另有技能管理路径。
    public bool SupportsOrdinarySpLearning => supportedType && Job is not 9 and not 10 && !IsFeatureSkill &&
        !HasComboDeclaration && growtypeFitness is not null && costs is not null && maximumLevels is not null &&
        growtypeMaximumLevels is not null && requiredLevel is not null && requiredLevelRange is not null && Prerequisites is not null;

    public int GetOrdinaryResourceSpCost(int currentLearnedLevel, int growtype)
    {
        if (!SupportsOrdinarySpLearning)
            throw new NotSupportedException("资源不在已确认的普通 SP 学习范围");
        if (growtype is < 0 or > 5)
            throw new ArgumentOutOfRangeException(nameof(growtype));
        // 02D4F8E0：先按 growtype fitness 百分比截断，再减角色技能状态 +1100 并钳制零。
        // 此方法只计算资源部分；调用方仍须应用已确认的角色状态减免，不能直接当无条件最终费用。
        return checked((int)((long)GetBasePointCost(currentLearnedLevel) * (100 - growtypeFitness![growtype]) / 100));
    }

    public int? PointCostLevelCount => costs is null || maximumLevels is null ? null : Math.Max(costs.Count, maximumLevels.Max());

    public int GetBasePointCost(int currentLearnedLevel)
    {
        RequireSupportedType();
        if (costs is null || PointCostLevelCount is not int count)
            throw new NotSupportedException("缺少已确认形状的 purchase cost 或 maximum level");
        if (currentLearnedLevel < 0 || currentLearnedLevel >= count)
            throw new ArgumentOutOfRangeException(nameof(currentLearnedLevel));
        // 04241A97 补尾重复最后项；02D4F8E0 按当前已学等级索引。最终费用还经过减免。
        return costs[Math.Min(currentLearnedLevel, costs.Count - 1)];
    }

    public int GetOrdinaryNextRequiredCharacterLevel(int currentLearnedLevel)
    {
        RequireSupportedType();
        if (requiredLevel is not int basis || requiredLevelRange is not int range || maximumLevels is null)
            throw new NotSupportedException("缺少已确认形状的 required level、range 或 maximum level");
        if (currentLearnedLevel < 0 || currentLearnedLevel >= maximumLevels.Max())
            throw new ArgumentOutOfRangeException(nameof(currentLearnedLevel));
        // 02D52330 将 current+1 传给 02D4BEB0；这里只给普通资源门槛，未应用特殊状态减五等级。
        return checked(basis + currentLearnedLevel * range);
    }

    public int GetDeclaredGrowtypeMaximum(int growtype)
    {
        if (growtype is < 0 or > 5)
            throw new ArgumentOutOfRangeException(nameof(growtype));
        if (growtypeMaximumLevels is null)
            throw new NotSupportedException("缺少已确认形状的 growtype maximum level");
        // 02D4C470 的单表声明。第二转职上限采用另一个 OR 分支，不能称为最终学习上限。
        return growtypeMaximumLevels[growtype];
    }

    private void RequireSupportedType()
    {
        if (!supportedType)
            throw new NotSupportedException("技能 type 缺失或不是已确认的 active/passive 资源形状");
    }
}

internal sealed class SkillLearningCatalog
{
    private static readonly string[] Lists =
    [
        "swordmanskill.lst", "fighterskill.lst", "gunnerskill.lst", "mageskill.lst", "priestskill.lst",
        "atgunnerskill.lst", "thiefskill.lst", "atfighterskill.lst", "atmageskill.lst", "dsswordmanskill.lst",
        "creatormageskill.lst", "atswordmanskill.lst", "knight.lst", "demoniclancerskill.lst", "atpriestskill.lst",
        "gunbladerskill.lst"
    ];
    private readonly PvfArchive pvf;
    private readonly Dictionary<(byte Job, ushort SkillId), string> paths;
    private readonly ConcurrentDictionary<(byte Job, ushort SkillId), SkillLearningDefinition> definitions = new();

    private SkillLearningCatalog(PvfArchive pvf, Dictionary<(byte, ushort), string> paths)
    {
        this.pvf = pvf;
        this.paths = paths;
    }

    public static SkillLearningCatalog Load(PvfArchive pvf)
    {

        var paths = new Dictionary<(byte, ushort), string>();
        for (byte job = 0; job < Lists.Length; job++)
        {
            PvfToken[] tokens = pvf.ReadTokens("skill/" + Lists[job]);
            if (tokens.Length == 0 || tokens.Length % 2 != 0)
                throw new InvalidDataException("职业技能列表长度无效");
            for (int i = 0; i < tokens.Length; i += 2)
            {
                if (tokens[i].Type != 0 || tokens[i + 1].Type != 6 || tokens[i].Value is < 0 or > ushort.MaxValue)
                    throw new InvalidDataException("职业技能列表整数或路径类型无效");
                string path = PvfArchive.NormalizePath("skill/" + pvf.ResolveString(tokens[i + 1].Value));
                if (!path.StartsWith("skill/", StringComparison.Ordinal) || !path.EndsWith(".skl", StringComparison.Ordinal) ||
                    path.Contains("..", StringComparison.Ordinal) || !paths.TryAdd((job, (ushort)tokens[i].Value), path))
                    throw new InvalidDataException("职业技能列表路径或重复ID无效");
            }
        }

        return new SkillLearningCatalog(pvf, paths);
    }

    public SkillLearningDefinition Get(byte job, ushort skillId)
    {
        if (!paths.TryGetValue((job, skillId), out string? path))
            throw new KeyNotFoundException("职业技能列表没有指定ID");
        return definitions.GetOrAdd((job, skillId), _ => Parse(job, skillId, path, pvf.ReadTokens(path), pvf.ResolveString));
    }

    internal static SkillLearningDefinition Parse(byte job, ushort skillId, string path, PvfToken[] tokens, Func<int, string> resolve)
    {
        string[] tags = ["type", "purchase cost", "maximum level", "growtype maximum level", "required level", "required level range", "pre required skill", "growtype fitness", "feature skill type", "combo set"];
        var fields = new Dictionary<string, PvfToken[]>(StringComparer.Ordinal);
        var unsupported = new List<string>();
        for (int i = 0; i < tokens.Length; i++)
        {
            if (tokens[i].Type != 3)
                continue;
            string tag = resolve(tokens[i].Value);
            if (tag.Length < 2 || tag[0] != '[' || tag[^1] != ']' || !tags.Contains(tag[1..^1]))
                continue;
            string name = tag[1..^1];
            int start = ++i;
            while (i < tokens.Length && tokens[i].Type != 3)
                i++;
            if (!fields.TryAdd(name, tokens[start..i]))
                throw new InvalidDataException("重复技能学习标签: " + name);
            if (name is "purchase cost" or "pre required skill")
            {
                if (i >= tokens.Length || resolve(tokens[i].Value) != "[/" + name + "]")
                    throw new InvalidDataException("技能学习标签没有结束块: " + name);
            }
            i--;
        }

        bool supportedType = fields.TryGetValue("type", out PvfToken[]? kind) && kind.Length == 1 && kind[0].Type == 6 &&
            resolve(kind[0].Value) is "[active]" or "[passive]";
        if (!supportedType)
            unsupported.Add("type");
        int[]? costs = Integers("purchase cost");
        if (costs is { Length: 0 })
        {
            unsupported.Add("purchase cost");
            costs = null;
        }
        int[]? maximum = Integers("maximum level");
        if (maximum is { Length: 1 })
            maximum = Enumerable.Repeat(maximum[0], 6).ToArray();
        if (maximum is not null && (maximum.Length != 6 || maximum.Any(value => value == 0)))
        {
            unsupported.Add("maximum level");
            maximum = null;
        }
        int[]? growtype = Integers("growtype maximum level");
        if (growtype is not null && growtype.Length != 6)
        {
            unsupported.Add("growtype maximum level");
            growtype = null;
        }
        int? basis = Scalar("required level");
        int? range = Scalar("required level range");
        // 04231E73/04231E7E 清空前置 vector；标签缺失即空表。
        int[]? pairs = fields.ContainsKey("pre required skill") ? Integers("pre required skill") : [];
        // 04232130..04232194 将六个 fitness 初始化为零。
        int[]? fitness = fields.ContainsKey("growtype fitness") ? Integers("growtype fitness") : new int[6];
        if (fitness is not null && (fitness.Length != 6 || fitness.Any(value => value > 100)))
        {
            unsupported.Add("growtype fitness");
            fitness = null;
        }
        bool featureSkill = false;
        if (fields.TryGetValue("feature skill type", out PvfToken[]? feature))
        {
            if (feature.Length != 1 || feature[0].Type != 0)
                throw new InvalidDataException("feature skill type 必须是一个整数");
            // 0423CF5B..0423CF60 原生 test/setne，显式负整数也表示 true。
            featureSkill = feature[0].Value != 0;
        }
        bool comboDeclared = fields.ContainsKey("combo set");
        if (comboDeclared)
            unsupported.Add("combo set");
        SkillPrerequisite[]? prerequisites = null;
        if (pairs is not null)
        {
            if (pairs.Length % 2 != 0 || pairs.Where((_, index) => index % 2 == 0).Any(id => id > ushort.MaxValue))
                throw new InvalidDataException("技能前置整数对无效");
            prerequisites = Enumerable.Range(0, pairs.Length / 2)
                .Select(i => new SkillPrerequisite((ushort)pairs[i * 2], pairs[i * 2 + 1])).ToArray();
        }

        return new SkillLearningDefinition(job, skillId, path, supportedType, costs, maximum, growtype, basis, range,
            prerequisites, fitness, featureSkill, comboDeclared, unsupported.ToArray());

        int[]? Integers(string tag)
        {
            if (!fields.TryGetValue(tag, out PvfToken[]? values))
            {
                unsupported.Add(tag);
                return null;
            }
            if (values.Any(token => token.Type != 0 || token.Value < 0))
                throw new InvalidDataException("技能学习字段不是非负整数: " + tag);
            return values.Select(token => token.Value).ToArray();
        }

        int? Scalar(string tag)
        {
            int[]? values = Integers(tag);
            if (values is null)
                return null;
            if (values.Length == 1)
                return values[0];
            unsupported.Add(tag);
            return null;
        }
    }
}
