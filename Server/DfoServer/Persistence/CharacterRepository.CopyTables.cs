using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DfoServer.Game.Characters;
using DfoServer.Game.Pvp;
using DfoServer.Game.Quests;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    // 显式角色快照清单；增加字段或关联表后先审查复制含义，不能自动搬运新业务状态。
    sealed record CopyTable(string Name, string Owner, string Fields);
    static readonly string[] CopyCharacterColumns = "id,account,slot,name,revision,town,area,x,y,direction,return_town,return_area,return_x,return_y,return_direction,job,grow_type,level,accepted_story_level,gold,experience,inventory_expansion,revive_coins,win_points,gold_carry_limit,auction_gold_limit,cargo_capacity,quest_cycle_day,equipment_slot_flags,emotion,clone_title_item_id,selected_skill_tree,blacklist_count,avatar_expansion,devil_usage,npc_favor,daily_booster_usage,weapon_skin_state,avatar_preset_state,knight_shield_deck,stamina_until,inventory_items_revision".Split(',');
    static readonly CopyTable[] CopyTables =
    [
        new("character_active_main_quests", "character_id", "character_id,slot,quest_id,condition_state,acceptance_id"),
        new("character_adventure_credits", "character_id", "character_id,account,baseline_experience,position_experience"),
        new("character_anton_normal_progress", "character_id", "character_id,account,day,branch,progress"),
        new("character_assault_daily", "character_id", "character_id,account,day,clear_count,hidden_count"),
        new("character_beast_dungeon_entries", "character_id", "character_id,account,day,dungeon_id,used"),
        new("character_client_settings", "character_id", "character_id,account,character_options"),
        new("character_collectbox_expirations", "character_id", "character_id,account,box_index,expires_at"),
        new("character_collectbox_slots", "character_id", "character_id,account,box_index,slot_index,item_id"),
        new("character_collectbox_state", "character_id", "character_id,account,selected_box"),
        new("character_completed_quests", "character_id", "character_id,quest_id,daily_period,answer"),
        new("character_consumable_states", "character_id", "character_id,account,kind,item_id,until_ms,maintenance,cool_group"),
        new("character_crack_daily", "character_id", "character_id,account,day,clear_count,daily_reward"),
        new("character_dungeon_incount", "character_id", "character_id,account,dungeon,day,daily_used,week,weekly_used"),
        new("character_dungeon_incount_extra", "character_id", "character_id,account,dungeon,day,daily_extra,week,weekly_extra"),
        new("character_dungeon_reward_extra", "character_id", "character_id,account,kind,target,day,daily_extra,week,weekly_extra"),
        new("character_dungeon_state", "character_id", "character_id,account,next_object_key,last_attempt,capsule_experience,proficiency_level,proficiency_experience,recommended_dungeon_clears,practice_day,practice_used"),
        new("character_ekern_cycle", "character_id", "character_id,account,service_day,normal_clears,trial_clears"),
        new("character_epic_production", "character_id", "character_id,weapon_id,charge,service_week,weekly_uses,since_big_chance,special_uses,additional_weekly_uses,intermediate_material_remainder,ordinary_material_remainder"),
        new("character_epic_wishes", "character_id", "character_id,account,item_ids"),
        new("character_exp_bonus", "character_id", "character_id,account,until_unix,permille"),
        new("character_expert_job", "character_id", "character_id,account,job_type,expertness,learned_recipes"),
        new("character_expert_meta", "character_id", "character_id,account,giveup_state,machine_grade,machine_endurance"),
        new("character_fatigue", "character_id", "character_id,account,used,maximum,used_maximum,remaining,context_auxiliary,selection_auxiliary"),
        new("character_fatigue_acceleration", "character_id", "character_id,account,enabled"),
        new("character_fatigue_reset_days", "character_id", "character_id,service_day"),
        new("character_fatigue_battery", "character_id", "character_id,charging,buff"),
        new("character_fiendwar_title_facts", "character_id", "character_id,account,quest_id,total,applied"),
        new("character_game_settings", "character_id", "character_id,account,game_options"),
        new("character_hotkey_settings", "character_id", "character_id,account,profile,hotkeys"),
        new("character_gold_card_uses", "character_id", "character_id,account,service_day,used"),
        new("character_grief_tower", "character_id", "character_id,account,game_day,cleared,layer,instance_id,task_attempt"),
        new("character_growup_change", "character_id", "character_id,account,change_count"),
        new("character_guild_hongbao_purchases", "character_id", "character_id,day_start_unix,cera"),
        new("character_luke_normal_drop_claims", "character_id", "character_id,account,day,source_monster_id,first_event_id,first_instance_id"),
        new("character_luke_normal_progress", "character_id", "character_id,account,day,progress"),
        new("character_permanent_stats", "character_id", "character_id,account,max_hp,max_mp,strength,vitality,intelligence,spirit,move_speed,all_element_resistance"),
        new("character_prey_normal_progress", "character_id", "character_id,account,day,branch,progress"),
        new("character_pvp_daily_missions", "character_id", "character_id,account,service_day,state_json"),
        new("character_pvp_mission_facts", "character_id", "character_id,account,kind,job,parameter,instance_id"),
        new("character_pvp_mission_rewards", "character_id", "character_id,account,mission_id,rewards_json"),
        new("character_pvp_ratings", "character_id", "character_id,account,revision,state_json"),
        new("character_pvp_training_skills", "character_id", "character_id,state_json,operation_id,before_revision"),
        new("character_quest_completion_receipts", "character_id", "character_id,account,acceptance_id,quest_id,event_id,instance_id,task_attempt,epoch,generation,rule_id,resource_source,quest_source,native_index,base_experience,raw_additional_percent,difficulty_key,difficulty_weight,experience_amount,gold_amount,before_level,after_level,before_experience,after_experience,before_revision,revision,rewards,clear_acceptance_id,condition_type,selector,multiplier,consumed,changed_items,plan_snapshot"),
        new("character_quest_daily_clear", "character_id", "character_id,account,quest_id,day,event_id,revision"),
        new("character_quest_effect_receipts", "character_id", "character_id,operation_id,before_revision,revision,claimed_quests,changed_items"),
        new("character_quest_notify", "character_id", "character_id,account,entries"),
        new("character_npc_bookmarks", "character_id", "character_id,account,entries"),
        new("character_quest_special_reward_claims", "character_id", "character_id,quest_id,operation_id"),
        new("character_quest_stage_claims", "character_id", "character_id,account,source_acceptance_id,trigger_acceptance_id,target_level,before_experience,after_experience,amount,revision"),
        new("character_quest_stage_witnesses", "character_id", "character_id,account,source_acceptance_id,source_quest_id,source_revision,target_level,rule_version,status,proof"),
        new("character_server_item_usage", "character_id", "character_id,state_json"),
        new("character_skill_bindings", "character_id", "character_id,context,slot,skill_id,level"),
        new("character_skill_combos", "character_id", "character_id,context,root_id,children"),
        new("character_skill_commands", "character_id", "character_id,context,skill_id,keys"),
        new("character_skill_point_sources", "character_id", "character_id,bonus_sp"),
        new("character_skill_points", "character_id", "character_id,context,sp,tp"),
        new("character_skill_switch_inventory", "character_id", "character_id,account,skill_id"),
        new("character_skill_switch_inventory_items", "character_id", "character_id,equipment_part,item_uid,item_id"),
        new("character_skills", "character_id", "character_id,skill_id,level"),
        new("character_teleport_cooldowns", "character_id", "character_id,account,potion_until,aura_until,upgrade_aura_until,party_aura_until,privilege_until"),
        new("character_title_progress", "character_id", "character_id,account,quest_id,remaining1,remaining2,remaining3,completed_revision"),
        new("character_title_sections", "character_id", "character_id,account,section_key,revision"),
        new("character_tp_sources", "character_id", "character_id,bonus_tp"),
        new("character_tutorial_state", "character_id", "character_id,account,dungeon_id,completed,reward_received"),
        new("character_weekly_dungeon", "character_id", "character_id,account,week,slot,used"),
        new("cube_premium", "charac_no", "charac_no,selected,cube_type"),
        new("dungeon_despair_progress", "character_id", "character_id,account,next_layer,last_clear_day,clear_count"),
        new("item_making_skill_info", "charac_no", "charac_no,account,weapon,cloth,leather,light_armor,heavy_armor,plate,amulet,wrist,ring,support,magic_stone"),
        new("item_purchase_counts", "character_id", "account,character_id,item_id,period,day,count"),
        new("mailbox_sent_gold", "character_id", "character_id,day,amount"),
        new("shop_purchase_counts", "character_id", "account,character_id,npc_id,item_id,reset_daily,day,count"),
        new("character_quest_clear_receipts", "character_id", "character_id,account,acceptance_id,quest_id,quest_slot,event_id,instance_id,task_attempt,epoch,dungeon_id,map_id,generation,revision,source_type,growth_event_id,scene_operation_id"),
        new("character_dungeon_growth_receipts", "character_id", "character_id,account,event_id,instance_id,quest_id,task_attempt,epoch,map_id,monster_key,combat_sequence,generation,amount,rule_id,resource_source,monster_id,monster_level,before_level,after_level,before_experience,after_experience,revision,proficiency_gain"),
        new("character_dungeon_scene_clear_receipts", "character_id", "character_id,account,operation_id,source_type,instance_id,quest_id,task_attempt,before_epoch,epoch,dungeon_id,map_id,template_id,source_ordinal,fingerprint,death_events,generation,revision"),
        new("character_quest_lifecycle_receipts", "character_id", "character_id,account,operation_id,action,quest_id,acceptance_id,before_revision,revision,before_active,after_active,changed_items,scene_event,before_completed,after_completed"),
    ];
    // 社会关系、账号共享表、付费资格与领取账本、战斗运行身份和历史投递收据只属于原角色。
    static readonly HashSet<string> UncopiedCharacterTables = new(StringComparer.Ordinal)
    {
        "account_adventure_expedition_members",
        "account_adventure_receipts",
        "account_adventure_representatives",
        "account_completed_quests",
        "account_daily_attendance_days",
        "account_daily_attendance_claims",
        "account_dazzlement_tower_bindings",
        "account_evildom_war_guides",
        "account_fiendwar_guides",
        "account_single_dungeon_bindings",
        "account_material_audit",
        "account_monster_mock_battle_runs",
        "account_pcroom_timepoint_days",
        "account_pcroom_timepoint_segments",
        "account_pcroom_timepoint_claims",
        "account_returning_reward_claims",
        "account_weekly_attendance",
        "account_pvp_private_characters",
        "account_pvp_total_teams",
        "account_server_item_refills",
        "account_strongway2019_rewards",
        "auction_listings",
        "cargo_audit_log",
        "cash_shop_promotion_orders",
        "character_beast_dungeon_reservations",
        "character_beast_stage_rewards",
        "character_revised_beast_stage_rewards",
        "character_revised_beast_rewards",
        "character_boss_selection_buff",
        "character_various_buffs",
        "character_clear_reward_resets",
        "character_dungeon_card_options",
        "character_dungeon_card_rewards",
        "character_dungeon_drop_batches",
        "character_dungeon_drop_objects",
        "character_dungeon_drop_pickups",
        "character_dungeon_fatigue_receipts",
        "character_dungeon_loot_receipts",
        "character_dungeon_passive_pickups",
        "character_dungeon_pickup_receipts",
        "character_dungeon_settlement_receipts",
        "character_dungeon_tournament_rewards",
        "character_evildom_war_hidden_rewards",
        "character_evildom_war_rewards",
        "character_evildom_war_seats",
        "character_fiendwar_counter_stream",
        "character_fiendwar_recovery",
        "character_grief_tower_clears",
        "character_grief_tower_entries",
        "character_guild_npc_benefits",
        "character_guild_npc_craft_weeks",
        "character_guild_npc_item_crafts",
        "character_inventory_items",
        "character_inventory_slots",
        "character_name_history",
        "character_operation_hope_reward_receipts",
        "character_pcroom_claims",
        "character_privilege_service",
        "character_pvp_rating_claims",
        "character_quest_drop_receipts",
        "character_quest_skip_receipts",
        "character_raid_reward_receipts",
        "character_raid_runs",
        "character_rename_pending",
        "character_social_links",
        "character_strongway2019_completions",
        "character_strongway2019_match_facts",
        "character_strongway2019_statistics",
        "character_title_book_slots",
        "character_tutorial_receipts",
        "characters",
        "currency_audit_log",
        "dungeon_altar_clear_receipts",
        "dungeon_despair_clear_receipts",
        "dungeon_special_entry_receipts",
        "epic_piece_audit_log",
        "guild_applications",
        "guild_attendance",
        "guild_cargo_receipts",
        "guild_cargo_slots",
        "guild_contribution_events",
        "guild_contribution_settlements",
        "guild_dungeon_clear_receipts",
        "guild_dungeon_reward_receipts",
        "guild_dungeon_scroll_receipts",
        "guild_economy_events",
        "guild_economy_receipts",
        "guild_experience_receipts",
        "guild_growth_item_receipts",
        "guild_hongbao_claims",
        "guild_members",
        "guild_membership_cooldowns",
        "guild_mileage_history",
        "guild_npc_craft_receipts",
        "guild_npc_receipts",
        "guild_request_claims",
        "guild_teleport_receipts",
        "guilds",
        "inventory_audit_log",
        "mailbox_audit_log",
        "mailbox_messages",
        "marriage_active_members",
        "marriage_records",
        "quest_eligibility_mail_dispatches",
    };

    static string[] CopyColumns(SqliteConnection db, SqliteTransaction tx, string table)
    {
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = $"PRAGMA table_info(\"{table.Replace("\"", "\"\"")}\")";
        using var reader = command.ExecuteReader(); var result = new List<string>();
        while (reader.Read()) result.Add(reader.GetString(1));
        return result.ToArray();
    }

    static void CheckCopySchema(SqliteConnection db, SqliteTransaction tx)
    {
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'";
        var names = new List<string>(); using (var reader = command.ExecuteReader()) while (reader.Read()) names.Add(reader.GetString(0));
        var specs = CopyTables.ToDictionary(table => table.Name, StringComparer.Ordinal);
        string[] ownerFields = ["character_id", "charac_no", "scope_character_id", "owner_character_id", "recipient_id", "sender_id", "master_character_id", "first_id", "second_id", "third_id", "owner_id", "target_id", "seller_character_id", "current_bidder_character_id"];
        foreach (string table in names)
        {
            var columns = CopyColumns(db, tx, table);
            string[]? expected = table == "characters" ? CopyCharacterColumns :
                specs.TryGetValue(table, out var spec) ? spec.Fields.Split(',') : null;
            if (expected is not null)
            {
                // 复制始终显式指定字段名；正常迁移追加字段后的物理列顺序不影响语义。
                // 启动时对字段类型、默认值和约束的既有校验仍然保留，不在此猜写新字段。
                var missing = expected.Except(columns, StringComparer.Ordinal).ToArray();
                var extra = columns.Except(expected, StringComparer.Ordinal).ToArray();
                if (missing.Length != 0 || extra.Length != 0)
                    throw new InvalidDataException("角色存档结构不兼容：" + CopySchemaLabel(table) +
                        "；缺少字段：" + CopySchemaNames(missing) +
                        "；额外字段：" + CopySchemaNames(extra) + "。" + CopySchemaAdvice(extra.Length != 0));
                // 现有 CharacterRepository.Read 仍按下标读取前 19 列，只有后缀字段使用名字读取。
                if (table == "characters")
                {
                    int mismatch = Enumerable.Range(0, 19).FirstOrDefault(index => columns[index] != expected[index], -1);
                    if (mismatch >= 0)
                        throw new InvalidDataException("角色核心字段顺序不兼容：characters；第" + (mismatch + 1) +
                            "列当前：" + CopySchemaLabel(columns[mismatch]) + "；预期：" + expected[mismatch] + "。" + CopySchemaAdvice(true));
                }
            }
            if (!specs.ContainsKey(table) && !UncopiedCharacterTables.Contains(table) &&
                (table.StartsWith("character_", StringComparison.Ordinal) || columns.Intersect(ownerFields).Any()))
                throw new InvalidDataException("发现未适配的角色关联表：" + CopySchemaLabel(table) +
                    "；当前字段：" + CopySchemaNames(columns) + "；预期：须先适配复制规则。" + CopySchemaAdvice(true));
        }
        var missingTables = CopyTables.Select(table => table.Name).Append("characters").Except(names, StringComparer.Ordinal).ToArray();
        if (missingTables.Length != 0)
            throw new InvalidDataException("角色复制所需的存档结构不完整；缺少表：" + CopySchemaNames(missingTables) + "。" + CopySchemaAdvice(false));
    }

    static string CopySchemaAdvice(bool newerOrCustom) => newerOrCustom
        ? "请先停服备份，改用支持此存档的配套服务端；自定义结构需作者适配。不要手动删除字段或表，也不要降级存档。"
        : "请先停服备份，用配套服务端正常启动完成迁移，再刷新原角色；仍失败请反馈完整提示，不要手动改表。";

    // 每次只报首个名字及总项数；单个名字不超过 96 个 UTF-8 字节，避免异常结构撑大 GM 提示。
    static string CopySchemaNames(string[] names) => names.Length == 0 ? "无" :
        CopySchemaLabel(names[0]) + (names.Length == 1 ? "" : "等" + names.Length + "项");

    static string CopySchemaLabel(string name)
    {
        var result = new StringBuilder();
        int bytes = 0;
        foreach (var rune in name.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > 93) { result.Append('…'); break; }
            result.Append(rune.ToString());
            bytes += rune.Utf8SequenceLength;
        }
        return result.ToString();
    }

    static void CopyCharacterRows(SqliteConnection db, SqliteTransaction tx, Character source,
        ushort target, string account, IReadOnlyDictionary<long, long> uids)
    {
        var guids = new Dictionary<Guid, Guid>();
        Guid NewGuid(Guid value)
        {
            if (value == Guid.Empty) return value;
            if (!guids.TryGetValue(value, out var result)) guids.Add(value, result = Guid.NewGuid());
            return result;
        }
        string GuidText(string text) => Guid.TryParse(text, out var value) ? NewGuid(value).ToString(text.Length == 36 ? "D" : "N") : text;
        db.CreateFunction("gm_copy_guid", (string value) => GuidText(value));
        db.CreateFunction("gm_copy_uid", (long value) => uids.GetValueOrDefault(value));
        db.CreateFunction("gm_copy_snapshot", (string json) =>
        {
            var root = JsonNode.Parse(json) ?? throw new InvalidDataException("阶段来源快照缺失");
            JsonNode? Rewrite(JsonNode? node, string key = "")
            {
                if (node is JsonObject obj)
                {
                    foreach (var name in obj.Select(pair => pair.Key).ToArray()) obj[name] = Rewrite(obj[name]?.DeepClone(), name);
                    return obj;
                }
                if (node is JsonArray array) return new JsonArray(array.Select(child => Rewrite(child?.DeepClone())).ToArray());
                if (node is JsonValue value)
                {
                    if (value.TryGetValue<string>(out var text) && Guid.TryParse(text, out _)) return JsonValue.Create(GuidText(text));
                    if (value.TryGetValue<long>(out long number))
                    {
                        if (key == "CharacterId" && number == source.Id) return JsonValue.Create(target);
                        if (key is "Uid" or "ItemUid" or "ClearAvatarSourceUid" && uids.TryGetValue(number, out long uid)) return JsonValue.Create(uid);
                    }
                }
                return node;
            }
            return Rewrite(root)!.ToJsonString();
        });
        db.CreateFunction("gm_copy_stage", (string json) =>
        {
            var witness = JsonSerializer.Deserialize<QuestStageWitness>(json) ?? throw new InvalidDataException("阶段见证缺失");
            return JsonSerializer.Serialize(witness with { SourceAcceptanceId = NewGuid(witness.SourceAcceptanceId) });
        });
        db.CreateFunction("gm_copy_training", (string json) =>
        {
            var training = JsonSerializer.Deserialize<PvpTrainingSkills>(json) ?? throw new InvalidDataException("训练技能状态缺失");
            return JsonSerializer.Serialize(training with { AcceptanceId = NewGuid(training.AcceptanceId), CopyAcceptanceId = NewGuid(training.CopyAcceptanceId) });
        });
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.Parameters.AddWithValue("$source", source.Id); command.Parameters.AddWithValue("$target", target);
        command.Parameters.AddWithValue("$account", account);
        // 特殊奖励资格外键依赖效果标记；只复制资格所需标记，不复制原物品变更历史。
        var ordered = CopyTables.OrderBy(table => table.Name switch
        {
            "character_quest_effect_receipts" or "character_quest_lifecycle_receipts" or "character_dungeon_growth_receipts" or
                "character_skill_points" or "character_skill_switch_inventory" or "character_fatigue" => 0,
            "character_dungeon_scene_clear_receipts" => 1,
            "character_quest_clear_receipts" => 2,
            "character_quest_completion_receipts" => 3,
            "character_quest_stage_witnesses" => 4,
            "character_quest_special_reward_claims" or "character_quest_stage_claims" or "character_skill_switch_inventory_items" => 5,
            _ => 3
        });
        foreach (var table in ordered)
        {
            var columns = table.Fields.Split(',');
            string Select(string column)
            {
                if (column == table.Owner) return "$target";
                if (table.Name == "character_skill_switch_inventory_items" && column == "item_uid") return "gm_copy_uid(item_uid)";
                if (column is "acceptance_id" or "source_acceptance_id" or "trigger_acceptance_id" or "operation_id" or "instance_id" or "first_event_id" or "first_instance_id" or "event_id" or "clear_acceptance_id" or "growth_event_id" or "scene_operation_id") return $"gm_copy_guid({column})";
                if (table.Name == "character_quest_stage_witnesses" && column == "proof") return "gm_copy_stage(proof)";
                if (table.Name == "character_pvp_training_skills" && column == "state_json") return "gm_copy_training(state_json)";
                if (table.Name == "character_quest_effect_receipts" && column == "changed_items") return "'[]'";
                if ((table.Name == "character_quest_completion_receipts" && column is "plan_snapshot" or "consumed" or "changed_items" or "rewards") ||
                    (table.Name == "character_quest_lifecycle_receipts" && column is "before_active" or "after_active" or "changed_items")) return $"gm_copy_snapshot({column})";
                if (table.Name == "character_adventure_credits" && column == "baseline_experience") return "position_experience";
                return column;
            }
            string filter = table.Owner + "=$source" + (columns.Contains("account") ? " AND account=$account" : "");
            if (table.Name == "character_quest_effect_receipts") filter += " AND operation_id IN(SELECT operation_id FROM character_quest_special_reward_claims WHERE character_id=$source)";
            const string completions = "SELECT * FROM character_quest_completion_receipts WHERE character_id=$source AND acceptance_id IN(SELECT source_acceptance_id FROM character_quest_stage_witnesses WHERE character_id=$source UNION SELECT trigger_acceptance_id FROM character_quest_stage_claims WHERE character_id=$source)";
            string clears = "SELECT * FROM character_quest_clear_receipts WHERE character_id=$source AND acceptance_id IN(SELECT clear_acceptance_id FROM (" + completions + "))";
            string scenes = "SELECT * FROM character_dungeon_scene_clear_receipts WHERE character_id=$source AND operation_id IN(SELECT scene_operation_id FROM (" + clears + "))";
            if (table.Name == "character_quest_completion_receipts") filter += " AND acceptance_id IN(SELECT acceptance_id FROM (" + completions + "))";
            if (table.Name == "character_quest_clear_receipts") filter += " AND acceptance_id IN(SELECT acceptance_id FROM (" + clears + "))";
            if (table.Name == "character_dungeon_scene_clear_receipts") filter += " AND operation_id IN(SELECT operation_id FROM (" + scenes + "))";
            if (table.Name == "character_quest_lifecycle_receipts") filter += " AND operation_id IN(SELECT operation_id FROM (" + scenes + "))";
            if (table.Name == "character_dungeon_growth_receipts") filter += " AND event_id IN(SELECT growth_event_id FROM (" + clears + "))";
            if (table.Name == "character_skill_switch_inventory_items") filter += " AND gm_copy_uid(item_uid)>0";
            command.CommandText = $"INSERT INTO {table.Name}({table.Fields}) SELECT {string.Join(',', columns.Select(Select))} FROM {table.Name} WHERE {filter}";
            command.ExecuteNonQuery();
        }
    }
}
