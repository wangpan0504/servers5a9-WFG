namespace DfoServer.Persistence;

internal static class EpicWishSchema
{
    public const string Definition = """
        CREATE TABLE character_epic_wishes (
            character_id INTEGER PRIMARY KEY,
            account TEXT NOT NULL,
            item_ids TEXT NOT NULL CHECK(json_valid(item_ids) AND json_type(item_ids)='array' AND json_array_length(item_ids)<=5),
            FOREIGN KEY(character_id,account) REFERENCES characters(id,account) ON DELETE CASCADE
        );
        """;
}
