namespace DfoServer.Persistence;

internal static class PermanentStatSchema
{
    internal const string Sql = """
        CREATE TABLE character_permanent_stats(
          character_id INTEGER PRIMARY KEY,
          account TEXT NOT NULL,
          max_hp INTEGER NOT NULL CHECK(max_hp BETWEEN 0 AND 4294967295),
          max_mp INTEGER NOT NULL CHECK(max_mp BETWEEN 0 AND 4294967295),
          strength INTEGER NOT NULL CHECK(strength BETWEEN 0 AND 32767),
          vitality INTEGER NOT NULL CHECK(vitality BETWEEN 0 AND 32767),
          intelligence INTEGER NOT NULL CHECK(intelligence BETWEEN 0 AND 32767),
          spirit INTEGER NOT NULL CHECK(spirit BETWEEN 0 AND 32767),
          move_speed INTEGER NOT NULL CHECK(move_speed BETWEEN 0 AND 4294967295),
          all_element_resistance INTEGER NOT NULL CHECK(all_element_resistance BETWEEN 0 AND 32767),
          FOREIGN KEY(character_id,account) REFERENCES characters(id,account)
        ) STRICT;
        """;
}
