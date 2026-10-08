using System.Collections.Concurrent;
using PvfLib;

namespace DfoServer.GameWorld.Characters;

internal readonly record struct SkillPrerequisite(ushort SkillId, int Level);

internal enum SkillType
{
    Unknown,
    Active,
    Passive
}

internal sealed class SkillLearningDefinition
{
    private readonly IReadOnlyList<int>? costs;
    private readonly IReadOnlyList<int>? maximumLevels;
    private readonly IReadOnlyList<int>? growtypeMaximumLevels;
    private readonly int? requiredLevel;
    private readonly int? requiredLevelRange;
    private readonly IReadOnlyList<int>? growtypeFitness;

    internal SkillLearningDefinition(byte job, ushort skillId, string path, SkillType type,
        int[]? costs, int[]? maximumLevels, int[]? growtypeMaximumLevels, int? requiredLevel,
        int? requiredLevelRange, SkillPrerequisite[]? prerequisites, int[]? fitness, bool featureSkill, bool comboDeclared,
        string[] unsupported, int? skillClass = null, int? skillFitnessGrowtypeCount = null,
        int[]? secondGrowtypeMaximumLevels = null, FixedSkillRule? fixedLevel = null,
        int[]? skillFitnessGrowtypes = null, int[]? skillFitnessSecondGrowtypes = null,
        bool guildSkill = false, bool expertSkill = false, int[]? tpCosts = null, ushort[]? comboSkills = null, bool commandCustomizing = true)
    {
        growtypeFitness = fitness is null ? null : Array.AsReadOnly(fitness);
        IsFeatureSkill = featureSkill;
        HasComboDeclaration = comboDeclared;
        Job = job;
        SkillId = skillId;
        Path = path;
        Type = type;
        SkillClass = skillClass;
        SkillFitnessGrowtypeCount = skillFitnessGrowtypeCount;
        SecondGrowtypeMaximumLevels = Array.AsReadOnly(secondGrowtypeMaximumLevels ?? new int[12]);
        FixedLevel = fixedLevel;
        SkillFitnessGrowtypes = Array.AsReadOnly(skillFitnessGrowtypes ?? []);
        SkillFitnessSecondGrowtypes = Array.AsReadOnly(skillFitnessSecondGrowtypes ?? []);
        IsGuildSkill = guildSkill;
        IsExpertSkill = expertSkill;
        TpCosts = Array.AsReadOnly(tpCosts ?? []);
        ComboSkills = Array.AsReadOnly(comboSkills ?? []);
        CommandCustomizing = commandCustomizing;
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
    public SkillType Type { get; }
    public int? SkillClass { get; }
    public int? SkillFitnessGrowtypeCount { get; }
    // 分组元数据与 growtype fitness 费用折扣分别读取；缺少声明不猜普通被动组。
    public int? SlotGroup => Type switch
    {
        SkillType.Active => 3,
        SkillType.Passive when SkillClass == 4 => 2,
        SkillType.Passive when SkillClass is >= 0 and <= 3 && SkillFitnessGrowtypeCount is int count => count <= 2 ? 1 : 0,
        // S4A21 的分组越界回退到普通被动组；V31 第五转职资源使用 class=5。
        SkillType.Passive when SkillClass is >= 5 => 0,
        _ => null
    };
    public IReadOnlyList<int>? DeclaredPointCosts => costs;
    public IReadOnlyList<int>? DeclaredMaximumLevels => maximumLevels;
    public IReadOnlyList<int>? DeclaredGrowtypeMaximumLevels => growtypeMaximumLevels;
    public IReadOnlyList<SkillPrerequisite>? Prerequisites { get; }
    public IReadOnlyList<string> UnsupportedFields { get; }
    public IReadOnlyList<int> TpCosts { get; }
    public IReadOnlyList<ushort> ComboSkills { get; }
    public bool CommandCustomizing { get; }
    public bool IsFeatureSkill { get; }
    public bool HasComboDeclaration { get; }
    public bool IsGuildSkill { get; }
    public bool IsExpertSkill { get; }
    public IReadOnlyList<int>? DeclaredGrowtypeFitness => growtypeFitness;
    public IReadOnlyList<int> SecondGrowtypeMaximumLevels { get; }
    public FixedSkillRule? FixedLevel { get; }
    public IReadOnlyList<int> SkillFitnessGrowtypes { get; }
    public IReadOnlyList<int> SkillFitnessSecondGrowtypes { get; }
    public int GetMaximum(byte growth)
    {
        int first = growth & 15, awakening = growth >> 4;
        if (first > 5 || awakening > 2 || first == 0 && awakening != 0 && Job is not (9 or 10))
            throw new NotSupportedException("职业成长索引不在普通技能范围");
        int second = awakening == 0 ? 0 : SecondGrowtypeMaximumLevels[first * 2 + awakening - 1];
        return Math.Max(growtypeMaximumLevels?[first] ?? 0, second);
    }
    public int GetFixedLevel(int characterLevel, byte growth, int contractLevels)
    {
        if (IsGuildSkill || IsExpertSkill || IsFeatureSkill || HasComboDeclaration || FixedLevel is not { } rule || requiredLevel is not int required || maximumLevels is null ||
            characterLevel < required || GetMaximum(growth) == 0 ||
            !(SkillFitnessGrowtypes.Contains(growth & 15) || SkillFitnessSecondGrowtypes.Any(value => (growth >> 4) >= value)))
            return 0;
        int level = (1 + (characterLevel - required) / rule.Interval) * rule.Add;
        if (rule.AppliesContract)
            level += contractLevels / rule.Interval;
        return Math.Min(level, maximumLevels[growth & 15]);
    }
    // 04234A70、04234ACD 初始化 feature/combo 为零；特殊职业另有技能管理路径。
    public bool SupportsOrdinarySpLearning => Type is SkillType.Active or SkillType.Passive &&
        !IsFeatureSkill &&
        !HasComboDeclaration && !IsGuildSkill && !IsExpertSkill && growtypeFitness is not null && costs is not null && maximumLevels is not null &&
        (growtypeMaximumLevels is not null || SecondGrowtypeMaximumLevels.Any(value => value > 0)) &&
        requiredLevel is not null && requiredLevelRange is not null && Prerequisites is not null;

    public bool SupportsTpLearning => IsFeatureSkill && !IsGuildSkill && !HasComboDeclaration &&
        Type is SkillType.Active or SkillType.Passive && TpCosts.Count > 0 && maximumLevels is not null &&
        growtypeMaximumLevels is not null && requiredLevel is not null && requiredLevelRange is not null && Prerequisites is not null;
    public bool SupportsPointLearning => SupportsOrdinarySpLearning || SupportsTpLearning;
    public int GetPointCost(int rank, byte growth) => IsFeatureSkill
        ? SupportsTpLearning ? TpCosts[Math.Min(rank, TpCosts.Count - 1)] : throw new NotSupportedException("TP费用缺失")
        : GetOrdinaryResourceSpCost(rank, growth & 15);

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
            throw new NotSupportedException("缺少已确认形状的 growtype maximum level: " + Path);
        // 02D4C470 的单表声明。第二转职上限采用另一个 OR 分支，不能称为最终学习上限。
        return growtypeMaximumLevels[growtype];
    }

    private void RequireSupportedType()
    {
        if (Type is not (SkillType.Active or SkillType.Passive))
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
    private readonly ConcurrentDictionary<byte, IReadOnlyList<SkillLearningDefinition>> fixedSkills = new();
    private SkillGrantCatalog grants = null!;
    private SkillCatalog points = null!;
    public IReadOnlyList<CharacterSkill> FreeSkills(byte job, byte growth) =>
        Array.AsReadOnly(grants.Get(job, growth).Where(skill => paths.ContainsKey((job, skill.Id))).ToArray());
    public int TotalSp(byte level) => points.GetLevelSpTotal(level);
    public int TotalTp(byte level) => points.GetLevelTpTotal(level);
    public IReadOnlyList<SkillLearningDefinition> FixedSkills(byte job) => fixedSkills.GetOrAdd(job, value =>
    {
        var result = new List<SkillLearningDefinition>();
        foreach (var entry in paths.Where(entry => entry.Key.Job == value))
        {
            var tokens = pvf.ReadTokens(entry.Value);
            if (tokens.Any(token => token.Type == 3 && pvf.ResolveString(token.Value) == "[fixed level skill]"))
                result.Add(Get(value, entry.Key.SkillId));
        }
        return result.AsReadOnly();
    });

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

        var catalog = new SkillLearningCatalog(pvf, paths) { grants = SkillGrantCatalog.Load(pvf), points = SkillCatalog.Load(pvf) };
        // 成长提交会引用赠技；在装配阶段解码，SQLite writer 内只访问已缓存的定义。
        foreach (var key in catalog.grants.SkillIds)
            if (paths.ContainsKey((key.Job, key.Id))) catalog.Get(key.Job, key.Id);
        for (byte job = 0; job < Lists.Length; job++)
            catalog.FixedSkills(job);
        return catalog;
    }

    public SkillLearningDefinition Get(byte job, ushort skillId)
    {
        if (!paths.TryGetValue((job, skillId), out string? path))
            throw new KeyNotFoundException($"职业技能列表没有指定ID: {job}/{skillId}");
        return definitions.GetOrAdd((job, skillId), _ => Parse(job, skillId, path, pvf.ReadTokens(path), pvf.ResolveString));
    }

    internal static SkillLearningDefinition Parse(byte job, ushort skillId, string path, PvfToken[] tokens, Func<int, string> resolve)
    {
        string[] tags = ["type", "skill class", "skill fitness growtype", "purchase cost", "maximum level",
            "growtype maximum level", "required level", "required level range", "pre required skill", "growtype fitness",
            "feature skill type", "combo set", "second growtype maximum level", "fixed level skill",
            "interval level", "add level per interval", "is apply contract skillmaster", "skill fitness second growtype", "guild skill", "expert skill", "special purchase cost", "command customizing"];
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
            {
                // 部分资源重复声明分组元数据；含义未闭合，只禁猜新被动组，不影响原有学习字段。
                if (name is "skill class" or "skill fitness growtype")
                    unsupported.Add(name);
                else
                    throw new InvalidDataException("重复技能学习标签: " + name);
            }
            if (name is "purchase cost" or "special purchase cost" or "pre required skill" or "combo set")
            {
                if (i >= tokens.Length || resolve(tokens[i].Value) != "[/" + name + "]")
                    throw new InvalidDataException("技能学习标签没有结束块: " + name);
            }
            i--;
        }

        SkillType type = fields.TryGetValue("type", out PvfToken[]? kind) && kind.Length == 1 && kind[0].Type == 6
            ? resolve(kind[0].Value) switch { "[active]" => SkillType.Active, "[passive]" => SkillType.Passive, _ => SkillType.Unknown }
            : SkillType.Unknown;
        if (type == SkillType.Unknown)
            unsupported.Add("type");
        int? skillClass = fields.ContainsKey("skill class") && !unsupported.Contains("skill class") ? Scalar("skill class") : null;
        int? skillFitnessGrowtypeCount = unsupported.Contains("skill fitness growtype") ? null :
            fields.ContainsKey("skill fitness growtype") ? Integers("skill fitness growtype")?.Length : 0;
        int[]? skillFitness = fields.ContainsKey("skill fitness growtype") && !unsupported.Contains("skill fitness growtype")
            ? Integers("skill fitness growtype") : [];
        int[]? secondFitness = fields.ContainsKey("skill fitness second growtype") ? Integers("skill fitness second growtype") : [];
        int[]? costs = Integers("purchase cost");
        if (costs is { Length: 0 })
        {
            unsupported.Add("purchase cost");
            costs = null;
        }
        int[]? maximum = Integers("maximum level");
        if (maximum is { Length: 1 })
            maximum = Enumerable.Repeat(maximum[0], 6).ToArray();
        if (maximum is not null && maximum.Length != 6)
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
        int[] combo = comboDeclared ? Integers("combo set")! : [];
        if (combo.Any(id => id > ushort.MaxValue) || combo.Length > 6) throw new InvalidDataException("连招默认子技能非法");
        SkillPrerequisite[]? prerequisites = null;
        if (pairs is not null)
        {
            if (pairs.Length % 2 != 0 || pairs.Where((_, index) => index % 2 == 0).Any(id => id > ushort.MaxValue))
                throw new InvalidDataException("技能前置整数对无效");
            prerequisites = Enumerable.Range(0, pairs.Length / 2)
                .Select(i => new SkillPrerequisite((ushort)pairs[i * 2], pairs[i * 2 + 1])).ToArray();
        }

        int[]? second = fields.ContainsKey("second growtype maximum level") ? Integers("second growtype maximum level") : new int[12];
        if (second is null || second.Length > 12 || second.Length % 2 != 0)
            throw new InvalidDataException("第二成长上限必须为最多六组整数对: " + path);
        // 枪剑士资源只声明五组，未使用的第六职业保持零。
        if (second.Length < 12) Array.Resize(ref second, 12);
        FixedSkillRule? fixedLevel = null;
        if (fields.ContainsKey("fixed level skill"))
        {
            int interval = Scalar("interval level") ?? 0, add = Scalar("add level per interval") ?? 0;
            int apply = Scalar("is apply contract skillmaster") ?? 0;
            if (interval <= 0 || add <= 0 || apply is not (0 or 1))
                throw new InvalidDataException("固定等级技能参数非法");
            fixedLevel = new(interval, add, apply == 1);
        }
        return new SkillLearningDefinition(job, skillId, path, type, costs, maximum, growtype, basis, range,
            prerequisites, fitness, featureSkill, comboDeclared, unsupported.ToArray(), skillClass, skillFitnessGrowtypeCount, second, fixedLevel,
            skillFitness, secondFitness, fields.ContainsKey("guild skill"), fields.ContainsKey("expert skill"),
            fields.ContainsKey("special purchase cost") ? Integers("special purchase cost") : null,
            combo.Select(id => (ushort)id).ToArray(), !fields.ContainsKey("command customizing") || Scalar("command customizing") != 0);

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
