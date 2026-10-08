using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using PvfLib;

namespace DfoServer.GameWorld.Items;

// First/Second 只表示声明顺序；未闭合消费前不能改称品质端点或相加。
internal sealed record EquipmentAttributePair(int First, int Second);

internal sealed record EquipmentAttributeDefinition(int TemplateId, string ScriptPath,
    IReadOnlyDictionary<string, int> Scalars, IReadOnlyDictionary<string, EquipmentAttributePair> Pairs)
{
    public bool NoRandom { get; init; }
    public bool HasConditionalRules { get; init; }
    public bool HasLevelSectionAbility { get; init; }
    public bool HasImport { get; init; }
    public bool HasSeparateRate { get; init; }
    private static readonly string[] ScalarTags =
    [
        "minimum level", "grade", "rarity", "sub type", "HP MAX", "MP MAX", "physical attack", "physical defense",
        "magical attack", "magical defense", "fire resistance", "water resistance", "dark resistance", "light resistance",
        "inventory limit", "MP regen speed", "move speed", "attack speed", "cast speed", "hit recovery", "jump power",
        "physical critical hit", "magical critical hit"
    ];
    private static readonly string[] PairTags =
    [
        "equipment physical attack", "add equipment physical attack",
        "equipment magical attack", "add equipment magical attack",
        "equipment physical defense", "add equipment physical defense",
        "equipment magical defense", "add equipment magical defense", "separate attack"
    ];

    public static EquipmentAttributeDefinition Parse(int templateId, string path, PvfDocument document)
    {
        var scalars = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var pairs = new Dictionary<string, EquipmentAttributePair>(StringComparer.OrdinalIgnoreCase);
        foreach (string tag in ScalarTags)
        {
            if (!document.Has(tag))
                continue;
            int[] values = Read(tag, 1);
            scalars.Add(tag, values[0]);
        }
        foreach (string tag in PairTags)
        {
            if (!document.Has(tag))
                continue;
            int[] values = Read(tag, 2);
            pairs.Add(tag, new(values[0], values[1]));
        }
        return new(templateId, path, new ReadOnlyDictionary<string, int>(scalars),
            new ReadOnlyDictionary<string, EquipmentAttributePair>(pairs))
        {
            NoRandom = document.Has("no random"),
            HasConditionalRules = document.Has("impossible contents"),
            HasLevelSectionAbility = document.Has("level section ability") || document.Has("level linear ability") ||
                document.Has("account bind linear ability"),
            HasImport = document.Has("import script"),
            HasSeparateRate = document.Has("separate rate")
        };

        int[] Read(string tag, int count)
        {
            string section = document.Section(tag);
            string pattern = count == 1 ? @"^\s*([-+]?\d+)\s*$" : @"^\s*([-+]?\d+)\s+([-+]?\d+)\s*$";
            Match match = Regex.Match(section, pattern, RegexOptions.CultureInvariant);
            if (!match.Success)
                throw new InvalidDataException("装备属性声明形状未支持: " + tag);
            var values = new int[count];
            for (int i = 0; i < count; i++)
            {
                if (!int.TryParse(match.Groups[i + 1].Value, NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture, out values[i]))
                    throw new InvalidDataException("装备属性声明越界: " + tag);
            }
            return values;
        }
    }
}
