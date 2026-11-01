using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;
using DfoServer.GameWorld.Items;
using PvfLib;

namespace DfoServer.GameWorld.Creatures;

internal sealed record CreatureItemGrowth(int ItemA, int ItemB, int SpeciesA, int SpeciesB, int SelectLevel,
    int GoalLevel, int ExperienceItem, int ExperienceSpecies, int ExperienceValue);

internal sealed record CreatureDefinition(int ItemId, int Species, bool Egg, int MaximumLevel,
    int EvolutionLevel, bool EvolutionQuest, int EvolutionSpecies, int OutputItemId,
    int SkillRecoveryTime, int OverSkillRecoveryTime, int OverSkillLevel)
{
    public string Name { get; init; } = "";
    public bool UsesExperienceItems { get; init; }
    public int ItemsPerLevel { get; init; } = 25;
    public int SelectEvolutionLevel { get; init; } = -1;
    public ImmutableArray<int> EvolutionChoices { get; init; } = [];
    public byte ArtifactSlots { get; init; }
    public CreatureItemGrowth? SelectiveGrowth { get; init; }
}

internal sealed record CreatureFeedDefinition(int ItemId, int MinimumLevel, int CoolTime,
    int Satiety, int EffectSeconds, int SkillRecoveryRate);
internal sealed record CreatureGrowthFood(int MinimumLevel, int CoolTime);
internal sealed record CreatureExperienceItemDefinition(int Kind, int Amount, int MinimumLevel, int CoolTime);

internal sealed class CreatureCatalog
{
    readonly Func<string, string> read;
    readonly Func<string, CreatureFile> readCreature;
    readonly IReadOnlyDictionary<int, string> equipment, stackable, species;
    readonly int[] thresholds;
    readonly ConcurrentDictionary<int, CreatureDefinition> creatures = new();
    readonly ConcurrentDictionary<int, CreatureFeedDefinition> feeds = new();
    readonly ConcurrentDictionary<int, Lazy<CreatureExperienceItemDefinition?>> experienceItems = new();
    readonly ConcurrentDictionary<int, int> foodRates = new();
    readonly ConcurrentDictionary<int, int> cooldownGroups = new();
    readonly Lazy<IReadOnlyDictionary<int, int[]>> evolutionItems;

    public bool Available => thresholds.Length != 0 && species.Count != 0;

    public CreatureCatalog(PvfArchive archive, IReadOnlyDictionary<int, string> equipment, IReadOnlyDictionary<int, string> stackable)
        : this(archive.ReadText, path => CreatureFile.Parse(archive.ReadTokens(path), archive.ResolveString), equipment, stackable,
            archive.Contains("creature/creature.lst") ? PvfScript.List(archive.ReadText("creature/creature.lst")) : new Dictionary<int, string>(),
            archive.Contains("creature/exptable.tbl") ? archive.ReadText("creature/exptable.tbl") : null) { }

    internal CreatureCatalog(Func<string, string> read, Func<string, CreatureFile> readCreature, IReadOnlyDictionary<int, string> equipment,
        IReadOnlyDictionary<int, string> stackable, IReadOnlyDictionary<int, string> species, string? experienceTable)
    {
        this.read = read;
        this.readCreature = readCreature;
        this.equipment = equipment;
        this.stackable = stackable;
        this.species = species;
        thresholds = ParseExperience(experienceTable);
        evolutionItems = new(() =>
        {
            var rows = new Dictionary<int, List<int>>();
            foreach (var (id, path) in equipment)
            {
                var script = new PvfDocument(read("equipment/" + path));
                if (script.Has("import script")) script = EquipmentFile.ReadScript(read, "equipment/" + path);
                if (!script.TryGetInt32("creature species", out int kind) ||
                    !script.TryGetInt32("sub type", out int subtype) || subtype != 0 ||
                    !Regex.IsMatch(script.Section("equipment type"), @"^\s*`\[creature\]`\s+0\s*$", RegexOptions.CultureInvariant)) continue;
                if (!rows.TryGetValue(kind, out var values)) rows.Add(kind, values = []);
                values.Add(id);
            }
            return rows.ToDictionary(row => row.Key, row => row.Value.ToArray());
        });
    }

    // 在监听前完成一次索引装配；进化不在账号锁内扫描整个装备目录。
    public void PrepareEvolutions() => _ = evolutionItems.Value;

    internal static int[] ParseExperience(string? text)
    {
        if (text is null) return [];
        var values = new List<int>();
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("//", StringComparison.Ordinal)) continue;
            foreach (string token in line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out int value) ||
                    value <= 0 || values.Count != 0 && value <= values[^1])
                    throw new InvalidDataException("宠物经验表必须是正数递增的累计阈值");
                values.Add(value);
            }
        }
        if (values.Count is 0 or > 254) throw new InvalidDataException("宠物经验表超出等级字节范围");
        return values.ToArray();
    }

    public byte Level(int experience, CreatureDefinition definition)
    {
        if (experience < 0 || !Available) throw new InvalidDataException("宠物经验或经验目录无效");
        int level;
        if (definition.UsesExperienceItems)
            level = experience / definition.ItemsPerLevel + 1;
        else
        {
            int index = Array.BinarySearch(thresholds, experience);
            level = (index >= 0 ? index + 1 : ~index) + 1;
        }
        return checked((byte)Math.Min(definition.MaximumLevel, level));
    }

    public int MaximumExperienceLevel => thresholds.Length + 1;

    public int ExperienceAtLevel(int level)
    {
        if (!Available || level < 1 || level > MaximumExperienceLevel)
            throw new InvalidDataException("宠物等级超出经验表范围");
        return level == 1 ? 0 : thresholds[level - 2];
    }

    public CreatureDefinition? Get(int itemId)
    {
        if (creatures.TryGetValue(itemId, out var known)) return known;
        if (!Available || !equipment.TryGetValue(itemId, out string? path)) return null;
        var item = EquipmentFile.ReadScript(read, "equipment/" + path);
        if (!Regex.IsMatch(item.Section("equipment type"), @"^\s*`\[creature\]`\s+0\s*$", RegexOptions.CultureInvariant) ||
            !item.TryGetInt32("creature species", out int kind) || !species.TryGetValue(kind, out string? creaturePath)) return null;
        var script = readCreature("creature/" + creaturePath);
        var definition = Read(itemId, kind, item, script);
        return creatures.GetOrAdd(itemId, definition);
    }

    internal static CreatureDefinition Read(int itemId, int species, PvfDocument item, CreatureFile script)
    {
        int maximum = script.MaximumLevel ?? 1;
        int divisor = script.ItemsPerLevel ?? 25;
        if (divisor == 0) divisor = 25;
        if (itemId <= 0 || species is < 1 or > ushort.MaxValue || maximum is < 1 or > byte.MaxValue || divisor < 0)
            throw new InvalidDataException("宠物种类、等级上限或道具成长除数无效");
        return new(itemId, species, Integer(item, "sub type", 0) == 1, maximum,
            script.EvolutionLevel ?? 0, script.EvolutionQuest is not (null or 0),
            script.EvolutionSpecies ?? 0, Integer(item, "output index", 0),
            script.SkillRecoveryTime ?? 0, script.OverSkillRecoveryTime ?? 0, script.OverSkillLevel ?? 0)
        {
            // 显式空名表示不显示实体名称，只有未声明时才使用装备名称兜底。
            Name = script.Name ?? item.Section("name").Trim('`', ' ', '\r', '\n'),
            UsesExperienceItems = script.SelectiveGrowth is { UsingExperienceItem: not (null or 0) } growth &&
                (growth.ItemA is >= 0 || growth.ItemB is >= 0 || growth.ExperienceItem is >= 0),
            ItemsPerLevel = divisor,
            SelectEvolutionLevel = script.SelectiveGrowth?.SelectLevel ?? -1,
            EvolutionChoices = script.EvolutionChoices,
            SelectiveGrowth = script.SelectiveGrowth is { } raw ? new(raw.ItemA ?? -1, raw.ItemB ?? -1,
                raw.SpeciesA ?? -1, raw.SpeciesB ?? -1, raw.SelectLevel ?? -1, raw.GoalLevel ?? -1,
                raw.ExperienceItem ?? -1, raw.ExperienceSpecies ?? -1, raw.ExperienceValue ?? 0) : null,
            ArtifactSlots = script.ArtifactSlots.Aggregate((byte)0, (flags, label) => (byte)(flags | (label switch
                { "red" => 1, "blue" => 2, "green" => 4, _ => 0 })))
        };
    }

    public CreatureDefinition? Evolution(CreatureDefinition source, int targetSpecies)
    {
        // 先采用源装备声明的精确产物，不能在同种类的活动、期限及普通模板之间随意选一个。
        if (source.OutputItemId > 0 && Get(source.OutputItemId) is { Egg: false } output && output.Species == targetSpecies)
            return output;
        if (evolutionItems.Value.TryGetValue(targetSpecies, out int[]? candidates) && candidates is [int id])
            return Get(id);
        return null;
    }

    public CreatureFeedDefinition? Feed(int itemId)
    {
        if (feeds.TryGetValue(itemId, out var known)) return known;
        if (!stackable.TryGetValue(itemId, out string? path)) return null;
        var script = new PvfDocument(read("stackable/" + path));
        if (!Regex.IsMatch(script.Section("stackable type"), @"^\s*`\[feed\]`\s+1\s*$", RegexOptions.CultureInvariant)) return null;
        int[] data = PvfScript.Integers(script.Block("int data"));
        int minimum = Integer(script, "creature minimum level", 1), cool = Integer(script, "cool time", 0);
        if (data.Length != 3 || data[0] < 0 || data[1] is < -100 or > 1000 || data[2] is <= 0 or > 100 || minimum < 0 || cool < 0)
            throw new InvalidDataException("宠物饲料持续时间、集气修正或饱食恢复声明无效");
        return feeds.GetOrAdd(itemId, new CreatureFeedDefinition(itemId, minimum, cool, data[2], data[0], data[1]));
    }

    public int CooldownGroup(int itemId) => cooldownGroups.GetOrAdd(itemId, id =>
    {
        if (!stackable.TryGetValue(id, out string? path)) throw new InvalidDataException("宠物使用道具缺少资源");
        var script = new PvfDocument(read("stackable/" + path));
        if (!script.Has("cooltime group")) return -id;
        int group = Integer(script, "cooltime group", 0);
        return group >= 0 ? group : throw new InvalidDataException("宠物道具冷却组不能为负数");
    });

    public bool RenameCard(int itemId)
    {
        if (!stackable.TryGetValue(itemId, out string? path)) return false;
        var script = new PvfDocument(read("stackable/" + path));
        // 同为creature/1的经验胶囊有独立action，不能拿它冒充改名卡消费。
        return !script.Has("action type") && Regex.IsMatch(script.Section("stackable type"),
            @"^\s*`\[creature\]`\s+1\s*$", RegexOptions.CultureInvariant);
    }

    public CreatureExperienceItemDefinition? ExperienceItem(int itemId)
    {
        if (!stackable.TryGetValue(itemId, out string? path)) return null;
        return experienceItems.GetOrAdd(itemId, _ => new(() =>
        {
            var script = new PvfDocument(read("stackable/" + path));
            if (!Regex.IsMatch(script.Section("stackable type"), @"^\s*`\[creature\]`\s+1\s*$", RegexOptions.CultureInvariant) ||
                !StackableActionDeclaration.TryRead(script, out var declaration) ||
                StackableActionCatalog.Arguments(declaration) is not { Label: "[creature use item]", Parameters: [>= 1 and <= 3, > 0] } action)
                return null;
            int minimum = Integer(script, "creature minimum level", 1), cool = Integer(script, "cool time", 0);
            if (minimum is < 1 or > 255 || cool < 0) return null;
            return new(action.Parameters[0], action.Parameters[1], minimum, cool);
        })).Value;
    }

    public CreatureGrowthFood? GrowthFood(int itemId)
    {
        if (!stackable.TryGetValue(itemId, out string? path)) return null;
        var script = new PvfDocument(read("stackable/" + path));
        if (!Regex.IsMatch(script.Section("stackable type"), @"^\s*`\[creature expitem\]`\s+0\s*$", RegexOptions.CultureInvariant)) return null;
        int minimum = Integer(script, "creature minimum level", 1), cool = Integer(script, "cool time", 0);
        if (minimum < 0 || cool < 0) throw new InvalidDataException("宠物成长道具的等级或冷却无效");
        return new(minimum, cool);
    }

    public int FoodConsumptionRate(int itemId)
    {
        if (foodRates.TryGetValue(itemId, out int known)) return known;
        if (!equipment.TryGetValue(itemId, out string? path)) return 0;
        return foodRates.GetOrAdd(itemId, Integer(EquipmentFile.ReadScript(read, "equipment/" + path), "creature food consume rate", 0));
    }

    public float ExperienceRate(int itemId, int creatureLevel)
    {
        if (!equipment.TryGetValue(itemId, out string? path)) return 0;
        var item = EquipmentFile.ReadScript(read, "equipment/" + path);
        if (!item.Has("creature experience amount rate")) return 0;
        if (Integer(item, "creature minimum level", 0) > creatureLevel) return 0;
        string raw = item.Section("creature experience amount rate").Trim();
        if (!float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out float rate) || !float.IsFinite(rate))
            throw new InvalidDataException("宠物装备经验倍率不是有限数值");
        return rate;
    }

    static int Integer(PvfDocument document, string tag, int fallback)
    {
        if (!document.Has(tag)) return fallback;
        return document.TryGetInt32(tag, out int value) ? value : throw new InvalidDataException("宠物资源整数声明无效: " + tag);
    }
}
