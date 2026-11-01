using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Dungeons;

internal sealed class DimensionDungeonCatalog
{
    public const int Entrance = 7100;
    public const string Path = "etc/dungeonetc/dimensiondungeoninfo.etc";
    public ImmutableDictionary<int, int> Tiers { get; }
    public int DailyLimit { get; }
    public int ExtraLimit { get; }
    // PVF 的 max daily enter count 是基础次数与追加次数的合计；客户端按基础 5 次显示。
    public int BaseDailyLimit => DailyLimit - ExtraLimit;
    public int HiddenWeeklyLimit { get; }
    public ImmutableDictionary<int, int> HiddenRates { get; }
    public ImmutableArray<(int Tier, int Item, int Value)> HiddenRewards { get; }
    public ImmutableArray<(int Tier, int Group, int Item)> Materials { get; }
    public DungeonIncountRow DailyRow { get; }
    public DungeonIncountRow HiddenRow => new(1, [Entrance], int.MaxValue, false, HiddenWeeklyLimit);

    public DimensionDungeonCatalog(PvfArchive archive) : this(archive.ReadText(Path)) { }

    internal DimensionDungeonCatalog(string text)
    {
        var document = new PvfDocument(text);
        int[] Values(string name) => PvfScript.Integers(document.Block(name));
        int Scalar(string name)
        {
            int[] values = PvfScript.Integers(document.Section(name));
            return values is [>= 0 and <= 10000] ? values[0] : throw new InvalidDataException("异界次数声明无效: " + name);
        }
        var ids = Values("dimension dungeon info");
        var rates = Values("hidden boss appear rate by enter count");
        var rewards = Values("hidden boss drop table");
        var materials = Values("drop item info");
        if (ids.Length == 0 || ids.Length % 2 != 0 || rates.Length == 0 || rates.Length % 2 != 0 ||
            rewards.Length % 3 != 0 || materials.Length % 3 != 0)
            throw new InvalidDataException("异界索引、隐藏概率或奖励行宽无效");
        Tiers = Enumerable.Range(0, ids.Length / 2).ToImmutableDictionary(index => ids[index * 2 + 1], index => ids[index * 2]);
        HiddenRates = Enumerable.Range(0, rates.Length / 2).ToImmutableDictionary(index => rates[index * 2], index => rates[index * 2 + 1]);
        HiddenRewards = [.. Enumerable.Range(0, rewards.Length / 3).Select(index => (rewards[index * 3], rewards[index * 3 + 1], rewards[index * 3 + 2]))];
        Materials = [.. Enumerable.Range(0, materials.Length / 3).Select(index => (materials[index * 3], materials[index * 3 + 1], materials[index * 3 + 2]))];
        DailyLimit = Scalar("max daily enter count"); ExtraLimit = Scalar("max extra enter count");
        HiddenWeeklyLimit = Scalar("hidden boss appear count on week");
        if (DailyLimit == 0 || ExtraLimit > DailyLimit || Tiers.Any(row => row.Key <= 0 || row.Value is < 0 or > 2) ||
            HiddenRates.Any(row => row.Key < 1 || row.Value is < 0 or > 10000) ||
            HiddenRewards.Any(row => row.Tier is < 0 or > 2 || row.Item <= 0 || row.Value <= 0) ||
            Materials.Any(row => row.Tier is < 0 or > 2 || row.Group is < 0 or > 1 || row.Item <= 0))
            throw new InvalidDataException("异界资源业务边界无效");
        DailyRow = new(0, [.. Tiers.Keys.Order()], BaseDailyLimit, false, 0);
    }

    public bool Contains(int dungeon) => Tiers.ContainsKey(dungeon);
    public int HiddenRate(int entryCount) => HiddenRates.GetValueOrDefault(Math.Min(entryCount, HiddenRates.Keys.Max()));

    // 再次挑战从当前档位实际登场的五张图中等概率抽取；包含当前图，约1/5概率原地重开。
    public int RetryTarget(int current, Func<int, int> draw)
    {
        if (!Tiers.TryGetValue(current, out int tier)) throw new InvalidDataException("再次挑战缺少当前异界档位");
        int[] candidates = [.. Tiers.Where(row => row.Value == tier).Select(row => row.Key).Order()];
        int index = draw(candidates.Length);
        if (index < 0 || index >= candidates.Length) throw new InvalidDataException("异界再次挑战随机索引越界");
        return candidates[index];
    }
}
