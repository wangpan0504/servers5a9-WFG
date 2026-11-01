using System.Collections.Immutable;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Raids;

namespace DfoServer.Game.Raids;

internal sealed record LukeDungeonCards(ushort Dungeon, byte Pattern, ImmutableArray<int> Monsters);
internal sealed record LukeCardsDisplay(byte Phase, ImmutableArray<LukeDungeonCards> Dungeons, ImmutableHashSet<int> Killed);

internal sealed partial class AntonRaidBattle
{
    readonly LukeCardSetCatalog? lukeCards;
    readonly ImmutableArray<LukeRaidMazeChoice> lukeMazes;
    readonly HashSet<(int Phase, int Monster)> lukeKilledNamed = [];

    internal LukeCardsDisplay? LukeCardsSnapshot(AntonRaidBattleView view)
    {
        lock (gate)
        {
            if (Kind != RaidKind.Luke || !ReferenceEquals(current, view) || lukeCards is not { Ready: true }) return null;
            var rows = ImmutableArray.CreateBuilder<LukeDungeonCards>();
            // 牌组布局仅由三圣所资源声明；二阶段发送空表清除旧阶段，不为其它图伪造布局。
            foreach (var dungeon in script.Phases[view.Phase].Dungeons.Where(dungeon => lukeCards.Patterns(dungeon.Id).Length != 0))
            {
                IEnumerable<int> mazes = view.Phase == 0 && lukeCardRoute is not null && lukeCardGroup is { } group
                    ? Enumerable.Range(0, 3).Select(step => lukeCardRoute.Maze(group, dungeon.Id, step)).OfType<int>()
                    : view.Phase == 0 && lukeFixedRoute is not null
                        ? Enumerable.Range(0, 3).Select(step => lukeFixedRoute.Maze(dungeon.Id, step)).OfType<int>()
                        : lukeMazes.Where(row => row.Phase == view.Phase && row.Dungeon == dungeon.Id).Select(row => row.MazeOrdinal);
                var monsters = mazes.SelectMany(maze => lukeCards.MazeCards.GetValueOrDefault((dungeon.Id, maze), [])).Distinct().ToImmutableArray();
                byte pattern = dungeon.Id is >= 5054 and <= 5056
                    ? checked((byte)view.Symbols.GetValueOrDefault(dungeon.Id switch
                    { 5054 => "[card set pattern a]", 5055 => "[card set pattern b]", _ => "[card set pattern c]" })) : (byte)0;
                rows.Add(new(checked((ushort)dungeon.Id), pattern, monsters));
            }
            return new(checked((byte)view.Phase), rows.ToImmutable(),
                lukeKilledNamed.Where(row => row.Phase == view.Phase).Select(row => row.Monster).ToImmutableHashSet());
        }
    }

    internal int[] LukeNamedTemplates => lukeCards?.Templates.Order().ToArray() ?? [];

    internal AntonRaidBattleChange? RecordLukeNamedDeath(RaidActor actor, DungeonRunIdentity identity, int monster)
    {
        lock (gate)
        {
            if (Kind != RaidKind.Luke || !actor.IsCurrent || !dungeonRuns.TryGetValue(identity.InstanceId, out var run) ||
                !run.Installed || run.Phase != current.Phase || !run.Active.Contains(actor) ||
                lukeCards?.Templates.Contains(monster) != true ||
                !lukeKilledNamed.Add((current.Phase, monster))) return null;
            var before = current;
            current = current with { Revision = checked(current.Revision + 1) };
            return new(before, current);
        }
    }
}
