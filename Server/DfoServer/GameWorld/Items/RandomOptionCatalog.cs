using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;
using PvfLib;

namespace DfoServer.GameWorld.Items;

internal readonly record struct RandomOptionQuantity(byte Count, long Weight);
internal readonly record struct RandomOptionCandidate(byte Type, byte Value1, byte Value2, long Weight);
internal sealed record RandomOptionPool(byte Count, long Weight, ImmutableArray<ImmutableArray<RandomOptionCandidate>> Groups);
internal sealed record RandomOptionDefinition(uint GoldCost, ImmutableArray<RandomOptionPool> Pools);

// 魔法封印使用本版随机词条资源，与普通装备的封装/绑定无关。
internal sealed partial class RandomOptionCatalog
{
    const string Root = "etc/randomoption/";
    readonly Func<string, string> read;
    readonly IReadOnlyDictionary<int, string> equipment;
    readonly IReadOnlyDictionary<int, string> options;
    readonly Dictionary<int, ImmutableArray<RandomOptionQuantity>> quantities = new();
    readonly Dictionary<(int Rarity, string Key, byte Count), int[]> selections = new();
    readonly Dictionary<int, (int Id, long Weight)[]> groups = new();
    readonly (int Rarity, int Level, uint Cost)[] costs;
    readonly ConcurrentDictionary<(int Group, byte Level), ImmutableArray<RandomOptionCandidate>> candidates = new();

    public RandomOptionCatalog(PvfArchive archive, IReadOnlyDictionary<int, string> equipment)
        : this(archive.ReadText, equipment, archive.ReadTokens, archive.ResolveString) { }

    public RandomOptionCatalog(Func<string, string> read, IReadOnlyDictionary<int, string> equipment,
        Func<string, PvfToken[]>? readTokens = null, Func<int, string>? resolve = null)
    {
        this.read = read;
        this.equipment = equipment;
        options = PvfScript.List(read(Root + "randomoption.lst"));
        string quantity = read(Root + "optionquantity.etc");
        foreach (var (rarity, tag) in new[] { (2, "common"), (3, "unique") })
        {
            var rows = Integers(PvfScript.Block(quantity, tag), 2);
            if (rows.Any(row => row[0] is < 1 or > 3 || row[1] < 0) || !rows.Any(row => row[1] > 0))
                throw new InvalidDataException("魔法封印词条数量权重无效");
            quantities.Add(rarity, rows.Select(row => new RandomOptionQuantity((byte)row[0], row[1])).ToImmutableArray());
        }
        var tokens = Regex.Matches(PvfScript.Block(read(Root + "optiongroupselection.etc"), "choose option group"),
            @"`[^`]*`|-?\d+").Select(match => match.Value).ToArray();
        if (tokens.Length == 0 || tokens.Length % 11 != 0)
            throw new InvalidDataException("魔法封印部位分组表无效");
        for (int i = 0; i < tokens.Length; i += 11)
        {
            int rarity = int.Parse(tokens[i], CultureInfo.InvariantCulture);
            if (rarity is not (2 or 3) || tokens[i + 1] != "-1" || !tokens[i + 2].StartsWith('`') ||
                !byte.TryParse(tokens[i + 3], out byte count) || count is < 1 or > 3 || tokens[i + 4] != "3")
                throw new InvalidDataException("魔法封印部位分组行无效");
            var values = tokens.Skip(i + 5).Take(6).Select(value => int.Parse(value, CultureInfo.InvariantCulture)).ToArray();
            if (!selections.TryAdd((rarity, Key(tokens[i + 2].Trim('`')), count), values[..3]))
                throw new InvalidDataException("魔法封印部位分组重复");
        }
        foreach (Match block in Regex.Matches(read(Root + "optiongrouping.etc"),
            @"\[option group\](.*?)\[/option group\]", RegexOptions.Singleline | RegexOptions.IgnoreCase))
        {
            var values = PvfScript.Integers(block.Groups[1].Value);
            if (values.Length < 3 || values.Length % 2 != 1 || values[0] <= 0)
                throw new InvalidDataException("魔法封印候选分组无效");
            var rows = new List<(int, long)>();
            for (int i = 1; i < values.Length; i += 2)
            {
                if (values[i] <= 0 || values[i + 1] < 0 || !options.ContainsKey(values[i]))
                    throw new InvalidDataException("魔法封印候选或权重无效");
                rows.Add((values[i], values[i + 1]));
            }
            if (!rows.Any(row => row.Item2 > 0) || !groups.TryAdd(values[0], rows.ToArray()))
                throw new InvalidDataException("魔法封印分组重复或无有效权重");
        }
        string overall2 = read(Root + "randomizedoptionoverall2.etc");
        ReadGradeRules(overall2);
        var fees = Integers(PvfScript.Block(overall2, "break seal cost"), 4);
        if (fees.Any(row => row[0] is not (2 or 3) || row[1] < 0 || row[2] is < 1 or > 3 || row[3] < 0))
            throw new InvalidDataException("魔法封印解封费用无效");
        costs = fees.Where(row => row[2] == 1).Select(row => (row[0], row[1], (uint)row[3])).ToArray();
        if (selections.Values.SelectMany(value => value).Any(id => !groups.ContainsKey(id)) || costs.Length == 0)
            throw new InvalidDataException("魔法封印资源引用缺失");
        if ((readTokens is null) != (resolve is null))
            throw new ArgumentException("随机属性token读取器和字符串解析器必须同时提供");
        changeRules = readTokens is not null ? ReadChangeRules(readTokens, resolve!) : null;
    }

    public RandomOptionDefinition? Resolve(ItemTemplate template)
    {
        if (template.Kind != ItemKind.Equipment || template.Rarity is not (2 or 3) ||
            !equipment.TryGetValue(template.Id, out var path))
            return null;
        var document = EquipmentFile.ReadScript(read, "equipment/" + path);
        var flag = PvfScript.Integers(document.Section("random option"));
        if (flag.Length == 0)
            return null;
        if (flag.Length != 1 || flag[0] < 0)
            throw new InvalidDataException("无效魔法封印声明");
        if (flag[0] == 0)
            return null;
        string type = Regex.Match(document.Section("equipment type"), @"\[([^\]]+)\]").Groups[1].Value;
        var keys = EquipmentKeys(path, type).Distinct().ToArray();
        var matchingFees = costs.Where(row => row.Rarity == template.Rarity && row.Level <= template.MinimumLevel).ToArray();
        if (matchingFees.Length == 0)
            throw new InvalidDataException("魔法封印装备等级费用缺失");
        uint fee = matchingFees.MaxBy(row => row.Level).Cost;
        var pools = quantities[template.Rarity].Select(quantity =>
        {
            var selected = keys.Select(key => selections.GetValueOrDefault((template.Rarity, key, quantity.Count)))
                .FirstOrDefault(value => value is not null) ?? throw new InvalidDataException("魔法封印装备部位分组缺失");
            var resolved = selected.Take(quantity.Count).Select(id => candidates.GetOrAdd((id, template.MinimumLevel),
                key => groups[key.Group].Select(row => ReadOption(row.Id, key.Level, row.Weight)).ToImmutableArray())).ToImmutableArray();
            return new RandomOptionPool(quantity.Count, quantity.Weight, resolved);
        }).ToImmutableArray();
        return new(fee, pools);
    }

    RandomOptionCandidate ReadOption(int id, byte level, long weight)
    {
        // 部分词条在pvp内再次声明dungeon；普通解封只能读取顶层的副本参数。
        string ordinary = Regex.Replace(read(Root + options[id]), @"\[pvp\].*?\[/pvp\]", "",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        string dungeon = PvfScript.Block(ordinary, "dungeon");
        var types = PvfScript.Integers(PvfScript.Section(dungeon, "option"));
        if (types.Length != 1 || types[0] is < 1 or > 255)
            throw new InvalidDataException("魔法封印词条类型无效");
        var block = Regex.Matches(dungeon, @"\[level\]\s*(\d+)(.*?)\[/level\]", RegexOptions.Singleline | RegexOptions.IgnoreCase)
            .Select(match => (Level: int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), Body: match.Groups[2].Value))
            .Where(row => row.Level <= level).MaxBy(row => row.Level);
        if (block.Body is null)
            throw new InvalidDataException("魔法封印词条缺少等级参数");
        string element = types[0] switch { 244 => "fireelement", 245 => "waterelement", 246 => "darkelement", 247 => "lightelement", _ => "" };
        if (element.Length != 0)
        {
            var endpoints = Regex.Matches(block.Body, @"`([^`]+)`")
                .Select(match => Key(match.Groups[1].Value)).ToArray();
            if (endpoints.Length != 2 || endpoints.Any(value => value != element))
                throw new InvalidDataException("魔法封印元素词条端点无效");
            return new((byte)types[0], 1, 1, weight);
        }
        string scalars = Regex.Replace(block.Body, @"(?m)^\[[^\r\n]*\]\s*$", "");
        var numbers = Regex.Matches(scalars, @"[-+]?\d+(?:\.\d+)?")
            .Select(match => double.Parse(match.Value, CultureInfo.InvariantCulture)).ToArray();
        if (numbers.Length < 2 || numbers.Any(value => !double.IsFinite(value)))
            throw new InvalidDataException("魔法封印词条端点无效");
        // 本版条目每端点一个字节；负值保留补码，小数向零取整，超范围按原存储边界饱和。
        return new((byte)types[0], Encode(numbers[^2]), Encode(numbers[^1]), weight);
    }

    static byte Encode(double value) => unchecked((byte)(int)Math.Clamp(Math.Truncate(value), sbyte.MinValue, byte.MaxValue));
    static string Key(string value) => value.Replace(" ", "").Replace("_", "").Replace("[", "").Replace("]", "").ToLowerInvariant();
    static IEnumerable<string> EquipmentKeys(string path, string type)
    {
        yield return Key(type);
        var segments = path.ToLowerInvariant().Replace('\\', '/').Split('/');
        for (int i = 0; i < segments.Length; i++)
        {
            if (segments[i] == "weapon" && i + 1 < segments.Length)
            {
                string weapon = Key(segments[i + 1]);
                yield return weapon;
                yield return weapon switch { "hsword" => "lswd", "boxglove" => "bglove", "beamsword" => "beamswd",
                    "twinsword" => "twinswd", "chakram" => "chakraweapon", _ => weapon };
            }
            string prefix = segments[i] switch { "cloth" => "cl", "leather" => "lt", "larmor" => "la", "harmor" => "ha", "plate" => "mt", _ => "" };
            if (prefix.Length != 0)
                yield return prefix + Key(type);
        }
    }

    static int[][] Integers(string text, int width)
    {
        var values = PvfScript.Integers(text);
        if (values.Length == 0 || values.Length % width != 0)
            throw new InvalidDataException("魔法封印资源行宽无效");
        return values.Chunk(width).ToArray();
    }
}
