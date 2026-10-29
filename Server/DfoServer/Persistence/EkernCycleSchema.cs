namespace DfoServer.Persistence;

internal static class EkernCycleSchema
{
    internal const string Definition = """
        CREATE TABLE IF NOT EXISTS character_ekern_cycle(
          character_id INTEGER NOT NULL,account TEXT NOT NULL,service_day INTEGER NOT NULL CHECK(service_day>=0),
          normal_clears INTEGER NOT NULL CHECK(normal_clears BETWEEN 0 AND 3),
          trial_clears INTEGER NOT NULL CHECK(trial_clears BETWEEN 0 AND 1),
          PRIMARY KEY(character_id),FOREIGN KEY(character_id,account) REFERENCES characters(id,account));
        """;
}
