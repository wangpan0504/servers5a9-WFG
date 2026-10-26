namespace DfoServer.GameWorld.Dungeons;

internal static class TagEvildomEntryPolicy
{
    // 专用popup不使用普通worldmap；这里只接受当前资源已核的双模式声明和实际5候选直线路线。
    public static bool Supports(DungeonDefinition definition)
    {
        int kind = definition.Id switch { 9608 => 3, 400000095 => 4, _ => 0 };
        bool Number(string tag, int value) => definition.RawSections.Where(row => row.Tag == tag).ToArray() is
            [{ Tokens: [{ Type: 0 } token] }] && token.Value == value;
        bool Marker(string tag) => definition.RawSections.Where(row => row.Tag == tag).ToArray() is [{ Tokens.Count: 0 }];
        return kind != 0 && Number("tag tournament dungeon", kind) && Number("tagsystem rule set", 2) &&
            Number("limit party count", 1) && Number("designate dungeon difficulty", 0) && Number("prohibit practice", 1) &&
            Marker("disable exp") && Marker("disable drop item") && Marker("no fatigue") && Marker("no giveup panalty") &&
            definition.EnterWithoutFatigue && definition.MinimumLevel == 95 && definition.RequiredItemRows.IsEmpty &&
            definition.Mazes.Count == 5 && definition.Mazes.All(maze => maze.SourceOrdinal >= 0 && maze.QuestConnectionTokens.Count == 0 &&
                maze.Width == 5 && maze.Height == 1 && maze.Starts.SequenceEqual([new DungeonPoint(0, 0)]) &&
                maze.Bosses.SequenceEqual([new DungeonPoint(4, 0)]) && maze.MapSpecification is { Count: 5 } && maze.ResourceError is null && maze.HellParty is null && maze.BossEntrance is null &&
                maze.LayeredMaps.Count == 0 && maze.WarpMaps.Count == 0 && maze.ConditionalMaps.Count == 0 &&
                maze.StoryStages.Count == 0 && maze.DummyStages.Count == 0 && maze.MapAlternatives.Count == 0 &&
                maze.ClearConditions.Count == 0 && maze.RandomizedObjects.Count == 0);
    }
}
