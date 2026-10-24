using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Dungeons;

// 已逐条核对本版49项：第三列是[title]/[condition under clear]子类型6的任务编号，-1为空。
internal sealed record TowerOfDespairFloor(int Layer, int DungeonId, int TitleQuestId);
internal sealed record TowerOfDespairAdventureCount(int MinimumLevel, int MaximumLevel, int AdditionalCount);

// 读取本版层数映射；次数字段保留资源含义，不能仅凭字段名把它们当成扣票或每日通关策略。
internal sealed record TowerOfDespairCatalog(int MinimumLevel, int EnterableCount, int AccountMaximum,
    ImmutableArray<TowerOfDespairAdventureCount> AdventureCounts, ImmutableArray<TowerOfDespairFloor> Floors)
{
    public const string ResourcePath = "etc/towerofdespair.etc";
    public int RootDungeonId => Floors[0].DungeonId;
    public static TowerOfDespairCatalog Load(PvfArchive archive) => Read(archive.ReadTokens(ResourcePath), archive.ResolveString);

    internal static TowerOfDespairCatalog Read(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve)
    {
        var sections = new PvfTokenDocument(tokens, resolve).Sections;
        ImmutableArray<int> Values(string name)
        {
            var rows = sections.Where(row => row.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (rows is not [{ Children.Length: 0 } row] || row.Values.IsDefaultOrEmpty || row.Values.Any(token => token.Type != 0))
                throw new InvalidDataException("绝望之塔字段缺失、重复或类型错误：" + name);
            return row.Values.Select(token => token.Value).ToImmutableArray();
        }
        int Scalar(string name)
        {
            var values = Values(name);
            return values is [> 0 and <= ushort.MaxValue] ? values[0] : throw new InvalidDataException("绝望之塔标量越界：" + name);
        }
        int top = Scalar("top layer");
        int minimum = Scalar("min level");
        if (minimum > byte.MaxValue || top > 100) throw new InvalidDataException("绝望之塔等级或层数超过本版范围");
        var matching = Values("each layer matching dungeon");
        if (matching.Length != top * 3) throw new InvalidDataException("绝望之塔楼层表不完整");
        var floors = ImmutableArray.CreateBuilder<TowerOfDespairFloor>(top);
        var dungeonIds = new HashSet<int>();
        for (int i = 0; i < matching.Length; i += 3)
        {
            if (matching[i] != i / 3 + 1 || matching[i + 1] <= 0 || !dungeonIds.Add(matching[i + 1]) || matching[i + 2] < -1)
                throw new InvalidDataException("绝望之塔楼层顺序、地下城或附加字段无效");
            floors.Add(new(matching[i], matching[i + 1], matching[i + 2]));
        }
        var additions = Values("adventure group add enterable count");
        if (additions.Length % 3 != 0) throw new InvalidDataException("绝望之塔冒险团次数表不完整");
        var adventure = ImmutableArray.CreateBuilder<TowerOfDespairAdventureCount>(additions.Length / 3);
        int previous = 0;
        for (int i = 0; i < additions.Length; i += 3)
        {
            if (additions[i] <= previous || additions[i + 1] < additions[i] || additions[i + 2] < 0)
                throw new InvalidDataException("绝望之塔冒险团区间重叠或无效");
            adventure.Add(new(additions[i], additions[i + 1], additions[i + 2]));
            previous = additions[i + 1];
        }
        return new(minimum, Scalar("enterable count"), Scalar("account enterable max count"), adventure.MoveToImmutable(), floors.MoveToImmutable());
    }
}
