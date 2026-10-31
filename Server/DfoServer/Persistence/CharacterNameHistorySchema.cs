namespace DfoServer.Persistence;

internal static class CharacterNameHistorySchema
{
    internal const string Definition = """
        CREATE TABLE IF NOT EXISTS character_name_history(
          character_id INTEGER NOT NULL,account TEXT NOT NULL,revision INTEGER NOT NULL CHECK(revision>=0),
          former_name TEXT NOT NULL,changed_at INTEGER NOT NULL CHECK(changed_at BETWEEN 0 AND 4294967295),
          PRIMARY KEY(character_id,revision),FOREIGN KEY(character_id,account) REFERENCES characters(id,account));
        """;
}
