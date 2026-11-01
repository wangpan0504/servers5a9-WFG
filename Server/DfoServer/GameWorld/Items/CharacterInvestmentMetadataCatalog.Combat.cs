using System.Collections.Concurrent;
using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Items;

// 官方风格战斗力（cp-official/v1）只读取装备脚本中的静态数值与伤害词条；不执行条件脚本，不读客户端面板。
// Stat 为规范化名称；Mode 为 `+`/`%`；Element 仅白字使用，`all` 表示无属性白字。Conditional 区分条件阶梯与普通叠加。
internal sealed record CombatEffectRow(string Stat, string Mode, string Element, double Value, bool Conditional = false);
internal sealed record CombatSetTier(int Threshold, ImmutableArray<CombatEffectRow> Rows);

internal sealed partial class CharacterInvestmentMetadataCatalog
{
    readonly ConcurrentDictionary<int, Lazy<ImmutableArray<CombatEffectRow>>> combatItems = new();
    readonly ConcurrentDictionary<int, Lazy<ImmutableArray<CombatSetTier>>> combatSets = new();

    // 根级数值（力量/智力/三攻/暴击/属强）只取根声明；伤害词条（白字/黄字/爆伤/黄追/爆追/技攻/所攻）
    // 可能位于 [if]/[then] 等条件块内，按“装备常驻面板口径”递归收集。
    static readonly ImmutableHashSet<string> RootStats = ImmutableHashSet.Create(StringComparer.Ordinal,
        "physical attack", "magical attack", "equipment physical attack", "equipment magical attack", "separate attack",
        "physical critical hit", "magical critical hit", "all elemental attack", "fire attack", "water attack",
        "dark attack", "light attack");
    static readonly ImmutableHashSet<string> StatBlockStats = ImmutableHashSet.Create(StringComparer.Ordinal,
        "physical attack", "magical attack", "equipment physical attack", "equipment magical attack", "separate attack",
        "physical critical hit", "magical critical hit", "all elemental attack", "fire attack", "water attack",
        "dark attack", "light attack", "skill attack bonus rate", "all attack bonus rate", "add increase damage",
        "add increase critical damage");

    // 仅供资源自测普查；运行时按已穿戴物品逐件查询。
    internal IEnumerable<int> CombatEquipmentIds => equipment.Keys;

    public ImmutableArray<CombatEffectRow> ReadCombatEffects(int itemId)
    {
        if (!equipment.TryGetValue(itemId, out var path))
            throw new InvalidDataException("战斗力装备模板不存在");
        return combatItems.GetOrAdd(itemId, _ => new(() =>
        {
            var chain = EquipmentChain(Path("equipment", path));
            var names = chain.SelectMany(document => document.Sections.Select(row => row.Name)).Distinct(StringComparer.Ordinal);
            var rows = ImmutableArray.CreateBuilder<CombatEffectRow>();
            foreach (string name in names)
            {
                // 子层声明覆盖父层，与建设套装/附魔口径一致。
                if (name == "piece set ability") continue;
                var effective = CombatEffective(chain, name);
                foreach (var row in effective) Collect(row, rows, root: true);
            }
            return rows.ToImmutable();
        })).Value;
    }

    // 战斗力只需成员归属：取子层优先的首个正整数套装编号；不复用建设分的严格阶梯校验（真实资源大量空阶梯/重复件数）。
    public int? ReadCombatSetId(int itemId)
    {
        if (!equipment.TryGetValue(itemId, out var path))
            throw new InvalidDataException("战斗力装备模板不存在");
        var declaration = CombatEffective(EquipmentChain(Path("equipment", path)), "part set index");
        foreach (var row in declaration)
            if (!row.IsBlock && row.Values.FirstOrDefault(t => t.Type == 0).Value is > 0 and var id) return id;
        return null;
    }

    public ImmutableArray<CombatSetTier> ReadCombatSetTiers(int setId)
    {
        return combatSets.GetOrAdd(setId, id => new(() =>
        {
            var tiers = CombatEffective(EquipmentChain(SetPath(id)), "piece set ability");
            // 同件数重复声明合并词条（子层已优先），空效果阶梯仍计件数分；件数头非法才拒绝。
            var merged = new SortedDictionary<int, ImmutableArray<CombatEffectRow>.Builder>();
            foreach (var tier in tiers)
            {
                if (!tier.IsBlock || tier.Values.Length != 1 || tier.Values[0].Type != 0 || tier.Values[0].Value <= 0)
                    throw new InvalidDataException("战斗力套装阶梯无效");
                if (!merged.TryGetValue(tier.Values[0].Value, out var rows))
                    merged.Add(tier.Values[0].Value, rows = ImmutableArray.CreateBuilder<CombatEffectRow>());
                foreach (var child in tier.Children) Collect(child, rows, root: true);
            }
            return merged.Select(pair => new CombatSetTier(pair.Key, pair.Value.ToImmutable())).ToImmutableArray();
        })).Value;
    }

    public ImmutableArray<CombatEffectRow> ReadCombatEnchant(int cardId, byte index)
    {
        if (cardId <= 0 || !stackable.TryGetValue(cardId, out var path))
            throw new InvalidDataException("战斗力附魔卡不存在");
        var document = Document(Path("stackable", path));
        var table = One(document, "enchant table");
        if (table is null || !table.IsBlock) throw new InvalidDataException("战斗力附魔表缺失");
        var entry = table.Children.FirstOrDefault(row => row.Name == "enchant index" && row.Values.Length == 1 &&
            row.Values[0].Type == 0 && row.Values[0].Value == index);
        if (entry is null) throw new InvalidDataException("战斗力附魔索引缺失");
        var rows = ImmutableArray.CreateBuilder<CombatEffectRow>();
        foreach (var child in entry.Children) Collect(child, rows, root: true);
        return rows.ToImmutable();
    }

    void Collect(PvfTokenSection row, ImmutableArray<CombatEffectRow>.Builder rows, bool root, bool conditional = false)
    {
        conditional |= row.Name is "stat by condition" or "if" or "then" or "else" or "condition";
        InvalidDataException Invalid() => new("战斗力词条无效: " + row.Name);
        double Amount(int index)
        {
            if (index >= row.Values.Length || Number(row.Values[index]) is not { } value)
                throw Invalid();
            return value;
        }
        switch (row.Name)
        {
            case var name when root && RootStats.Contains(name):
                if (row.IsBlock || row.Values.IsEmpty) throw Invalid();
                // 根属性允许资源中的数值向量，但不能越过坏字段寻找后续数值。
                if (row.Values.Any(t => Number(t) is null)) throw Invalid();
                rows.Add(new(name, "+", "", Amount(0), conditional));
                return;
            case "stat":
            case "stat by condition":
                if (row.Values.IsEmpty || Text(row.Values[0]) is not { } stat) throw Invalid();
                if (!StatBlockStats.Contains(stat)) break;
                if (row.Values.Length < 3 || Text(row.Values[1]) is not ("+" or "%")) throw Invalid();
                rows.Add(new(stat, Text(row.Values[1])!, "", Amount(2), conditional));
                break;
            case "unique increase damage":
            case "unique increase critical damage":
                if (row.Values.Length < 3 || row.Values[0].Type != 0 || Text(row.Values[1]) != "%") throw Invalid();
                rows.Add(new(row.Name, "%", "", Amount(2), conditional));
                break;
            case "add absolute damage buff":
                if (row.Values.Length < 4 || row.Values[0].Type != 0 ||
                    Text(row.Values[1]) is not ("all" or "fire" or "water" or "dark" or "light") ||
                    Text(row.Values[2]) is not ("+" or "%")) throw Invalid();
                double white = Amount(3);
                // 合法的 + 为固定追加伤害，不属于百分比白字乘区；仍验证结构与数值。
                if (Text(row.Values[2]) == "%")
                    rows.Add(new(row.Name, "%", Text(row.Values[1])!, white, conditional));
                break;
        }
        foreach (var child in row.Children) Collect(child, rows, root: false, conditional);
    }

    // 与 Effective 同为子层优先，但允许同名字段同时出现在条件块内（伤害词条常见）。
    static PvfTokenSection[] CombatEffective(IEnumerable<PvfTokenDocument> chain, string name)
    {
        foreach (var document in chain)
        {
            var rows = document.Sections.Where(row => row.Name == name).ToArray();
            if (rows.Length != 0) return rows;
        }
        return [];
    }

    string? Text(PvfToken token) => token.Type is 6 or 7 ? resolve(token.Value) : null;
    static double? Number(PvfToken token)
    {
        double value = token.Type switch
        {
            0 => token.Value,
            2 => BitConverter.Int32BitsToSingle(token.Value),
            _ => double.NaN
        };
        return double.IsFinite(value) ? value : null;
    }
}
