using System.Collections.Immutable;
using DfoServer.GameWorld.Raids;

namespace DfoServer.Game.Raids;

// 正式Run显式隔离普通经济；手动复活另须经独立策略核验，不继承普通图或安徒恩默认规则。
internal enum LukeRaidRunEconomy { ControlledNoOrdinaryRewards }
internal sealed record LukeRaidMazeChoice(int Phase, int Dungeon, int MazeOrdinal);
internal sealed record LukeRaidRunPolicy(string Source, ImmutableArray<LukeRaidMazeChoice> Mazes, LukeRaidRunEconomy Economy)
{
    public LukeFixedCardRouteOptions? FixedRouteOptions { get; init; }
    public LukeFixedCardRoute? FixedRoute { get; init; }
    public bool UseResourceCardRoute { get; init; }
    public LukeSanctumRoute? CardRoute { get; init; }
    public bool UseEnergyObjects { get; init; }
    public IReadOnlyList<(int Dungeon, int MazeOrdinal)> ObjectChoices(RaidDungeonCatalog catalog)
    {
        var sanctums = UseResourceCardRoute ? LukeSanctumRoute.ResourceChoices(catalog) :
            FixedRouteOptions is { } options ? LukeFixedCardRoute.ResourceChoices(options, catalog) :
            Mazes.Where(row => row.Phase == 0).Select(row => (row.Dungeon, row.MazeOrdinal)).ToArray();
        return sanctums.Concat(UseEnergyObjects ? Mazes.Where(row => row.Phase == 1 && row.Dungeon is 5063 or 5066)
            .Select(row => (row.Dungeon, row.MazeOrdinal)) : []).Distinct().ToArray();
    }

    public string? EnergyRejection(RaidDungeonCatalog catalog, LukeRaidObjectCatalog? objects) => !UseEnergyObjects ? null :
        !new[] { 5063, 5066 }.All(id => Maze(1, id) is { } ordinal &&
            catalog.Find(id)?.Mazes.SingleOrDefault(maze => maze.SourceOrdinal == ordinal) is { } maze &&
            objects?.Energy(id, maze) is not null) ? "luke-energy-object-resource-unavailable" : null;
    public string? Rejection(RaidDungeonCatalog catalog)
    {
        if (FixedRouteOptions is not null) return "luke-fixed-route-unresolved";
        if (UseResourceCardRoute) return "luke-card-route-unresolved";
        if (CardRoute is { } cards && (FixedRoute is not null || !cards.Matches(catalog) || !cards.Matches(catalog.Script)))
            return "luke-card-route-catalog-changed";
        if (string.IsNullOrWhiteSpace(Source) || catalog.Type != 1 || !catalog.MapsReady ||
            Economy != LukeRaidRunEconomy.ControlledNoOrdinaryRewards || Mazes.IsDefaultOrEmpty ||
            Mazes.Length > catalog.Script.Phases.Sum(phase => phase.Dungeons.Length) ||
            Mazes.Select(row => (row.Phase, row.Dungeon)).Distinct().Count() != Mazes.Length)
            return "luke-run-policy-invalid";
        if (FixedRoute is { } route && (!route.Matches(catalog) || !route.Matches(catalog.Script)))
            return "luke-fixed-route-catalog-changed";
        foreach (var choice in Mazes)
            if (choice.Phase < 0 || choice.Phase >= catalog.Script.Phases.Length ||
                !catalog.Script.Phases[choice.Phase].Dungeons.Any(row => row.Id == choice.Dungeon) ||
                catalog.Find(choice.Dungeon)?.Mazes.Count(maze => maze.SourceOrdinal == choice.MazeOrdinal &&
                    maze.QuestConnectionTokens.Count == 0) != 1)
                return "luke-run-maze-unregistered";
        return null;
    }

    public int? Maze(int phase, int dungeon) => Mazes.SingleOrDefault(row => row.Phase == phase && row.Dungeon == dungeon)?.MazeOrdinal;
}

// 归原Session选图上下文，替换选择或清理上下文即失效；尚未扣费或占用地下城。
internal sealed record RaidDungeonSelection(RaidDungeonAdmission Admission, int Dungeon, int MazeOrdinal)
{
    public LukeRouteLease? Route { get; init; }
}
