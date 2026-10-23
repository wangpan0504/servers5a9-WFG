using System.Collections.Immutable;
using DfoServer.GameWorld.Items;
using PvfLib;

namespace DfoServer.GameWorld.Dungeons;

// 通用声明解析由PvfLib负责；这里只解释区域、列表编号和物品/权重对，不推定原版触发关系。
internal sealed class DungeonHellEpicCatalog
{
    public const string Path = "etc/helldropepicitemtable.etc";
    public ImmutableDictionary<int, ImmutableDictionary<int, ImmutableArray<DungeonSpecialDropItem>>> Areas { get; }

    private DungeonHellEpicCatalog(ImmutableDictionary<int, ImmutableDictionary<int, ImmutableArray<DungeonSpecialDropItem>>> areas)
        => Areas = areas;

    public static DungeonHellEpicCatalog Load(PvfArchive pvf) => Parse(pvf.ReadTokens(Path), pvf.ResolveString);

    internal static DungeonHellEpicCatalog Parse(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve)
    {
        var document = new PvfTokenDocument(tokens, resolve);
        if (!document.Diagnostics.IsEmpty || document.Sections.IsEmpty)
            throw new InvalidDataException("深渊史诗表为空或存在未配对标签: " + Path);
        var areas = ImmutableDictionary.CreateBuilder<int, ImmutableDictionary<int, ImmutableArray<DungeonSpecialDropItem>>>();
        foreach (var area in document.Sections)
        {
            if (area.Name != "drop area" || !area.IsBlock || area.Values.Length != 1)
                throw new InvalidDataException("深渊史诗区域声明不完整");
            int areaId = Positive(area.Values[0]);
            var lists = ImmutableDictionary.CreateBuilder<int, ImmutableArray<DungeonSpecialDropItem>>();
            foreach (var list in area.Children)
            {
                if (list.Name != "drop epic item list" || !list.IsBlock || !list.Children.IsEmpty ||
                    list.Values.Length < 3 || list.Values.Length % 2 != 1)
                    throw new InvalidDataException("深渊史诗列表须提供编号及完整物品/权重对");
                int listId = Positive(list.Values[0]);
                var items = ImmutableArray.CreateBuilder<DungeonSpecialDropItem>();
                var ids = new HashSet<int>();
                long total = 0;
                for (int at = 1; at < list.Values.Length; at += 2)
                {
                    int id = Positive(list.Values[at]), weight = Positive(list.Values[at + 1]);
                    if (!ids.Add(id) || (total += weight) > int.MaxValue)
                        throw new InvalidDataException("深渊史诗列表物品重复或总权重越界");
                    items.Add(new(id, weight));
                }
                if (!lists.TryAdd(listId, items.ToImmutable()))
                    throw new InvalidDataException("深渊史诗列表编号重复: " + listId);
            }
            if (lists.Count == 0 || !areas.TryAdd(areaId, lists.ToImmutable()))
                throw new InvalidDataException("深渊史诗区域为空或编号重复: " + areaId);
        }
        return new(areas.ToImmutable());

        static int Positive(PvfToken token) => token.Type == 0 && token.Value > 0 ? token.Value :
            throw new InvalidDataException("深渊史诗表须使用正整数编号及权重");
    }

    public DungeonItemDropPool Pool(int area, int list, Func<int, ItemTemplate> resolveItem)
    {
        if (!Areas.TryGetValue(area, out var lists) || !lists.TryGetValue(list, out var rows))
            throw new InvalidDataException($"深渊史诗表缺少区域/列表: {area}/{list}");
        return new(rows.Select(row =>
        {
            var item = resolveItem(row.ItemId);
            if (item.Id != row.ItemId || item.Kind != ItemKind.Equipment || item.Rarity != 4 ||
                item.StackLimit != 1 || !item.Expiration.SupportsCreation)
                throw new InvalidDataException("深渊史诗候选不是可新建史诗装备: " + row.ItemId);
            // 专表权重独立于creation rate，不能借自然掉落资格删减显式候选。
            return new DungeonWeightedDropItem(item, row.Weight);
        }));
    }
}
