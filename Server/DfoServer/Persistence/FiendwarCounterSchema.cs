namespace DfoServer.Persistence;

internal static class FiendwarCounterSchema
{
    internal const string Definition = """
        CREATE TABLE character_fiendwar_counter_stream(
          character_id INTEGER PRIMARY KEY, account TEXT NOT NULL,
          stream TEXT NOT NULL CHECK(length(stream) BETWEEN 1 AND 256),
          sequence INTEGER NOT NULL CHECK(sequence BETWEEN 1 AND 4294967295),
          FOREIGN KEY(character_id,account) REFERENCES characters(id,account)
        );
        """;
}
