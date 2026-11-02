using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Items;

internal sealed record AntiEvilUpgradeRules(ImmutableArray<float> RarityWeights,
    ImmutableArray<int> TypeWeights, int Divisor);

internal sealed class AntiEvilUpgradeCatalog
{
    readonly Lazy<AntiEvilUpgradeRules> reinforce;
    readonly Lazy<AntiEvilUpgradeRules> amplify;
    readonly Lazy<ImmutableArray<ImmutableArray<int>>> forge;
    public AntiEvilUpgradeRules Reinforce => reinforce.Value;
    public AntiEvilUpgradeRules Amplify => amplify.Value;
    public ImmutableArray<ImmutableArray<int>> Forge => forge.Value;

    public AntiEvilUpgradeCatalog(PvfArchive archive)
        : this(path => new(archive.ReadTokens(path), archive.ResolveString)) { }

    internal AntiEvilUpgradeCatalog(Func<string, PvfTokenDocument> read)
    {
        reinforce = new(() => Parse(read("etc/upgrade.etc")));
        amplify = new(() => Parse(read("etc/amplifyupgrade.etc")));
        forge = new(() => ParseForge(read("etc/upgrade_separate.etc")));
    }

    internal static AntiEvilUpgradeRules Parse(PvfTokenDocument document)
    {
        var block = One(document.Sections, "anti evil upgrade");
        if (!block.IsBlock || !block.Values.IsEmpty)
            throw new InvalidDataException("抗魔升级权重必须是嵌套声明");
        var rarity = AmplificationFiles.Numbers(One(block.Children, "rarity weights"));
        var types = AmplificationFiles.Integers(One(block.Children, "type weights"));
        var divisor = AmplificationFiles.Integers(One(block.Children, "div weights"));
        if (rarity.Length != 7 || types.Length != 6 || divisor.Length != 1 ||
            rarity.Any(value => !float.IsFinite(value)))
            throw new InvalidDataException("抗魔升级权重列数或数值无效");
        return new(rarity, types, divisor[0]);
    }

    internal static ImmutableArray<ImmutableArray<int>> ParseForge(PvfTokenDocument document)
    {
        var block = One(document.Sections, "anti evil upgrade");
        if (!block.IsBlock || !block.Children.IsEmpty)
            throw new InvalidDataException("锻造抗魔表必须是整数行块");
        var values = AmplificationFiles.Integers(block);
        if (values.Length == 0 || values.Length % 7 != 0)
            throw new InvalidDataException("锻造抗魔表必须保留完整七列");
        return values.Chunk(7).Select(row => row.ToImmutableArray()).ToImmutableArray();
    }

    internal static int ReadBase(PvfTokenDocument document) => ApplyBase(document.Sections, 0);
    internal static int ReadBase(ImmutableArray<PvfTokenSection> sections) => ApplyBase(sections, 0);

    static int ApplyBase(ImmutableArray<PvfTokenSection> sections, int result)
    {
        foreach (var section in sections)
        {
            if (section.Name is not ("anti evil" or "add anti evil")) continue;
            if (section.IsBlock || !section.Children.IsEmpty || section.Values is not [{ Type: 0 } value])
                throw new InvalidDataException("装备基础抗魔必须是根级单整数");
            // 原生loader两种标签共享608：anti evil覆盖，add anti evil在当前值上追加。
            result = section.Name == "anti evil" ? value.Value : checked(result + value.Value);
        }
        return result;
    }

    // 仅基础抗魔读取兼容已完全闭合脚本后的单个孤立字符串，不解释该字符串的业务含义。
    internal static PvfTokenDocument ReadEquipmentBaseDocument(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve)
    {
        if (tokens.Count >= 2 && tokens[^1].Type == 6 && tokens[^2].Type == 3)
        {
            string lastTag = resolve(tokens[^2].Value);
            if (lastTag.StartsWith("[/", StringComparison.Ordinal) && lastTag.EndsWith(']'))
            {
                var document = new PvfTokenDocument(tokens.Take(tokens.Count - 1).ToArray(), resolve);
                if (!document.Diagnostics.IsEmpty)
                    throw new InvalidDataException("装备抗魔尾串兼容要求前缀完整闭合且无结构诊断");
                return document;
            }
        }
        return new(tokens, resolve);
    }

    internal static int ReadEquipmentBase(Func<string, PvfTokenDocument> read, Func<int, string> resolve, string path)
    {
        var documents = ReadEquipmentChain(read, resolve, path);
        int result = 0;
        // 导入位置不影响覆盖顺序，本地add在已继承的值上追加。
        for (int i = documents.Count - 1; i >= 0; i--)
            result = ApplyBase(documents[i].Sections, result);
        return result;
    }

    internal static IReadOnlyList<PvfTokenDocument> ReadEquipmentChain(Func<string, PvfTokenDocument> read,
        Func<int, string> resolve, string path)
    {
        path = PvfArchive.NormalizePath(path);
        var chain = new HashSet<string>(StringComparer.Ordinal);
        var documents = new List<PvfTokenDocument>();
        while (true)
        {
            if (!chain.Add(path))
                throw new InvalidDataException("装备模板循环导入: " + path);
            PvfTokenDocument document;
            try { document = read(path); }
            catch (FileNotFoundException exception)
            {
                throw new InvalidDataException("装备模板文件缺失: " + path, exception);
            }
            documents.Add(document);
            var imports = document.Sections.Where(section => section.Name == "import script").ToArray();
            if (imports.Length == 0) break;
            if (imports is not [{ IsBlock: false, Children.IsEmpty: true, Values: [{ Type: 6 } reference] }] ||
                string.IsNullOrWhiteSpace(resolve(reference.Value)))
                throw new InvalidDataException("装备模板导入路径无效: " + path);
            path = PvfArchive.NormalizePath("equipment/" + resolve(reference.Value));
        }
        return documents;
    }

    static PvfTokenSection One(ImmutableArray<PvfTokenSection> sections, string name)
    {
        var matches = sections.Where(section => section.Name == name).ToArray();
        return matches.Length == 1 ? matches[0] :
            throw new InvalidDataException("抗魔升级字段缺失或重复: " + name);
    }
}
