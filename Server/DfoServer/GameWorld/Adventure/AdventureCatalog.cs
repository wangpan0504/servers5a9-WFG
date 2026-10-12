using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using PvfLib;

namespace DfoServer.GameWorld.Adventure;

internal readonly record struct AdventureProfession(byte Job, byte Grow, byte Group, byte Awakening);
internal sealed record AdventureBenefit(ImmutableDictionary<string, float> Stats, ImmutableDictionary<string, float> Growth);
internal sealed record AdventureProduct(byte Category, int ItemId, int RequiredLevel, uint Cost, uint Limit, int ResetType)
{
    public int Index { get; init; }
}
internal sealed record AdventureShop(byte Index, uint Maximum, bool MonthlyReset, ulong ExperiencePerPoint, uint PointsPerStep);
internal sealed record AdventureCapsule(ulong MinimumExperience, ulong GaugeExperience, uint MaximumGauges,
    int MinimumLevel, int MaximumLevel, uint GrantedExperience, DateTime ResetAnchor);
internal sealed class AdventureCatalog
{
    public const string ResourcePath = "etc/adventurersystem/adventurersystem2018.etc";
    static readonly string[] Jobs = ["swordman", "fighter", "gunner", "mage", "priest", "at gunner", "thief", "at fighter",
        "at mage", "demonic swordman", "creator mage", "at swordman", "knight", "demonic lancer", "at priest", "gun blader"];
    public int MaximumLevel { get; }
    public decimal ExperienceRate { get; }
    public FrozenDictionary<int, ulong> Experience { get; }
    public FrozenDictionary<int, AdventureBenefit> Benefits { get; }
    public FrozenDictionary<(byte Job, byte Grow), AdventureProfession> Professions { get; }
    public FrozenDictionary<byte, AdventureShop> Shops { get; }
    public FrozenDictionary<(byte Category, int Item), AdventureProduct> Products { get; }
    public AdventureCapsule Capsule { get; }

    public AdventureCatalog(PvfArchive pvf) : this(pvf.ReadTokens, pvf.ResolveString) { }
    internal AdventureCatalog(Func<string, PvfToken[]> read, Func<int, string> resolve)
    {
        var main = new PvfTokenDocument(read(ResourcePath), resolve).Sections;
        PvfTokenSection Required(ImmutableArray<PvfTokenSection> rows, string name) => rows.Single(row => row.Name == name);
        int Integer(PvfToken token) => token.Type == 0 ? token.Value : throw new InvalidDataException("冒险团字段不是整数");
        string String(PvfToken token) => token.Type is 6 or 7 ? resolve(token.Value) : throw new InvalidDataException("冒险团字段不是字符串");
        float Number(PvfToken token) => token.Type == 2 ? BitConverter.Int32BitsToSingle(token.Value) : Integer(token);
        int Scalar(ImmutableArray<PvfTokenSection> rows, string name) => Integer(Required(rows, name).Values.Single());
        MaximumLevel = Scalar(main, "max adventure level");
        float rate = Number(Required(main, "adventure exp rate").Values.Single());
        if (MaximumLevel is < 1 or > 255 || !float.IsFinite(rate) || rate < 0) throw new InvalidDataException("冒险团等级或经验倍率非法");
        ExperienceRate = (decimal)rate;
        var exp = Required(main, "adventure exp table").Values;
        if (exp.Length == 0 || exp.Length % 2 != 0) throw new InvalidDataException("冒险团经验表不是等级/字符串数值对");
        var experience = new Dictionary<int, ulong>();
        for (int i = 0; i < exp.Length; i += 2)
            experience.Add(Integer(exp[i]), ulong.Parse(String(exp[i + 1]), CultureInfo.InvariantCulture));
        if (Enumerable.Range(2, MaximumLevel).Any(level => !experience.TryGetValue(level, out ulong value) || value == 0 || value > long.MaxValue))
            throw new InvalidDataException("冒险团经验表缺少可用等级边界");
        Experience = experience.ToFrozenDictionary();
        Benefits = Required(main, "attribute list").Children.Where(row => row.Name == "attribute").ToFrozenDictionary(
            row => Integer(row.Values.Single()), row => new AdventureBenefit(Values(row, "stat"), Values(row, "grow")));
        ImmutableDictionary<string, float> Values(PvfTokenSection row, string name) => row.Children.Where(child => child.Name == name)
            .ToImmutableDictionary(child => String(child.Values[1]), child => Number(child.Values[2]));
        var professions = new Dictionary<(byte, byte), AdventureProfession>();
        foreach (var group in Required(main, "expert list").Children.Where(row => row.Name == "group"))
        {
            byte classification = checked((byte)Integer(group.Values[0]));
            if (classification >= 20) throw new InvalidDataException("冒险团职业分类超过本版20项");
            foreach (var row in group.Children.Where(row => row.Name == "info"))
            {
                int job = Array.IndexOf(Jobs, String(row.Values[0]).Trim('[', ']'));
                byte awakening = String(row.Values[1]) is "second awakening" or "second realization" ? (byte)2 :
                    throw new InvalidDataException("冒险团职业觉醒声明未核验");
                byte grow = checked((byte)Integer(row.Values[2]));
                if (job < 0 || grow is < 1 or > 15) throw new InvalidDataException("冒险团职业声明非法");
                professions.Add(((byte)job, grow), new((byte)job, grow, classification, awakening));
            }
        }
        Professions = professions.ToFrozenDictionary();
        var shops = new Dictionary<byte, AdventureShop>();
        var products = new Dictionary<(byte, int), AdventureProduct>();
        foreach (var shop in Required(main, "adventurer shop list").Children.Where(row => row.Name == "shop"))
        {
            byte index = checked((byte)Scalar(shop.Children, "index"));
            var maximum = shop.Children.SingleOrDefault(row => row.Name == "max shop point");
            var convert = shop.Children.SingleOrDefault(row => row.Name == "exp to get shop point");
            shops.Add(index, new(index, maximum is null ? uint.MaxValue : checked((uint)Integer(maximum.Values.Single())),
                shop.Children.Any(row => row.Name == "reset point"), convert is null ? 0 : checked((ulong)Integer(convert.Values[0])),
                convert is null ? 0 : checked((uint)Integer(convert.Values[1]))));
            var values = Required(shop.Children, "adventurer shop purchase info").Values;
            if (index >= 5 || values.Length % 5 != 0) throw new InvalidDataException("冒险团商店类别或商品行非法");
            for (int i = 0; i < values.Length; i += 5)
            {
                var product = new AdventureProduct(index, Integer(values[i]), Integer(values[i + 1]), checked((uint)Integer(values[i + 2])),
                    checked((uint)Integer(values[i + 3])), Integer(values[i + 4])) { Index = i / 5 };
                if (product.ItemId <= 0 || product.RequiredLevel < 1 || product.Cost == 0 || product.ResetType is < 0 or > 3)
                    throw new InvalidDataException("冒险团商品资格或周期非法");
                products.Add((index, product.ItemId), product);
            }
        }
        Shops = shops.ToFrozenDictionary(); Products = products.ToFrozenDictionary();
        Capsule = new(checked((ulong)Scalar(main, "capsule need minimum exp")), checked((ulong)Scalar(main, "capsule max exp")),
            checked((uint)Scalar(main, "capsule max count")), Scalar(main, "capsule limit level min"), Scalar(main, "capsule limit level max"),
            checked((uint)Scalar(main, "capsule give exp")), DateTime.ParseExact(String(Required(main, "capsule update date").Values.Single()),
                "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        if (Capsule.MinimumExperience == 0 || Capsule.GaugeExperience < Capsule.MinimumExperience || Capsule.MaximumGauges == 0 ||
            Capsule.GrantedExperience == 0 || Capsule.MinimumLevel < 1 || Capsule.MaximumLevel < Capsule.MinimumLevel ||
            CapsuleCapacity > long.MaxValue) throw new InvalidDataException("冒险团胶囊配置非法");
    }

    public ulong CapsuleCapacity => checked(Capsule.GaugeExperience * Capsule.MaximumGauges);
    public AdventureBenefit Benefit(int level) => Benefits.GetValueOrDefault(level) ?? throw new InvalidDataException("冒险团等级缺少属性声明");
    // 外传角色的CHR首成长为0，而收藏/远征资源以分支1声明；仅转换本领域资源索引，不改Actor或技能树。
    public static byte ProfessionGrow(byte job, byte grow) => job is 9 or 10 && (grow & 15) == 0 ? (byte)1 : (byte)(grow & 15);
    public bool Eligible(byte job, byte grow, byte level) => Professions.TryGetValue((job, ProfessionGrow(job, grow)), out var profession) &&
        (grow >> 4) >= profession.Awakening && level == Characters.CharacterGrowthCatalog.MaximumSupportedLevel;
}
