namespace DfoServer.Persistence;

internal static class CharacterRenameCardSchema
{
    internal static string OriginalDefinition => Definition.Replace("CHECK(source_uid>=0)", "CHECK(source_uid>0)", StringComparison.Ordinal);
    internal const string Definition = """
        CREATE TABLE IF NOT EXISTS character_rename_pending(
          character_id INTEGER NOT NULL,account TEXT NOT NULL,source_uid INTEGER NOT NULL CHECK(source_uid>=0),
          PRIMARY KEY(character_id),FOREIGN KEY(character_id,account) REFERENCES characters(id,account));
        """;
}
