using System.Collections.Immutable;
using DfoServer.GameWorld.Raids;

namespace DfoServer.Game.Raids;

internal sealed partial class AntonRaidBattle
{
    readonly LukeFixedCardRoute? lukeFixedRoute;
    internal bool UsesFixedRoute(int phase) => phase == 0 && lukeFixedRoute is not null;

    internal LukeRouteLease? CaptureLukeRoute(int phase, int dungeon, LukeFixedCardRoute? policy)
    {
        lock (gate)
        {
            if (phase != 0 || current.Phase != phase || !ReferenceEquals(policy, lukeFixedRoute) || lukeFixedRoute is null ||
                pendingTransition is not null || TerminalRequested(current) || LukePhaseDueLocked() ||
                current.Dungeons.GetValueOrDefault(dungeon) != RaidDungeonState.Open ||
                !current.LukeRoutes.TryGetValue(dungeon, out var progress) || progress.Invalid ||
                progress.Generation != current.DungeonGenerations.GetValueOrDefault(dungeon) ||
                lukeFixedRoute.Maze(dungeon, progress.Step) is not { } ordinal) return null;
            var definition = script.Phases[phase].Dungeons.Single(row => row.Id == dungeon);
            int hp = current.Symbols.GetValueOrDefault(definition.HpSymbol);
            if (hp != lukeFixedRoute.Options.InitialHp - progress.Step || hp <= 0) return null;
            return new(Instance, phase, dungeon, progress.Generation, progress.Revision,
                lukeFixedRoute.Options.Group, progress.Step, ordinal, hp, lukeFixedRoute.Options.ResourceFingerprint);
        }
    }

    internal bool IsLukeRouteCurrent(LukeRouteLease lease) =>
        lease == CaptureLukeRoute(lease.Phase, lease.Dungeon, lukeFixedRoute);

    // generation/HP与路线在同一候选中协调；异常HP变化只封闭入场，不反推未知进度。
    AntonRaidBattleView ReconcileLukeRoutes(AntonRaidBattleView view)
    {
        if (lukeFixedRoute is null || view.Phase != 0) return view;
        var routes = view.LukeRoutes;
        foreach (int id in lukeFixedRoute.Options.ColumnDungeons)
        {
            long generation = view.DungeonGenerations.GetValueOrDefault(id);
            string hpSymbol = script.Phases[0].Dungeons.Single(row => row.Id == id).HpSymbol;
            int hp = view.Symbols.GetValueOrDefault(hpSymbol);
            var progress = routes.GetValueOrDefault(id);
            if (progress is null) progress = new(generation, 0, 0);
            else if (progress.Generation != generation)
                progress = new(generation, 0, checked(progress.Revision + 1), hp != lukeFixedRoute.Options.InitialHp);
            if (hp != lukeFixedRoute.Options.InitialHp - progress.Step)
                progress = progress with { Invalid = true };
            routes = routes.SetItem(id, progress);
        }
        return view with { LukeRoutes = routes };
    }
}
