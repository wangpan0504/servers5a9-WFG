using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal static class DungeonPremiumCardSchema
{
    internal static readonly string[] Statements =
    [
        """
        CREATE TABLE account_card_memberships(
          account TEXT PRIMARY KEY,black_diamond_expires INTEGER NOT NULL DEFAULT 0 CHECK(black_diamond_expires>=0),
          gold_card_expires INTEGER NOT NULL DEFAULT 0 CHECK(gold_card_expires>=0),revision INTEGER NOT NULL DEFAULT 0 CHECK(revision>=0));
        """,
        """
        CREATE TABLE character_gold_card_uses(
          character_id INTEGER NOT NULL,account TEXT NOT NULL,service_day INTEGER NOT NULL,used INTEGER NOT NULL CHECK(used BETWEEN 0 AND 10),
          PRIMARY KEY(character_id,service_day),FOREIGN KEY(character_id,account) REFERENCES characters(id,account));
        """,
        """
        CREATE TABLE character_dungeon_card_options(
          character_id INTEGER NOT NULL,account TEXT NOT NULL,instance_id TEXT NOT NULL,
          options_json TEXT NOT NULL CHECK(json_valid(options_json) AND json_type(options_json)='object'),
          black_diamond_reward_json TEXT CHECK(black_diamond_reward_json IS NULL OR json_valid(black_diamond_reward_json)),
          bonus_mail_id INTEGER,paid_reward_json TEXT CHECK(paid_reward_json IS NULL OR json_valid(paid_reward_json)),
          paid_mail_id INTEGER,paid_cost INTEGER CHECK(paid_cost IS NULL OR paid_cost>=0),paid_revision INTEGER,paid_day INTEGER,
          PRIMARY KEY(character_id,instance_id),
          FOREIGN KEY(character_id,account) REFERENCES characters(id,account),
          FOREIGN KEY(character_id,instance_id) REFERENCES character_dungeon_card_rewards(character_id,instance_id),
          FOREIGN KEY(bonus_mail_id) REFERENCES mailbox_messages(mail_id),FOREIGN KEY(paid_mail_id) REFERENCES mailbox_messages(mail_id));
        """
    ];

    internal static void Create(SqliteConnection db,SqliteTransaction tx)
    {
        foreach (string sql in Statements) SqliteMigrations.Execute(db,tx,sql);
    }
}
