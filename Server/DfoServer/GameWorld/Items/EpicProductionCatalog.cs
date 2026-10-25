using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Text.RegularExpressions;
using PvfLib;
using static PvfLib.PvfScript;

namespace DfoServer.GameWorld.Items;

internal sealed record EpicProductionLiquid(int ItemId, int Group, int MinimumLevel, int Charge,
    ImmutableArray<ItemMaterialCost> RequiredItems, ImmutableArray<int> AllowedItems,
    string? ItemType, string? Parts, string? Grade, int MinimumItemLevel, int MaximumItemLevel,
    int NeededCount, int MinimumOutput, int MaximumOutput, int MaximumOutputRate,
    bool RequiresSealable, bool UsesMaterialPoints, bool AffectsTryCount);
internal sealed record EpicProductionRange(int Minimum, int Maximum, int Value);

// 只解释制作器自己的资源，不借用史诗图鉴的碎片制作配方。
internal sealed class EpicProductionCatalog
{
    public const string Path = "etc/epicproduction/epicproduction.etc";
    public int MinimumLevel { get; }
    public int WeeklyLimit { get; }
    public int MaximumCharge { get; }
    public int MaterialCount { get; }
    public ImmutableArray<ItemMaterialCost> RequiredItems { get; }
    public ImmutableArray<ItemMaterialCost> ChangeItems { get; }
    public ImmutableArray<ItemMaterialCost> SpecialChangeItems { get; }
    public FrozenDictionary<string,ImmutableArray<int>> Weapons { get; }
    public FrozenDictionary<int,EpicProductionLiquid> Liquids { get; }
    public FrozenDictionary<int,int> MaterialPoints { get; }
    public ImmutableArray<EpicProductionRange> TryCountAdjustments { get; }
    public ImmutableArray<EpicProductionRange> UpgradeOutput { get; }
    public ImmutableArray<EpicProductionRange> GenuineUpgradeOutput { get; }
    public int MinimumBigChance { get; }
    public int MaximumBigChance { get; }
    public int BigChanceRate { get; }
    public int BigChanceMaximumRate { get; }
    public int BigChanceMultiplier { get; }
    public ImmutableArray<(int Ceiling,int Value)> BigChanceRanges { get; }
    public int OutputMaximumRate { get; }

    public EpicProductionCatalog(PvfArchive pvf) : this(pvf.ReadText(Path)) { }
    internal EpicProductionCatalog(string text)
    {
        MinimumLevel = One(text,"level limit",1,99);
        WeeklyLimit = One(text,"weekly limit",1,byte.MaxValue);
        MaximumCharge = One(text,"max charge point",1,int.MaxValue);
        MaterialCount = One(text,"process material need count",1,int.MaxValue);
        int productionAt = text.IndexOf("[production item]",StringComparison.OrdinalIgnoreCase);
        if (productionAt < 0) throw Invalid("production item");
        RequiredItems = Costs(Block(text[..productionAt],"must need item"));
        ChangeItems = Costs(Block(text,"need item type change"));
        SpecialChangeItems = Costs(Block(text,"need item type change special"));
        string chance = Block(text,"big chance info");
        MinimumBigChance = One(chance,"min big chance limit",0,byte.MaxValue);
        MaximumBigChance = One(chance,"max big chance limit",MinimumBigChance,byte.MaxValue);
        BigChanceMaximumRate = One(chance,"big chance max rate",1,int.MaxValue);
        BigChanceRate = One(chance,"big chance rate",0,BigChanceMaximumRate);
        BigChanceMultiplier = One(chance,"big chance multiply",1,int.MaxValue);
        int[] boundaries = Integers(Block(chance,"big chance fix range"));
        if (boundaries.Length == 0 || boundaries.Length % 2 != 0) throw Invalid("big chance fix range");
        BigChanceRanges = Enumerable.Range(0,boundaries.Length/2).Select(i => (boundaries[2*i],boundaries[2*i+1])).ToImmutableArray();
        if (BigChanceRanges[^1].Ceiling != BigChanceMaximumRate || BigChanceRanges.Any(row => row.Ceiling <= 0 || row.Value <= 0) ||
            BigChanceRanges.Zip(BigChanceRanges.Skip(1)).Any(pair => pair.First.Ceiling >= pair.Second.Ceiling))
            throw Invalid("big chance fix range");
        var weapons = new Dictionary<string,ImmutableArray<int>>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(Block(text,"production item"),@"\[info\](.*?)\[/info\]",RegexOptions.Singleline))
        {
            string body = match.Groups[1].Value;
            string job = Text(body,"job") ?? throw Invalid("production item/job");
            int[] ids = Integers(Section(body,"indexes"));
            if (ids.Length == 0 || ids.Any(id => id <= 0) || ids.Distinct().Count() != ids.Length || !weapons.TryAdd(job,ids.ToImmutableArray()))
                throw Invalid("production item/indexes");
        }
        if (weapons.Count == 0) throw Invalid("production item");
        Weapons = weapons.ToFrozenDictionary(StringComparer.Ordinal);
        string liquidText = Block(text,"liquid list");
        var liquids = new Dictionary<int,EpicProductionLiquid>();
        foreach (Match match in Regex.Matches(liquidText,@"\[item index\]\s*(\d+)\s*(.*?)\[/item index\]",RegexOptions.Singleline))
        {
            int id = int.Parse(match.Groups[1].Value,System.Globalization.CultureInfo.InvariantCulture);
            string body = match.Groups[2].Value;
            int[] allowed = Integers(Block(body,"need item list"));
            var row = new EpicProductionLiquid(id,One(body,"group key",0,2),One(body,"level limit",1,99),
                One(body,"get point",1,int.MaxValue),Costs(Block(body,"must need item")),allowed.ToImmutableArray(),
                Text(body,"need item type"),Text(body,"need item parts"),Text(body,"need item grade"),
                Optional(body,"need item min level limit",0,0,99),Optional(body,"need item max level limit",99,0,99),
                One(body,"need item count",1,int.MaxValue),One(body,"min make item count",1,int.MaxValue),
                One(body,"max make item count",1,int.MaxValue),Optional(body,"max item count rate",0,0,int.MaxValue),
                Optional(body,"is seal",0,0,1)==1,Optional(body,"is material point",0,0,1)==1,
                Optional(body,"is affect try count",0,0,1)==1);
            if (allowed.Any(value => value <= 0) || row.MinimumOutput > row.MaximumOutput || row.MinimumItemLevel > row.MaximumItemLevel ||
                !liquids.TryAdd(id,row)) throw Invalid("liquid list/item index");
        }
        if (liquids.Count == 0) throw Invalid("liquid list");
        Liquids = liquids.ToFrozenDictionary();
        OutputMaximumRate = One(liquidText,"max item count max rate",1,int.MaxValue);
        if (liquids.Values.Any(row => row.MaximumOutputRate > OutputMaximumRate)) throw Invalid("max item count rate");
        int[] points = Integers(Block(liquidText,"material point list"));
        if (points.Length == 0 || points.Length % 2 != 0) throw Invalid("material point list");
        var materials = new Dictionary<int,int>();
        for (int i=0;i<points.Length;i+=2)
            if (points[i] < 0 || points[i+1] <= 0 || !materials.TryAdd(points[i],points[i+1])) throw Invalid("material point list");
        MaterialPoints = materials.ToFrozenDictionary();
        TryCountAdjustments = Ranges(liquidText,"affect try count");
        UpgradeOutput = Ranges(liquidText,"upgrade apply count");
        GenuineUpgradeOutput = Ranges(liquidText,"genuine upgrade apply count");
    }

    static ImmutableArray<ItemMaterialCost> Costs(string text)
    {
        int[] values = Integers(text);
        if (values.Length % 2 != 0 || values.Any(value => value <= 0)) throw Invalid("materials");
        return Enumerable.Range(0,values.Length/2).Select(i => new ItemMaterialCost(values[2*i],values[2*i+1])).ToImmutableArray();
    }
    static ImmutableArray<EpicProductionRange> Ranges(string text,string field)
    {
        int[] values = Integers(Block(text,field));
        if (values.Length == 0 || values.Length % 3 != 0) throw Invalid(field);
        var rows = Enumerable.Range(0,values.Length/3).Select(i => new EpicProductionRange(values[3*i],values[3*i+1],values[3*i+2])).ToImmutableArray();
        if (rows.Any(row => row.Minimum < 0 || row.Maximum < row.Minimum || row.Value < 0) ||
            rows.Zip(rows.Skip(1)).Any(pair => pair.First.Maximum >= pair.Second.Minimum)) throw Invalid(field);
        return rows;
    }
    static int One(string text,string field,int minimum,int maximum)
    {
        int[] values = Integers(Section(text,field));
        return values is [var value] && value >= minimum && value <= maximum ? value : throw Invalid(field);
    }
    static int Optional(string text,string field,int fallback,int minimum,int maximum) =>
        string.IsNullOrWhiteSpace(Section(text,field)) ? fallback : One(text,field,minimum,maximum);
    static string? Text(string text,string field)
    {
        var match = Regex.Match(Section(text,field),"`([^`]*)`");
        return match.Success ? match.Groups[1].Value : null;
    }
    static InvalidDataException Invalid(string field) => new("史诗制作器资源无效："+field);
}
