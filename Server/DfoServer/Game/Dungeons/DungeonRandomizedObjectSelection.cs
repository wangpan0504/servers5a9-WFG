using System.Collections.Immutable;
using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Game.Dungeons;

internal sealed record DungeonRandomizedObjectSelectionGroup(int SourceOrdinal, bool Regenerate, int? MinimapIcon,
    ImmutableArray<DungeonRandomizedObject> Objects);

// 创建时只抽一次；同一共享Run的28图标和29生成必须引用此同一结果。
internal sealed record DungeonRandomizedObjectSelection(ImmutableArray<DungeonRandomizedObjectSelectionGroup> Groups)
{
    public static DungeonRandomizedObjectSelection Create(IReadOnlyList<DungeonRandomizedObjectGroup> groups,
        Func<int, int>? next = null)
    {
        next ??= Random.Shared.Next;
        var result = ImmutableArray.CreateBuilder<DungeonRandomizedObjectSelectionGroup>(groups.Count);
        for (int ordinal = 0; ordinal < groups.Count; ordinal++)
        {
            var group = groups[ordinal];
            var candidates = group.Objects.ToArray();
            int count = group.SelectCount <= 0 ? candidates.Length : Math.Min(group.SelectCount, candidates.Length);
            if (count < candidates.Length)
                for (int at = candidates.Length - 1; at > 0; at--)
                {
                    int selected = next(at + 1);
                    if ((uint)selected > (uint)at) throw new InvalidOperationException("随机对象选择器越界");
                    (candidates[at], candidates[selected]) = (candidates[selected], candidates[at]);
                }
            result.Add(new(ordinal, group.Regenerate, group.MinimapIcon,
                ImmutableArray.CreateRange(candidates.AsSpan(0, count).ToArray())));
        }
        return new(result.MoveToImmutable());
    }

    public ImmutableArray<DungeonRandomizedObject> ForRoom(DungeonPoint room) =>
        Groups.SelectMany(group => group.Objects).Where(row => row.Map == room).ToImmutableArray();

    // 客户端按含icon块的序号索引图标；空选择仍保留一个空组，不能向前压缩后续组。
    public ImmutableArray<ImmutableArray<DungeonPoint>> MinimapGroups => Groups.Where(group => group.MinimapIcon.HasValue)
        .Select(group => group.Objects.Select(row => row.Map).Distinct().ToImmutableArray()).ToImmutableArray();
}
