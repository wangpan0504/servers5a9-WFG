using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Items;

internal sealed record AvatarCompoundCandidate(int ItemId, int Weight);
internal sealed record AvatarCompoundPartPool(string Part, ImmutableArray<AvatarCompoundCandidate> RarePool,
    ImmutableArray<AvatarCompoundCandidate> TargetPool);
internal sealed record AvatarCompoundJobDefinition(int Job, int Grade, int UpperGrade,
    ImmutableDictionary<string, int> RareRates, ImmutableDictionary<string, int> UpperRareRates,
    int MaterialItemId, int UpperMaterialItemId, int MasterMaterialItemId,
    ImmutableDictionary<string, AvatarCompoundPartPool> Parts);

// etc/compoundavatar.etc 只列出各职业的合成配置；候选装备仍由共享 ItemCatalog 按请求惰性解析。
internal sealed class AvatarCompoundCatalog
{
    public const string RootPath = "etc/compoundavatar.etc";
    public static readonly ImmutableArray<string> PartNames =
        ["hat", "hair", "face", "neck", "coat", "pants", "belt", "shoes"];

    readonly Func<string, PvfToken[]> readTokens;
    readonly Func<int, string> resolve;
    readonly ImmutableDictionary<int, (string Path, Lazy<AvatarCompoundJobDefinition> Definition)> jobs;

    public AvatarCompoundCatalog(PvfArchive archive)
        : this(archive.ReadText, archive.ReadTokens, archive.ResolveString, archive.Contains) { }

    internal AvatarCompoundCatalog(Func<string, string> readText, Func<string, PvfToken[]> readTokens,
        Func<int, string> resolve, Func<string, bool> contains)
    {
        this.readTokens = readTokens;
        this.resolve = resolve;
        var paths = PvfScript.List(readText(RootPath));
        if (paths.Count == 0)
            throw new InvalidDataException("装扮合成职业列表为空");
        var builder = ImmutableDictionary.CreateBuilder<int, (string, Lazy<AvatarCompoundJobDefinition>)>();
        foreach (var (job, relative) in paths)
        {
            if (job <= 0 || job > byte.MaxValue)
                throw new InvalidDataException("装扮合成职业索引必须为正数: " + job);
            string path = ResolvePath(relative, contains);
            var definition = new Lazy<AvatarCompoundJobDefinition>(() => Parse(job, path,
                new PvfTokenDocument(this.readTokens(path), this.resolve), this.resolve), LazyThreadSafetyMode.ExecutionAndPublication);
            if (!builder.TryAdd(job, (path, definition)))
                throw new InvalidDataException("装扮合成职业索引重复: " + job);
        }
        jobs = builder.ToImmutable();
    }

    public AvatarCompoundJobDefinition ForCharacterJob(byte job)
    {
        int oneBased = job + 1;
        if (!jobs.TryGetValue(oneBased, out var row))
            throw new InvalidDataException("当前职业缺少装扮合成配置: " + oneBased);
        return row.Definition.Value;
    }

    internal static AvatarCompoundJobDefinition Parse(int job, string path, PvfTokenDocument document,
        Func<int, string> resolve)
    {
        if (job <= 0)
            throw new InvalidDataException("装扮合成职业索引必须为正数");
        int grade = Scalar(document, "grade", path);
        int upperGrade = Scalar(document, "upper grade", path);
        if (grade < 0 || upperGrade < 0 || grade == upperGrade)
            throw new InvalidDataException(path + " 的普通与高级品级无效");
        var rareRates = Rates(document, "rare rate", path, resolve);
        var upperRareRates = Rates(document, "upper rare rate", path, resolve);
        int material = Material(document, "material", path);
        int upperMaterial = Material(document, "material_upper", path);
        int masterMaterial = Material(document, "material_master", path);
        if (new[] { material, upperMaterial, masterMaterial }.Distinct().Count() != 3)
            throw new InvalidDataException(path + " 的三档合成材料重复");

        var parts = ImmutableDictionary.CreateBuilder<string, AvatarCompoundPartPool>(StringComparer.Ordinal);
        foreach (string part in PartNames)
        {
            var section = One(document, part + " avatar", path);
            RequireLeafBlock(section, path);
            if (section.Values.IsEmpty || section.Values.Any(token => token.Type != 0))
                throw new InvalidDataException($"{path} [{section.Name}] 必须只包含整数");
            int rareCount = section.Values[0].Value;
            int pairValues = section.Values.Length - 1;
            if (rareCount < 0 || pairValues <= 0 || pairValues % 2 != 0 || rareCount > pairValues / 2)
                throw new InvalidDataException($"{path} [{section.Name}] 的稀有数量或候选对无效");
            var candidates = ImmutableArray.CreateBuilder<AvatarCompoundCandidate>(pairValues / 2);
            var ids = new HashSet<int>();
            for (int at = 1; at < section.Values.Length; at += 2)
            {
                int itemId = section.Values[at].Value, weight = section.Values[at + 1].Value;
                if (itemId <= 0 || weight <= 0 || !ids.Add(itemId))
                    throw new InvalidDataException($"{path} [{section.Name}] 含无效或重复候选");
                candidates.Add(new(itemId, weight));
            }
            if (rareCount == candidates.Count)
                throw new InvalidDataException($"{path} [{section.Name}] 缺少普通目标候选");
            _ = candidates.Aggregate(0L, (sum, candidate) => checked(sum + candidate.Weight));
            parts.Add(part, new(part, candidates.Take(rareCount).ToImmutableArray(),
                candidates.Skip(rareCount).ToImmutableArray()));
        }
        return new(job, grade, upperGrade, rareRates, upperRareRates, material, upperMaterial, masterMaterial,
            parts.ToImmutable());
    }

    public static string? Part(byte equipmentSlot) => equipmentSlot switch
    {
        0 => "hat",
        1 => "hair",
        2 => "face",
        3 => "coat",
        4 => "pants",
        5 => "shoes",
        6 => "neck",
        7 => "belt",
        _ => null
    };

    static string ResolvePath(string relative, Func<string, bool> contains)
    {
        string normalized = PvfArchive.NormalizePath(relative);
        if (contains(normalized))
            return normalized;
        string underEtc = PvfArchive.NormalizePath("etc/" + normalized);
        if (contains(underEtc))
            return underEtc;
        throw new InvalidDataException("装扮合成职业配置不存在: " + relative);
    }

    static PvfTokenSection One(PvfTokenDocument document, string name, string path)
    {
        var matches = document.Sections.Where(section => section.Name == name).ToArray();
        if (matches.Length != 1)
            throw new InvalidDataException($"{path} 必须恰有一个根级 [{name}]");
        return matches[0];
    }

    static int Scalar(PvfTokenDocument document, string name, string path)
    {
        var section = One(document, name, path);
        if (section.Values is not [{ Type: 0 } value] || !section.Children.IsEmpty)
            throw new InvalidDataException($"{path} [{name}] 必须是单个整数");
        return value.Value;
    }

    static int Material(PvfTokenDocument document, string name, string path)
    {
        var section = One(document, name, path);
        RequireLeafBlock(section, path);
        if (section.Values is not [{ Type: 0, Value: 1 }, { Type: 0 } item, { Type: 0, Value: 1 }] ||
            item.Value <= 0)
            throw new InvalidDataException($"{path} [{name}] 必须是 1 itemId 1");
        return item.Value;
    }

    static ImmutableDictionary<string, int> Rates(PvfTokenDocument document, string name, string path,
        Func<int, string> resolve)
    {
        var section = One(document, name, path);
        RequireLeafBlock(section, path);
        if (section.Values.Length != PartNames.Length * 2)
            throw new InvalidDataException($"{path} [{name}] 必须完整声明八个部位");
        var rates = ImmutableDictionary.CreateBuilder<string, int>(StringComparer.Ordinal);
        for (int at = 0; at < section.Values.Length; at += 2)
        {
            var partToken = section.Values[at];
            var rateToken = section.Values[at + 1];
            if (partToken.Type != 6 || rateToken.Type != 0)
                throw new InvalidDataException($"{path} [{name}] 的部位概率记录无效");
            string part = NormalizePart(resolve(partToken.Value));
            int rate = rateToken.Value;
            if (!PartNames.Contains(part) || rate is < 0 or > 10000 || !rates.TryAdd(part, rate))
                throw new InvalidDataException($"{path} [{name}] 含未知、重复部位或越界概率");
        }
        if (rates.Count != PartNames.Length)
            throw new InvalidDataException($"{path} [{name}] 缺少部位概率");
        return rates.ToImmutable();
    }

    static string NormalizePart(string value)
    {
        string part = value.Trim().Trim('`').Trim().Trim('[', ']').Trim().ToLowerInvariant();
        if (part.EndsWith(" avatar", StringComparison.Ordinal))
            part = part[..^" avatar".Length].Trim();
        return part switch { "breast" => "neck", "waist" => "belt", _ => part };
    }

    static void RequireLeafBlock(PvfTokenSection section, string path)
    {
        if (!section.IsBlock || !section.Children.IsEmpty)
            throw new InvalidDataException($"{path} [{section.Name}] 必须是已闭合的根级值块");
    }
}
