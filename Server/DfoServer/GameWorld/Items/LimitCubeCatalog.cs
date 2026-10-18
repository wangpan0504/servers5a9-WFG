using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;
using PvfLib;

namespace DfoServer.GameWorld.Items;

internal sealed record LimitCubeResultItem(int ItemId, int Count, long Weight);
internal sealed record LimitCubeDefinition(int ItemId, ImmutableArray<ItemMaterialCost> Targets,
    ImmutableArray<ItemMaterialCost> Materials, ImmutableArray<LimitCubeResultItem> Results)
{
    public int? RequiredTargetCount(int itemId) =>
        Targets.FirstOrDefault(row => row.ItemId == itemId)?.Count;
}

// 只按实际使用的券惰性读取 STK；玩法仅接受已确认的 [upgrade limit cube] 0。
internal sealed class LimitCubeCatalog
{
    readonly Func<string, string> read;
    readonly IReadOnlyDictionary<int, string> stackable;
    readonly IReadOnlyDictionary<int, string> equipment;
    readonly ConcurrentDictionary<int, Lazy<LimitCubeDefinition?>> definitions = new();
    readonly ConcurrentDictionary<int, Lazy<bool>> battleSpirit = new();

    public LimitCubeCatalog(PvfArchive archive, IReadOnlyDictionary<int, string> stackable,
        IReadOnlyDictionary<int, string> equipment) : this(archive.ReadText, stackable, equipment) { }

    internal LimitCubeCatalog(Func<string, string> read, IReadOnlyDictionary<int, string> stackable,
        IReadOnlyDictionary<int, string>? equipment = null)
    {
        this.read = read;
        this.stackable = stackable;
        this.equipment = equipment ?? new Dictionary<int, string>();
    }

    public LimitCubeDefinition? Get(int itemId)
    {
        var pending = definitions.GetOrAdd(itemId, id => new(() => Load(id)));
        try { return pending.Value; }
        catch
        {
            definitions.TryRemove(new KeyValuePair<int, Lazy<LimitCubeDefinition?>>(itemId, pending));
            throw;
        }
    }

    public bool IsBattleSpirit(int itemId)
    {
        var pending = battleSpirit.GetOrAdd(itemId, id => new(() => LoadBattleSpirit(id)));
        try { return pending.Value; }
        catch
        {
            battleSpirit.TryRemove(new KeyValuePair<int, Lazy<bool>>(itemId, pending));
            throw;
        }
    }

    LimitCubeDefinition? Load(int itemId)
    {
        if (!stackable.TryGetValue(itemId, out string? path))
            return null;
        string text = read("stackable/" + path);
        var types = Fields(text, "stackable type");
        if (types.Count != 1)
            return null;
        var type = Regex.Match(RawValues(text, types[0]),
            @"^\s*`\[upgrade limit cube\]`\s+(-?\d+)\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!type.Success || !int.TryParse(type.Groups[1].Value, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out int variant) || variant != 0)
            return null;

        // 这些资格在当前库存模型中没有对应账本；不能忽略后把受限券扩大成普通券。
        foreach (string field in new[] { "cool time", "cooltime group", "total usable count",
            "total weekly usable count", "total accumulated usable count", "use count limit all server item" })
            if (Fields(text, field).Count != 0)
                throw new InvalidDataException("变更券包含尚未支持的使用资格: " + field);

        var infos = Blocks(text, "upgrade limit cube info");
        if (infos.Count != 1)
            throw new InvalidDataException("变更券升级信息缺失或重复");
        string info = infos[0];
        var targetBlocks = Blocks(info, "a condition item");
        // 现场PVF的B条件以后一字段为边界，不保证存在对应关闭标签。
        var materialBlocks = Fields(info, "b condition item")
            .Select(match => RawValues(info, match)).ToList();
        var resultBlocks = Blocks(info, "result item");
        if (targetBlocks.Count == 0 || materialBlocks.Count == 0 || resultBlocks.Count == 0)
            throw new InvalidDataException("变更券条件或结果块缺失");

        var targets = new Dictionary<int, int>();
        foreach (string block in targetBlocks)
        {
            long[] values = Integers(block);
            if (values.Length == 0 || values.Length % 2 != 0)
                throw new InvalidDataException("变更券A条件行无效");
            for (int offset = 0; offset < values.Length; offset += 2)
            {
                int targetId = PositiveInt(values[offset], "A条件物品");
                int count = PositiveInt(values[offset + 1], "A条件数量");
                if (targets.TryGetValue(targetId, out int previous) && previous != count)
                    throw new InvalidDataException("变更券A条件数量冲突");
                targets[targetId] = count;
            }
        }

        var materials = new Dictionary<int, int>();
        foreach (string block in materialBlocks)
        {
            long[] values = Integers(block);
            if (values.Length % 2 != 0)
                throw new InvalidDataException("变更券B条件行无效");
            for (int offset = 0; offset < values.Length; offset += 2)
            {
                int materialId = PositiveInt(values[offset], "B条件物品");
                int count = PositiveInt(values[offset + 1], "B条件数量");
                long total = (long)materials.GetValueOrDefault(materialId) + count;
                if (total > int.MaxValue)
                    throw new InvalidDataException("变更券B条件数量溢出");
                materials[materialId] = (int)total;
            }
        }

        var results = ImmutableArray.CreateBuilder<LimitCubeResultItem>();
        foreach (string block in resultBlocks)
        {
            long[] values = Integers(block);
            if (values.Length == 0 || values.Length % 3 != 0)
                throw new InvalidDataException("变更券结果行无效");
            for (int offset = 0; offset < values.Length; offset += 3)
            {
                int resultId = PositiveInt(values[offset], "结果物品");
                int count = PositiveInt(values[offset + 1], "结果数量");
                long weight = values[offset + 2];
                if (weight <= 0)
                    throw new InvalidDataException("变更券结果权重无效");
                results.Add(new(resultId, count, weight));
            }
        }
        _ = results.Aggregate(0L, (sum, row) => checked(sum + row.Weight));
        return new(itemId,
            targets.Select(row => new ItemMaterialCost(row.Key, row.Value)).ToImmutableArray(),
            materials.Select(row => new ItemMaterialCost(row.Key, row.Value)).ToImmutableArray(),
            results.ToImmutable());
    }

    bool LoadBattleSpirit(int itemId)
    {
        if (!equipment.TryGetValue(itemId, out string? path))
            return false;
        var document = EquipmentFile.ReadScript(read, "equipment/" + path);
        bool hasType = document.Has("custom ability type");
        bool hasCount = document.Has("custom ability count");
        if (!hasType && !hasCount)
            return false;
        if (!hasType || !hasCount ||
            !document.TryGetString("custom ability type", out string type) ||
            !document.TryGetInt32("custom ability count", out int count))
            throw new InvalidDataException("装备自定义能力声明无效");
        type = type.Trim().Trim('[', ']').Trim();
        return count == 2 && type.Equals("warriors dungeon", StringComparison.OrdinalIgnoreCase);
    }

    static int PositiveInt(long value, string field)
    {
        if (value is <= 0 or > int.MaxValue)
            throw new InvalidDataException("变更券" + field + "无效");
        return (int)value;
    }

    static long[] Integers(string text)
    {
        const string token = @"(?<!\S)-?\d+(?!\S)";
        if (!string.IsNullOrWhiteSpace(Regex.Replace(text, token, "",
                RegexOptions.CultureInvariant)))
            throw new InvalidDataException("变更券数值块包含未知内容");
        return Regex.Matches(text, token, RegexOptions.CultureInvariant)
            .Select(match => long.Parse(match.Value, NumberStyles.Integer, CultureInfo.InvariantCulture)).ToArray();
    }

    static List<string> Blocks(string text, string name)
    {
        string masked = MaskStrings(text);
        string open = @"\[" + Regex.Escape(name) + @"\]";
        string close = @"\[/" + Regex.Escape(name) + @"\]";
        var openings = Regex.Matches(masked, open, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var closings = Regex.Matches(masked, close, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var matches = Regex.Matches(masked, open + @"(.*?)" + close,
            RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (openings.Count != closings.Count || openings.Count != matches.Count)
            throw new InvalidDataException("变更券PVF块未闭合: " + name);
        return matches.Select(match => text.Substring(match.Groups[1].Index, match.Groups[1].Length)).ToList();
    }

    static MatchCollection Fields(string text, string name) => Regex.Matches(MaskStrings(text),
        @"\[" + Regex.Escape(name) + @"\]([^\[]*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static string RawValues(string text, Match field) =>
        text.Substring(field.Groups[1].Index, field.Groups[1].Length);
    static string MaskStrings(string text) =>
        Regex.Replace(text, "`[^`]*`", match => new string(' ', match.Length));
}
