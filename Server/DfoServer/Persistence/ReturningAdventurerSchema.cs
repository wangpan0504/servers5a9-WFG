namespace DfoServer.Persistence;

internal static class ReturningAdventurerSchema
{
    internal const string Definition = """
        CREATE TABLE account_returning_mission_progress(
            account TEXT NOT NULL,
            period_until INTEGER NOT NULL CHECK(period_until BETWEEN 1 AND 4294967295),
            mission_id INTEGER NOT NULL CHECK(mission_id BETWEEN 1 AND 100),
            progress INTEGER NOT NULL CHECK(progress BETWEEN 0 AND 4294967295),
            PRIMARY KEY(account,period_until,mission_id)
        ) STRICT;
        CREATE TABLE account_returning_reward_claims(
            account TEXT NOT NULL,
            period_until INTEGER NOT NULL CHECK(period_until BETWEEN 1 AND 4294967295),
            action INTEGER NOT NULL CHECK(action IN (1,3,4)),
            argument INTEGER NOT NULL CHECK(argument BETWEEN 0 AND 100),
            day INTEGER NOT NULL,
            recipient_id INTEGER NOT NULL,
            item_id INTEGER NOT NULL CHECK(item_id>0),
            item_count INTEGER NOT NULL CHECK(item_count>0),
            claimed_at INTEGER NOT NULL,
            PRIMARY KEY(account,period_until,action,argument,day)
        ) STRICT;
        """;
}
