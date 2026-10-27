using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Items;

internal sealed record EnchantmentInvestmentMetadata(bool Known, bool ValidIndex, string? Error)
{
    public ImmutableHashSet<(ItemKind Kind, byte Slot)> Targets { get; init; } = [];
}
internal sealed record EquipmentSetInvestmentMetadata(int SetId, ImmutableArray<int> Thresholds);

// 仅确认建设来源与套装件数，不解释伤害词条或把附魔索引当效果强度。
internal sealed class CharacterInvestmentMetadataCatalog
{
    readonly Func<string, PvfTokenDocument> read;
    readonly Func<int, string> resolve;
    readonly IReadOnlyDictionary<int, string> equipment, stackable;
    readonly Lazy<ImmutableDictionary<int, ImmutableArray<PvfTokenSection>>> sets;
    readonly Lazy<PvfTokenDocument> randomTitles;

    public CharacterInvestmentMetadataCatalog(PvfArchive archive, IReadOnlyDictionary<int, string> equipment,
        IReadOnlyDictionary<int, string> stackable)
        : this(path => new PvfTokenDocument(archive.ReadTokens(path), archive.ResolveString),
            archive.ResolveString, equipment, stackable) { }

    internal CharacterInvestmentMetadataCatalog(Func<string, PvfTokenDocument> read, Func<int, string> resolve,
        IReadOnlyDictionary<int, string> equipment, IReadOnlyDictionary<int, string> stackable)
    {
        this.read = read;
        this.resolve = resolve;
        this.equipment = equipment;
        this.stackable = stackable;
        sets = new(ReadSets);
        randomTitles = new(() => Document("etc/randomtitleenchant.etc"));
    }

    public EnchantmentInvestmentMetadata ReadEnchantment(int cardId, byte index)
    {
        if (cardId <= 0 || !stackable.TryGetValue(cardId, out var path))
            return new(false, false, "unknown-card");
        var document = Document(Path("stackable", path));
        if (Roots(document, "import script").Length != 0)
            throw new InvalidDataException("建设附魔卡不支持脚本导入");
        var type = One(document, "stackable type");
        if (type is null || type.IsBlock || type.Values.Length != 2 || type.Values[0].Type != 6 ||
            resolve(type.Values[0].Value) != "[material expert job]" || type.Values[1].Type != 0 || type.Values[1].Value != 1)
            return new(false, false, "unsupported-card-type");
        var strings = One(document, "string data");
        if (strings is null || !strings.IsBlock || !strings.Children.IsEmpty || strings.Values.Length < 2 ||
            strings.Values.Any(token => token.Type != 6))
            throw new InvalidDataException("建设附魔卡缺少完整目标字符串表");
        var targets = ImmutableHashSet.CreateBuilder<(ItemKind Kind, byte Slot)>();
        // 首项为说明标题，后续项才是适用部位；不从说明文字猜目标。
        foreach (var token in strings.Values.Skip(1))
        {
            string target = resolve(token.Value);
            if (target.Length < 3 || target[0] != '[' || target[^1] != ']')
                throw new InvalidDataException("建设附魔卡目标不是完整部位标签");
            byte slot = ItemCatalog.Slot(target[1..^1]);
            ItemKind kind = slot switch
            {
                <= 11 => ItemKind.Avatar,
                <= 25 or 30 => ItemKind.Equipment,
                26 => ItemKind.Creature,
                >= 27 and <= 29 => ItemKind.CreatureEquipment,
                32 => ItemKind.GuildMedal,
                34 or 35 => ItemKind.Talisman,
                _ => throw new InvalidDataException("建设附魔卡目标部位未支持")
            };
            if (!targets.Add((kind, slot)))
                throw new InvalidDataException("建设附魔卡目标重复");
        }
        var table = One(document, "enchant table");
        if (table is null || !table.IsBlock || !table.Values.IsEmpty || table.Children.IsEmpty)
            throw new InvalidDataException("建设附魔卡效果表缺失或结构无效");
        var indices = new HashSet<int>();
        foreach (var entry in table.Children)
        {
            if (entry.Name != "enchant index" || !entry.IsBlock || entry.Values.Length != 1 ||
                entry.Values[0].Type != 0 || entry.Values[0].Value is < 0 or > byte.MaxValue ||
                !indices.Add(entry.Values[0].Value) || entry.Children.IsEmpty)
                throw new InvalidDataException("建设附魔索引缺失、重复或结构无效");
            // 复用已支持附魔族的完整整数向量边界；未知效果形状不冒充合法安装。
            if (entry.Children.Select(row => row.Name).Distinct(StringComparer.Ordinal).Count() != entry.Children.Length ||
                entry.Children.Any(row => row.IsBlock || !row.Children.IsEmpty || row.Values.IsEmpty ||
                    row.Values.Any(token => token.Type != 0)))
                throw new InvalidDataException("建设附魔效果不是唯一完整整数向量");
        }
        return new(true, indices.Contains(index), indices.Contains(index) ? null : "unknown-enchant-index")
        {
            Targets = targets.ToImmutable()
        };
    }

    public bool ValidateRandomTitle(uint value)
    {
        int poolId = (ushort)value, entryId = (ushort)(value >> 16);
        if (poolId == 0 || entryId == 0) return false;
        // 已有解析器验证技能向量和权重，并保留客户端首次同序号声明语义。
        var document = randomTitles.Value;
        _ = Roots(document, "index");
        if (Roots(document, "import script").Length != 0)
            throw new InvalidDataException("建设随机称号附魔不支持脚本导入");
        var pool = RandomTitleEnchantmentPool.Parse(document, poolId);
        return pool.Entries.Any(entry => entry.Index == entryId);
    }

    public EquipmentSetInvestmentMetadata? ReadSet(int itemId)
    {
        if (!equipment.TryGetValue(itemId, out var path))
            throw new InvalidDataException("建设套装的装备模板不存在");
        var chain = EquipmentChain(Path("equipment", path));
        var declaration = Effective(chain, "part set index");
        if (declaration.Length == 0) return null;
        if (declaration.Length != 1 || declaration[0].IsBlock || declaration[0].Values.Length != 1 ||
            declaration[0].Values[0].Type != 0 || declaration[0].Values[0].Value <= 0)
            throw new InvalidDataException("建设套装编号必须是唯一正整数");
        int id = declaration[0].Values[0].Value;
        string setPath = SetPath(id);
        var tiers = Effective(EquipmentChain(setPath), "piece set ability");
        if (tiers.Length == 0)
            throw new InvalidDataException("建设套装缺少件数阶梯");
        var thresholds = new HashSet<int>();
        foreach (var tier in tiers)
        {
            if (!tier.IsBlock || tier.Values.Length != 1 || tier.Values[0].Type != 0 ||
                tier.Values[0].Value <= 0 || tier.Children.IsEmpty || !thresholds.Add(tier.Values[0].Value))
                throw new InvalidDataException("建设套装阶梯必须是唯一正件数及完整效果块");
        }
        return new(id, thresholds.Order().ToImmutableArray());
    }

    ImmutableDictionary<int, ImmutableArray<PvfTokenSection>> ReadSets()
    {
        var document = Document("etc/equipmentpartset.etc");
        if (document.Sections.Any(row => row.Name != "equipment part set"))
            throw new InvalidDataException("建设套装目录存在未知根声明");
        var result = new Dictionary<int, List<PvfTokenSection>>();
        foreach (var row in Roots(document, "equipment part set"))
        {
            // 先保证能可靠分组。无关套装的重复/坏成员不污染当前请求，但破坏闭合或无法识别编号仍拒绝。
            if (!row.IsBlock || row.Values.IsEmpty || row.Values[0].Type != 0 || row.Values[0].Value <= 0)
                throw new InvalidDataException("建设套装目录缺少可识别正编号或完整块");
            int id = row.Values[0].Value;
            if (!result.TryGetValue(id, out var entries)) result.Add(id, entries = []);
            entries.Add(row);
        }
        if (result.Count == 0) throw new InvalidDataException("建设套装目录为空");
        return result.ToImmutableDictionary(row => row.Key, row => row.Value.ToImmutableArray());
    }

    string SetPath(int id)
    {
        if (!sets.Value.TryGetValue(id, out var entries) || entries.Length != 1)
            throw new InvalidDataException("建设套装编号缺失或重复");
        var row = entries[0];
        // 当前资源是闭合块：编号/路径/名称，随后每成员为名称、部位标签、两个原始整数。
        // 成员名称与尾整数不参与评分；成员归属仍取实际装备的 part set index。
        if (!row.Children.IsEmpty || row.Values.Length < 7 || (row.Values.Length - 3) % 4 != 0 ||
            row.Values[1].Type != 6 || row.Values[2].Type != 6)
            throw new InvalidDataException("建设套装目标条目结构无效");
        for (int at = 3; at < row.Values.Length; at += 4)
        {
            if (row.Values[at].Type != 6 || row.Values[at + 1].Type != 6 ||
                row.Values[at + 2].Type != 0 || row.Values[at + 3].Type != 0)
                throw new InvalidDataException("建设套装成员必须保留完整四元向量");
            string slot = resolve(row.Values[at + 1].Value);
            if (slot.Length < 3 || slot[0] != '[' || slot[^1] != ']')
                throw new InvalidDataException("建设套装成员部位标签无效");
            _ = ItemCatalog.Slot(slot[1..^1]);
        }
        return Path("equipment", resolve(row.Values[1].Value));
    }

    // 子声明（包括空声明）覆盖父层；遍历全链校验，不能因子层已有值而漏过坏导入。
    List<PvfTokenDocument> EquipmentChain(string path)
    {
        var chain = new List<PvfTokenDocument>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            if (chain.Count >= 32 || !visited.Add(path))
                throw new InvalidDataException("建设装备导入循环或超过32层");
            var document = Document(path);
            chain.Add(document);
            var import = One(document, "import script");
            if (import is null) return chain;
            if (import.IsBlock || import.Values.Length != 1 || import.Values[0].Type != 6)
                throw new InvalidDataException("建设装备导入必须是唯一字符串路径");
            path = Path("equipment", resolve(import.Values[0].Value));
        }
    }

    PvfTokenDocument Document(string path)
    {
        PvfTokenDocument document;
        try { document = read(path); }
        catch (FileNotFoundException error) { throw new InvalidDataException("建设元数据脚本缺失: " + path, error); }
        if (!document.Diagnostics.IsEmpty)
            throw new InvalidDataException("建设元数据脚本结构存在诊断: " + path);
        return document;
    }

    static PvfTokenSection? One(PvfTokenDocument document, string name)
    {
        var rows = Roots(document, name);
        if (rows.Length > 1) throw new InvalidDataException("建设元数据字段重复: " + name);
        return rows.Length == 0 ? null : rows[0];
    }

    static PvfTokenSection[] Effective(IEnumerable<PvfTokenDocument> chain, string name)
    {
        PvfTokenSection[]? effective = null;
        foreach (var document in chain)
        {
            var rows = Roots(document, name);
            if (effective is null && rows.Length != 0) effective = rows;
        }
        return effective ?? [];
    }

    static PvfTokenSection[] Roots(PvfTokenDocument document, string name)
    {
        if (document.Sections.Any(row => Nested(row.Children, name)))
            throw new InvalidDataException("建设元数据字段不得嵌套: " + name);
        return document.Sections.Where(row => row.Name == name).ToArray();
        static bool Nested(ImmutableArray<PvfTokenSection> rows, string name) =>
            rows.Any(row => row.Name == name || Nested(row.Children, name));
    }

    static string Path(string root, string relative)
    {
        relative = relative.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(relative) || relative.StartsWith('/') || relative.Contains(':') ||
            relative.Split('/').Any(part => part is "" or "." or ".."))
            throw new InvalidDataException("建设元数据引用路径无效");
        return PvfArchive.NormalizePath(root + "/" + relative);
    }
}
