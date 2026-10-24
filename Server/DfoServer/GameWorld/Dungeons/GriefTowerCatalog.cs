using System.Collections.Frozen;
using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Dungeons;

internal sealed record GriefTowerFloor(int Layer, int DungeonId, int AlternateDungeonId, int AntiEvil, string RewardGroup);
internal sealed record GriefTowerAdventureBand(int MinimumLevel, int MaximumLevel, int AdditionalCount);

// 专表负责资源身份。保留原次数声明，不把仍在PVF中的旧字段自动解释成当前每日准入规则。
internal sealed class GriefTowerCatalog
{
    public const string ResourcePath = "etc/towerofgrief.etc";
    public int MinimumLevel { get; }
    public int TopLayer { get; }
    public int DeclaredEnterableCount { get; }
    public int DeclaredAccountMaximum { get; }
    public ImmutableArray<GriefTowerAdventureBand> DeclaredAdventureBands { get; }
    public FrozenDictionary<int, GriefTowerFloor> FloorsByDungeon { get; }
    public FrozenDictionary<int, int> ItemUseLimits { get; }
    public ImmutableDictionary<string, ImmutableArray<GriefTowerRewardSlot>> RewardTables { get; } =
        ImmutableDictionary<string, ImmutableArray<GriefTowerRewardSlot>>.Empty;

    public GriefTowerCatalog(PvfArchive archive) : this(archive.ReadTokens(ResourcePath), archive.ResolveString)
    {
        RewardTables = GriefTowerRewardTable.Parse(archive.ReadTokens(GriefTowerRewardTable.ResourcePath),archive.ResolveString);
        if (FloorsByDungeon.Values.Any(floor => !RewardTables.ContainsKey(floor.RewardGroup)))
            throw new InvalidDataException("悲叹楼层引用了不存在的卡槽奖励组");
    }

    internal GriefTowerCatalog(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve)
    {
        var document = new PvfTokenDocument(tokens, resolve);
        if (!document.Diagnostics.IsEmpty) throw new InvalidDataException("悲叹专表块结构异常");
        var sections = document.Sections;
        MinimumLevel = Scalar("min level", 1, 255);
        TopLayer = Scalar("top layer", 1, 100); // 01E0DD30将客户端楼层限定为1..100。
        DeclaredEnterableCount = Scalar("enterable count", 0, int.MaxValue);
        DeclaredAccountMaximum = Scalar("account enterable max count", 0, int.MaxValue);
        var bands = Rows("adventure group add enterable count", 3);
        var adventure = ImmutableArray.CreateBuilder<GriefTowerAdventureBand>();
        for (int i = 0; i < bands.Length; i += 3)
        {
            int low = Integer(bands[i]), high = Integer(bands[i + 1]), count = Integer(bands[i + 2]);
            if (low < 0 || high < low || count < 0 || adventure.Any(row => low <= row.MaximumLevel && high >= row.MinimumLevel))
                throw new InvalidDataException("悲叹冒险团次数区间非法或重叠");
            adventure.Add(new(low, high, count));
        }
        DeclaredAdventureBands = adventure.ToImmutable();
        // 0255FA79读取(item,limit)，0255DF80检查负数不限次/剩余>0；0255E010每次使用减一。
        var itemLimits = new Dictionary<int, int>();
        var itemValues = Rows("use allow item list in dungeon", 2);
        for (int i = 0; i < itemValues.Length; i += 2)
        {
            int item = Integer(itemValues[i]), limit = Integer(itemValues[i + 1]);
            if (item <= 0 || !itemLimits.TryAdd(item, limit))
                throw new InvalidDataException("悲叹可用物品编号非法或重复");
        }
        ItemUseLimits = itemLimits.ToFrozenDictionary();
        var antiEvil = new Dictionary<int, int>();
        var values = Rows("each layer dungeon anti evil value", 2);
        for (int i = 0; i < values.Length; i += 2)
        {
            int layer = Layer(values[i]), value = Integer(values[i + 1]);
            if (value < 0 || !antiEvil.TryAdd(layer, value)) throw new InvalidDataException("悲叹抗魔值非法或楼层重复");
        }
        var rewards = new Dictionary<int, string>();
        values = Rows("each layer dungeon clear reward", 3);
        for (int i = 0; i < values.Length; i += 3)
        {
            int first = Layer(values[i]), last = Layer(values[i + 1]);
            if (last < first || values[i + 2].Type != 6) throw new InvalidDataException("悲叹奖励区间或组名非法");
            string group = resolve(values[i + 2].Value);
            if (string.IsNullOrWhiteSpace(group)) throw new InvalidDataException("悲叹奖励组名为空");
            for (int layer = first; layer <= last; layer++)
                if (!rewards.TryAdd(layer, group)) throw new InvalidDataException("悲叹奖励区间重叠");
        }
        var floors = new Dictionary<int, GriefTowerFloor>();
        var layers = new HashSet<int>();
        values = Rows("each layer matching dungeon", 3);
        for (int i = 0; i < values.Length; i += 3)
        {
            int layer = Layer(values[i]), dungeon = Integer(values[i + 1]), alternate = Integer(values[i + 2]);
            if (dungeon <= 0 || alternate < -1 || !layers.Add(layer) ||
                !antiEvil.TryGetValue(layer, out int resistance) || !rewards.TryGetValue(layer, out string? group) ||
                !floors.TryAdd(dungeon, new(layer, dungeon, alternate, resistance, group)))
                throw new InvalidDataException("悲叹楼层重复、缺少奖励/抗魔或副本身份非法");
        }
        if (layers.Count != TopLayer || antiEvil.Count != TopLayer || rewards.Count != TopLayer)
            throw new InvalidDataException("悲叹楼层、抗魔与奖励必须覆盖完整专表");
        FloorsByDungeon = floors.ToFrozenDictionary();

        int Integer(PvfToken value) => value.Type == 0 ? value.Value : throw new InvalidDataException("悲叹专表需要整数");
        int Layer(PvfToken value)
        {
            int layer = Integer(value);
            return layer >= 1 && layer <= TopLayer ? layer : throw new InvalidDataException("悲叹楼层越界");
        }
        PvfTokenSection Required(string name)
        {
            var found = sections.Where(row => row.Name == name).ToArray();
            return found.Length == 1 && found[0].Children.IsEmpty ? found[0] :
                throw new InvalidDataException("悲叹专表字段缺失、重复或嵌套: " + name);
        }
        int Scalar(string name, int minimum, int maximum)
        {
            var row = Required(name);
            if (row.IsBlock || row.Values.Length != 1) throw new InvalidDataException("悲叹标量形状非法: " + name);
            int value = Integer(row.Values[0]);
            return value >= minimum && value <= maximum ? value : throw new InvalidDataException("悲叹标量越界: " + name);
        }
        ImmutableArray<PvfToken> Rows(string name, int width)
        {
            var row = Required(name);
            if (!row.IsBlock || row.Values.Length % width != 0) throw new InvalidDataException("悲叹表格缺少闭合或记录不完整: " + name);
            return row.Values;
        }
    }
}
