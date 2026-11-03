using DfoServer.Game.Accounts;
using DfoServer.Game.Adventure;
using DfoServer.Game.Characters;
using DfoServer.GameWorld.Characters;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    // 子账本先于父账本删除；未知的新外键依赖会使整笔事务失败，而不是留下半份存档。
    static readonly string[] DeletionTables =
    [
        "character_strongway2019_statistics",
        "character_beast_dungeon_reservations",
        "character_beast_stage_rewards",
        "character_pcroom_claims", "character_fatigue_acceleration",
        "character_evildom_war_rewards",
        "character_evildom_war_hidden_rewards",
        "character_dungeon_incount_extra", "character_ekern_cycle", "character_rename_pending", "character_name_history",
        "character_evildom_war_seats",
        "character_pvp_daily_missions", "character_pvp_mission_rewards", "character_pvp_mission_facts", "character_pvp_rating_claims", "character_pvp_ratings",
        "character_luke_normal_drop_claims", "character_luke_normal_progress", "character_anton_normal_progress",
        "character_prey_normal_progress",
        "character_boss_selection_buff",
        "character_privilege_service",
        "character_tutorial_receipts", "character_tutorial_state",
        "character_grief_tower_clears", "character_grief_tower_entries", "character_grief_tower",
        "character_fiendwar_counter_stream", "character_fiendwar_title_facts", "character_title_book_slots", "character_title_progress", "character_title_sections",
        "character_dungeon_tournament_rewards", "character_dungeon_card_options", "character_dungeon_card_rewards",
        "character_dungeon_drop_pickups", "character_dungeon_drop_objects", "character_dungeon_drop_batches",
        "character_dungeon_passive_pickups",
        "character_dungeon_pickup_receipts", "character_dungeon_loot_receipts",
        "character_quest_stage_claims", "character_quest_stage_witnesses",
        "character_operation_hope_reward_receipts",
        "character_fiendwar_recovery", "character_raid_reward_receipts", "character_raid_runs",
        "character_quest_completion_receipts", "character_quest_clear_receipts", "character_quest_daily_clear", "character_quest_drop_receipts",
        "character_dungeon_settlement_receipts", "character_dungeon_scene_clear_receipts",
        "character_dungeon_growth_receipts", "character_adventure_credits",
        "character_dungeon_fatigue_receipts", "character_gold_card_uses",
        "character_quest_skip_receipts", "character_quest_lifecycle_receipts",
        "character_quest_special_reward_claims", "character_quest_effect_receipts",
        "character_active_main_quests", "character_completed_quests", "character_skill_bindings",
        "character_skill_points", "character_skills", "character_skill_commands", "character_skill_combos",
        "character_skill_point_sources", "character_tp_sources", "character_permanent_stats", "character_client_settings", "character_game_settings", "character_quest_notify", "character_epic_wishes",
        "character_fatigue", "character_dungeon_state", "mailbox_sent_gold", "character_teleport_cooldowns", "character_exp_bonus", "character_consumable_states", "character_expert_job", "character_expert_meta", "character_assault_daily", "character_beast_dungeon_entries", "character_crack_daily", "character_growup_change", "character_weekly_dungeon", "character_dungeon_incount",
        "character_collectbox_slots", "character_collectbox_expirations", "character_collectbox_state", "character_epic_production", "character_pvp_training_skills",
        "character_skill_switch_inventory_items", "character_skill_switch_inventory",
        // 公会成员行随角色删除清理（guild_members.character_id 外键到 characters）。
        "guild_members"
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
            SELECT c.id,c.slot,c.name,c.revision,EXISTS(SELECT 1 FROM account_adventure_expedition_members m WHERE m.account=$account AND m.character_id=c.id),
              (EXISTS(SELECT 1 FROM marriage_active_members m WHERE m.character_id=c.id) OR
               EXISTS(SELECT 1 FROM marriage_records r,json_each(r.aggregate_json,'$.Wedding.Gifts') gift
                 WHERE json_extract(gift.value,'$.CharacterId')=c.id AND json_extract(gift.value,'$.Settled')=0))
            FROM characters c WHERE c.account=$account AND c.slot=$slot
            """;
        command.Parameters.AddWithValue("$account", account);
        command.Parameters.AddWithValue("$slot", slot);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new InvalidDataException("Deletion slot absent");
        return new(checked((ushort)reader.GetInt32(0)), checked((ushort)reader.GetInt32(1)), reader.GetString(2), reader.GetInt64(3))
            { AdventureBusy = reader.GetBoolean(4), MarriageBusy = reader.GetBoolean(5) };
    }

    public CharacterDeletion Delete(AccountOwners.Lease owner, CharacterDeletion expected) => owner.Execute(() =>
    {
        using var db = database.Open();
        using var write = db.BeginWrite("CharacterRepository.Delete");
        using var tx = write.Transaction;
        if (ReadDeletionTarget(db, tx, owner.Account, expected.Slot) != expected)
            throw new InvalidOperationException("Character deletion identity or revision changed");
        if (expected.AdventureBusy) throw new InvalidDataException("Character participates in an adventure expedition");
        if (expected.MarriageBusy) throw new InvalidDataException("角色存在有效婚约或未结清婚礼礼金，不能删除");
        RequireDeletionDependencies(db, tx);
        RemoveTagEvildomCharacter(db, tx, owner.Account, expected.Id);
        RemoveImperialArenaCharacter(db, tx, owner.Account, expected.Id);
        RemovePvpTotalCharacter(db, tx, owner.Account, expected.Id);
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.Parameters.AddWithValue("$id", expected.Id);
        command.Parameters.AddWithValue("$account", owner.Account);
        command.CommandText = """
            SELECT 1 FROM guild_cargo_slots s JOIN guilds g ON g.guild_id=s.guild_id
            WHERE g.master_character_id=$id LIMIT 1
            """;
        if (command.ExecuteScalar() is not null)
            throw new InvalidDataException("公会仓库仍有物品，不能删除会长角色");
        // 会长角色被删除时公会不能留下悬空的 master_character_id：连同该公会全部成员行一起解散。
        // 接任/转让由公会成员玩法（阶段 3）接管后再替换本策略；在此之前解散是唯一不产生悬空状态的选择。
        command.CommandText = "DELETE FROM guild_members WHERE guild_id IN (SELECT guild_id FROM guilds WHERE master_character_id=$id)";
        command.ExecuteNonQuery();
        command.CommandText = "DELETE FROM guilds WHERE master_character_id=$id";
        command.ExecuteNonQuery();
        // 删除角色不减少账号冒险团总量：先把该角色里程净贡献折进开启经验，再由下方删除清单移除里程行。
        bool adventureChanged = false;
        if (adventureCatalog is { } catalog && growthCatalog is { } growth)
        {
            command.CommandText = """
                SELECT baseline_experience,position_experience,
                  (SELECT level FROM characters WHERE id=$id AND account=$account)
                FROM character_adventure_credits WHERE character_id=$id AND account=$account
                """;
            ulong baseline = 0, position = 0;
            int level = 0;
            bool credited = false;
            using (var reader = command.ExecuteReader())
            {
                if (reader.Read())
                {
                    baseline = checked((ulong)reader.GetInt64(0));
                    position = checked((ulong)reader.GetInt64(1));
                    level = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);
                    credited = true;
                }
            }
            if (credited)
            {
                ulong cap = growth.GetLevelStartExperience(CharacterGrowthCatalog.MaximumSupportedLevel);
                decimal rate = catalog.ExperienceRate;
                // 40级以下角色只有待追溯的自然经验，从未计入账号；删除时随角色一起作废，不折进开启经验。
                decimal contribution = level < AdventureCredit.CountingLevel ? 0
                    : AdventureCredit.Value(position, cap, rate) - AdventureCredit.Value(baseline, cap, rate);
                command.CommandText = "SELECT opening_experience FROM account_adventures WHERE account=$account";
                decimal opening = command.ExecuteScalar() is string text
                    ? decimal.Parse(text, System.Globalization.CultureInfo.InvariantCulture) : 0;
                command.CommandText = "UPDATE account_adventures SET opening_experience=$opening WHERE account=$account";
                command.Parameters.AddWithValue("$opening", (opening + contribution).ToString(System.Globalization.CultureInfo.InvariantCulture));
                command.ExecuteNonQuery();
                command.Parameters.RemoveAt("$opening");
                adventureChanged = contribution != 0;
            }
        }
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
        if (command.ExecuteNonQuery() != 0 || adventureChanged)
        {
            command.CommandText = "UPDATE account_adventures SET revision=revision+1 WHERE account=$account";
            command.ExecuteNonQuery();
        }
        command.CommandText = "DELETE FROM shop_purchase_counts WHERE account=$account AND character_id=$id";
        command.ExecuteNonQuery();
        command.CommandText = "DELETE FROM item_purchase_counts WHERE account=$account AND character_id=$id";
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
        command.CommandText = "DELETE FROM quest_eligibility_mail_dispatches WHERE account=$account AND scope_character_id=$id";
        command.ExecuteNonQuery();
        // 冷却留在账号/转职账本，删角、改名和重登不能重置；与角色删除同事务。
        command.CommandText="UPDATE account_pvp_private_characters SET blocked_until=$now+recreate_days*86400,character_id=NULL WHERE account=$account AND character_id=$id";
        command.Parameters.AddWithValue("$now",DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        command.ExecuteNonQuery();
        command.Parameters.RemoveAt("$now");
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
        var affected = DeletionTables.Concat(["characters", "cube_premium", "item_making_skill_info", "character_inventory_items", "character_inventory_slots",
            "inventory_avatar_details", "inventory_creature_details", "mailbox_messages", "mailbox_attachments", "mailbox_audit_log",
            "shop_purchase_counts", "item_purchase_counts", "account_completed_quests", "account_pvp_private_characters", "account_adventure_representatives",
            // ON DELETE SET NULL保留眩惑本周名额，不能加入按角色删除的表清单。
            "account_dazzlement_tower_bindings",
            "account_pvp_total_teams",
            // 队伍身份不随单个角色删除，独立评分及整局来源也继续保留。
            "account_pvp_total_ratings", "account_pvp_total_rating_claims",
            // 疲劳owner删除时级联清除日戳，不对没有account列的子表重复执行删除。
            "character_fatigue_reset_days",
            "character_server_item_usage",
            "account_adventure_receipts", "account_adventure_expedition_members", "account_adventure_expeditions",
            // 两端均以 ON DELETE CASCADE 清理，账号级冒险团好友不随单个角色删除。
            "character_social_links", "guild_membership_cooldowns", "guild_applications", "guild_cargo_slots", "guild_attendance",
            "guild_contribution_events", "guild_contribution_settlements", "guild_npc_progress", "guild_npc_crafts",
            // 活动婚约已由删除事务显式拒绝；历史聚合不外键角色，不能随删角清账。
            "marriage_active_members", "character_guild_hongbao_purchases",
            // 个人NPC福利由角色外键级联清理，公共福利及请求历史不随单个成员删除。
            "character_guild_npc_benefits", "character_guild_npc_item_crafts", "character_guild_npc_craft_weeks"])
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
