using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Dungeons;

// etc/particularhellparty.etc：每个 [dungeon] 的 [index] 和 [item list] 各自装入副本编号 → 物品 id。
// 物品 id 只来自文件。触发条件没有证明之前，这份表不进入任何通知。
internal sealed class ParticularHellPartyCatalog
{
    public const string Path = "etc/particularhellparty.etc";

    public int DailyOpenChance { get; }
    public int RequiredItemWeight { get; }
    public float InfernoWeaponCreationRate { get; }
    public ImmutableDictionary<int, ImmutableArray<int>> ItemsByDungeon { get; }

    ParticularHellPartyCatalog(int dailyOpenChance, int requiredItemWeight, float infernoWeaponCreationRate,
        ImmutableDictionary<int, ImmutableArray<int>> itemsByDungeon)
    {
        DailyOpenChance = dailyOpenChance;
        RequiredItemWeight = requiredItemWeight;
        InfernoWeaponCreationRate = infernoWeaponCreationRate;
        ItemsByDungeon = itemsByDungeon;
    }

    public ImmutableArray<int> Items(int dungeonIndex) => ItemsByDungeon.TryGetValue(dungeonIndex, out var items)
        ? items : throw new InvalidDataException("particularhellparty 没有这个副本: " + dungeonIndex);

    public int Draw(int dungeonIndex, int roll)
    {
        var items = Items(dungeonIndex);
        if ((uint)roll >= (uint)items.Length)
            throw new ArgumentOutOfRangeException(nameof(roll));
        return items[roll];
    }

    public static ParticularHellPartyCatalog Load(PvfArchive archive, Func<int, bool> isItem) =>
        Parse(archive.ReadTokens(Path), archive.ResolveString, isItem);

    internal static ParticularHellPartyCatalog Parse(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve, Func<int, bool> isItem)
    {
        ArgumentNullException.ThrowIfNull(isItem);
        bool sawChance = false, sawWeight = false, sawRate = false;
        int chance = 0, weight = 0;
        float rate = 0;
        var lists = new Dictionary<int, ImmutableArray<int>>();
        for (int at = 0; at < tokens.Count;)
        {
            string tag = TagAt(tokens, resolve, ref at);
            switch (tag)
            {
                case "[daily open chance]":
                    if (sawChance) throw new InvalidDataException("particularhellparty 每日开放次数重复");
                    chance = PositiveInt(tokens, ref at, "particularhellparty 每日开放次数");
                    sawChance = true;
                    break;
                case "[required item weight]":
                    if (sawWeight) throw new InvalidDataException("particularhellparty 权重重复");
                    weight = Weight(tokens, ref at);
                    sawWeight = true;
                    break;
                case "[inferno weapon creation rate]":
                    if (sawRate) throw new InvalidDataException("particularhellparty 史诗生成率重复");
                    rate = Rate(tokens, ref at);
                    sawRate = true;
                    break;
                case "[dungeon]":
                    ReadDungeon(tokens, resolve, isItem, ref at, lists);
                    break;
                default:
                    throw new InvalidDataException("particularhellparty 未知段: " + tag);
            }
        }
        if (!sawChance || !sawWeight || !sawRate || lists.Count == 0)
            throw new InvalidDataException("particularhellparty 缺少每日次数、权重、副本块或史诗生成率");
        return new(chance, weight, rate, lists.ToImmutableDictionary());
    }

    static void ReadDungeon(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve, Func<int, bool> isItem,
        ref int at, Dictionary<int, ImmutableArray<int>> lists)
    {
        int? index = null;
        ImmutableArray<int>? items = null;
        while (at < tokens.Count)
        {
            string tag = TagAt(tokens, resolve, ref at);
            switch (tag)
            {
                case "[index]" when index is null:
                    index = PositiveInt(tokens, ref at, "particularhellparty 副本编号");
                    break;
                case "[item list]" when items is null:
                    items = ItemList(tokens, resolve, isItem, ref at);
                    break;
                case "[/dungeon]" when index is not null && items is not null:
                    if (!lists.TryAdd(index.Value, items.Value))
                        throw new InvalidDataException("particularhellparty 副本编号重复: " + index.Value);
                    return;
                default:
                    throw new InvalidDataException("particularhellparty 副本块不完整: " + tag);
            }
        }
        throw new InvalidDataException("particularhellparty 副本块未闭合");
    }

    static ImmutableArray<int> ItemList(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve, Func<int, bool> isItem, ref int at)
    {
        var items = new List<int>();
        while (at < tokens.Count && tokens[at].Type != 3)
        {
            if (tokens[at].Type != 0)
                throw new InvalidDataException("particularhellparty 物品 id 不是整数");
            int id = tokens[at++].Value;
            if (id <= 0 || !isItem(id))
                throw new InvalidDataException("particularhellparty 物品不存在: " + id);
            items.Add(id);
        }
        if (items.Count == 0 || at >= tokens.Count || TagAt(tokens, resolve, ref at) != "[/item list]")
            throw new InvalidDataException("particularhellparty 物品表缺失或未闭合");
        return items.ToImmutableArray();
    }

    static int Weight(IReadOnlyList<PvfToken> tokens, ref int at)
    {
        if (at >= tokens.Count || tokens[at].Type != 0)
            throw new InvalidDataException("particularhellparty 权重非法");
        int weight = tokens[at++].Value;
        if (weight <= 0 || (at < tokens.Count && tokens[at].Type != 3))
            throw new InvalidDataException("particularhellparty 权重非法");
        return weight;
    }

    static float Rate(IReadOnlyList<PvfToken> tokens, ref int at)
    {
        if (at >= tokens.Count || tokens[at].Type != 2)
            throw new InvalidDataException("particularhellparty 史诗生成率缺失");
        float rate = BitConverter.Int32BitsToSingle(tokens[at++].Value);
        if (!float.IsFinite(rate) || rate < 0 || rate > 1 || at < tokens.Count && tokens[at].Type != 3)
            throw new InvalidDataException("particularhellparty 史诗生成率非法");
        return rate;
    }

    static int PositiveInt(IReadOnlyList<PvfToken> tokens, ref int at, string name)
    {
        if (at >= tokens.Count || tokens[at].Type != 0 || tokens[at].Value <= 0 ||
            (at + 1 < tokens.Count && tokens[at + 1].Type != 3))
            throw new InvalidDataException(name + " 非法");
        return tokens[at++].Value;
    }

    static string TagAt(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve, ref int at)
    {
        if (at >= tokens.Count || tokens[at].Type != 3)
            throw new InvalidDataException("particularhellparty 段标签缺失");
        return resolve(tokens[at++].Value).Trim().ToLowerInvariant();
    }
}
