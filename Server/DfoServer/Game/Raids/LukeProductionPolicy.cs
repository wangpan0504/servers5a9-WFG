using System.Collections.Immutable;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Raids;

namespace DfoServer.Game.Raids;

// 同一策略用于1–8人；保留原资源规则，统一节奏由Battle在原脚本上施加明确差量。
internal static class LukeProductionPolicy
{
    internal static LukeRaidStartPolicy Create(RaidResources resources, ItemCatalog items, BoosterCatalog boosters)
    {
        if (!resources.ResourcesReady || resources.EntryCatalog is not { MapsReady: true } catalog ||
            resources.Script is not { Issues.IsEmpty: true, Phases.Length: 2 } script ||
            script.Phases.Sum(phase => phase.Rules.Length) != 40)
            throw new InvalidDataException("卢克完整资源或原规则不可用");
        var rewards = new LukeResourceRewards(script, items, boosters);
        var prototype = rewards.Create(false, draw: _ => 0);
        var completion = new LukeRaidCompletionPolicy(LukeResourceRewards.Source, 2,
            new(DayOfWeek.Sunday, new(6, 0), TimeSpan.FromHours(8)),
            script.Phases.Select((phase, index) => new LukePhaseCompletionPolicy(
                phase.Declarations.Single(row => row.Tag == "[minimal dungeon clear count]").Values.Single().Number,
                2, prototype[index])).ToImmutableArray(), LukeParticipationCutoff.TerminalCommittedFacts);
        return new("95jp/luke/full-team-v1", script)
        {
            Run = new("95jp/luke/resource-mazes-v1", script.Phases.SelectMany((phase, index) => phase.Dungeons.Select(row =>
                new LukeRaidMazeChoice(index, row.Id, catalog.Find(row.Id)!.Mazes.First(maze => maze.QuestConnectionTokens.Count == 0).SourceOrdinal))).ToImmutableArray(),
                LukeRaidRunEconomy.ControlledNoOrdinaryRewards) { UseResourceCardRoute = true, UseEnergyObjects = true },
            Completion = completion, ResourceRewards = rewards, Cards = resources.LukeCardSet,
            Revival = new("95jp/luke/shared-resource-coins-v1"), Timers = new("95jp/luke/resource-timers-v1"),
            PhaseTimeout = LukeRaidPhaseTimeoutPolicy.Shared95,
            Facilities = new("95jp/luke/resource-facilities-v1", true) { UseResourceBudgets = true },
            Pacing = new("95jp/luke/shared-pacing-v1", 300, 600, 5), Flow = LukeRaidFlowPolicy.Shared95
        };
    }
}
