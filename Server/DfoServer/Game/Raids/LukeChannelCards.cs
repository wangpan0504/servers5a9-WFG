using System.Collections.Immutable;
using DfoServer.GameWorld.Raids;

namespace DfoServer.Game.Raids;

internal sealed record LukeChannelCardSelection(int Day, int Group, ImmutableDictionary<int, byte> Patterns,
    LukeCardsDisplay Display);

// 原版时机为北京时间06:00、全频道共用；资源未给历史抽组序列，本服按声明顺序逐日轮换。
// 只计算不可变日牌组，不保存第二套在线成员或攻坚状态。
internal sealed class LukeChannelCards(LukeSanctumRoute route, LukeCardSetCatalog cards, TimeProvider? time = null)
{
    readonly TimeProvider clock = time ?? TimeProvider.System;
    static readonly int FirstDay = new DateOnly(1970, 1, 1).DayNumber;

    internal static int Day(DateTimeOffset now) => DateOnly.FromDateTime(now.ToOffset(TimeSpan.FromHours(8)).DateTime.AddHours(-6)).DayNumber;
    internal LukeChannelCardSelection Current => Select(clock.GetUtcNow());
    internal int CurrentDay => Day(clock.GetUtcNow());

    internal LukeChannelCardSelection Select(DateTimeOffset now)
    {
        if (!cards.Ready || route.GroupCount <= 0) throw new InvalidDataException("卢克频道日牌组缺少可执行资源");
        int day = Day(now), sequence = day - FirstDay;
        int group = Index(sequence, route.GroupCount);
        var patterns = ImmutableDictionary.CreateBuilder<int, byte>();
        var dungeons = ImmutableArray.CreateBuilder<LukeDungeonCards>(3);
        foreach (int dungeon in new[] { 5054, 5055, 5056 })
        {
            var available = cards.Patterns(dungeon);
            if (available.Length == 0) throw new InvalidDataException("卢克频道日牌组缺少合法布局");
            byte pattern = checked((byte)available[Index(sequence, available.Length)]);
            patterns.Add(dungeon, pattern);
            var monsters = Enumerable.Range(0, 3).Select(step => route.Maze(group, dungeon, step) ??
                throw new InvalidDataException("卢克频道日牌组路线不完整"))
                .SelectMany(maze => cards.MazeCards.GetValueOrDefault((dungeon, maze), []))
                .Distinct().ToImmutableArray();
            if (monsters.IsEmpty) throw new InvalidDataException("卢克频道日牌组没有实际怪物");
            dungeons.Add(new(checked((ushort)dungeon), pattern, monsters));
        }
        return new(day, group, patterns.ToImmutable(), new(0, dungeons.MoveToImmutable(), []) { AutoDisplay = true });
    }

    static int Index(int value, int length) => (value % length + length) % length;
}
