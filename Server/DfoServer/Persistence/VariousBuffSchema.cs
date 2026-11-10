using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal static class VariousBuffSchema
{
    internal const string Definition = """
        CREATE TABLE character_various_buffs(
            character_id INTEGER PRIMARY KEY,
            account TEXT NOT NULL,
            buffs TEXT NOT NULL CHECK(json_valid(buffs)),
            FOREIGN KEY(character_id,account) REFERENCES characters(id,account) ON DELETE CASCADE
        ) STRICT;
        """;
}
