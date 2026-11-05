using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Dungeons;

// 特殊副本通关奖励与奖励容器每日/每周上限（etc/clearrewardlimit.etc）。
internal sealed record ClearRewardItemLimit(int ItemId, int DailyLimit, int WeeklyLimit);

internal sealed record ClearRewardDungeonLimit(int GroupId, ImmutableArray<int> Dungeons, int DailyLimit, int WeeklyLimit);

internal sealed class ClearRewardLimitCatalog
{
    public const string Path = "etc/clearrewardlimit.etc";

    public ImmutableArray<ClearRewardItemLimit> ItemLimits { get; }
    public ImmutableArray<ClearRewardDungeonLimit> DungeonLimits { get; }

    ClearRewardLimitCatalog(ImmutableArray<ClearRewardItemLimit> itemLimits, ImmutableArray<ClearRewardDungeonLimit> dungeonLimits)
    {
        ItemLimits = itemLimits;
        DungeonLimits = dungeonLimits;
    }

    public ClearRewardItemLimit? FindItem(int itemId) =>
        ItemLimits.FirstOrDefault(row => row.ItemId == itemId);

    public ClearRewardDungeonLimit? FindDungeon(int dungeonId) =>
        DungeonLimits.FirstOrDefault(row => row.Dungeons.Contains(dungeonId));

    public static ClearRewardLimitCatalog Load(PvfArchive archive) =>
        Parse(archive.ReadTokens(Path), archive.ResolveString);

    internal static ClearRewardLimitCatalog Parse(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve)
    {
        var document = new PvfTokenDocument(tokens, resolve);
        if (document.Sections.Any(section => section.Children.Length != 0 ||
            section.Name is not ("item limit" or "dungeon clear reward limit")))
            throw new InvalidDataException("clearrewardlimit 含未知或嵌套段");

        int[] SingleSection(string name)
        {
            var matched = document.Sections.Where(section => section.Name == name).ToArray();
            if (matched.Length != 1)
                throw new InvalidDataException($"缺少或重复 [{name}]");
            if (matched[0].Values.Any(token => token.Type != 0))
                throw new InvalidDataException($"[{name}] 只允许整数");
            return [.. matched[0].Values.Select(token => token.Value)];
        }

        var itemValues = SingleSection("item limit");
        if (itemValues.Length == 0 || itemValues.Length % 3 != 0)
            throw new InvalidDataException("[item limit] 必须按 物品、每日上限、每周上限 三列成组");
        var items = ImmutableArray.CreateBuilder<ClearRewardItemLimit>(itemValues.Length / 3);
        var seenItems = new HashSet<int>();
        for (int index = 0; index < itemValues.Length; index += 3)
        {
            int itemId = itemValues[index];
            int daily = itemValues[index + 1];
            int weekly = itemValues[index + 2];
            if (itemId <= 0 || daily <= 0 || weekly < daily || !seenItems.Add(itemId))
                throw new InvalidDataException("[item limit] 物品编号、上限范围或唯一性无效");
            items.Add(new(itemId, daily, weekly));
        }

        var dungeonValues = SingleSection("dungeon clear reward limit");
        if (dungeonValues.Length == 0)
            throw new InvalidDataException("[dungeon clear reward limit] 不能为空");
        var dungeons = ImmutableArray.CreateBuilder<ClearRewardDungeonLimit>();
        var seenGroups = new HashSet<int>();
        var seenDungeons = new HashSet<int>();
        for (int index = 0; index < dungeonValues.Length;)
        {
            if (index + 2 > dungeonValues.Length)
                throw new InvalidDataException("[dungeon clear reward limit] 行头不完整");
            int groupId = dungeonValues[index];
            int count = dungeonValues[index + 1];
            if (groupId <= 0 || count <= 0 || index + 2 + count + 2 > dungeonValues.Length || !seenGroups.Add(groupId))
                throw new InvalidDataException("[dungeon clear reward limit] 组号或副本数量无效");
            var groupDungeons = ImmutableArray.CreateBuilder<int>(count);
            for (int offset = 0; offset < count; offset++)
            {
                int dungeonId = dungeonValues[index + 2 + offset];
                if (dungeonId <= 0 || !seenDungeons.Add(dungeonId))
                    throw new InvalidDataException("[dungeon clear reward limit] 副本编号无效或跨行重复");
                groupDungeons.Add(dungeonId);
            }
            int tail = index + 2 + count;
            int daily = dungeonValues[tail];
            int weekly = dungeonValues[tail + 1];
            if (daily <= 0 || weekly < daily)
                throw new InvalidDataException("[dungeon clear reward limit] 每日或每周上限无效");
            dungeons.Add(new(groupId, groupDungeons.MoveToImmutable(), daily, weekly));
            index = tail + 2;
        }

        return new(items.MoveToImmutable(), dungeons.ToImmutable());
    }
}
