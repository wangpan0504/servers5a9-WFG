using System.Collections.Concurrent;
using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Items;

internal enum AmplificationOperation : byte { Invest, Twist, GoldInvest, Purify, Clear, GoldClear }
internal readonly record struct AmplificationLevel(byte Level, long Weight);
internal readonly record struct AmplificationDropOption(byte Type, int Weight);
internal sealed record AmplificationMaterial(AmplificationOperation Operation, int Count, byte Option,
    ImmutableArray<AmplificationLevel> Levels, int MinimumItemLevel = 0, int MaximumItemLevel = 255,
    bool Village = true, bool SeriaRoom = true);
internal sealed record AmplificationRules(int MinimumLevel, ImmutableDictionary<byte, int> BaseValues,
    ImmutableDictionary<int, float> RarityWeights, ImmutableDictionary<int, int> GradeCorrections,
    ImmutableDictionary<(AmplificationOperation Operation, int ItemId), AmplificationMaterial> Materials)
{
    public ImmutableArray<int> GenerationRates { get; init; } = [];
    public ImmutableArray<AmplificationDropOption> GenerationOptions { get; init; } = [];
    public ushort BaseValue(byte type, int rarity)
    {
        if (!BaseValues.TryGetValue(type, out int basis) || !RarityWeights.TryGetValue(rarity, out float weight)) return 0;
        // 旧端以float32倍率做x87乘法后向零取整；不能先把乘积舍入回float32。
        double value = (double)weight * basis;
        return value is > 0 and <= ushort.MaxValue ? (ushort)value : (ushort)0;
    }
}

internal sealed class AmplificationCatalog
{
    readonly Func<string, PvfToken[]> readTokens;
    readonly Func<int, string> resolve;
    readonly Func<string, string> readText;
    readonly IReadOnlyDictionary<int, string> stackable, equipment;
    readonly Lazy<AmplificationRules> rules;
    readonly ConcurrentDictionary<(AmplificationOperation, int), Lazy<AmplificationMaterial?>> materials = new();
    readonly ConcurrentDictionary<int, bool> growthEquipment = new();
    public AmplificationRules Rules => rules.Value;

    public AmplificationCatalog(PvfArchive archive, IReadOnlyDictionary<int, string> stackable, IReadOnlyDictionary<int, string> equipment)
    {
        readTokens = archive.ReadTokens;
        resolve = archive.ResolveString;
        readText = archive.ReadText;
        this.stackable = stackable;
        this.equipment = equipment;
        rules = new(() => Parse(Document("etc/amplifyitem.etc"), Document("etc/amplifyupgrade.etc"), resolve));
    }

    public AmplificationMaterial? Get(AmplificationOperation operation, int itemId)
    {
        var key = (operation, itemId);
        if (!Rules.Materials.ContainsKey(key) || !stackable.ContainsKey(itemId)) return null;
        var pending = materials.GetOrAdd(key, _ => new(() => ReadMaterial(operation, itemId)));
        try { return pending.Value; }
        catch { materials.TryRemove(new KeyValuePair<(AmplificationOperation, int), Lazy<AmplificationMaterial?>>(key, pending)); throw; }
    }

    public bool UsesCharacterLevel(int itemId) => equipment.ContainsKey(itemId) && growthEquipment.GetOrAdd(itemId,
        id => EquipmentFile.ReadScript(readText, "equipment/" + equipment[id]).Has("upgradable level"));

    AmplificationMaterial? ReadMaterial(AmplificationOperation operation, int itemId)
    {
        var document = Document("stackable/" + stackable[itemId]);
        var result = Rules.Materials[(operation, itemId)];
        foreach (string name in new[] { "total usable count", "total weekly usable count", "total accumulated usable count",
            "use count limit all server item", "usable datetime", "usable first growtype" })
            if (document.Sections.Any(section => section.Name == name)) return null;
        var limits = document.Sections.Where(section => section.Name == "check usable itemlevel").ToArray();
        if (limits.Length > 1) throw new InvalidDataException("增幅道具目标等级限制重复");
        if (limits.Length == 1)
        {
            var rows = AmplificationFiles.Levels(limits[0]);
            if (rows.Length != 1 || rows[0].Level < 0 || rows[0].Weight < rows[0].Level)
                throw new InvalidDataException("增幅道具目标等级限制无效");
            result = result with { MinimumItemLevel = rows[0].Level, MaximumItemLevel = rows[0].Weight };
        }
        var places = document.Sections.Where(section => section.Name == "action usable place").ToArray();
        if (places.Length > 0)
        {
            var labels = places.SelectMany(section => section.Values.TakeWhile(token => token.Type == 6))
                .Select(token => resolve(token.Value)).ToArray();
            result = result with { Village = StackableUsePlaces.Allows(labels, "[village]"),
                SeriaRoom = StackableUsePlaces.Allows(labels, "[seria room]") };
        }
        if (operation == AmplificationOperation.GoldInvest)
        {
            var rows = AmplificationFiles.Levels(One(document.Sections, "amplification random value"));
            if (rows.IsEmpty || rows.Any(row => row.Level is < 0 or > 31 || row.Weight < 0) || !rows.Any(row => row.Weight > 0))
                throw new InvalidDataException("黄金增幅书等级权重无效");
            result = result with { Levels = rows.Select(row => new AmplificationLevel((byte)row.Level, row.Weight)).ToImmutableArray() };
        }
        return result;
    }

    PvfTokenDocument Document(string path) => new(readTokens(path), resolve);

    internal static AmplificationRules Parse(PvfTokenDocument item, PvfTokenDocument upgrade, Func<int, string> resolve)
    {
        var mapping = AmplificationFiles.NamedNumbers(One(item.Sections, "option mapping table"), resolve);
        if (mapping.Any(row => row.Value is < 0 or > 5 || row.Value != MathF.Truncate(row.Value)))
            throw new InvalidDataException("异次元属性映射无效");
        var optionTypes = mapping.ToImmutableDictionary(row => row.Label, row => (byte)row.Value);
        var options = AmplificationFiles.Options(One(item.Sections, "option data"), resolve);
        if (options.Length != 4 || options.Any(row => !optionTypes.TryGetValue(row.Label, out byte type) || type is < 1 or > 4 ||
            row.Weight < 0 || row.BaseValue is <= 0 or > ushort.MaxValue))
            throw new InvalidDataException("异次元属性基础值无效");
        var values = options.ToImmutableDictionary(row => optionTypes[row.Label], row => row.BaseValue);
        var rates = AmplificationFiles.Integers(One(item.Sections, "amplification rate by rarity"));
        if (rates.Length != 7 || rates.Any(rate => rate is < 0 or > 10000) || options.Sum(row => (long)row.Weight) > 10000)
            throw new InvalidDataException("异界气息生成概率或属性权重无效");
        int minimum = AmplificationFiles.Scalar(One(item.Sections, "equip level const"));
        if (minimum is < 0 or > 255) throw new InvalidDataException("异次元属性最低等级无效");
        var weights = NamedRarities(One(item.Sections, "rarity weight"), resolve);
        if (weights.Count != 7 || weights.Values.Any(value => !float.IsFinite(value) || value <= 0))
            throw new InvalidDataException("异次元属性稀有度倍率无效");
        var corrections = NamedRarities(One(One(upgrade.Sections, "disjoint").Children, "correction grade by rarity"), resolve);
        if (corrections.Count != 7 || corrections.Values.Any(value => !float.IsFinite(value) || value < 0 ||
            (double)value > int.MaxValue || value != MathF.Truncate(value)))
            throw new InvalidDataException("异次元属性装备品级修正无效");
        var materials = ImmutableDictionary.CreateBuilder<(AmplificationOperation, int), AmplificationMaterial>();
        foreach (var section in item.Sections)
        {
            AmplificationOperation? operation = section.Name switch
            {
                "purify material" => AmplificationOperation.Purify,
                "purify only material" or "purify only cera material" => AmplificationOperation.Clear,
                "gold purify material" => AmplificationOperation.GoldClear,
                "invest option" => AmplificationOperation.Invest,
                "reinvest option" => AmplificationOperation.Twist,
                "random invest upgrade option" => AmplificationOperation.GoldInvest,
                _ => null
            };
            if (operation is not { } kind) continue;
            bool selected = kind is AmplificationOperation.Invest or AmplificationOperation.Twist or AmplificationOperation.GoldInvest;
            foreach (var row in AmplificationFiles.Materials(section, resolve, selected))
            {
                if (row.ItemId <= 0 || row.Count <= 0 || selected &&
                    (row.Option is null || !optionTypes.TryGetValue(row.Option, out byte type) || type is < 1 or > 5))
                    throw new InvalidDataException("异次元属性材料映射无效");
                var definition = new AmplificationMaterial(kind, row.Count, selected ? optionTypes[row.Option!] : (byte)0, []);
                var key = (selected ? kind : AmplificationOperation.Purify, row.ItemId);
                // 当前PVF有相同的黄金清除行重复；仅允许完全相同的重复声明。
                if (materials.TryGetValue(key, out var old) && old != definition)
                    throw new InvalidDataException("异次元属性材料映射冲突");
                materials[key] = definition;
            }
        }
        return new(minimum, values, weights, corrections.ToImmutableDictionary(row => row.Key, row => (int)row.Value), materials.ToImmutable())
        {
            GenerationRates = rates,
            GenerationOptions = options.Select(row => new AmplificationDropOption(optionTypes[row.Label], row.Weight)).ToImmutableArray()
        };
    }

    static ImmutableDictionary<int, float> NamedRarities(PvfTokenSection section, Func<int, string> resolve)
    {
        string[] names = ["common", "uncommon", "rare", "unique", "epic", "chronicle", "legendary"];
        var rows = AmplificationFiles.NamedNumbers(section, resolve);
        if (rows.Any(row => !names.Contains(row.Label))) throw new InvalidDataException("异次元属性稀有度名称无效");
        return rows.ToImmutableDictionary(row => Array.IndexOf(names, row.Label), row => row.Value);
    }

    static PvfTokenSection One(IEnumerable<PvfTokenSection> sections, string name)
    {
        var rows = sections.Where(section => section.Name == name).ToArray();
        return rows.Length == 1 ? rows[0] : throw new InvalidDataException("异次元属性字段缺失或重复: " + name);
    }
}
