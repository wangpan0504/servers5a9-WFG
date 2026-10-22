using System.Collections.Frozen;
using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Adventure;

internal sealed record ExpeditionArea(byte Index, int RequiredLevel, ImmutableArray<string> Groups,
    ImmutableDictionary<string, string> Uniqueness, ImmutableDictionary<uint, float> Rewards);
internal readonly record struct ExpeditionAttribute(string Name, string Group, byte Id);


internal sealed class AdventureExpeditionCatalog
{
    public const string ResourcePath = "etc/adventurersystem/adventurerexpeditionsystem.etc";
    public FrozenDictionary<byte, ExpeditionArea> Areas { get; }
    public FrozenDictionary<string, ExpeditionAttribute> Attributes { get; }
    public FrozenDictionary<string, ImmutableArray<string>> AttributeGroups { get; }
    public FrozenDictionary<(byte Job, byte Grow), ImmutableArray<string>> CharacterAttributes { get; }
    public ImmutableArray<float> PartyRates { get; }
    public ImmutableArray<float> AttributeRates { get; }
    public float ExtraRate { get; }
    public int RotationDays { get; }


    public AdventureExpeditionCatalog(PvfArchive pvf) : this(pvf.ReadTokens, pvf.ResolveString) { }
    internal AdventureExpeditionCatalog(Func<string, PvfToken[]> read, Func<int, string> resolve)
    {
        var expedition = new PvfTokenDocument(read(ResourcePath), resolve).Sections;
        PvfTokenSection Required(ImmutableArray<PvfTokenSection> rows, string name) => rows.Single(row => row.Name == name);
        int Integer(PvfToken token) => token.Type == 0 ? token.Value : throw new InvalidDataException("远征字段不是整数");
        string String(PvfToken token) => token.Type is 6 or 7 ? resolve(token.Value) : throw new InvalidDataException("远征字段不是字符串");
        float Number(PvfToken token) => token.Type == 2 ? BitConverter.Int32BitsToSingle(token.Value) : Integer(token);
        int Scalar(ImmutableArray<PvfTokenSection> rows, string name) => Integer(Required(rows, name).Values.Single());
        var areas = new Dictionary<byte, ExpeditionArea>();
        foreach (var area in Required(expedition, "area info").Children.Where(row => row.Name == "area"))
        {
            byte index = checked((byte)Scalar(area.Children, "index"));
            var values = Required(area.Children, "reward info").Values;
            var rewards = new Dictionary<uint, float>();
            if (values.Length % 2 != 0) throw new InvalidDataException("远征时间/倍率行不完整");
            for (int i = 0; i < values.Length; i += 2) rewards.Add(checked((uint)Integer(values[i]) * 3600), Number(values[i + 1]));
            var unique = area.Children.SingleOrDefault(row => row.Name == "uniqueness")?.Values ?? [];
            var uniqueness = ImmutableDictionary.CreateBuilder<string, string>();
            for (int i = 0; i < unique.Length; i += 2) uniqueness.Add(String(unique[i]), String(unique[i + 1]));
            areas.Add(index, new(index, Scalar(area.Children, "required level"), Required(area.Children, "designated attribute").Values
                .Select(String).ToImmutableArray(), uniqueness.ToImmutable(), rewards.ToImmutableDictionary()));
        }
        Areas = areas.ToFrozenDictionary();
        var bonus = Required(expedition, "reward bonus info").Children;
        ExtraRate = Number(Required(bonus, "extra rate").Values.Single());
        PartyRates = Required(bonus, "party member rate").Values.Select(Number).ToImmutableArray();
        AttributeRates = Required(bonus, "attribute achieve rate").Values.Select(Number).ToImmutableArray();
        RotationDays = Scalar(expedition, "area attribute rotation period");
        var attributes = Required(expedition, "attribute data").Values;
        var data = new Dictionary<string, ExpeditionAttribute>();
        // 每条为线上ID、名称、组名、图片索引；普通属性组名为空，id 255是组别名并带目标组名。
        if (attributes.Length < 4 || attributes.Length % 4 != 0) throw new InvalidDataException("远征属性声明不完整");
        for (int i = 0; i < attributes.Length; i += 4)
        {
            int id = Integer(attributes[i]);
            string name = String(attributes[i + 1]), group = String(attributes[i + 2]);
            _ = Integer(attributes[i + 3]);
            if (id is <= 0 or > byte.MaxValue || (id == byte.MaxValue) != (group.Length != 0))
                throw new InvalidDataException("远征属性行缺少组名或线上ID");
            data.Add(name, new(name, group, checked((byte)id)));
        }
        Attributes = data.ToFrozenDictionary();
        AttributeGroups = Required(expedition, "attribute group").Children.Where(row => row.Name == "group")
            .ToFrozenDictionary(row => String(row.Values[0]), row => row.Values.Skip(1).Select(String).ToImmutableArray());
        var character = Required(expedition, "character attribute").Values;
        var characterAttributes = new Dictionary<(byte, byte), ImmutableArray<string>>();
        for (int i = 0; i < character.Length;)
        {
            byte job = checked((byte)Integer(character[i++])), grow = checked((byte)Integer(character[i++]));
            _ = Integer(character[i++]);
            var names = ImmutableArray.CreateBuilder<string>();
            while (i < character.Length && character[i].Type != 0) names.Add(String(character[i++]));
            if (names.Any(name => !data.ContainsKey(name))) throw new InvalidDataException("远征角色引用未知属性");
            characterAttributes.Add((job, grow), names.ToImmutable());
        }
        CharacterAttributes = characterAttributes.ToFrozenDictionary();
        if (RotationDays <= 0 || PartyRates.Length < 5 || AttributeRates.Length < 5 ||
            !float.IsFinite(ExtraRate) || ExtraRate < 0 || PartyRates.Concat(AttributeRates).Any(value => !float.IsFinite(value) || value < 0) ||
            Areas.Values.Any(area => area.Rewards.Any(pair => pair.Key == 0 || !float.IsFinite(pair.Value) || pair.Value < 0) ||
                area.Groups.Any(group => !data.ContainsKey(group) && !AttributeGroups.ContainsKey(group))))
            throw new InvalidDataException("远征倍率、周期或属性组非法");
    }
}
