using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Dungeons;

internal sealed record DungeonPassiveItemGroup(int Index, int Level, ImmutableArray<DungeonSpecialDropItem> Items)
{
    public int DeclaredIndex { get; init; } = Index;
    internal static ImmutableArray<DungeonPassiveItemGroup> Parse(IReadOnlyList<DungeonRawSection> sections)
    {
        var blocks = sections.Where(section => section.Tag == "special passive object item").ToArray();
        // 两端读取块时先清空vector；首个整数只被消费，实际引用按vector位置定位。
        var tokens = blocks.LastOrDefault()?.Tokens.ToArray() ?? [];
        var groups = ImmutableArray.CreateBuilder<DungeonPassiveItemGroup>();
        int at = 0;
        while (at < tokens.Length)
        {
            int index = Integer(), level = Integer(), count = Integer();
            if (count < 0 || count > (tokens.Length - at) / 2)
                throw new InvalidDataException("特殊对象掉落组索引、等级或条目数量无效");
            var items = ImmutableArray.CreateBuilder<DungeonSpecialDropItem>(count);
            for (int i = 0; i < count; i++)
            {
                int item = Integer(), weight = Integer();
                if (item <= 0 || weight < 0)
                    throw new InvalidDataException("特殊对象掉落物品或权重无效");
                items.Add(new(item, weight));
            }
            groups.Add(new(groups.Count, level, items.MoveToImmutable()) { DeclaredIndex = index });
        }
        return groups.ToImmutable();

        int Integer() => at < tokens.Length && tokens[at].Type == 0 ? tokens[at++].Value :
            throw new InvalidDataException("特殊对象掉落声明缺少完整整数");
    }
}
