namespace DfoServer.Persistence;

internal static class DungeonIncountExtraSchema
{
    public const string Table = "character_dungeon_incount_extra";
    public const string Definition = """
        CREATE TABLE IF NOT EXISTS character_dungeon_incount_extra(
          character_id INTEGER NOT NULL,account TEXT NOT NULL,dungeon INTEGER NOT NULL CHECK(dungeon>0),
          day INTEGER NOT NULL,daily_extra INTEGER NOT NULL CHECK(daily_extra BETWEEN 0 AND 1000000),
          week INTEGER NOT NULL,weekly_extra INTEGER NOT NULL CHECK(weekly_extra BETWEEN 0 AND 1000000),
          PRIMARY KEY(character_id,dungeon),
          FOREIGN KEY(character_id,account) REFERENCES characters(id,account) ON DELETE CASCADE);
        """;
}
