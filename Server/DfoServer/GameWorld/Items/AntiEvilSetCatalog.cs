using System.Collections.Concurrent;
using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Items;

internal sealed record AntiEvilSetTier(int RequiredPieces, int AntiEvil);

internal sealed class AntiEvilSetCatalog
{
    readonly Func<string, PvfTokenDocument> read;
    readonly Func<int, string> resolve;
    readonly Lazy<PvfTokenDocument> registry;
    readonly ConcurrentDictionary<int, Lazy<ImmutableArray<AntiEvilSetTier>>> definitions = new();

    public AntiEvilSetCatalog(PvfArchive archive) : this(path =>
    {
        var tokens = archive.ReadTokens(path);
        // 没有抗魔或导入声明的套装无需解释无关技能、条件块的旧格式。
        if (path != "etc/equipmentpartset.etc" && !tokens.Any(token => token.Type == 3 &&
            archive.ResolveString(token.Value) is "[anti evil]" or "[add anti evil]" or "[import script]"))
            return new([], archive.ResolveString);
        return new(tokens, archive.ResolveString);
    }, archive.ResolveString) { }

    internal AntiEvilSetCatalog(Func<string, PvfTokenDocument> read, Func<int, string> resolve)
    {
        this.read = read;
        this.resolve = resolve;
        registry = new(() => read("etc/equipmentpartset.etc"));
    }

    public ImmutableArray<AntiEvilSetTier> Get(int setId) => definitions.GetOrAdd(setId,
        id => new(() => Read(id))).Value;

    ImmutableArray<AntiEvilSetTier> Read(int setId)
    {
        var entries = registry.Value.Sections.Where(row => row.Name == "equipment part set" &&
            !row.Values.IsEmpty && row.Values[0].Type == 0 && row.Values[0].Value == setId).ToArray();
        if (setId <= 0 || entries is not [{ IsBlock: true } entry] ||
            entry.Values.Length < 3 || entry.Values[1].Type != 6)
            throw new InvalidDataException("抗魔套装目录缺失或重复");
        string path = resolve(entry.Values[1].Value);
        if (!path.StartsWith("equipment/", StringComparison.OrdinalIgnoreCase)) path = "equipment/" + path;
        var chain = AntiEvilUpgradeCatalog.ReadEquipmentChain(read, resolve, path);
        foreach (var document in chain)
        {
            var tiers = document.Sections.Where(row => row.Name == "piece set ability").ToArray();
            if (tiers.Length == 0) continue;
            var result = ImmutableArray.CreateBuilder<AntiEvilSetTier>();
            foreach (var tier in tiers)
            {
                if (!tier.Children.Any(row => row.Name is "anti evil" or "add anti evil")) continue;
                if (!tier.IsBlock || tier.Values is not [{ Type: 0, Value: > 0 } threshold])
                    throw new InvalidDataException("抗魔套装阶梯件数无效");
                result.Add(new(threshold.Value, AntiEvilUpgradeCatalog.ReadBase(tier.Children)));
            }
            if (result.Select(tier => tier.RequiredPieces).Distinct().Count() != result.Count)
                throw new InvalidDataException("抗魔套装阶梯重复");
            return result.ToImmutable();
        }
        return [];
    }

    // 原生每次声明先清空唯一键集合；只解析当前生效字段，保留有符号成员。
    internal static ImmutableArray<int> ReadMemberSetIds(PvfDocument document)
    {
        string? declaration = document.Sections("part set index").LastOrDefault();
        if (declaration is null) return [];
        var result = ImmutableArray.CreateBuilder<int>();
        foreach (string token in declaration.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(token, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out int id))
                throw new InvalidDataException("装备套装成员列表必须是完整有符号整数");
            result.Add(id);
        }
        return result.Distinct().Order().ToImmutableArray();
    }
}
