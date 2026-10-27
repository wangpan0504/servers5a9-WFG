namespace DfoServer.GameWorld.Dungeons;

internal static class ImperialArenaEntryPolicy
{
    public static bool Supports(DungeonDefinition definition)
    {
        int mode = definition.Id switch { 320 => 1, 336 => 2, _ => 0 };
        int width = mode == 1 ? 6 : 8;
        bool Number(string tag, int value) => definition.RawSections.Where(row => row.Tag == tag).ToArray() is
            [{ Tokens: [{ Type: 0 } token] }] && token.Value == value;
        bool Marker(string tag) => definition.RawSections.Where(row => row.Tag == tag).ToArray() is [{ Tokens.Count: 0 }];
        return mode != 0 && Number("tag tournament dungeon", mode) && Number("tagsystem rule set", 1) &&
            Number("designate dungeon difficulty", 0) && Marker("disable exp") && Marker("disable drop item") &&
            Marker("no fatigue") && Marker("no giveup panalty") && definition.EnterWithoutFatigue && definition.MinimumLevel == 90 &&
            definition.Mazes is [var maze] && maze.Width == width && maze.Height == 1 && maze.ResourceError is null &&
            maze.Starts.SequenceEqual([new DungeonPoint(0, 0)]) && maze.Bosses.SequenceEqual([new DungeonPoint(width - 1, 0)]) &&
            maze.MapSpecification?.Count == width && maze.ConditionalMaps.Count == 0 && maze.LayeredMaps.Count == 0;
    }
}
