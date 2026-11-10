namespace DfoServer.Persistence;

internal static class DungeonCardRewardSchema
{
    // 只排除刷新前已冻结的奖励额度，不改写牌面、领取状态或原实例身份。
    internal const string ResetSql = """
        CREATE TABLE character_clear_reward_resets(
          character_id INTEGER NOT NULL,instance_id TEXT NOT NULL,
          PRIMARY KEY(character_id,instance_id),
          FOREIGN KEY(character_id,instance_id) REFERENCES character_dungeon_card_rewards(character_id,instance_id) ON DELETE CASCADE);
        """;

    internal const string Sql = """
        CREATE TABLE character_dungeon_card_rewards(
          character_id INTEGER NOT NULL,account TEXT NOT NULL,instance_id TEXT NOT NULL,
          plan_json TEXT NOT NULL CHECK(json_valid(plan_json) AND json_type(plan_json)='object'),
          reward_json TEXT NOT NULL CHECK(json_valid(reward_json) AND json_type(reward_json)='object'),
          mail_id INTEGER,revision INTEGER NOT NULL CHECK(revision>=0),
          PRIMARY KEY(character_id,instance_id),
          FOREIGN KEY(character_id,account) REFERENCES characters(id,account),
          FOREIGN KEY(character_id,instance_id) REFERENCES character_dungeon_settlement_receipts(character_id,instance_id),
          FOREIGN KEY(mail_id) REFERENCES mailbox_messages(mail_id));
        """;
}
