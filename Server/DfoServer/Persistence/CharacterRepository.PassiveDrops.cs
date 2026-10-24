namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    internal const string PassiveDropSchema = """
        CREATE TABLE character_dungeon_passive_pickups(
          character_id INTEGER NOT NULL,account TEXT NOT NULL,
          ground_key INTEGER NOT NULL CHECK(ground_key BETWEEN 1 AND 65535),
          instance_id TEXT NOT NULL CHECK(length(instance_id)=32),
          task_attempt INTEGER NOT NULL CHECK(task_attempt>0),epoch INTEGER NOT NULL CHECK(epoch>0),
          picked_core TEXT,gold_amount INTEGER NOT NULL CHECK(gold_amount>=0),
          destination_slot INTEGER,item_uid INTEGER,after_core TEXT,currency_kind INTEGER,
          amount INTEGER NOT NULL CHECK(amount>0),after_balance INTEGER,account_revision INTEGER,
          revision INTEGER NOT NULL CHECK(revision>0),
          CHECK((picked_core IS NULL AND gold_amount>0) OR
            (picked_core IS NOT NULL AND json_valid(picked_core) AND json_type(picked_core)='object' AND gold_amount=0)),
          CHECK((destination_slot IS NOT NULL AND item_uid IS NOT NULL AND destination_slot BETWEEN 0 AND 351
            AND item_uid>0 AND after_core IS NOT NULL AND json_valid(after_core)
            AND json_type(after_core)='object' AND currency_kind IS NULL AND after_balance IS NULL AND account_revision IS NULL)
            OR (destination_slot IS NULL AND item_uid IS NULL AND after_core IS NULL AND currency_kind IS NOT NULL
            AND currency_kind BETWEEN 0 AND 18 AND after_balance IS NOT NULL AND after_balance>=0
            AND account_revision IS NOT NULL AND account_revision>=0)),
          PRIMARY KEY(character_id,ground_key),
          FOREIGN KEY(character_id,account) REFERENCES characters(id,account) ON DELETE CASCADE);
        """;

}
