using System.Collections.Immutable;
using DfoServer.GameWorld.Raids;

namespace DfoServer.Game.Raids;

// 首个正式Run用例显式隔离经济与复活；不把普通图或安徒恩默认规则当卢克规则。
internal enum LukeRaidRunEconomy { ControlledNoRewardsOrRevival }
internal sealed record LukeRaidMazeChoice(int Phase, int Dungeon, int MazeOrdinal);
internal sealed record LukeRaidRunPolicy(string Source, ImmutableArray<LukeRaidMazeChoice> Mazes, LukeRaidRunEconomy Economy)
{
    public string? Rejection(RaidDungeonCatalog catalog)
    {
        if (string.IsNullOrWhiteSpace(Source) || catalog.Type != 1 || !catalog.MapsReady ||
            Economy != LukeRaidRunEconomy.ControlledNoRewardsOrRevival || Mazes.IsDefaultOrEmpty ||
            Mazes.Length > catalog.Script.Phases.Sum(phase => phase.Dungeons.Length) ||
            Mazes.Select(row => (row.Phase, row.Dungeon)).Distinct().Count() != Mazes.Length)
            return "luke-run-policy-invalid";
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
internal sealed record RaidDungeonSelection(RaidDungeonAdmission Admission, int Dungeon, int MazeOrdinal);
