namespace DfoServer.Persistence;

internal static class FatigueAccelerationSchema
{
    internal const string Definition = """
        CREATE TABLE character_fatigue_acceleration(
            character_id INTEGER PRIMARY KEY,
            account TEXT NOT NULL,
            enabled INTEGER NOT NULL CHECK(typeof(enabled)='integer' AND enabled IN(0,1)),
            FOREIGN KEY(character_id,account) REFERENCES characters(id,account) ON DELETE CASCADE
        );
        """;
}
