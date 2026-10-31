namespace DfoServer.Persistence;

internal static class FiendwarRecoverySchema
{
    internal const string Definition = """
        CREATE TABLE character_fiendwar_recovery(
          character_id INTEGER NOT NULL, instance_id TEXT NOT NULL,
          owner_generation INTEGER NOT NULL CHECK(owner_generation>0),
          selection_epoch INTEGER NOT NULL CHECK(selection_epoch>=0),
          state INTEGER NOT NULL CHECK(state IN(0,1,2)),
          rejoin_until INTEGER,
          payment_json TEXT NOT NULL CHECK(json_valid(payment_json) AND json_type(payment_json)='object'),
          PRIMARY KEY(character_id,instance_id),
          CHECK((state=0 AND (rejoin_until IS NULL OR rejoin_until>=0)) OR (state IN(1,2) AND rejoin_until IS NOT NULL AND rejoin_until>=0)),
          FOREIGN KEY(character_id,instance_id) REFERENCES character_raid_runs(character_id,instance_id) ON DELETE CASCADE
        );
        """;
}
