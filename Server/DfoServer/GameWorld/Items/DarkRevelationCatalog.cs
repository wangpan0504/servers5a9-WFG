using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using PvfLib;
using static PvfLib.PvfScript;

namespace DfoServer.GameWorld.Items;

internal sealed record DarkRevelationRequirement(string EquipmentType, ImmutableArray<int> Items);
internal sealed record DarkRevelationWeapon(int Slots, bool Upgraded);

internal sealed class DarkRevelationCatalog
{
    public const string ResourcePath = "etc/customability/darkrevelation.etc";
    public ImmutableArray<ItemMaterialCost> FirstMaterials { get; }
    public ImmutableArray<ItemMaterialCost> RepeatMaterials { get; }
    public FrozenSet<int> Abilities { get; }
    public FrozenDictionary<int, string> AbilityGroups { get; }
    public FrozenDictionary<int, ImmutableArray<DarkRevelationRequirement>> Conditions { get; }
    readonly IReadOnlyDictionary<int, string> equipment;
    readonly Func<string, string> read;
    readonly ConcurrentDictionary<int, Lazy<DarkRevelationWeapon?>> weapons = new();

    public DarkRevelationCatalog(PvfArchive archive, IReadOnlyDictionary<int, string> equipment)
        : this(archive.Contains(ResourcePath) ? archive.ReadText(ResourcePath)
            : throw new InvalidDataException("PVF缺少黑暗诉求表: " + ResourcePath), equipment, archive.ReadText) { }

    internal DarkRevelationCatalog(string text) : this(text, new Dictionary<int, string>(),
        _ => throw new InvalidDataException("缺少黑暗诉求装备资源")) { }

    internal DarkRevelationCatalog(string text, IReadOnlyDictionary<int, string> equipment, Func<string, string> read)
    {
        this.equipment = equipment;
        this.read = read;
        FirstMaterials = Materials(Block(text, "first material"));
        RepeatMaterials = Materials(Block(text, "material"));
        var abilities = new HashSet<int>();
        var abilityGroups = new Dictionary<int, string>();
        foreach (Match match in Regex.Matches(text, @"\[group\](.*?)\[/group\]", RegexOptions.Singleline | RegexOptions.IgnoreCase))
        {
            string key = Section(match.Groups[1].Value, "key").Trim().Trim('`');
            if (key.Length == 0) throw new InvalidDataException("PVF黑暗诉求状态组无效");
            foreach (int ability in Integers(Block(match.Groups[1].Value, "indexes")))
            {
                if (ability <= 0 || !abilities.Add(ability))
                    throw new InvalidDataException("PVF黑暗诉求能力编号无效");
                abilityGroups.Add(ability, key);
            }
        }
        if (abilities.Count == 0) throw new InvalidDataException("PVF黑暗诉求能力为空");
        Abilities = abilities.ToFrozenSet();
        AbilityGroups = abilityGroups.ToFrozenDictionary();

        var conditions = new Dictionary<int, ImmutableArray<DarkRevelationRequirement>>();
        foreach (Match match in Regex.Matches(text, @"\[enchant condition\](.*?)\[/enchant condition\]", RegexOptions.Singleline | RegexOptions.IgnoreCase))
        {
            string body = match.Groups[1].Value;
            int[] slots = Integers(Section(body, "slot"));
            if (slots.Length != 1 || slots[0] is < 0 or > 1)
                throw new InvalidDataException("PVF黑暗诉求权能槽无效");
            var requirements = ImmutableArray.CreateBuilder<DarkRevelationRequirement>();
            var types = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match equipmentMatch in Regex.Matches(body, @"\[equipment\](.*?)\[/equipment\]", RegexOptions.Singleline | RegexOptions.IgnoreCase))
            {
                string type = Section(equipmentMatch.Groups[1].Value, "type").Trim().Trim('`');
                var items = Integers(Block(equipmentMatch.Groups[1].Value, "list")).ToImmutableArray();
                if (type.Length == 0 || !types.Add(type) || items.IsEmpty || items.Any(id => id <= 0) ||
                    items.Distinct().Count() != items.Length)
                    throw new InvalidDataException("PVF黑暗诉求装备条件无效");
                requirements.Add(new(type, items));
            }
            if (requirements.Count == 0 || !conditions.TryAdd(slots[0], requirements.ToImmutable()))
                throw new InvalidDataException("PVF黑暗诉求权能条件为空或重复");
        }
        if (conditions.Count != 2) throw new InvalidDataException("PVF黑暗诉求缺少权能条件");
        Conditions = conditions.ToFrozenDictionary();
    }

    public DarkRevelationWeapon? Weapon(int itemId)
    {
        if (!equipment.TryGetValue(itemId, out string? path)) return null;
        return weapons.GetOrAdd(itemId, _ => new(() =>
        {
            var document = EquipmentFile.ReadScript(read, "equipment/" + path);
            if (!document.TryGetString("custom ability type", out string type) ||
                !type.Equals("dark revelation", StringComparison.OrdinalIgnoreCase)) return null;
            if (!document.TryGetInt32("custom ability count", out int slots) || slots is < 1 or > 3)
                throw new InvalidDataException("黑暗诉求武器权能槽数量无效");
            bool upgraded = document.TryGetInt32("upgrade dark revelation", out int value) && value != 0;
            return new DarkRevelationWeapon(slots, upgraded);
        })).Value;
    }

    static ImmutableArray<ItemMaterialCost> Materials(string text)
    {
        int[] values = Integers(text);
        if (values.Length == 0 || values.Length % 2 != 0)
            throw new InvalidDataException("PVF黑暗诉求材料不完整");
        var result = ImmutableArray.CreateBuilder<ItemMaterialCost>(values.Length / 2);
        var ids = new HashSet<int>();
        for (int offset = 0; offset < values.Length; offset += 2)
        {
            if (values[offset] <= 0 || values[offset + 1] <= 0 || !ids.Add(values[offset]))
                throw new InvalidDataException("PVF黑暗诉求材料无效");
            result.Add(new(values[offset], values[offset + 1]));
        }
        return result.MoveToImmutable();
    }
}
