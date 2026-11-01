namespace DfoServer.Persistence;

internal static class PrivilegeServiceSchema
{
    internal const string Definition = """
        CREATE TABLE character_privilege_service(
          character_id INTEGER PRIMARY KEY REFERENCES characters(id) ON DELETE CASCADE,
          started_at INTEGER NOT NULL CHECK(started_at>0),
          expires_at INTEGER NOT NULL CHECK(expires_at>started_at AND expires_at<=4294967295),
          purchase_season INTEGER NOT NULL CHECK(purchase_season>=0),
          reward_season INTEGER NOT NULL CHECK(reward_season>=0),
          last_login_day INTEGER NOT NULL CHECK(last_login_day BETWEEN 1 AND 3652058),
          login_days INTEGER NOT NULL CHECK(login_days BETWEEN 1 AND last_login_day),
          daily_claim_day INTEGER NOT NULL CHECK(daily_claim_day BETWEEN 0 AND last_login_day),
          claimed_milestones INTEGER NOT NULL CHECK(claimed_milestones BETWEEN 0 AND 4294967295)
        );
        """;
}
