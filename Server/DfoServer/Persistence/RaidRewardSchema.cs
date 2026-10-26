namespace DfoServer.Persistence;

internal static class RaidRewardSchema
{
    internal const string Definition = """
        CREATE TABLE IF NOT EXISTS character_raid_reward_receipts(
            character_id INTEGER NOT NULL, account TEXT NOT NULL,
            instance_id TEXT NOT NULL CHECK(length(instance_id)=32 AND instance_id NOT GLOB '*[^0-9a-f]*' AND instance_id<>'00000000000000000000000000000000'),
            phase INTEGER NOT NULL CHECK(phase BETWEEN 0 AND 126),
            kind INTEGER NOT NULL CHECK(kind BETWEEN 0 AND 255),
            card INTEGER NOT NULL CHECK(card BETWEEN 0 AND 255),
            week_key INTEGER NOT NULL CHECK(week_key>0),
            final_phase INTEGER NOT NULL CHECK(final_phase IN(0,1)),
            plan_json TEXT NOT NULL CHECK(json_valid(plan_json) AND json_type(plan_json)='object'),
            mail_ids TEXT NOT NULL CHECK(json_valid(mail_ids) AND json_type(mail_ids)='array'),
            created_at INTEGER NOT NULL CHECK(created_at>=0),
            PRIMARY KEY(character_id,instance_id,phase,kind),
            FOREIGN KEY(character_id,account) REFERENCES characters(id,account) ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS character_raid_rewards_week ON character_raid_reward_receipts(character_id,week_key,final_phase,instance_id);
        """;
}
