using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;
using PvfLib;

namespace DfoServer.GameWorld.Items;

internal sealed record EnchantmentDefinition(int BeadItemId, int CardItemId, byte Upgrade,
    ImmutableHashSet<(ItemKind Kind, byte Slot)> Targets, ImmutableHashSet<int> LimitedItems)
{
    public RandomTitleEnchantmentPool? RandomTitle { get; init; }
    public RandomCardEnchantmentPool? RandomCards { get; init; }
    public bool VillageOnly { get; init; }
}
internal sealed record RandomCardEnchantmentPool(ImmutableArray<int> Positions)
{
    public int Draw(Func<int, int> next)
    {
        int position = next(Positions.Length);
        if (position < 0 || position >= Positions.Length)
            throw new InvalidDataException("随机卡片附魔抽样越界");
        return Positions[position];
    }
}
internal sealed record EnchantmentEffect(string Tag, ImmutableArray<int> IntegerValues);

// 宝珠与卡片共享当前PVF的STK目录；只解析实际使用的依赖，不扫描所有卡片。
internal sealed partial class EnchantmentCatalog
{
    readonly Func<string, string> read;
    readonly IReadOnlyDictionary<int, string> stackable;
    readonly Func<string, PvfTokenDocument>? readDocument;
    readonly Lazy<PvfTokenDocument> randomTitles;
    readonly ConcurrentDictionary<int, Lazy<EnchantmentDefinition?>> definitions = new();

    public EnchantmentCatalog(PvfArchive archive, IReadOnlyDictionary<int, string> stackable)
        : this(archive.ReadText, stackable, path => new(archive.ReadTokens(path), archive.ResolveString)) { }

    internal EnchantmentCatalog(Func<string, string> read, IReadOnlyDictionary<int, string> stackable,
        Func<string, PvfTokenDocument>? readDocument = null)
    {
        this.read = read;
        this.stackable = stackable;
        this.readDocument = readDocument;
        randomTitles = new(() => readDocument?.Invoke("etc/randomtitleenchant.etc") ??
            throw new InvalidDataException("随机称号附魔缺少token读取器"));
    }

    // 仅供显式属性对族校验完整效果；普通Get及CMD272不附加本族规则。
    public ImmutableArray<EnchantmentEffect> ReadEffects(int cardId, byte index)
    {
        if (readDocument is null || !stackable.TryGetValue(cardId, out string? path))
            throw new InvalidDataException("属性对卡片缺少token读取器或路径");
        var document = readDocument("stackable/" + path);
        if (!document.Diagnostics.IsEmpty)
            throw new InvalidDataException("属性对卡片块结构无效");
        var tables = document.Sections.Where(row => row.Name == "enchant table").ToArray();
        if (tables.Length != 1 || !tables[0].IsBlock || !tables[0].Values.IsEmpty ||
            tables[0].Children.Any(row => row.Name != "enchant index" || !row.IsBlock ||
                row.Values.Length != 1 || row.Values[0].Type != 0) ||
            tables[0].Children.Select(row => row.Values[0].Value).Distinct().Count() != tables[0].Children.Length)
            throw new InvalidDataException("属性对卡片效果表无效");
        var entries = tables[0].Children.Where(row => row.Values[0].Value == index).ToArray();
        if (entries.Length != 1 || entries[0].Children.IsEmpty)
            throw new InvalidDataException("属性对卡片效果索引缺失或重复");
        return entries[0].Children.Select(row =>
        {
            if (row.IsBlock || !row.Children.IsEmpty || row.Values.IsEmpty || row.Values.Any(token => token.Type != 0))
                throw new InvalidDataException("属性对卡片效果必须保留完整整数向量");
            return new EnchantmentEffect("[" + row.Name + "]", row.Values.Select(token => token.Value).ToImmutableArray());
        }).ToImmutableArray();
    }

    public EnchantmentDefinition? Get(int itemId)
    {
        var pending = definitions.GetOrAdd(itemId, id => new(() => Load(id)));
        try { return pending.Value; }
        catch
        {
            definitions.TryRemove(new KeyValuePair<int, Lazy<EnchantmentDefinition?>>(itemId, pending));
            throw;
        }
    }

    EnchantmentDefinition? Load(int itemId)
    {
        if (!stackable.TryGetValue(itemId, out var path)) return null;
        string bead = read("stackable/" + path);
        var typeFields = Fields(bead, "stackable type");
        if (typeFields.Count != 1) return null;
        bool randomTitle = Regex.IsMatch(RawValues(bead, typeFields[0]), @"^\s*`\[auto enchant\]`\s+0\s*$");
        if (!randomTitle && !Regex.IsMatch(RawValues(bead, typeFields[0]), @"^\s*`\[enchant waste\]`\s+0\s*$"))
            return null; // 普通卡片、收集箱使用和其他券入口不在CMD272消费。
        foreach (string field in new[] { "cooltime maintenance", "effect maintenance", "total usable count", "total weekly usable count",
            "total accumulated usable count", "use count limit all server item", "usable datetime", "usable first growtype",
            "action usable server type", "usable game server type", "usable server type", "check usable itemlevel" })
            if (Fields(bead, field).Count != 0) return null; // 额外资格和次数账本尚未闭合，不能只消费普通源。
        var restrictions = Fields(bead, "bead limited usable item");
        if (restrictions.Count > 1) throw new InvalidDataException("宝珠适用物品清单重复");
        var restricted = ImmutableHashSet.CreateBuilder<int>();
        if (restrictions.Count == 1)
            foreach (string token in RawValues(bead, restrictions[0]).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) || id <= 0)
                    throw new InvalidDataException("宝珠适用物品清单无效");
                restricted.Add(id);
            }
        if (Fields(bead, "target item id").Count != 0)
            return null; // 尚未核对的旧式限制不能被忽略后扩大目标范围。
        if (randomTitle) return LoadRandomTitle(itemId, bead, restricted.ToImmutable());
        var cardFields = Fields(bead, "monster card id");
        if (cardFields.Count != 1) throw new InvalidDataException("宝珠字段无效: monster card id");
        var cardIds = ImmutableArray.CreateBuilder<int>();
        foreach (string token in RawValues(bead, cardFields[0]).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out int cardId) || cardId <= 0)
                throw new InvalidDataException("宝珠字段无效: monster card id");
            cardIds.Add(cardId);
        }
        if (cardIds.Count == 0) throw new InvalidDataException("宝珠字段无效: monster card id");
        bool villageOnly = false;
        if (cardIds.Count > 1)
        {
            if (Fields(bead, "usable expired item").Count != 0 ||
                !TryRootBlock(bead, "action usable place", out string? places) ||
                !TryRootBlock(bead, "item category", out string? category) ||
                places is not null && !Regex.IsMatch(places, @"^\s*`\[village\]`(?:\s+`\[seria room\]`)?\s*$") ||
                category is not null && !Regex.IsMatch(category, @"^\s*`event`\s*$"))
                return null; // 未证过期标记、地点或分类不能因普通卡片可解析而放行。
            villageOnly = places is not null;
            // need material 属于模板的NPC购买价，使用宝珠不再次收费。
        }
        int index = Scalar(bead, "enchant index", optional: true);
        if (index is < 0 or > byte.MaxValue) return null;
        ImmutableHashSet<(ItemKind Kind, byte Slot)>? targets = null;
        // 重复位置就是权重；只复用候选校验，不去重实际抽样列表。
        foreach (int cardId in cardIds.Distinct())
        {
            var allowed = CardTargets(cardId, index);
            if (allowed is null) return null;
            targets = targets is null ? allowed : targets.Intersect(allowed);
        }
        if (targets is null || targets.IsEmpty) return null;
        return new(itemId, cardIds.Count == 1 ? cardIds[0] : 0, (byte)index, targets, restricted.ToImmutable())
        { RandomCards = cardIds.Count == 1 ? null : new(cardIds.ToImmutable()), VillageOnly = villageOnly };
    }

    ImmutableHashSet<(ItemKind Kind, byte Slot)>? CardTargets(int cardId, int index)
    {
        if (!stackable.TryGetValue(cardId, out var cardPath)) return null;
        string card = read("stackable/" + cardPath);
        var cardTypes = Fields(card, "stackable type");
        if (cardTypes.Count != 1 || !Regex.IsMatch(RawValues(card, cardTypes[0]), @"^\s*`\[material expert job\]`\s+1\s*$"))
            return null; // 当前确认的普通怪物卡资源族；其他独立附魔资源不能只凭名称冒充效果。
        var stringBlocks = Regex.Matches(MaskStrings(card), @"\[string data\](.*?)\[/string data\]", RegexOptions.Singleline);
        if (stringBlocks.Count != 1) return null;
        var strings = Regex.Matches(RawValues(card, stringBlocks[0]), "`([^`]*)`")
            .Select(match => match.Groups[1].Value).ToArray();
        if (strings.Length < 2) return null;
        var targets = ImmutableHashSet.CreateBuilder<(ItemKind Kind, byte Slot)>();
        foreach (string tag in strings.Skip(1))
        {
            if (tag.Length < 3 || tag[0] != '[' || tag[^1] != ']') return null;
            byte slot = ItemCatalog.Slot(tag[1..^1]);
            ItemKind kind = slot <= 11 ? ItemKind.Avatar : slot <= 25 ? ItemKind.Equipment :
                slot == 26 ? ItemKind.Creature : slot <= 29 ? ItemKind.CreatureEquipment : ItemKind.GuildMedal;
            targets.Add((kind, slot));
        }
        var tables = Regex.Matches(MaskStrings(card), @"\[enchant table\](.*?)\[/enchant table\]", RegexOptions.Singleline);
        if (tables.Count != 1) return null;
        var entries = Regex.Matches(tables[0].Groups[1].Value,
            @"\[enchant index\]\s*(-?\d+)(?=\s|$)(.*?)\[/enchant index\]", RegexOptions.Singleline);
        if (entries.Count != Fields(tables[0].Groups[1].Value, "enchant index").Count)
            return null; // 索引必须是完整整数token；不能截取0.5、0x0或0suffix的数字前缀。
        var matching = entries.Where(match => int.TryParse(match.Groups[1].Value, NumberStyles.Integer,
            CultureInfo.InvariantCulture, out int value) && value == index).ToArray();
        // 空表或重复等级不能视作成功效果；卡片属性由客户端按完整Card/Upgrade派生。
        if (matching.Length != 1 || !matching[0].Groups[2].Value.Contains('[')) return null;
        return targets.ToImmutable();
    }

    // 仅新多卡资格读取完整根块；说明字符串不参与标签和块层级判断。
    static bool TryRootBlock(string text, string name, out string? values)
    {
        values = null;
        if (text.Count(value => value == '`') % 2 != 0) return false;
        string masked = MaskStrings(text);
        var tags = Regex.Matches(masked, @"\[(/?)([^\[\]\r\n]+)\]");
        var blocks = tags.Where(tag => tag.Groups[1].Length != 0)
            .Select(tag => tag.Groups[2].Value).ToHashSet(StringComparer.Ordinal);
        var stack = new Stack<string>();
        for (int index = 0; index < tags.Count; index++)
        {
            var tag = tags[index];
            string current = tag.Groups[2].Value;
            if (tag.Groups[1].Length != 0)
            {
                if (stack.Count == 0 || stack.Pop() != current) return false;
                continue;
            }
            if (current == name)
            {
                if (values is not null || stack.Count != 0 || index + 1 >= tags.Count ||
                    tags[index + 1].Groups[1].Value != "/" || tags[index + 1].Groups[2].Value != name)
                    return false;
                int start = tag.Index + tag.Length;
                values = text.Substring(start, tags[index + 1].Index - start);
            }
            if (blocks.Contains(current)) stack.Push(current);
        }
        int declarations = Regex.Matches(masked, @"\[/?" + Regex.Escape(name) + @"(?=[\]\s]|$)").Count;
        return stack.Count == 0 && declarations == (values is null ? 0 : 2);
    }

    static int Scalar(string text, string name, bool optional = false)
    {
        var fields = Fields(text, name);
        if (fields.Count == 0 && optional) return 0;
        if (fields.Count != 1 || !int.TryParse(RawValues(text, fields[0]).Trim(), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out int value)) throw new InvalidDataException("宝珠字段无效: " + name);
        return value;
    }

    static MatchCollection Fields(string text, string name) => Regex.Matches(MaskStrings(text),
        @"\[" + Regex.Escape(name) + @"\]([^\[]*)", RegexOptions.CultureInvariant);
    static string RawValues(string text, Match field) => text.Substring(field.Groups[1].Index, field.Groups[1].Length);
    // 保留原跨度，名称/说明中的标签文本不能变成资格或卡片映射字段。
    static string MaskStrings(string text) => Regex.Replace(text, "`[^`]*`", match => new string(' ', match.Length));
}
