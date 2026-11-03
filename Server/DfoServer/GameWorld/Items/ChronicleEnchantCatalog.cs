using System.Collections.Concurrent;
using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Items;

internal sealed record ChronicleEnchantDefinition(byte Type, int FirstChance, int SecondChance,
    ImmutableDictionary<ChronicleCategory, ImmutableHashSet<byte>> Skills)
{
    // 旧端发送原始红/蓝/绿/黄效果模板，不是消耗的活动变体编号。
    public int EffectItemId => 1254 + Type;
}

internal sealed class ChronicleEnchantCatalog
{
    readonly Func<string, PvfToken[]> read;
    readonly Func<int, string> resolve;
    readonly IReadOnlyDictionary<int, string> stackable, equipment;
    readonly ConcurrentDictionary<string, Lazy<ChronicleEnchantFile>> files = new();
    readonly ConcurrentDictionary<string, Lazy<ChronicleEnchantFile>> effective = new();
    static readonly ImmutableDictionary<byte, string> Parts = new Dictionary<byte, string>
    {
        [12] = "weapon", [14] = "coat", [15] = "shoulder", [16] = "pants", [17] = "shoes", [18] = "waist",
        [19] = "amulet", [20] = "wrist", [21] = "ring", [22] = "support", [23] = "magic stone", [25] = "earring"
    }.ToImmutableDictionary();

    public ChronicleEnchantCatalog(PvfArchive archive, IReadOnlyDictionary<int, string> stackable,
        IReadOnlyDictionary<int, string> equipment) : this(archive.ReadTokens, archive.ResolveString, stackable, equipment) { }
    internal ChronicleEnchantCatalog(Func<string, PvfToken[]> read, Func<int, string> resolve,
        IReadOnlyDictionary<int, string> stackable, IReadOnlyDictionary<int, string> equipment)
    { this.read = read; this.resolve = resolve; this.stackable = stackable; this.equipment = equipment; }

    public ChronicleEnchantDefinition? Get(int id)
    {
        if (!stackable.TryGetValue(id, out string? path)) return null;
        var file = Effective("stackable/" + path, "stackable/");
        // 未接入的独立限用账本不凭客户端检查放行；need material是商店成本，不属于使用费。
        if (file.DeclaredFields.Overlaps(new[] { "total usable count", "total weekly usable count", "total accumulated usable count",
            "use count limit all server item", "usable datetime", "usable first growtype", "check usable itemlevel" })) return null;
        return file.Type is { } type && file.Probability is { } rates && !file.Skills.IsEmpty && stackable.ContainsKey(1254 + type)
            ? new(type, rates.First, rates.Second, file.Skills) : null;
    }

    public bool Allows(int id, byte job, byte growType, byte part, ChronicleEnchantDefinition definition, byte number)
    {
        if (!Parts.TryGetValue(part, out string? label) || !equipment.TryGetValue(id, out string? path)) return false;
        var file = Effective("equipment/" + path, "equipment/");
        var key = new ChronicleCategory(job, (byte)(growType & 0x0F), label);
        if (!file.CharacterChecks.Contains(key) || !definition.Skills.TryGetValue(key, out var options) || !options.Contains(number) ||
            !stackable.TryGetValue(definition.EffectItemId, out string? effectPath)) return false;
        var effect = Effective("stackable/" + effectPath, "stackable/");
        return effect.Type == definition.Type && effect.Skills.TryGetValue(key, out var displayed) && displayed.Contains(number);
    }

    ChronicleEnchantFile Effective(string path, string prefix)
    {
        path = PvfArchive.NormalizePath(path);
        var pending = effective.GetOrAdd(path, key => new(() => Read(key, prefix, new HashSet<string>(StringComparer.Ordinal))));
        try { return pending.Value; }
        catch { effective.TryRemove(new KeyValuePair<string, Lazy<ChronicleEnchantFile>>(path, pending)); throw; }
    }

    ChronicleEnchantFile Read(string path, string prefix, HashSet<string> chain)
    {
        if (!path.StartsWith(prefix, StringComparison.Ordinal) || chain.Count >= 16 || !chain.Add(path))
            throw new InvalidDataException("气息模板导入越界或循环");
        var pending = files.GetOrAdd(path, key => new(() => ChronicleEnchantFile.Parse(read(key), resolve, prefix == "equipment/")));
        ChronicleEnchantFile file;
        try { file = pending.Value; }
        catch { files.TryRemove(new KeyValuePair<string, Lazy<ChronicleEnchantFile>>(path, pending)); throw; }
        if (file.Import is null) return file;
        var parent = Read(PvfArchive.NormalizePath(prefix + file.Import), prefix, chain);
        foreach (var row in file.Skills)
            if (parent.Skills.TryGetValue(row.Key, out var previous) && !previous.SetEquals(row.Value))
                throw new InvalidDataException("气息模板存在冲突选项声明");
        return file with
        {
            Type = file.Type ?? parent.Type, Probability = file.Probability ?? parent.Probability,
            CharacterChecks = file.CharacterChecks.IsEmpty ? parent.CharacterChecks : file.CharacterChecks,
            Skills = parent.Skills.SetItems(file.Skills), DeclaredFields = parent.DeclaredFields.Union(file.DeclaredFields)
        };
    }
}
