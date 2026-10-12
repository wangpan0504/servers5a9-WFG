using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    // 子账本先于父账本删除；未知的新外键依赖会使整笔事务失败，而不是留下半份存档。
    static readonly string[] DeletionTables =
    [
        "character_tutorial_receipts", "character_tutorial_state",
        "character_title_book_slots", "character_title_progress", "character_title_sections",
        "character_dungeon_card_options", "character_dungeon_card_rewards",
        "character_dungeon_drop_pickups", "character_dungeon_drop_objects", "character_dungeon_drop_batches",
        "character_dungeon_pickup_receipts", "character_dungeon_loot_receipts",
        "character_quest_completion_receipts", "character_quest_clear_receipts", "character_quest_drop_receipts",
        "character_dungeon_growth_receipts", "character_dungeon_settlement_receipts",
        "character_dungeon_fatigue_receipts", "character_gold_card_uses",
        "character_quest_skip_receipts", "character_quest_lifecycle_receipts",
        "character_active_main_quests", "character_completed_quests", "character_skill_bindings",
        "character_skill_points", "character_skills", "character_skill_commands", "character_skill_combos",
        "character_skill_point_sources", "character_tp_sources", "character_client_settings", "character_game_settings",
        "character_fatigue", "character_dungeon_state", "mailbox_sent_gold"
    ];

    public CharacterDeletion LoadDeletionTarget(AccountOwners.Lease owner, ushort slot) => owner.Execute(() =>
    {
        using var db = database.Open();
        return ReadDeletionTarget(db, null, owner.Account, slot);
    });

    static CharacterDeletion ReadDeletionTarget(SqliteConnection db, SqliteTransaction? tx, string account, ushort slot)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            SELECT c.id,c.slot,c.name,c.revision,EXISTS(SELECT 1 FROM account_adventure_expedition_members m WHERE m.account=$account AND m.character_id=c.id)
            FROM characters c WHERE c.account=$account AND c.slot=$slot
            """;
        command.Parameters.AddWithValue("$account", account);
        command.Parameters.AddWithValue("$slot", slot);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new InvalidDataException("Deletion slot absent");
        return new(checked((ushort)reader.GetInt32(0)), checked((ushort)reader.GetInt32(1)), reader.GetString(2), reader.GetInt64(3))
            { AdventureBusy = reader.GetBoolean(4) };
    }

    public CharacterDeletion Delete(AccountOwners.Lease owner, CharacterDeletion expected) => owner.Execute(() =>
    {
        using var db = database.Open();
        using var tx = db.BeginTransaction(deferred: false);
        if (ReadDeletionTarget(db, tx, owner.Account, expected.Slot) != expected)
            throw new InvalidOperationException("Character deletion identity or revision changed");
        if (expected.AdventureBusy) throw new InvalidDataException("Character participates in an adventure expedition");
        RequireDeletionDependencies(db, tx);
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.Parameters.AddWithValue("$id", expected.Id);
        command.Parameters.AddWithValue("$account", owner.Account);
        foreach (string table in DeletionTables)
        {
            command.CommandText = "DELETE FROM " + table + " WHERE character_id=$id";
            command.ExecuteNonQuery();
        }
        // 账号任务完成事实独立于来源角色；仅解除来源，不改派到其他角色。
        command.CommandText = "UPDATE account_completed_quests SET character_id=NULL WHERE account=$account AND character_id=$id";
        command.ExecuteNonQuery();
        command.CommandText = "UPDATE account_adventure_receipts SET character_id=NULL WHERE account=$account AND character_id=$id";
        command.ExecuteNonQuery();
        command.CommandText = "UPDATE account_adventure_representatives SET character_id=NULL WHERE account=$account AND character_id=$id";
        if (command.ExecuteNonQuery() != 0)
        {
            command.CommandText = "UPDATE account_adventures SET revision=revision+1 WHERE account=$account";
            command.ExecuteNonQuery();
        }
        command.CommandText = "DELETE FROM shop_purchase_counts WHERE account=$account AND character_id=$id";
        command.ExecuteNonQuery();
        // 发给其他角色的邮件保留发送者快照；本角色收件邮箱连同历史和托管资产一并清理。
        command.CommandText = "DELETE FROM mailbox_audit_log WHERE mail_id IN (SELECT mail_id FROM mailbox_messages WHERE recipient_id=$id AND recipient_account=$account)";
        command.ExecuteNonQuery();
        command.CommandText = "DELETE FROM mailbox_attachments WHERE mail_id IN (SELECT mail_id FROM mailbox_messages WHERE recipient_id=$id AND recipient_account=$account)";
        command.ExecuteNonQuery();
        command.CommandText = "DELETE FROM mailbox_messages WHERE recipient_id=$id AND recipient_account=$account";
        command.ExecuteNonQuery();
        // 私人物品包含背包、穿戴、个人仓库与邮箱托管；账号仓库的NULL owner保持原样。
        command.CommandText = "DELETE FROM character_inventory_items WHERE account=$account AND character_id=$id";
        command.ExecuteNonQuery();
        command.CommandText = "DELETE FROM characters WHERE id=$id AND account=$account AND slot=$slot AND name=$name AND revision=$revision";
        command.Parameters.AddWithValue("$slot", expected.Slot);
        command.Parameters.AddWithValue("$name", expected.Name);
        command.Parameters.AddWithValue("$revision", expected.Revision);
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("Character deletion identity or revision changed");
        tx.Commit();
        return expected;
    });

    static void RequireDeletionDependencies(SqliteConnection db, SqliteTransaction tx)
    {
        var affected = DeletionTables.Concat(["characters", "character_inventory_items", "character_inventory_slots",
            "inventory_avatar_details", "inventory_creature_details", "mailbox_messages", "mailbox_attachments", "mailbox_audit_log",
            "shop_purchase_counts", "account_completed_quests", "account_adventure_representatives", "account_adventures",
            "account_adventure_receipts", "account_adventure_expedition_members", "account_adventure_expeditions"])
            .ToHashSet(StringComparer.Ordinal);
        using var query = db.CreateCommand();
        query.Transaction = tx;
        query.CommandText = """
            SELECT m.name,f."table" FROM sqlite_schema m,pragma_foreign_key_list(m.name) f
            WHERE m.type='table' AND m.name NOT LIKE 'sqlite_%'
            """;
        using var reader = query.ExecuteReader();
        while (reader.Read())
            if (!affected.Contains(reader.GetString(0)) && affected.Contains(reader.GetString(1)))
                throw new InvalidDataException("删除角色存在未识别的外键依赖：" + reader.GetString(0));
    }
}
