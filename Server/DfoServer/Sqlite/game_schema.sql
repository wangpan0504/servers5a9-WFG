CREATE TABLE character_dungeon_tournament_rewards(
          character_id INTEGER NOT NULL,account TEXT NOT NULL,instance_id TEXT NOT NULL,
          plan_json TEXT NOT NULL CHECK(json_valid(plan_json) AND json_type(plan_json)='object'),
          first_choice INTEGER NOT NULL CHECK(first_choice BETWEEN 0 AND 1),
          second_choice INTEGER NOT NULL CHECK(second_choice BETWEEN 0 AND 1),
          rewards_json TEXT NOT NULL CHECK(json_valid(rewards_json) AND json_type(rewards_json)='array' AND json_array_length(rewards_json)=2),
          mail_id INTEGER,revision INTEGER NOT NULL CHECK(revision>=0),
          PRIMARY KEY(character_id,instance_id),
          FOREIGN KEY(character_id,account) REFERENCES characters(id,account),
          FOREIGN KEY(character_id,instance_id) REFERENCES character_dungeon_settlement_receipts(character_id,instance_id),
          FOREIGN KEY(mail_id) REFERENCES mailbox_messages(mail_id));
CREATE TABLE character_anton_normal_progress(
          character_id INTEGER PRIMARY KEY,
          account TEXT NOT NULL,
          day INTEGER NOT NULL CHECK(typeof(day)='integer' AND day BETWEEN 10000101 AND 99991231),
          branch INTEGER NOT NULL CHECK(typeof(branch)='integer' AND branch BETWEEN 1 AND 4),
          progress INTEGER NOT NULL CHECK(typeof(progress)='integer' AND progress BETWEEN 0 AND 6),
          FOREIGN KEY(character_id,account) REFERENCES characters(id,account)
        );
CREATE TABLE character_luke_normal_drop_claims(
          character_id INTEGER NOT NULL,
          account TEXT NOT NULL,
          day INTEGER NOT NULL CHECK(typeof(day)='integer' AND day BETWEEN 10000101 AND 99991231),
          source_monster_id INTEGER NOT NULL CHECK(typeof(source_monster_id)='integer' AND source_monster_id>0),
          first_event_id TEXT NOT NULL CHECK(length(first_event_id)=32),
          first_instance_id TEXT NOT NULL CHECK(length(first_instance_id)=32),
          PRIMARY KEY(character_id,day,source_monster_id),
          UNIQUE(character_id,first_event_id),
          FOREIGN KEY(character_id,account) REFERENCES characters(id,account)
        );
CREATE TABLE character_prey_normal_progress(
          character_id INTEGER PRIMARY KEY,
          account TEXT NOT NULL,
          day INTEGER NOT NULL CHECK(typeof(day)='integer' AND day BETWEEN 10000101 AND 99991231),
          branch INTEGER NOT NULL CHECK(typeof(branch)='integer' AND branch BETWEEN 1 AND 6),
          progress INTEGER NOT NULL CHECK(typeof(progress)='integer' AND progress BETWEEN 0 AND 5),
          FOREIGN KEY(character_id,account) REFERENCES characters(id,account)
        );
CREATE TABLE character_luke_normal_progress(
          character_id INTEGER PRIMARY KEY,
          account TEXT NOT NULL,
          day INTEGER NOT NULL CHECK(typeof(day)='integer' AND day BETWEEN 10000101 AND 99991231),
          progress INTEGER NOT NULL CHECK(typeof(progress)='integer' AND progress BETWEEN 0 AND 6),
          FOREIGN KEY(character_id,account) REFERENCES characters(id,account)
        );
CREATE TABLE account_tag_evildom_runs(
  account TEXT NOT NULL,
  instance_id TEXT NOT NULL CHECK(length(instance_id)=32),
  first INTEGER NOT NULL CHECK(typeof(first)='integer' AND first BETWEEN 1 AND 65535),
  second INTEGER NOT NULL CHECK(typeof(second)='integer' AND second BETWEEN 1 AND 65535 AND second<>first),
  task_attempt INTEGER NOT NULL CHECK(typeof(task_attempt)='integer' AND task_attempt>0),
  configuration_revision INTEGER NOT NULL CHECK(typeof(configuration_revision)='integer' AND configuration_revision>0),
  team_index INTEGER NOT NULL CHECK(typeof(team_index)='integer' AND team_index BETWEEN 0 AND 2),
  dungeon_id INTEGER NOT NULL CHECK(dungeon_id IN(9608,400000095)),
  resource_key TEXT NOT NULL CHECK(length(resource_key)=64),
  admitted_at INTEGER NOT NULL CHECK(typeof(admitted_at)='integer' AND admitted_at>=0),
  clearance_receipt TEXT,
  clear_event TEXT CHECK(clear_event IS NULL OR length(clear_event)=32),
  reward_json TEXT CHECK(reward_json IS NULL OR json_valid(reward_json)),
  cleared_at INTEGER CHECK(cleared_at IS NULL OR (typeof(cleared_at)='integer' AND cleared_at>=0)),
  elapsed_ms INTEGER CHECK(elapsed_ms IS NULL OR (typeof(elapsed_ms)='integer' AND elapsed_ms>=0)),
  mail_id INTEGER CHECK(mail_id IS NULL OR (typeof(mail_id)='integer' AND mail_id>0)),
  PRIMARY KEY(account,instance_id),UNIQUE(first,task_attempt),
  CHECK((clearance_receipt IS NULL AND clear_event IS NULL AND reward_json IS NULL AND cleared_at IS NULL AND elapsed_ms IS NULL AND mail_id IS NULL)
    OR (clearance_receipt IS NOT NULL AND clearance_receipt=instance_id AND clear_event IS NOT NULL AND reward_json IS NOT NULL AND cleared_at IS NOT NULL AND elapsed_ms IS NOT NULL AND mail_id IS NOT NULL)),
  FOREIGN KEY(account,clearance_receipt) REFERENCES account_tag_evildom_clearances(account,receipt)
);

-- S5A9 当前完整数据库结构；账号与游戏数据统一保存。
CREATE TABLE account_tag_evildom_clearances(
  receipt TEXT NOT NULL CHECK(length(receipt)>0),
  account TEXT NOT NULL,
  day INTEGER NOT NULL CHECK(typeof(day)='integer' AND day>0),
  week INTEGER NOT NULL CHECK(typeof(week)='integer' AND week>0),
  first INTEGER NOT NULL CHECK(typeof(first)='integer' AND first BETWEEN 1 AND 65535),
  second INTEGER NOT NULL CHECK(typeof(second)='integer' AND second BETWEEN 1 AND 65535 AND second<>first),
  PRIMARY KEY(account,receipt)
);
CREATE TABLE account_tag_evildom_configuration(
  account TEXT PRIMARY KEY,
  revision INTEGER NOT NULL CHECK(typeof(revision)='integer' AND revision>0),
  selected_team INTEGER NOT NULL CHECK(typeof(selected_team)='integer' AND selected_team BETWEEN 0 AND 2),
  first0 INTEGER NOT NULL CHECK(typeof(first0)='integer' AND first0 BETWEEN 0 AND 65535),
  second0 INTEGER NOT NULL CHECK(typeof(second0)='integer' AND second0 BETWEEN 0 AND 65535),
  first1 INTEGER NOT NULL CHECK(typeof(first1)='integer' AND first1 BETWEEN 0 AND 65535),
  second1 INTEGER NOT NULL CHECK(typeof(second1)='integer' AND second1 BETWEEN 0 AND 65535),
  first2 INTEGER NOT NULL CHECK(typeof(first2)='integer' AND first2 BETWEEN 0 AND 65535),
  second2 INTEGER NOT NULL CHECK(typeof(second2)='integer' AND second2 BETWEEN 0 AND 65535),
  CHECK((first0=0 AND second0=0) OR (first0>0 AND second0>0 AND first0<>second0)),
  CHECK((first1=0 AND second1=0) OR (first1>0 AND second1>0 AND first1<>second1)),
  CHECK((first2=0 AND second2=0) OR (first2>0 AND second2>0 AND first2<>second2))
);
CREATE TABLE item_making_skill_info(
  charac_no INTEGER PRIMARY KEY,
  account TEXT NOT NULL,
  weapon INTEGER NOT NULL DEFAULT 0 CHECK(weapon BETWEEN 0 AND 1000),
  cloth INTEGER NOT NULL DEFAULT 0 CHECK(cloth BETWEEN 0 AND 1000),
  leather INTEGER NOT NULL DEFAULT 0 CHECK(leather BETWEEN 0 AND 1000),
  light_armor INTEGER NOT NULL DEFAULT 0 CHECK(light_armor BETWEEN 0 AND 1000),
  heavy_armor INTEGER NOT NULL DEFAULT 0 CHECK(heavy_armor BETWEEN 0 AND 1000),
  plate INTEGER NOT NULL DEFAULT 0 CHECK(plate BETWEEN 0 AND 1000),
  amulet INTEGER NOT NULL DEFAULT 0 CHECK(amulet BETWEEN 0 AND 1000),
  wrist INTEGER NOT NULL DEFAULT 0 CHECK(wrist BETWEEN 0 AND 1000),
  ring INTEGER NOT NULL DEFAULT 0 CHECK(ring BETWEEN 0 AND 1000),
  support INTEGER NOT NULL DEFAULT 0 CHECK(support BETWEEN 0 AND 1000),
  magic_stone INTEGER NOT NULL DEFAULT 0 CHECK(magic_stone BETWEEN 0 AND 1000),
  FOREIGN KEY(charac_no,account) REFERENCES characters(id,account) ON DELETE CASCADE
) STRICT;
CREATE TABLE character_permanent_stats(
  character_id INTEGER PRIMARY KEY,
  account TEXT NOT NULL,
  max_hp INTEGER NOT NULL CHECK(max_hp BETWEEN 0 AND 4294967295),
  max_mp INTEGER NOT NULL CHECK(max_mp BETWEEN 0 AND 4294967295),
  strength INTEGER NOT NULL CHECK(strength BETWEEN 0 AND 32767),
  vitality INTEGER NOT NULL CHECK(vitality BETWEEN 0 AND 32767),
  intelligence INTEGER NOT NULL CHECK(intelligence BETWEEN 0 AND 32767),
  spirit INTEGER NOT NULL CHECK(spirit BETWEEN 0 AND 32767),
  move_speed INTEGER NOT NULL CHECK(move_speed BETWEEN 0 AND 4294967295),
  all_element_resistance INTEGER NOT NULL CHECK(all_element_resistance BETWEEN 0 AND 32767),
  FOREIGN KEY(character_id,account) REFERENCES characters(id,account)
) STRICT;
CREATE TABLE character_assault_daily(
  character_id INTEGER PRIMARY KEY,
  account TEXT NOT NULL,
  day INTEGER NOT NULL,
  clear_count INTEGER NOT NULL CHECK(clear_count BETWEEN 0 AND 255),
  hidden_count INTEGER NOT NULL CHECK(hidden_count BETWEEN 0 AND 255),
  FOREIGN KEY(character_id,account) REFERENCES characters(id,account) ON DELETE CASCADE
);
CREATE TABLE character_boss_selection_buff(
    character_id INTEGER PRIMARY KEY,
    account TEXT NOT NULL,
    state_json TEXT NOT NULL CHECK(json_valid(state_json) AND json_type(state_json)='object'),
    FOREIGN KEY(character_id,account) REFERENCES characters(id,account) ON DELETE CASCADE
);
CREATE TABLE character_consumable_states(
  character_id INTEGER NOT NULL,
  account TEXT NOT NULL,
  kind INTEGER NOT NULL CHECK(kind IN(0,1)),
  item_id INTEGER NOT NULL CHECK(item_id>0),
  until_ms INTEGER NOT NULL CHECK(until_ms>0),
  maintenance INTEGER NOT NULL CHECK(maintenance BETWEEN 0 AND 15 AND (kind=0 OR maintenance<=7)),
  cool_group INTEGER NOT NULL,
  PRIMARY KEY(character_id,kind,item_id),
  FOREIGN KEY(character_id,account) REFERENCES characters(id,account) ON DELETE CASCADE
);
CREATE TABLE character_crack_daily(
  character_id INTEGER PRIMARY KEY,
  account TEXT NOT NULL,
  day INTEGER NOT NULL,
  clear_count INTEGER NOT NULL CHECK(clear_count BETWEEN 0 AND 1000000),
  daily_reward INTEGER NOT NULL CHECK(daily_reward IN (0,1)),
  FOREIGN KEY(character_id,account) REFERENCES characters(id,account) ON DELETE CASCADE
);
CREATE TABLE character_growup_change(
  character_id INTEGER PRIMARY KEY,
  account TEXT NOT NULL,
  change_count INTEGER NOT NULL CHECK(change_count BETWEEN 1 AND 127),
  FOREIGN KEY(character_id,account) REFERENCES characters(id,account) ON DELETE CASCADE
);
CREATE TABLE character_dungeon_incount(
  character_id INTEGER NOT NULL,
  account TEXT NOT NULL,
  dungeon INTEGER NOT NULL,
  day INTEGER NOT NULL,
  daily_used INTEGER NOT NULL CHECK(daily_used BETWEEN 0 AND 1000000),
  week INTEGER NOT NULL,
  weekly_used INTEGER NOT NULL CHECK(weekly_used BETWEEN 0 AND 1000000),
  PRIMARY KEY(character_id,dungeon),
  FOREIGN KEY(character_id,account) REFERENCES characters(id,account) ON DELETE CASCADE
);
CREATE TABLE character_dungeon_incount_extra(
  character_id INTEGER NOT NULL,account TEXT NOT NULL,dungeon INTEGER NOT NULL CHECK(dungeon>0),
  day INTEGER NOT NULL,daily_extra INTEGER NOT NULL CHECK(daily_extra BETWEEN 0 AND 1000000),
  week INTEGER NOT NULL,weekly_extra INTEGER NOT NULL CHECK(weekly_extra BETWEEN 0 AND 1000000),
  PRIMARY KEY(character_id,dungeon),
  FOREIGN KEY(character_id,account) REFERENCES characters(id,account) ON DELETE CASCADE);
CREATE TABLE account_dungeon_incount(
  account TEXT NOT NULL,
  dungeon INTEGER NOT NULL,
  period INTEGER NOT NULL,
  used INTEGER NOT NULL CHECK(used BETWEEN 0 AND 1000000),
  PRIMARY KEY(account,dungeon)
);
CREATE TABLE account_dungeon_incount_extra(
  account TEXT NOT NULL,dungeon INTEGER NOT NULL CHECK(dungeon>0),
  period INTEGER NOT NULL,extra INTEGER NOT NULL CHECK(extra BETWEEN 0 AND 1000000),
  PRIMARY KEY(account,dungeon),FOREIGN KEY(account) REFERENCES accounts(account_id));
CREATE TABLE character_dungeon_reward_extra(
  character_id INTEGER NOT NULL,account TEXT NOT NULL,kind INTEGER NOT NULL CHECK(kind IN(0,1)),
  target INTEGER NOT NULL CHECK(target>0),day INTEGER NOT NULL,
  daily_extra INTEGER NOT NULL CHECK(daily_extra BETWEEN 0 AND 1000000),week INTEGER NOT NULL,
  weekly_extra INTEGER NOT NULL CHECK(weekly_extra BETWEEN 0 AND 1000000),
  PRIMARY KEY(character_id,kind,target),
  FOREIGN KEY(character_id,account) REFERENCES characters(id,account) ON DELETE CASCADE);
CREATE TABLE account_single_dungeon_bindings(
  account TEXT NOT NULL,group_id INTEGER NOT NULL CHECK(group_id IN(1,3)),
  period INTEGER NOT NULL,character_id INTEGER NOT NULL CHECK(character_id BETWEEN 1 AND 65535),
  PRIMARY KEY(account,group_id,character_id),
  FOREIGN KEY(account) REFERENCES accounts(account_id));
CREATE TABLE character_exp_bonus(
  character_id INTEGER PRIMARY KEY,
  account TEXT NOT NULL,
  until_unix INTEGER NOT NULL CHECK(until_unix>=0),
  permille INTEGER NOT NULL CHECK(permille BETWEEN 0 AND 10000),
  FOREIGN KEY(character_id,account) REFERENCES characters(id,account) ON DELETE CASCADE
);
CREATE TABLE character_expert_job(
  character_id INTEGER PRIMARY KEY,
  account TEXT NOT NULL,
  job_type INTEGER NOT NULL CHECK(job_type BETWEEN 1 AND 4),
  expertness INTEGER NOT NULL DEFAULT 0 CHECK(expertness>=0),
  learned_recipes TEXT NOT NULL DEFAULT '[]' CHECK(json_valid(learned_recipes) AND json_type(learned_recipes)='array' AND json_array_length(learned_recipes)<=255),
  FOREIGN KEY(character_id,account) REFERENCES characters(id,account) ON DELETE CASCADE
);
CREATE TABLE character_expert_meta(
  character_id INTEGER PRIMARY KEY,
  account TEXT NOT NULL,
  giveup_state INTEGER NOT NULL DEFAULT 0 CHECK(giveup_state BETWEEN 0 AND 3),
  machine_grade INTEGER NOT NULL DEFAULT 1 CHECK(machine_grade BETWEEN 0 AND 11),
  machine_endurance INTEGER NOT NULL DEFAULT 300 CHECK(machine_endurance>=0),
  FOREIGN KEY(character_id,account) REFERENCES characters(id,account) ON DELETE CASCADE
);
CREATE TABLE character_teleport_cooldowns(
  character_id INTEGER PRIMARY KEY,
  account TEXT NOT NULL,
  potion_until INTEGER NOT NULL CHECK(potion_until>=0),
  aura_until INTEGER NOT NULL CHECK(aura_until>=0),
  upgrade_aura_until INTEGER NOT NULL DEFAULT 0 CHECK(upgrade_aura_until>=0),
  party_aura_until INTEGER NOT NULL DEFAULT 0 CHECK(party_aura_until>=0),
  privilege_until INTEGER NOT NULL DEFAULT 0 CHECK(privilege_until>=0),
  FOREIGN KEY(character_id,account) REFERENCES characters(id,account) ON DELETE CASCADE
);
CREATE TABLE cube_premium(
  charac_no INTEGER PRIMARY KEY REFERENCES characters(id) ON DELETE CASCADE,
  selected INTEGER NOT NULL CHECK(selected IN (0,1)),
  cube_type INTEGER NOT NULL CHECK(cube_type BETWEEN -1 AND 5)
);
CREATE TABLE account_adventures(
  account TEXT PRIMARY KEY REFERENCES accounts(account_id),revision INTEGER NOT NULL CHECK(revision>=0),
  name TEXT NOT NULL CHECK(length(name)>0),created_date INTEGER NOT NULL CHECK(created_date BETWEEN 19000101 AND 99991231),
  level INTEGER NOT NULL CHECK(level BETWEEN 1 AND 255),experience INTEGER NOT NULL CHECK(experience>=0),
  remainder TEXT NOT NULL,capsule_experience INTEGER NOT NULL CHECK(capsule_experience>=0),
  brave_experience INTEGER NOT NULL CHECK(brave_experience>=0),points BLOB NOT NULL CHECK(length(points)=20),
  month INTEGER NOT NULL CHECK(month BETWEEN 190001 AND 999912),
  name_initialized INTEGER NOT NULL DEFAULT 1 CHECK(name_initialized IN (0,1)),
  capsule_week INTEGER NOT NULL DEFAULT 0 CHECK(capsule_week>=0),
  login_streak_days INTEGER NOT NULL DEFAULT 0 CHECK(login_streak_days>=0),
  last_login_day INTEGER NOT NULL DEFAULT 0 CHECK(last_login_day>=0),
  opening_experience TEXT NOT NULL DEFAULT '0' CHECK(typeof(opening_experience)='text'),
  excluded_experience TEXT NOT NULL DEFAULT '0' CHECK(typeof(excluded_experience)='text'));
CREATE INDEX account_adventure_name ON account_adventures(name COLLATE NOCASE);

CREATE TABLE character_tutorial_state(
  character_id INTEGER PRIMARY KEY,account TEXT NOT NULL,dungeon_id INTEGER NOT NULL CHECK(dungeon_id>0),
  completed INTEGER NOT NULL CHECK(completed IN(0,1)),reward_received INTEGER NOT NULL CHECK(reward_received IN(0,1)),
  FOREIGN KEY(character_id,account) REFERENCES characters(id,account));

CREATE TABLE IF NOT EXISTS guild_dungeon_progress(
  guild_id INTEGER NOT NULL REFERENCES guilds(guild_id) ON DELETE CASCADE,
  dungeon_id INTEGER NOT NULL CHECK(dungeon_id BETWEEN 1 AND 4294967295),
  clear_count INTEGER NOT NULL CHECK(clear_count BETWEEN 0 AND 4294967295),
  last_clear_unix INTEGER NOT NULL CHECK(last_clear_unix>0),
  PRIMARY KEY(guild_id,dungeon_id)
);
CREATE TABLE IF NOT EXISTS guild_dungeon_clear_receipts(
  guild_id INTEGER NOT NULL REFERENCES guilds(guild_id) ON DELETE CASCADE,
  instance_id TEXT NOT NULL CHECK(length(instance_id)=32 AND instance_id NOT GLOB '*[^0-9a-f]*'),
  dungeon_id INTEGER NOT NULL CHECK(dungeon_id BETWEEN 1 AND 4294967295),
  character_id INTEGER NOT NULL CHECK(character_id BETWEEN 1 AND 65535),
  joined_at_unix INTEGER NOT NULL CHECK(joined_at_unix>=0),
  committed_at_unix INTEGER NOT NULL CHECK(committed_at_unix>0),
  PRIMARY KEY(guild_id,instance_id)
);
CREATE TABLE IF NOT EXISTS guild_hongbao_records(
  record_id INTEGER PRIMARY KEY AUTOINCREMENT CHECK(record_id BETWEEN 1 AND 4294967295),
  guild_id INTEGER NOT NULL CHECK(guild_id>0),sender_account TEXT NOT NULL,
  sender_name TEXT NOT NULL,item_id INTEGER NOT NULL CHECK(item_id>0),
  total INTEGER NOT NULL CHECK(total>0),remaining INTEGER NOT NULL CHECK(remaining BETWEEN 0 AND total),
  portions INTEGER NOT NULL CHECK(portions BETWEEN 5 AND 30),taken INTEGER NOT NULL CHECK(taken BETWEEN 0 AND portions),
  created_at INTEGER NOT NULL CHECK(created_at>0),expires_at INTEGER NOT NULL CHECK(expires_at>created_at),
  refunded INTEGER NOT NULL DEFAULT 0 CHECK(refunded IN(0,1))
);
CREATE TABLE IF NOT EXISTS guild_hongbao_claims(
  record_id INTEGER NOT NULL REFERENCES guild_hongbao_records(record_id),account TEXT NOT NULL,
  character_id INTEGER NOT NULL CHECK(character_id BETWEEN 1 AND 65535),name TEXT NOT NULL,
  level INTEGER NOT NULL CHECK(level BETWEEN 1 AND 255),amount INTEGER NOT NULL CHECK(amount>0),
  ordinal INTEGER NOT NULL CHECK(ordinal BETWEEN 0 AND 29),day_start_unix INTEGER NOT NULL,
  created_at INTEGER NOT NULL CHECK(created_at>0),PRIMARY KEY(record_id,account),UNIQUE(record_id,ordinal)
);
CREATE INDEX IF NOT EXISTS guild_hongbao_list ON guild_hongbao_records(guild_id,expires_at,record_id);
CREATE INDEX IF NOT EXISTS guild_hongbao_refund ON guild_hongbao_records(sender_account,refunded,expires_at);
CREATE INDEX IF NOT EXISTS guild_hongbao_daily ON guild_hongbao_claims(account,day_start_unix);
CREATE TABLE IF NOT EXISTS character_guild_hongbao_purchases(
  character_id INTEGER NOT NULL REFERENCES characters(id) ON DELETE CASCADE,
  day_start_unix INTEGER NOT NULL CHECK(day_start_unix>=0),
  cera INTEGER NOT NULL CHECK(cera BETWEEN 1 AND 2147483647),
  PRIMARY KEY(character_id,day_start_unix)
);
CREATE TABLE character_tutorial_receipts(
  character_id INTEGER NOT NULL,account TEXT NOT NULL,event_id TEXT NOT NULL CHECK(length(event_id)=32),
  kind TEXT NOT NULL CHECK(kind IN('death','settlement','reward','skip','clear')),
  instance_id TEXT NOT NULL CHECK(length(instance_id)=32),task_attempt INTEGER NOT NULL CHECK(task_attempt>=0),
  epoch INTEGER NOT NULL CHECK(epoch>=0),map_id INTEGER NOT NULL CHECK(map_id>=0),subject INTEGER NOT NULL CHECK(subject>=0),
  experience INTEGER NOT NULL CHECK(experience BETWEEN 0 AND 4294967295),
  before_revision INTEGER NOT NULL CHECK(before_revision>=0),revision INTEGER NOT NULL CHECK(revision=before_revision+1),
  plan_snapshot TEXT NOT NULL CHECK(length(plan_snapshot)>0),
  PRIMARY KEY(character_id,event_id),FOREIGN KEY(character_id,account) REFERENCES characters(id,account),
  FOREIGN KEY(character_id) REFERENCES character_tutorial_state(character_id));

CREATE TABLE schema_metadata(
    key TEXT NOT NULL PRIMARY KEY,
    value TEXT NOT NULL
);

CREATE TABLE character_dungeon_state(
    character_id INTEGER NOT NULL PRIMARY KEY,account TEXT NOT NULL,
    next_object_key INTEGER NOT NULL CHECK(next_object_key BETWEEN 1 AND 65536),
    last_attempt INTEGER NOT NULL CHECK(last_attempt>=0),
    capsule_experience INTEGER NOT NULL CHECK(capsule_experience BETWEEN 0 AND 4294967295),
    proficiency_level INTEGER NOT NULL CHECK(proficiency_level BETWEEN 0 AND 4294967295),
    proficiency_experience INTEGER NOT NULL CHECK(proficiency_experience>=0),
    recommended_dungeon_clears INTEGER NOT NULL DEFAULT 0 CHECK(recommended_dungeon_clears BETWEEN 0 AND 65535),
    FOREIGN KEY(character_id,account) REFERENCES characters(id,account)
);

CREATE TABLE dungeon_special_entry_receipts(
    character_id INTEGER NOT NULL, account TEXT NOT NULL,
    instance_id TEXT NOT NULL CHECK(length(instance_id)=32 AND instance_id NOT GLOB '*[^0-9a-f]*'),
    task_attempt INTEGER NOT NULL CHECK(task_attempt>0),
    service_day INTEGER NOT NULL CHECK(service_day BETWEEN 1 AND 3652058),
    kind INTEGER NOT NULL CHECK(kind BETWEEN 1 AND 3),
    dungeon_id INTEGER NOT NULL CHECK(dungeon_id>0),
    layer INTEGER NOT NULL CHECK((kind=3 AND layer BETWEEN 1 AND 100) OR (kind<>3 AND layer=0)),
    gold INTEGER NOT NULL CHECK(gold BETWEEN 0 AND 2147483647),
    revision INTEGER NOT NULL CHECK(revision>0),
    PRIMARY KEY(character_id,instance_id), UNIQUE(character_id,task_attempt)
);
CREATE INDEX dungeon_special_entry_day ON dungeon_special_entry_receipts(account,kind,service_day,character_id);

CREATE TABLE dungeon_altar_clear_receipts(
    character_id INTEGER NOT NULL, account TEXT NOT NULL, instance_id TEXT NOT NULL,
    event_id TEXT NOT NULL CHECK(length(event_id)=32 AND event_id NOT GLOB '*[^0-9a-f]*'),
    task_attempt INTEGER NOT NULL CHECK(task_attempt>0), epoch INTEGER NOT NULL CHECK(epoch>0),
    dungeon_id INTEGER NOT NULL CHECK(dungeon_id IN (11006,11007)),
    completed_rounds INTEGER NOT NULL CHECK(completed_rounds BETWEEN 0 AND maximum_rounds),
    maximum_rounds INTEGER NOT NULL CHECK((dungeon_id=11006 AND maximum_rounds=15) OR (dungeon_id=11007 AND maximum_rounds=7)),
    play_milliseconds INTEGER NOT NULL CHECK(play_milliseconds BETWEEN 0 AND 4294967295),
    cleared INTEGER NOT NULL CHECK(cleared IN (0,1) AND (cleared=0 OR completed_rounds=maximum_rounds)),
    death_events TEXT NOT NULL, reward_json TEXT NOT NULL,
    gained_experience INTEGER NOT NULL CHECK(gained_experience BETWEEN 0 AND 1000000),
    proficiency_gained INTEGER NOT NULL CHECK(proficiency_gained BETWEEN 0 AND 1000000),
    PRIMARY KEY(character_id,instance_id), UNIQUE(character_id,event_id),
    FOREIGN KEY(character_id,instance_id) REFERENCES dungeon_special_entry_receipts(character_id,instance_id)
);
CREATE INDEX dungeon_altar_best ON dungeon_altar_clear_receipts(account,character_id,dungeon_id,completed_rounds DESC,play_milliseconds);
CREATE TABLE dungeon_despair_progress(
    character_id INTEGER NOT NULL PRIMARY KEY, account TEXT NOT NULL,
    next_layer INTEGER NOT NULL CHECK(next_layer BETWEEN 1 AND 100),
    last_clear_day INTEGER NOT NULL CHECK(last_clear_day BETWEEN 0 AND 3652058),
    clear_count INTEGER NOT NULL CHECK(clear_count>=0)
);
CREATE TABLE dungeon_despair_clear_receipts(
    character_id INTEGER NOT NULL, account TEXT NOT NULL, instance_id TEXT NOT NULL,
    event_id TEXT NOT NULL CHECK(length(event_id)=32 AND event_id NOT GLOB '*[^0-9a-f]*'),
    task_attempt INTEGER NOT NULL CHECK(task_attempt>0), epoch INTEGER NOT NULL CHECK(epoch>0),
    dungeon_id INTEGER NOT NULL CHECK(dungeon_id BETWEEN 11008 AND 11107),
    clear_day INTEGER NOT NULL CHECK(clear_day BETWEEN 1 AND 3652058),
    play_milliseconds INTEGER NOT NULL CHECK(play_milliseconds BETWEEN 0 AND 4294967295),
    death_events TEXT NOT NULL, assets_json TEXT NOT NULL,
    PRIMARY KEY(character_id,instance_id), UNIQUE(character_id,event_id),
    FOREIGN KEY(character_id,instance_id) REFERENCES dungeon_special_entry_receipts(character_id,instance_id)
);

CREATE TABLE accounts(
    account_id TEXT PRIMARY KEY,
    salt BLOB NOT NULL,
    password_hash BLOB NOT NULL,
    iterations INTEGER NOT NULL,
    disabled INTEGER NOT NULL DEFAULT 0,
    revision INTEGER NOT NULL DEFAULT 1
);

CREATE TABLE guild_settings(
    guild_id INTEGER PRIMARY KEY REFERENCES guilds(guild_id) ON DELETE CASCADE,
    revision INTEGER NOT NULL DEFAULT 0 CHECK(revision>=0),
    promotion TEXT NOT NULL DEFAULT '',
    tags INTEGER NOT NULL DEFAULT 0 CHECK(tags>=0 AND tags<=4294967295),
    join_mode INTEGER NOT NULL DEFAULT 0 CHECK(join_mode IN(0,1)),
    webpage INTEGER NOT NULL DEFAULT 0 CHECK(webpage IN(0,1))
);
CREATE TABLE guild_roles(
    guild_id INTEGER NOT NULL REFERENCES guilds(guild_id) ON DELETE CASCADE,
    grade INTEGER NOT NULL CHECK(grade BETWEEN 1 AND 5),
    permissions INTEGER NOT NULL CHECK(permissions BETWEEN 0 AND 524287),
    name TEXT NOT NULL,
    PRIMARY KEY(guild_id,grade)
);
CREATE TABLE guild_membership_cooldowns(
    character_id INTEGER PRIMARY KEY REFERENCES characters(id) ON DELETE CASCADE,
    until_unix INTEGER NOT NULL CHECK(until_unix>=0)
);
CREATE TABLE guild_applications(
    guild_id INTEGER NOT NULL REFERENCES guilds(guild_id) ON DELETE CASCADE,
    character_id INTEGER NOT NULL REFERENCES characters(id) ON DELETE CASCADE,
    message TEXT NOT NULL DEFAULT '',
    region INTEGER NOT NULL CHECK(region BETWEEN 0 AND 249),
    created_at_unix INTEGER NOT NULL CHECK(created_at_unix>=0),
    PRIMARY KEY(guild_id,character_id)
);
CREATE INDEX guild_applications_character ON guild_applications(character_id,guild_id);

CREATE TABLE characters(
    id INTEGER PRIMARY KEY AUTOINCREMENT CHECK(id BETWEEN 1 AND 65535),
    account TEXT NOT NULL,
    slot INTEGER NOT NULL CHECK(slot BETWEEN 0 AND 61),
    name TEXT NOT NULL COLLATE NOCASE UNIQUE,
    revision INTEGER NOT NULL DEFAULT 0,
    town INTEGER NOT NULL,
    area INTEGER NOT NULL,
    x INTEGER NOT NULL,
    y INTEGER NOT NULL,
    direction INTEGER NOT NULL,
    return_town INTEGER,
    return_area INTEGER,
    return_x INTEGER,
    return_y INTEGER,
    return_direction INTEGER,
    job INTEGER NOT NULL DEFAULT 0 CHECK(job BETWEEN 0 AND 15),
    grow_type INTEGER NOT NULL DEFAULT 0 CHECK(grow_type BETWEEN 0 AND 127),
    level INTEGER NOT NULL DEFAULT 1 CHECK(level BETWEEN 1 AND 95),
    accepted_story_level INTEGER NOT NULL DEFAULT 0 CHECK(accepted_story_level BETWEEN 0 AND 95),
    gold INTEGER NOT NULL DEFAULT 0 CHECK(gold BETWEEN 0 AND 4294967295),
    experience INTEGER NOT NULL DEFAULT 0 CHECK(experience BETWEEN 0 AND 9223372036854775807),
    inventory_expansion INTEGER NOT NULL DEFAULT 0 CHECK(inventory_expansion IN (0,8,16,24)),
    revive_coins INTEGER NOT NULL DEFAULT 0 CHECK(revive_coins BETWEEN 0 AND 2147483647),
    win_points INTEGER NOT NULL DEFAULT 0 CHECK(win_points BETWEEN 0 AND 2147483647),
    gold_carry_limit INTEGER NOT NULL DEFAULT 0 CHECK(gold_carry_limit BETWEEN 0 AND 2147483647),
    auction_gold_limit INTEGER NOT NULL DEFAULT 400000000 CHECK(auction_gold_limit BETWEEN 0 AND 2147483647),
    cargo_capacity INTEGER NOT NULL DEFAULT 8 CHECK(cargo_capacity BETWEEN 8 AND 264),
    quest_cycle_day INTEGER NOT NULL DEFAULT 0 CHECK(quest_cycle_day=0 OR quest_cycle_day BETWEEN 19000101 AND 99991231),
    equipment_slot_flags INTEGER NOT NULL DEFAULT 0 CHECK(equipment_slot_flags BETWEEN 0 AND 255),
    emotion INTEGER NOT NULL DEFAULT 0 CHECK(emotion BETWEEN 0 AND 32767),
    clone_title_item_id INTEGER NOT NULL DEFAULT 0 CHECK(typeof(clone_title_item_id)='integer' AND clone_title_item_id BETWEEN 0 AND 4294967295),
    selected_skill_tree INTEGER NOT NULL DEFAULT -1 CHECK(typeof(selected_skill_tree)='integer' AND selected_skill_tree BETWEEN -1 AND 1),
    blacklist_count INTEGER NOT NULL DEFAULT 0 CHECK(typeof(blacklist_count)='integer' AND blacklist_count BETWEEN 0 AND 2147483647),
    avatar_expansion INTEGER NOT NULL DEFAULT 105 CHECK(avatar_expansion BETWEEN 0 AND 315 AND avatar_expansion%7=0),
    devil_usage TEXT NOT NULL DEFAULT '[]' CHECK(json_valid(devil_usage) AND json_type(devil_usage)='array'),
    npc_favor TEXT NOT NULL DEFAULT '[]' CHECK(json_valid(npc_favor) AND json_type(npc_favor)='array'),
    daily_booster_usage TEXT NOT NULL DEFAULT '[]' CHECK(json_valid(daily_booster_usage) AND json_type(daily_booster_usage)='array'),
    weapon_skin_state TEXT NOT NULL DEFAULT '{"Owned":[],"Selected":0}' CHECK(json_valid(weapon_skin_state) AND json_type(weapon_skin_state)='object'),
    avatar_preset_state TEXT NOT NULL DEFAULT '{"Capacity":1,"Presets":[]}' CHECK(json_valid(avatar_preset_state) AND json_type(avatar_preset_state)='object'),
    knight_shield_deck TEXT NOT NULL DEFAULT '[0,0,0,0,0]' CHECK(json_valid(knight_shield_deck) AND json_type(knight_shield_deck)='array' AND json_array_length(knight_shield_deck)=5),
    stamina_until INTEGER NOT NULL DEFAULT 0 CHECK(stamina_until>=0),
    UNIQUE(account,slot)
);

CREATE TABLE character_completed_quests(
    character_id INTEGER NOT NULL REFERENCES characters(id),
    quest_id INTEGER NOT NULL CHECK(quest_id BETWEEN 1 AND 29999),
    daily_period INTEGER NOT NULL DEFAULT 0 CHECK(daily_period=0 OR daily_period BETWEEN 19000101 AND 99991231),
    answer INTEGER CHECK(answer BETWEEN 0 AND 254),
    PRIMARY KEY(character_id,quest_id)
);

CREATE TABLE character_quest_drop_receipts(
    character_id INTEGER NOT NULL,account TEXT NOT NULL,event_id TEXT NOT NULL CHECK(length(event_id)=32 AND event_id NOT GLOB '*[^0-9a-f]*'),
    before_revision INTEGER NOT NULL CHECK(before_revision>=0),revision INTEGER NOT NULL CHECK(revision=before_revision+1),
    plan_snapshot TEXT NOT NULL CHECK(json_valid(plan_snapshot)),
    changed_items TEXT NOT NULL CHECK(json_valid(changed_items) AND json_type(changed_items)='array'),
    PRIMARY KEY(character_id,event_id),
    FOREIGN KEY(character_id,event_id) REFERENCES character_dungeon_growth_receipts(character_id,event_id),
    FOREIGN KEY(character_id,account) REFERENCES characters(id,account)
);

CREATE TABLE account_completed_quests(
    account TEXT NOT NULL REFERENCES accounts(account_id),
    quest_id INTEGER NOT NULL CHECK(quest_id BETWEEN 1 AND 29999),
    character_id INTEGER,
    acceptance_id TEXT NOT NULL CHECK(length(acceptance_id)=32 AND acceptance_id NOT GLOB '*[^0-9a-f]*' AND acceptance_id<>'00000000000000000000000000000000'),
    daily_period INTEGER NOT NULL DEFAULT 0 CHECK(daily_period=0 OR daily_period BETWEEN 19000101 AND 99991231),
    answer INTEGER CHECK(answer BETWEEN 0 AND 254),
    PRIMARY KEY(account,quest_id),FOREIGN KEY(character_id,account) REFERENCES characters(id,account)
);

CREATE TABLE character_skills(
    character_id INTEGER NOT NULL REFERENCES characters(id),
    skill_id INTEGER NOT NULL CHECK(skill_id BETWEEN 0 AND 65535),
    level INTEGER NOT NULL CHECK(level BETWEEN 1 AND 255),
    PRIMARY KEY(character_id,skill_id)
);

CREATE TABLE character_active_main_quests(
    character_id INTEGER NOT NULL REFERENCES characters(id),
    slot INTEGER NOT NULL CHECK(slot BETWEEN 0 AND 29),
    quest_id INTEGER NOT NULL CHECK(quest_id BETWEEN 1 AND 29999),
    condition_state INTEGER NOT NULL DEFAULT 0 CHECK(condition_state BETWEEN 0 AND 4294967295),
    acceptance_id TEXT NOT NULL DEFAULT '00000000000000000000000000000000'
        CHECK(length(acceptance_id)=32 AND acceptance_id NOT GLOB '*[^0-9a-f]*'),
    PRIMARY KEY(character_id,slot)
);

CREATE TABLE character_quest_daily_clear(
  character_id INTEGER NOT NULL,account TEXT NOT NULL,
  quest_id INTEGER NOT NULL CHECK(quest_id IN(13200,8683)),
  day INTEGER NOT NULL CHECK(day BETWEEN 19000101 AND 99991231),
  event_id TEXT NOT NULL CHECK(length(event_id)=32 AND event_id NOT GLOB '*[^0-9a-f]*'),
  revision INTEGER NOT NULL CHECK(revision>0),
  PRIMARY KEY(character_id,quest_id),
  FOREIGN KEY(character_id,account) REFERENCES characters(id,account)
);

CREATE TABLE character_quest_clear_receipts(
    character_id INTEGER NOT NULL,account TEXT NOT NULL,
    acceptance_id TEXT NOT NULL CHECK(length(acceptance_id)=32 AND acceptance_id NOT GLOB '*[^0-9a-f]*' AND acceptance_id<>'00000000000000000000000000000000'),
    quest_id INTEGER NOT NULL CHECK(quest_id BETWEEN 1 AND 29999),
    quest_slot INTEGER NOT NULL CHECK(quest_slot BETWEEN 0 AND 29),
    event_id TEXT NOT NULL,instance_id TEXT NOT NULL,
    task_attempt INTEGER NOT NULL CHECK(task_attempt>0),epoch INTEGER NOT NULL CHECK(epoch>0),
    dungeon_id INTEGER NOT NULL,map_id INTEGER NOT NULL,
    generation INTEGER NOT NULL CHECK(generation>0),revision INTEGER NOT NULL CHECK(revision>0),
    source_type TEXT NOT NULL CHECK(source_type IN('MonsterDeath','PassiveObject')),
    growth_event_id TEXT,scene_operation_id TEXT,
    CHECK((source_type='MonsterDeath' AND growth_event_id=event_id AND growth_event_id IS NOT NULL AND scene_operation_id IS NULL)
      OR (source_type='PassiveObject' AND scene_operation_id=event_id AND scene_operation_id IS NOT NULL AND growth_event_id IS NULL)),
    PRIMARY KEY(character_id,acceptance_id),

    FOREIGN KEY(character_id,growth_event_id) REFERENCES character_dungeon_growth_receipts(character_id,event_id),
    FOREIGN KEY(character_id,scene_operation_id) REFERENCES character_dungeon_scene_clear_receipts(character_id,operation_id),
    FOREIGN KEY(character_id,account) REFERENCES characters(id,account)
);

CREATE UNIQUE INDEX character_active_quest_acceptance ON character_active_main_quests(character_id,acceptance_id)
    WHERE acceptance_id<>'00000000000000000000000000000000';

CREATE TABLE character_dungeon_scene_clear_receipts(
  character_id INTEGER NOT NULL,account TEXT NOT NULL,operation_id TEXT NOT NULL,
  source_type TEXT NOT NULL CHECK(source_type='PassiveObject'),
  instance_id TEXT NOT NULL,quest_id INTEGER NOT NULL CHECK(quest_id BETWEEN 0 AND 29999),
  task_attempt INTEGER NOT NULL CHECK(task_attempt>0),before_epoch INTEGER NOT NULL CHECK(before_epoch>0),
  epoch INTEGER NOT NULL CHECK(epoch=before_epoch+1),dungeon_id INTEGER NOT NULL CHECK(dungeon_id>0),map_id INTEGER NOT NULL CHECK(map_id>0),
  template_id INTEGER NOT NULL CHECK(template_id>0),source_ordinal INTEGER NOT NULL CHECK(source_ordinal>=0),
  fingerprint TEXT NOT NULL CHECK(length(fingerprint)=64),
  death_events BLOB NOT NULL CHECK(typeof(death_events)='blob' AND length(death_events)%16=0),
  generation INTEGER NOT NULL CHECK(generation>0),revision INTEGER NOT NULL CHECK(revision>0),
  PRIMARY KEY(character_id,operation_id),UNIQUE(character_id,instance_id),
  FOREIGN KEY(character_id,operation_id) REFERENCES character_quest_lifecycle_receipts(character_id,operation_id),
  FOREIGN KEY(character_id,account) REFERENCES characters(id,account)
);

CREATE TABLE character_quest_completion_receipts(
          character_id INTEGER NOT NULL,account TEXT NOT NULL,
          acceptance_id TEXT NOT NULL CHECK(length(acceptance_id)=32 AND acceptance_id NOT GLOB '*[^0-9a-f]*' AND acceptance_id<>'00000000000000000000000000000000'),
          quest_id INTEGER NOT NULL CHECK(quest_id BETWEEN 1 AND 29999),event_id TEXT,instance_id TEXT,
          task_attempt INTEGER CHECK(task_attempt>0),epoch INTEGER CHECK(epoch>0),
          generation INTEGER NOT NULL CHECK(generation>0),rule_id TEXT NOT NULL,
          resource_source TEXT NOT NULL,quest_source TEXT NOT NULL,
          native_index INTEGER NOT NULL CHECK(native_index>=0),base_experience INTEGER NOT NULL CHECK(base_experience>=0),
          raw_additional_percent INTEGER NOT NULL,difficulty_key TEXT NOT NULL,difficulty_weight INTEGER NOT NULL CHECK(difficulty_weight>=0),
          experience_amount INTEGER NOT NULL CHECK(experience_amount BETWEEN 0 AND 2147483647),
          gold_amount INTEGER NOT NULL CHECK(gold_amount BETWEEN 0 AND 2147483647),
          before_level INTEGER NOT NULL CHECK(before_level BETWEEN 1 AND 95),after_level INTEGER NOT NULL CHECK(after_level BETWEEN before_level AND 95),
          before_experience INTEGER NOT NULL CHECK(before_experience>=0),after_experience INTEGER NOT NULL CHECK(after_experience>=before_experience AND after_experience<=before_experience+experience_amount),
          before_revision INTEGER NOT NULL CHECK(before_revision>=0),revision INTEGER NOT NULL CHECK(revision=before_revision+1),
          rewards TEXT NOT NULL CHECK(json_valid(rewards) AND json_type(rewards)='array' AND json_array_length(rewards)<=255),
          clear_acceptance_id TEXT,condition_type INTEGER NOT NULL DEFAULT 3 CHECK(condition_type IN(0,1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,17,18,19,20,21,22,23,24,25,26,27,28,29,30,31,32,33,34,35,36,37,38,39,40,41,42,43,44,45,46,47,48,49,52,53,54,55,56,57,58,59,60,62,63,64,65,66,67,68,69,70,71)),
          selector INTEGER NOT NULL DEFAULT 65535 CHECK(selector BETWEEN 0 AND 65535),multiplier INTEGER NOT NULL DEFAULT 1 CHECK(multiplier BETWEEN 1 AND 65535),
          consumed TEXT NOT NULL DEFAULT '[]' CHECK(json_valid(consumed) AND json_type(consumed)='array' AND json_array_length(consumed)<=255),
          changed_items TEXT NOT NULL DEFAULT '[]' CHECK(json_valid(changed_items) AND json_type(changed_items)='array'),
          plan_snapshot TEXT CHECK(plan_snapshot IS NULL OR (json_valid(plan_snapshot) AND json_type(plan_snapshot)='object')),
          CHECK((condition_type IN(1,3,45) AND (condition_type<>45 OR (clear_acceptance_id IS NOT NULL AND plan_snapshot IS NOT NULL)) AND clear_acceptance_id=acceptance_id AND event_id IS NOT NULL AND instance_id IS NOT NULL AND task_attempt IS NOT NULL AND epoch IS NOT NULL)
            OR (condition_type IN(0,2,4,5,6,7,8,9,10,11,12,13,14,15,16,17,18,19,20,21,22,23,24,25,26,27,28,29,30,31,32,33,34,35,36,37,38,39,40,41,42,43,44,46,47,48,49,52,53,54,55,56,57,58,59,60,62,63,64,65,66,67,68,69,70,71) AND clear_acceptance_id IS NULL AND event_id IS NULL AND instance_id IS NULL AND task_attempt IS NULL AND epoch IS NULL AND plan_snapshot IS NOT NULL)),
          PRIMARY KEY(character_id,acceptance_id),
          FOREIGN KEY(character_id,clear_acceptance_id) REFERENCES character_quest_clear_receipts(character_id,acceptance_id),
          FOREIGN KEY(character_id,account) REFERENCES characters(id,account));

CREATE TABLE character_skill_points(
    character_id INTEGER NOT NULL REFERENCES characters(id),
    context INTEGER NOT NULL CHECK(context BETWEEN 0 AND 1),
    sp INTEGER NOT NULL CHECK(sp BETWEEN 0 AND 65535),
    tp INTEGER NOT NULL CHECK(tp BETWEEN 0 AND 65535),
    PRIMARY KEY(character_id,context)
);

CREATE TABLE character_skill_bindings(
    character_id INTEGER NOT NULL,
    context INTEGER NOT NULL CHECK(context BETWEEN 0 AND 1),
    slot INTEGER NOT NULL CHECK(slot BETWEEN 0 AND 254),
    skill_id INTEGER NOT NULL CHECK(skill_id BETWEEN 0 AND 65535),
    level INTEGER NOT NULL CHECK(level BETWEEN 1 AND 255),
    PRIMARY KEY(character_id,context,skill_id),
    UNIQUE(character_id,context,slot),
    FOREIGN KEY(character_id,context) REFERENCES character_skill_points(character_id,context)
);

CREATE TABLE inventory_audit_log(
    audit_id INTEGER PRIMARY KEY AUTOINCREMENT,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    account TEXT NOT NULL,
    character_id INTEGER NOT NULL,
    generation INTEGER NOT NULL,
    action_name TEXT NOT NULL,
    list_type INTEGER NOT NULL,
    slot_index INTEGER NOT NULL,
    item_uid_before INTEGER,
    item_uid_after INTEGER,
    item_id_before INTEGER,
    item_id_after INTEGER,
    value_before INTEGER,
    value_after INTEGER,
    item_kind_before INTEGER,
    item_kind_after INTEGER,
    count_before INTEGER NOT NULL,
    count_after INTEGER NOT NULL,
    count_delta INTEGER NOT NULL,
    core_hash_before TEXT,
    core_hash_after TEXT
);

CREATE TABLE account_wallets(
    character_slots INTEGER NOT NULL DEFAULT 32 CHECK(character_slots BETWEEN 32 AND 62),
    account TEXT PRIMARY KEY,
    revision INTEGER NOT NULL DEFAULT 0 CHECK(revision>=0),
    rental_points INTEGER NOT NULL DEFAULT 0 CHECK(rental_points BETWEEN 0 AND 2147483647),
    mileage INTEGER NOT NULL DEFAULT 0 CHECK(mileage BETWEEN 0 AND 2147483647),
    mileage_cash_earned INTEGER NOT NULL DEFAULT 0 CHECK(mileage_cash_earned BETWEEN 0 AND 2147483647),
    mileage_shop_purchases TEXT NOT NULL DEFAULT '{}' CHECK(json_valid(mileage_shop_purchases) AND json_type(mileage_shop_purchases)='object'),
    seria_luck INTEGER NOT NULL DEFAULT 0 CHECK(seria_luck BETWEEN 0 AND 8),
    premium_contracts TEXT NOT NULL DEFAULT '{}' CHECK(json_valid(premium_contracts) AND json_type(premium_contracts)='object')
);

CREATE TABLE account_currency_balances(
    account TEXT NOT NULL REFERENCES account_wallets(account),
    currency_kind INTEGER NOT NULL CHECK(currency_kind BETWEEN 3 AND 17),
    balance INTEGER NOT NULL CHECK(balance BETWEEN 1 AND 2147483647),
    PRIMARY KEY(account,currency_kind)
);

CREATE TABLE account_material_balances(
  account TEXT NOT NULL REFERENCES account_wallets(account),
  item_id INTEGER NOT NULL CHECK(item_id>0),
  balance INTEGER NOT NULL CHECK(balance BETWEEN 0 AND 2147483647),
  raid_week INTEGER NOT NULL CHECK(raid_week BETWEEN 0 AND 3652058),
  raid_earned INTEGER NOT NULL CHECK(raid_earned BETWEEN 0 AND 2147483647),
  character_earned TEXT NOT NULL CHECK(json_valid(character_earned) AND json_type(character_earned)='object'),
  PRIMARY KEY(account,item_id)
);

CREATE TABLE account_material_audit(
  audit_id INTEGER PRIMARY KEY AUTOINCREMENT,
  account TEXT NOT NULL,
  character_id INTEGER NOT NULL CHECK(character_id BETWEEN 0 AND 65535),
  generation INTEGER NOT NULL CHECK(generation>=0),
  action_name TEXT NOT NULL,
  item_id INTEGER NOT NULL CHECK(item_id>0),
  before_state TEXT NOT NULL CHECK(json_valid(before_state) AND json_type(before_state)='object'),
  after_state TEXT NOT NULL CHECK(json_valid(after_state) AND json_type(after_state)='object')
);

CREATE TABLE currency_audit_log(
    audit_id INTEGER PRIMARY KEY AUTOINCREMENT,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    account TEXT NOT NULL,
    character_id INTEGER NOT NULL,
    generation INTEGER NOT NULL,
    action_name TEXT NOT NULL,
    currency_kind INTEGER NOT NULL,
    account_scope INTEGER NOT NULL,
    balance_before INTEGER NOT NULL,
    balance_after INTEGER NOT NULL
);

CREATE UNIQUE INDEX character_account_identity ON characters(id,account);

CREATE TABLE character_quest_notify(
    character_id INTEGER PRIMARY KEY,
    account TEXT NOT NULL,
    entries BLOB NOT NULL CHECK(typeof(entries)='blob' AND length(entries)=16),
    FOREIGN KEY(character_id,account) REFERENCES characters(id,account)
);

CREATE TABLE account_client_settings(
    account TEXT NOT NULL PRIMARY KEY REFERENCES accounts(account_id),
    game_options BLOB NOT NULL CHECK(typeof(game_options)='blob' AND length(game_options)=204)
);

CREATE TABLE character_client_settings(
    character_id INTEGER PRIMARY KEY,
    account TEXT NOT NULL,
    character_options BLOB NOT NULL CHECK(typeof(character_options)='blob' AND length(character_options)=512),
    FOREIGN KEY(character_id,account) REFERENCES characters(id,account)
);

CREATE TABLE character_game_settings(
    character_id INTEGER PRIMARY KEY,
    account TEXT NOT NULL,
    game_options BLOB NOT NULL CHECK(typeof(game_options)='blob' AND length(game_options)=204),
    FOREIGN KEY(character_id,account) REFERENCES characters(id,account)
);

CREATE TABLE character_quest_skip_receipts(
    character_id INTEGER NOT NULL,account TEXT NOT NULL,
    acceptance_id TEXT CHECK(length(acceptance_id)=32 AND acceptance_id NOT GLOB '*[^0-9a-f]*' AND acceptance_id<>'00000000000000000000000000000000'),
    parent_key INTEGER CHECK(parent_key BETWEEN 0 AND 2147483647),question_quest INTEGER CHECK(question_quest BETWEEN 1 AND 29999),
    before_revision INTEGER NOT NULL CHECK(before_revision>=0),revision INTEGER NOT NULL CHECK(revision=before_revision+1),
    plan_snapshot TEXT NOT NULL CHECK(json_valid(plan_snapshot) AND json_type(plan_snapshot)='object'),
    operation_id TEXT NOT NULL CHECK(length(operation_id)=32 AND operation_id NOT GLOB '*[^0-9a-f]*' AND operation_id<>'00000000000000000000000000000000'),
  operation_kind TEXT NOT NULL CHECK(operation_kind IN('QuickClear','Manual','Ticket')),
    CHECK((operation_kind='QuickClear' AND acceptance_id IS NOT NULL AND operation_id=acceptance_id AND parent_key IS NOT NULL AND question_quest IS NOT NULL)
    OR (operation_kind IN('Manual','Ticket') AND acceptance_id IS NULL AND parent_key IS NULL AND question_quest IS NULL)),
    PRIMARY KEY(character_id,operation_id),UNIQUE(character_id,acceptance_id),
    FOREIGN KEY(character_id,account) REFERENCES characters(id,account)
);

CREATE TABLE character_quest_lifecycle_receipts(
    character_id INTEGER NOT NULL, account TEXT NOT NULL, operation_id TEXT NOT NULL CHECK(length(operation_id)=32 AND operation_id NOT GLOB '*[^0-9a-f]*' AND operation_id<>'00000000000000000000000000000000'),
    action TEXT NOT NULL CHECK(action IN ('Accept','Giveup','Condition')),
    quest_id INTEGER NOT NULL CHECK(quest_id BETWEEN 0 AND 29999 AND (quest_id<>0 OR (action='Condition' AND scene_event IS NOT NULL AND before_active=after_active AND changed_items='[]'))),
    acceptance_id TEXT NOT NULL CHECK(length(acceptance_id)=32 AND acceptance_id NOT GLOB '*[^0-9a-f]*' AND ((quest_id=0 AND acceptance_id='00000000000000000000000000000000') OR (quest_id<>0 AND acceptance_id<>'00000000000000000000000000000000'))),
    before_revision INTEGER NOT NULL CHECK(before_revision>=0), revision INTEGER NOT NULL CHECK(revision=before_revision+1),
    before_active TEXT NOT NULL CHECK(json_valid(before_active) AND json_type(before_active)='array'),
    after_active TEXT NOT NULL CHECK(json_valid(after_active) AND json_type(after_active)='array'),
  changed_items TEXT NOT NULL CHECK(json_valid(changed_items) AND json_type(changed_items)='array'),
  scene_event TEXT CHECK(scene_event IS NULL OR json_valid(scene_event)),
  before_completed TEXT CHECK(before_completed IS NULL OR (json_valid(before_completed) AND json_type(before_completed)='array')),
  after_completed TEXT CHECK(after_completed IS NULL OR (json_valid(after_completed) AND json_type(after_completed)='array')),
    PRIMARY KEY(character_id,operation_id),
    FOREIGN KEY(character_id,account) REFERENCES characters(id,account)
);

CREATE TABLE account_cargo(
    account TEXT PRIMARY KEY,
    revision INTEGER NOT NULL DEFAULT 0 CHECK(revision>=0),
    capacity INTEGER NOT NULL DEFAULT 0 CHECK(capacity BETWEEN 0 AND 240),
    gold INTEGER NOT NULL DEFAULT 0 CHECK(gold BETWEEN 0 AND 2147483647)
);

CREATE TABLE account_skin_storage(
    account TEXT PRIMARY KEY,
    state_json TEXT NOT NULL DEFAULT '{"Owned":[]}' CHECK(json_valid(state_json) AND json_type(state_json)='object')
);

CREATE TABLE "character_inventory_items"(
    item_uid INTEGER PRIMARY KEY AUTOINCREMENT,
    character_id INTEGER,
    account TEXT NOT NULL,
    owner_character_id INTEGER GENERATED ALWAYS AS (COALESCE(character_id,0)) STORED,
    item_core TEXT NOT NULL CHECK(json_valid(item_core)),
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UNIQUE(account,owner_character_id,item_uid),
    FOREIGN KEY(character_id,account) REFERENCES characters(id,account)
);

CREATE TABLE cargo_audit_log(
    audit_id INTEGER PRIMARY KEY AUTOINCREMENT,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    account TEXT NOT NULL,
    character_id INTEGER NOT NULL,
    generation INTEGER NOT NULL,
    action_name TEXT NOT NULL,
    personal_capacity_before INTEGER NOT NULL,
    personal_capacity_after INTEGER NOT NULL,
    account_capacity_before INTEGER NOT NULL,
    account_capacity_after INTEGER NOT NULL,
    account_gold_before INTEGER NOT NULL,
    account_gold_after INTEGER NOT NULL
);

CREATE TABLE shop_purchase_counts(
    account TEXT NOT NULL,
    character_id INTEGER NOT NULL CHECK(character_id BETWEEN 0 AND 65535),
    npc_id INTEGER NOT NULL CHECK(npc_id>=0),
    item_id INTEGER NOT NULL CHECK(item_id>0),
    reset_daily INTEGER NOT NULL CHECK(reset_daily IN (0,1)),
    day INTEGER NOT NULL CHECK(day>=0),
    count INTEGER NOT NULL CHECK(count BETWEEN 1 AND 2147483647),
    PRIMARY KEY(account,character_id,npc_id,item_id,reset_daily)
);

CREATE TABLE item_purchase_counts(
    account TEXT NOT NULL,
    character_id INTEGER NOT NULL CHECK(character_id BETWEEN 0 AND 65535),
    item_id INTEGER NOT NULL CHECK(item_id>0),
    period INTEGER NOT NULL CHECK(period BETWEEN 1 AND 4),
    day INTEGER NOT NULL CHECK((period=4 AND day=0) OR (period BETWEEN 1 AND 3 AND day BETWEEN 1 AND 3652058)),
    count INTEGER NOT NULL CHECK(count BETWEEN 1 AND 2147483647),
    PRIMARY KEY(account,character_id,item_id,period)
);

CREATE TABLE account_epic_books(
    account TEXT PRIMARY KEY,
    revision INTEGER NOT NULL CHECK(revision>=1),
    order_hash TEXT NOT NULL CHECK(length(order_hash)=64),
    counts TEXT NOT NULL CHECK(json_valid(counts) AND json_type(counts)='array')
);

CREATE TABLE epic_piece_audit_log(
    audit_id INTEGER PRIMARY KEY AUTOINCREMENT,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    account TEXT NOT NULL,
    character_id INTEGER NOT NULL,
    generation INTEGER NOT NULL,
    action_name TEXT NOT NULL,
    order_hash TEXT NOT NULL,
    piece_index INTEGER NOT NULL CHECK(piece_index>=0),
    count_before INTEGER NOT NULL CHECK(count_before BETWEEN 0 AND 2147483647),
    count_after INTEGER NOT NULL CHECK(count_after BETWEEN 0 AND 2147483647)
);

CREATE TABLE inventory_avatar_details(
    item_uid INTEGER PRIMARY KEY CHECK(item_uid BETWEEN 1 AND 2147483647),
    detail TEXT NOT NULL CHECK(json_valid(detail) AND json_type(detail)='object'),
    FOREIGN KEY(item_uid) REFERENCES character_inventory_items(item_uid) ON DELETE CASCADE
);

CREATE TABLE inventory_creature_details(
    item_uid INTEGER PRIMARY KEY CHECK(item_uid BETWEEN 1 AND 2147483647),
    detail TEXT NOT NULL CHECK(json_valid(detail) AND json_type(detail)='object'),
    FOREIGN KEY(item_uid) REFERENCES character_inventory_items(item_uid) ON DELETE CASCADE
);

CREATE TABLE "character_inventory_slots"(
    character_id INTEGER,
    account TEXT NOT NULL,
    list_type INTEGER NOT NULL CHECK(list_type IN (0,1,2,3,7,12,38,45,46)),
    owner_character_id INTEGER GENERATED ALWAYS AS (COALESCE(character_id,0)) STORED,
    slot_index INTEGER NOT NULL CHECK(slot_index BETWEEN 0 AND 351 OR (list_type=1 AND slot_index BETWEEN 352 AND 419)),
    item_uid INTEGER NOT NULL UNIQUE,
    CHECK((list_type=12 AND character_id IS NULL AND slot_index<240) OR (list_type=45 AND character_id IS NULL AND slot_index<50) OR (list_type NOT IN(12,45) AND character_id IS NOT NULL AND (list_type<>2 OR slot_index<264))),
    CHECK(list_type<>1 OR slot_index<420),
    CHECK(list_type<>7 OR slot_index<240),
    CHECK(list_type<>38 OR slot_index<98),
    UNIQUE(character_id,list_type,slot_index),
    FOREIGN KEY(account,owner_character_id,item_uid) REFERENCES character_inventory_items(account,owner_character_id,item_uid) ON DELETE CASCADE
);

CREATE UNIQUE INDEX account_cargo_slot ON character_inventory_slots(account,slot_index) WHERE list_type=12;
CREATE UNIQUE INDEX account_common45_slot ON character_inventory_slots(account,slot_index) WHERE list_type=45;

-- 邮箱 v4：附件沿用完整物品实例与详情，只变更持久所有者和所在位置。
CREATE TABLE mailbox_messages(
    mail_id INTEGER PRIMARY KEY AUTOINCREMENT CHECK(mail_id BETWEEN 1 AND 1073741823),
    revision INTEGER NOT NULL DEFAULT 0 CHECK(revision>=0),
    recipient_id INTEGER NOT NULL,
    recipient_account TEXT NOT NULL,
    sender_id INTEGER NOT NULL CHECK(sender_id BETWEEN 0 AND 65535),
    sender_name TEXT NOT NULL,
    body TEXT NOT NULL,
    gold INTEGER NOT NULL CHECK(gold BETWEEN 0 AND 2147483647),
    created_at INTEGER NOT NULL CHECK(created_at>=0),
    expires_at INTEGER,
    state INTEGER NOT NULL DEFAULT 1 CHECK(state BETWEEN 0 AND 3),
    system_mail INTEGER NOT NULL CHECK(system_mail IN (0,1)),
    delivery_key TEXT UNIQUE,
    delivery_content TEXT,
    FOREIGN KEY(recipient_id,recipient_account) REFERENCES characters(id,account),
    CHECK(expires_at IS NULL OR expires_at>created_at),
    CHECK((system_mail=0 AND delivery_key IS NULL AND delivery_content IS NULL) OR
          (system_mail=1 AND delivery_key IS NOT NULL AND delivery_content IS NOT NULL))
);
CREATE INDEX mailbox_inbox ON mailbox_messages(recipient_id,state,created_at,mail_id);
CREATE TABLE mailbox_attachments(
    attachment_id INTEGER PRIMARY KEY AUTOINCREMENT CHECK(attachment_id BETWEEN 1 AND 1073741823),
    mail_id INTEGER NOT NULL REFERENCES mailbox_messages(mail_id),
    ordinal INTEGER NOT NULL CHECK(ordinal BETWEEN 0 AND 9),
    item_uid INTEGER UNIQUE REFERENCES character_inventory_items(item_uid),
    source_uid INTEGER NOT NULL CHECK(source_uid>=0),
    source_list INTEGER NOT NULL,
    source_slot INTEGER NOT NULL,
    claimed INTEGER NOT NULL DEFAULT 0 CHECK(claimed IN (0,1)),
    UNIQUE(mail_id,ordinal),
    CHECK((claimed=0 AND item_uid IS NOT NULL) OR (claimed=1 AND item_uid IS NULL))
);
CREATE TABLE mailbox_sent_gold(
    character_id INTEGER PRIMARY KEY REFERENCES characters(id),
    day INTEGER NOT NULL,
    amount INTEGER NOT NULL CHECK(amount BETWEEN 0 AND 9223372036854775807)
);
CREATE TABLE mailbox_audit_log(
    audit_id INTEGER PRIMARY KEY AUTOINCREMENT,
    mail_id INTEGER NOT NULL REFERENCES mailbox_messages(mail_id),
    account TEXT NOT NULL,
    character_id INTEGER NOT NULL,
    generation INTEGER NOT NULL,
    action_name TEXT NOT NULL,
    created_at INTEGER NOT NULL,
    assets TEXT NOT NULL CHECK(json_valid(assets))
);
CREATE TABLE character_dungeon_growth_receipts(
character_id INTEGER NOT NULL,account TEXT NOT NULL,event_id TEXT NOT NULL,
instance_id TEXT NOT NULL,quest_id INTEGER NOT NULL CHECK(quest_id>=0),
task_attempt INTEGER NOT NULL CHECK(task_attempt>0),epoch INTEGER NOT NULL CHECK(epoch>0),
map_id INTEGER NOT NULL,monster_key INTEGER NOT NULL CHECK(monster_key BETWEEN 0 AND 65535),
combat_sequence INTEGER NOT NULL CHECK(combat_sequence>0),
generation INTEGER NOT NULL CHECK(generation>0),amount INTEGER NOT NULL CHECK(amount>=0),
rule_id TEXT NOT NULL,resource_source TEXT NOT NULL,
monster_id INTEGER NOT NULL,monster_level INTEGER NOT NULL CHECK(monster_level BETWEEN 1 AND 255),
before_level INTEGER NOT NULL CHECK(before_level BETWEEN 1 AND 95),
after_level INTEGER NOT NULL CHECK(after_level BETWEEN before_level AND 95),
before_experience INTEGER NOT NULL CHECK(before_experience>=0),
after_experience INTEGER NOT NULL CHECK(after_experience>=before_experience AND after_experience<=before_experience+amount),
revision INTEGER NOT NULL CHECK(revision>0),
proficiency_gain INTEGER NOT NULL DEFAULT 0 CHECK(proficiency_gain BETWEEN 0 AND 4294967295),
PRIMARY KEY(character_id,event_id),UNIQUE(character_id,instance_id,monster_key),
UNIQUE(character_id,instance_id,combat_sequence),
FOREIGN KEY(character_id,account) REFERENCES characters(id,account));

CREATE TABLE character_dungeon_loot_receipts(
  character_id INTEGER NOT NULL,account TEXT NOT NULL,event_id TEXT NOT NULL,
  instance_id TEXT NOT NULL,task_attempt INTEGER NOT NULL CHECK(task_attempt>0),
  epoch INTEGER NOT NULL CHECK(epoch>0),map_id INTEGER NOT NULL,
  monster_key INTEGER NOT NULL CHECK(monster_key BETWEEN 1 AND 65535),
  rule_id TEXT NOT NULL,resource_source TEXT NOT NULL,
  ground_key INTEGER CHECK(ground_key BETWEEN 1 AND 65535),
  core TEXT CHECK(core IS NULL OR (json_valid(core) AND json_type(core)='object')),
  x INTEGER CHECK(x BETWEEN 0 AND 65535),y INTEGER CHECK(y BETWEEN 0 AND 65535),
  record_slot INTEGER CHECK(record_slot BETWEEN -32768 AND 32767),
  CHECK((ground_key IS NULL AND core IS NULL AND x IS NULL AND y IS NULL AND record_slot IS NULL) OR
    (ground_key IS NOT NULL AND core IS NOT NULL AND x IS NOT NULL AND y IS NOT NULL AND record_slot IS NOT NULL)),
  PRIMARY KEY(character_id,event_id),UNIQUE(character_id,instance_id,ground_key),
  FOREIGN KEY(character_id,event_id) REFERENCES character_dungeon_growth_receipts(character_id,event_id),
  FOREIGN KEY(character_id,account) REFERENCES characters(id,account));

CREATE TABLE character_dungeon_pickup_receipts(
  character_id INTEGER NOT NULL,account TEXT NOT NULL,event_id TEXT NOT NULL,
  ground_key INTEGER NOT NULL CHECK(ground_key BETWEEN 1 AND 65535),
  instance_id TEXT NOT NULL,task_attempt INTEGER NOT NULL CHECK(task_attempt>0),
  epoch INTEGER NOT NULL CHECK(epoch>0),destination_list INTEGER NOT NULL,
  destination_slot INTEGER NOT NULL CHECK(destination_slot BETWEEN 0 AND 351),
  item_uid INTEGER NOT NULL CHECK(item_uid>0),
  after_core TEXT NOT NULL CHECK(json_valid(after_core) AND json_type(after_core)='object'),
  revision INTEGER NOT NULL CHECK(revision>0),
  PRIMARY KEY(character_id,event_id),UNIQUE(character_id,instance_id,ground_key),
  FOREIGN KEY(character_id,event_id) REFERENCES character_dungeon_loot_receipts(character_id,event_id),
  FOREIGN KEY(character_id,account) REFERENCES characters(id,account));


CREATE TABLE character_fatigue(
  character_id INTEGER PRIMARY KEY,account TEXT NOT NULL,
  used INTEGER NOT NULL CHECK(used BETWEEN 0 AND maximum),
  maximum INTEGER NOT NULL CHECK(maximum BETWEEN 1 AND 65535),
  used_maximum INTEGER NOT NULL CHECK(used_maximum BETWEEN 0 AND 65535),
  remaining INTEGER NOT NULL CHECK(remaining BETWEEN 0 AND maximum),
  context_auxiliary INTEGER NOT NULL CHECK(context_auxiliary BETWEEN 0 AND 65535),
  selection_auxiliary INTEGER NOT NULL CHECK(selection_auxiliary BETWEEN 0 AND 65535),
  FOREIGN KEY(character_id,account) REFERENCES characters(id,account));

CREATE TABLE character_server_item_usage(
  character_id INTEGER PRIMARY KEY REFERENCES characters(id) ON DELETE CASCADE,
  state_json TEXT NOT NULL CHECK(json_valid(state_json) AND json_type(state_json)='object')
);

CREATE TABLE account_server_item_refills(
  account TEXT NOT NULL,item_id INTEGER NOT NULL CHECK(item_id>0),
  service_day INTEGER NOT NULL CHECK(service_day BETWEEN 1 AND 3652058),
  character_id INTEGER NOT NULL CHECK(character_id BETWEEN 1 AND 65535),
  completed INTEGER NOT NULL CHECK(completed IN (0,1)),
  PRIMARY KEY(account,item_id)
);

CREATE TABLE character_fatigue_reset_days(
  character_id INTEGER PRIMARY KEY REFERENCES character_fatigue(character_id) ON DELETE CASCADE,
  service_day INTEGER NOT NULL CHECK(typeof(service_day)='integer' AND service_day BETWEEN 1 AND 3652058)
);

CREATE TABLE character_dungeon_fatigue_receipts(
  character_id INTEGER NOT NULL,account TEXT NOT NULL,instance_id TEXT NOT NULL CHECK(length(instance_id)=32),
  task_attempt INTEGER NOT NULL CHECK(task_attempt>0),map_id INTEGER NOT NULL CHECK(map_id>0),
  epoch INTEGER NOT NULL CHECK(epoch>0),generation INTEGER NOT NULL CHECK(generation>0),rule_id TEXT NOT NULL,
  before_revision INTEGER NOT NULL CHECK(before_revision>=0),revision INTEGER NOT NULL CHECK(revision=before_revision+1),
  before_state TEXT NOT NULL CHECK(json_valid(before_state) AND json_type(before_state)='object'),
  after_state TEXT NOT NULL CHECK(json_valid(after_state) AND json_type(after_state)='object'),
  PRIMARY KEY(character_id,instance_id,map_id),UNIQUE(character_id,task_attempt,map_id),
  FOREIGN KEY(character_id,account) REFERENCES characters(id,account));

CREATE TABLE character_skill_point_sources(
  character_id INTEGER PRIMARY KEY,bonus_sp INTEGER NOT NULL CHECK(bonus_sp BETWEEN 0 AND 65535),
  FOREIGN KEY(character_id) REFERENCES characters(id) ON DELETE CASCADE);

CREATE TABLE account_expert_contracts(
  account TEXT PRIMARY KEY,expires_at INTEGER NOT NULL CHECK(expires_at>=0),
  FOREIGN KEY(account) REFERENCES accounts(account_id) ON DELETE CASCADE);

CREATE TABLE character_dungeon_settlement_receipts(
  character_id INTEGER NOT NULL,account TEXT NOT NULL,event_id TEXT NOT NULL,
  instance_id TEXT NOT NULL,quest_id INTEGER NOT NULL CHECK(quest_id>=0),
  task_attempt INTEGER NOT NULL CHECK(task_attempt>0),epoch INTEGER NOT NULL CHECK(epoch>0),
  dungeon_id INTEGER NOT NULL CHECK(dungeon_id>0),map_id INTEGER NOT NULL,
  death_events BLOB NOT NULL CHECK(typeof(death_events)='blob' AND length(death_events)%16=0),
  report_body BLOB NOT NULL CHECK(typeof(report_body)='blob' AND length(report_body) BETWEEN 1 AND 4096),
  generation INTEGER NOT NULL CHECK(generation>0),
  base_experience INTEGER NOT NULL CHECK(base_experience BETWEEN 0 AND 4294967295),
  rank_experience INTEGER NOT NULL CHECK(rank_experience BETWEEN 0 AND 4294967295),
  total_experience INTEGER NOT NULL CHECK(total_experience BETWEEN 0 AND 4294967295 AND total_experience=base_experience+rank_experience+growth_contract_experience+potion_experience+adventure_experience+other_bonus_experience),
  rank_bonus_index INTEGER NOT NULL CHECK(rank_bonus_index BETWEEN -1 AND 4),
  rule_id TEXT NOT NULL,resource_source TEXT NOT NULL,
  before_level INTEGER NOT NULL CHECK(before_level BETWEEN 1 AND 95),
  after_level INTEGER NOT NULL CHECK(after_level BETWEEN before_level AND 95),
  before_experience INTEGER NOT NULL CHECK(before_experience>=0),
  after_experience INTEGER NOT NULL CHECK(after_experience>=before_experience AND after_experience<=before_experience+total_experience),
  revision INTEGER NOT NULL CHECK(revision>0),
  scene_operation_id TEXT,
  growth_contract_experience INTEGER NOT NULL DEFAULT 0 CHECK(growth_contract_experience BETWEEN 0 AND 4294967295),
  potion_experience INTEGER NOT NULL DEFAULT 0 CHECK(potion_experience BETWEEN 0 AND 4294967295),
  adventure_experience INTEGER NOT NULL DEFAULT 0 CHECK(adventure_experience BETWEEN 0 AND 4294967295),
  other_bonus_experience INTEGER NOT NULL DEFAULT 0 CHECK(other_bonus_experience BETWEEN 0 AND 4294967295),
  bonus_json TEXT NOT NULL DEFAULT '[]' CHECK(json_valid(bonus_json) AND json_type(bonus_json)='array'),
  proficiency_gain INTEGER NOT NULL DEFAULT 0 CHECK(proficiency_gain BETWEEN 0 AND 4294967295),
  CHECK(length(death_events)>0 OR scene_operation_id IS NOT NULL),
  PRIMARY KEY(character_id,event_id),UNIQUE(character_id,instance_id),
  FOREIGN KEY(character_id,scene_operation_id) REFERENCES character_dungeon_scene_clear_receipts(character_id,operation_id),
  FOREIGN KEY(character_id,account) REFERENCES characters(id,account));

CREATE TABLE account_card_memberships(
  account TEXT PRIMARY KEY,black_diamond_expires INTEGER NOT NULL DEFAULT 0 CHECK(black_diamond_expires>=0),
  gold_card_expires INTEGER NOT NULL DEFAULT 0 CHECK(gold_card_expires>=0),revision INTEGER NOT NULL DEFAULT 0 CHECK(revision>=0));

CREATE TABLE character_gold_card_uses(
  character_id INTEGER NOT NULL,account TEXT NOT NULL,service_day INTEGER NOT NULL,used INTEGER NOT NULL CHECK(used BETWEEN 0 AND 10),
  PRIMARY KEY(character_id,service_day),FOREIGN KEY(character_id,account) REFERENCES characters(id,account));

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

CREATE TABLE character_dungeon_card_rewards(
  character_id INTEGER NOT NULL,account TEXT NOT NULL,instance_id TEXT NOT NULL,
  plan_json TEXT NOT NULL CHECK(json_valid(plan_json) AND json_type(plan_json)='object'),
  reward_json TEXT NOT NULL CHECK(json_valid(reward_json) AND json_type(reward_json)='object'),
  mail_id INTEGER,revision INTEGER NOT NULL CHECK(revision>=0),claimed INTEGER NOT NULL DEFAULT 1 CHECK(claimed IN(0,1)),
  PRIMARY KEY(character_id,instance_id),
  FOREIGN KEY(character_id,account) REFERENCES characters(id,account),
  FOREIGN KEY(character_id,instance_id) REFERENCES character_dungeon_settlement_receipts(character_id,instance_id),
  FOREIGN KEY(mail_id) REFERENCES mailbox_messages(mail_id));

CREATE TABLE character_clear_reward_resets(
  character_id INTEGER NOT NULL,instance_id TEXT NOT NULL,
  PRIMARY KEY(character_id,instance_id),
  FOREIGN KEY(character_id,instance_id) REFERENCES character_dungeon_card_rewards(character_id,instance_id) ON DELETE CASCADE);

CREATE TABLE server_events(
  event_id INTEGER NOT NULL PRIMARY KEY CHECK(event_id BETWEEN 1 AND 65535),
  revision INTEGER NOT NULL CHECK(revision>0),starts_at INTEGER,ends_at INTEGER,
  override_active INTEGER CHECK(override_active IS NULL OR override_active IN (0,1)),
  CHECK((starts_at IS NULL AND ends_at IS NULL) OR
    (starts_at IS NOT NULL AND ends_at IS NOT NULL AND starts_at>=0 AND ends_at>starts_at AND ends_at<=253402300799)));

CREATE TABLE character_tp_sources(
 character_id INTEGER PRIMARY KEY,bonus_tp INTEGER NOT NULL CHECK(bonus_tp BETWEEN 0 AND 65535),
 FOREIGN KEY(character_id) REFERENCES characters(id) ON DELETE CASCADE);
CREATE TABLE character_skill_commands(
 character_id INTEGER NOT NULL,context INTEGER NOT NULL CHECK(context IN(0,1)),skill_id INTEGER NOT NULL CHECK(skill_id BETWEEN 0 AND 65535),
 keys BLOB NOT NULL CHECK(typeof(keys)='blob' AND length(keys) BETWEEN 1 AND 255),
 PRIMARY KEY(character_id,context,skill_id),FOREIGN KEY(character_id) REFERENCES characters(id) ON DELETE CASCADE);
CREATE TABLE character_skill_combos(
 character_id INTEGER NOT NULL,context INTEGER NOT NULL CHECK(context IN(0,1)),root_id INTEGER NOT NULL CHECK(root_id BETWEEN 1 AND 32767),
 children BLOB NOT NULL CHECK(typeof(children)='blob' AND length(children)<=12 AND length(children)%2=0),
 PRIMARY KEY(character_id,context,root_id),FOREIGN KEY(character_id) REFERENCES characters(id) ON DELETE CASCADE);

-- 拍卖行挂牌跨账号可见，不归属单个角色；卖家为机器人时 seller_character_id 为 NULL，
-- 因此不建立指向 characters 的外键。item_entry 是物品条目的字节快照，长度与内容由物品条目写入方负责，本表只保存字节。
-- state: 0=在售 1=已成交 2=已下架 3=已过期；buyout_price 为 -1 表示无一口价。
CREATE TABLE auction_listings(
  listing_id INTEGER PRIMARY KEY AUTOINCREMENT CHECK(listing_id BETWEEN 1 AND 2147483647),
  seller_character_id INTEGER,
  seller_name TEXT NOT NULL,
  item_template_id INTEGER NOT NULL CHECK(item_template_id BETWEEN 0 AND 2147483647),
  item_kind INTEGER NOT NULL CHECK(item_kind BETWEEN 0 AND 255),
  item_count INTEGER NOT NULL CHECK(item_count BETWEEN 1 AND 2147483647),
  item_entry BLOB NOT NULL CHECK(typeof(item_entry)='blob' AND length(item_entry)>0),
  starting_price INTEGER NOT NULL CHECK(starting_price BETWEEN 0 AND 2147483647),
  buyout_price INTEGER NOT NULL CHECK(buyout_price BETWEEN -1 AND 2147483647),
  current_bid INTEGER NOT NULL DEFAULT 0 CHECK(current_bid BETWEEN 0 AND 2147483647),
  current_bidder_character_id INTEGER,
  state INTEGER NOT NULL CHECK(state BETWEEN 0 AND 3),
  posted_at_unix INTEGER NOT NULL CHECK(posted_at_unix>=0),
  expires_at_unix INTEGER NOT NULL CHECK(expires_at_unix>=posted_at_unix),
  settled_at_unix INTEGER,
  -- deposit_gold: 挂牌阶段收取的保证金（金币，逐行记录）。取消上架不退还、到期未售按此金额原额退还。
  deposit_gold INTEGER NOT NULL DEFAULT 0 CHECK(deposit_gold BETWEEN 0 AND 2147483647)
);
CREATE INDEX auction_listings_expiry ON auction_listings(state,expires_at_unix);
CREATE INDEX auction_listings_seller ON auction_listings(seller_character_id,state);
CREATE INDEX auction_listings_template ON auction_listings(state,item_template_id,listing_id);
CREATE TABLE character_dungeon_drop_batches(
  character_id INTEGER NOT NULL,account TEXT NOT NULL,event_id TEXT NOT NULL,
  instance_id TEXT NOT NULL,quest_id INTEGER NOT NULL CHECK(quest_id>=0),
  task_attempt INTEGER NOT NULL CHECK(task_attempt>0),epoch INTEGER NOT NULL CHECK(epoch>0),
  map_id INTEGER NOT NULL,monster_key INTEGER NOT NULL CHECK(monster_key BETWEEN 1 AND 65535),
  rule_id TEXT NOT NULL,resource_source TEXT NOT NULL,object_count INTEGER NOT NULL CHECK(object_count BETWEEN 0 AND 500),
  freeze_format INTEGER NOT NULL DEFAULT 0 CHECK(freeze_format IN(0,1)),
  combat_sequence INTEGER CHECK(combat_sequence IS NULL OR combat_sequence>0),
  committed_epoch INTEGER CHECK(committed_epoch IS NULL OR committed_epoch>0),
  map_snapshot TEXT CHECK(map_snapshot IS NULL OR (json_valid(map_snapshot) AND json_type(map_snapshot)='object')),
  uses_collection INTEGER CHECK(uses_collection IS NULL OR uses_collection IN(0,1)),
  PRIMARY KEY(character_id,event_id),UNIQUE(character_id,event_id,instance_id),UNIQUE(character_id,instance_id,monster_key),
  FOREIGN KEY(character_id,event_id) REFERENCES character_dungeon_growth_receipts(character_id,event_id),
  FOREIGN KEY(character_id,account) REFERENCES characters(id,account));
CREATE TABLE character_dungeon_drop_objects(
  character_id INTEGER NOT NULL,event_id TEXT NOT NULL,instance_id TEXT NOT NULL,ordinal INTEGER NOT NULL CHECK(ordinal BETWEEN 0 AND 499),
  ground_key INTEGER NOT NULL CHECK(ground_key BETWEEN 1 AND 65535),
  core TEXT CHECK(core IS NULL OR (json_valid(core) AND json_type(core)='object')),
  gold_amount INTEGER NOT NULL CHECK(gold_amount BETWEEN 0 AND 2147483647),
  x INTEGER NOT NULL CHECK(x BETWEEN 0 AND 65535),y INTEGER NOT NULL CHECK(y BETWEEN 0 AND 65535),
  record_slot INTEGER NOT NULL CHECK(record_slot BETWEEN -32768 AND 32767),
  CHECK((core IS NULL AND gold_amount>0) OR (core IS NOT NULL AND gold_amount=0)),
  PRIMARY KEY(character_id,instance_id,ground_key),UNIQUE(character_id,event_id,ordinal),UNIQUE(character_id,event_id,instance_id,ground_key),
  FOREIGN KEY(character_id,event_id,instance_id) REFERENCES character_dungeon_drop_batches(character_id,event_id,instance_id));
CREATE TABLE character_dungeon_drop_pickups(
  character_id INTEGER NOT NULL,account TEXT NOT NULL,event_id TEXT NOT NULL,ground_key INTEGER NOT NULL,
  instance_id TEXT NOT NULL,task_attempt INTEGER NOT NULL CHECK(task_attempt>0),epoch INTEGER NOT NULL CHECK(epoch>0),
  destination_slot INTEGER,item_uid INTEGER,after_core TEXT,
  currency_kind INTEGER,amount INTEGER NOT NULL CHECK(amount>0),after_balance INTEGER,account_revision INTEGER,
  revision INTEGER NOT NULL CHECK(revision>0),
  receipt_format INTEGER NOT NULL DEFAULT 0 CHECK(receipt_format IN(0,1)),
  destination_list INTEGER CHECK(destination_list IS NULL OR destination_list BETWEEN 0 AND 255),
  epic_piece TEXT CHECK(epic_piece IS NULL OR (json_valid(epic_piece) AND json_type(epic_piece)='object')),
  account_material TEXT CHECK(account_material IS NULL OR (json_valid(account_material) AND json_type(account_material)='object')),
  CHECK((account_material IS NULL AND ((epic_piece IS NULL AND ((destination_slot IS NOT NULL AND item_uid IS NOT NULL AND destination_slot BETWEEN 0 AND 351 AND item_uid>0 AND
    after_core IS NOT NULL AND json_valid(after_core) AND json_type(after_core)='object' AND currency_kind IS NULL AND
    after_balance IS NULL AND account_revision IS NULL) OR
    (destination_slot IS NULL AND item_uid IS NULL AND after_core IS NULL AND currency_kind IS NOT NULL AND currency_kind BETWEEN 0 AND 18 AND
    after_balance IS NOT NULL AND after_balance>=0 AND account_revision IS NOT NULL AND account_revision>=0))) OR (epic_piece IS NOT NULL AND destination_slot IS NULL AND item_uid IS NULL AND after_core IS NULL AND currency_kind IS NULL AND after_balance IS NULL AND account_revision IS NULL AND json_type(epic_piece,'$.Index') IS 'integer' AND json_extract(epic_piece,'$.Index')>=0 AND json_type(epic_piece,'$.PieceId') IS 'integer' AND json_extract(epic_piece,'$.PieceId')>0 AND json_type(epic_piece,'$.Amount') IS 'integer' AND json_extract(epic_piece,'$.Amount')=amount AND json_type(epic_piece,'$.Balance') IS 'integer' AND json_extract(epic_piece,'$.Balance')>=amount AND json_type(epic_piece,'$.BookRevision') IS 'integer' AND json_extract(epic_piece,'$.BookRevision')>0))) OR (account_material IS NOT NULL AND epic_piece IS NULL AND destination_slot IS NULL AND item_uid IS NULL AND after_core IS NULL AND currency_kind IS NULL AND after_balance IS NULL AND account_revision IS NULL AND destination_list IS NULL AND receipt_format=1 AND json_type(account_material,'$.ItemId') IS 'integer' AND json_extract(account_material,'$.ItemId')>0 AND json_type(account_material,'$.Amount') IS 'integer' AND json_extract(account_material,'$.Amount')=amount AND json_type(account_material,'$.Balance') IS 'integer' AND json_extract(account_material,'$.Balance') BETWEEN amount AND 2147483647 AND json_type(account_material,'$.WalletRevision') IS 'integer' AND json_extract(account_material,'$.WalletRevision')>0)),
  PRIMARY KEY(character_id,instance_id,ground_key),
  FOREIGN KEY(character_id,event_id,instance_id,ground_key) REFERENCES character_dungeon_drop_objects(character_id,event_id,instance_id,ground_key),
  FOREIGN KEY(character_id,account) REFERENCES characters(id,account));

CREATE TABLE character_title_book_slots(
    character_id INTEGER NOT NULL,
    account TEXT NOT NULL,
    list_type INTEGER NOT NULL CHECK(list_type BETWEEN 18 AND 22),
    slot_index INTEGER NOT NULL CHECK(slot_index BETWEEN 0 AND 255),
    item_uid INTEGER NOT NULL UNIQUE,
    PRIMARY KEY(character_id,list_type,slot_index),
    FOREIGN KEY(character_id,account) REFERENCES characters(id,account),
    FOREIGN KEY(account,character_id,item_uid) REFERENCES character_inventory_items(account,owner_character_id,item_uid) ON DELETE CASCADE
);
CREATE TABLE character_title_progress(
    character_id INTEGER NOT NULL,
    account TEXT NOT NULL,
    quest_id INTEGER NOT NULL CHECK(quest_id BETWEEN 1 AND 29999),
    remaining1 INTEGER NOT NULL CHECK(remaining1 BETWEEN 0 AND 65535),
    remaining2 INTEGER NOT NULL CHECK(remaining2 BETWEEN 0 AND 65535),
    remaining3 INTEGER NOT NULL CHECK(remaining3 BETWEEN 0 AND 65535),
    completed_revision INTEGER NOT NULL DEFAULT 0 CHECK(completed_revision>=0),
    PRIMARY KEY(character_id,quest_id),
    FOREIGN KEY(character_id,account) REFERENCES characters(id,account),
    CHECK((remaining1+remaining2+remaining3=0 AND completed_revision>0) OR (remaining1+remaining2+remaining3>0 AND completed_revision=0))
);
CREATE TABLE character_title_sections(
    character_id INTEGER NOT NULL,
    account TEXT NOT NULL,
    section_key INTEGER NOT NULL CHECK(section_key BETWEEN 0 AND 1279),
    revision INTEGER NOT NULL CHECK(revision>0),
    PRIMARY KEY(character_id,section_key),
    FOREIGN KEY(character_id,account) REFERENCES characters(id,account)
);
CREATE TABLE character_collectbox_state(
    character_id INTEGER PRIMARY KEY,
    account TEXT NOT NULL,
    selected_box INTEGER NOT NULL CHECK(selected_box BETWEEN 0 AND 255),
    FOREIGN KEY(character_id,account) REFERENCES characters(id,account) ON DELETE CASCADE
);
CREATE TABLE character_collectbox_slots(
    character_id INTEGER NOT NULL,
    account TEXT NOT NULL,
    box_index INTEGER NOT NULL CHECK(box_index BETWEEN 1 AND 255),
    slot_index INTEGER NOT NULL CHECK(slot_index BETWEEN 0 AND 65535),
    item_id INTEGER NOT NULL CHECK(item_id BETWEEN 1 AND 2147483647),
    PRIMARY KEY(character_id,box_index,slot_index),
    UNIQUE(character_id,box_index,item_id),
    FOREIGN KEY(character_id,account) REFERENCES characters(id,account) ON DELETE CASCADE
);
CREATE TABLE character_collectbox_expirations(
    character_id INTEGER NOT NULL,
    account TEXT NOT NULL,
    box_index INTEGER NOT NULL CHECK(box_index BETWEEN 1 AND 255),
    expires_at INTEGER NOT NULL CHECK(expires_at>0),
    PRIMARY KEY(character_id,box_index),
    FOREIGN KEY(character_id,account) REFERENCES characters(id,account) ON DELETE CASCADE
);
CREATE TABLE character_skill_switch_inventory(
    character_id INTEGER PRIMARY KEY,
    account TEXT NOT NULL,
    skill_id INTEGER NOT NULL CHECK(skill_id BETWEEN 0 AND 65534),
    FOREIGN KEY(character_id,account) REFERENCES characters(id,account) ON DELETE CASCADE
);
CREATE TABLE character_skill_switch_inventory_items(
    character_id INTEGER NOT NULL,
    equipment_part INTEGER NOT NULL CHECK(equipment_part BETWEEN 0 AND 35),
    item_uid INTEGER NOT NULL CHECK(item_uid>0),
    item_id INTEGER NOT NULL CHECK(item_id>0),
    PRIMARY KEY(character_id,equipment_part),
    UNIQUE(character_id,item_uid),
    FOREIGN KEY(character_id) REFERENCES character_skill_switch_inventory(character_id) ON DELETE CASCADE,
    FOREIGN KEY(item_uid) REFERENCES character_inventory_items(item_uid) ON DELETE CASCADE
);
CREATE TRIGGER title_book_exclusive_insert BEFORE INSERT ON character_title_book_slots
WHEN EXISTS(SELECT 1 FROM character_inventory_slots WHERE item_uid=NEW.item_uid) OR
     EXISTS(SELECT 1 FROM mailbox_attachments WHERE item_uid=NEW.item_uid)
BEGIN SELECT RAISE(ABORT,'title item already has a position'); END;
CREATE TRIGGER title_book_exclusive_update BEFORE UPDATE ON character_title_book_slots
WHEN EXISTS(SELECT 1 FROM character_inventory_slots WHERE item_uid=NEW.item_uid) OR
     EXISTS(SELECT 1 FROM mailbox_attachments WHERE item_uid=NEW.item_uid)
BEGIN SELECT RAISE(ABORT,'title item already has a position'); END;
CREATE TRIGGER inventory_not_title_insert BEFORE INSERT ON character_inventory_slots
WHEN EXISTS(SELECT 1 FROM character_title_book_slots WHERE item_uid=NEW.item_uid)
BEGIN SELECT RAISE(ABORT,'item is in title book'); END;
CREATE TRIGGER inventory_not_title_update BEFORE UPDATE ON character_inventory_slots
WHEN EXISTS(SELECT 1 FROM character_title_book_slots WHERE item_uid=NEW.item_uid)
BEGIN SELECT RAISE(ABORT,'item is in title book'); END;
CREATE TRIGGER mail_not_title_insert BEFORE INSERT ON mailbox_attachments
WHEN EXISTS(SELECT 1 FROM character_title_book_slots WHERE item_uid=NEW.item_uid)
BEGIN SELECT RAISE(ABORT,'item is in title book'); END;
CREATE TRIGGER mail_not_title_update BEFORE UPDATE ON mailbox_attachments
WHEN EXISTS(SELECT 1 FROM character_title_book_slots WHERE item_uid=NEW.item_uid)
BEGIN SELECT RAISE(ABORT,'item is in title book'); END;
CREATE TABLE account_adventure_representatives(
  account TEXT NOT NULL REFERENCES account_adventures(account),job INTEGER NOT NULL CHECK(job BETWEEN 0 AND 15),
  grow INTEGER NOT NULL CHECK(grow BETWEEN 1 AND 15),character_id INTEGER,
  PRIMARY KEY(account,job,grow),FOREIGN KEY(character_id,account) REFERENCES characters(id,account) ON DELETE CASCADE);
CREATE TABLE account_adventure_purchases(
  account TEXT NOT NULL REFERENCES account_adventures(account),item_id INTEGER NOT NULL CHECK(item_id>0),
  count INTEGER NOT NULL CHECK(count BETWEEN 1 AND 4294967295),PRIMARY KEY(account,item_id));
CREATE TABLE account_adventure_weekly_config(
  account TEXT PRIMARY KEY REFERENCES account_adventures(account),
  slots BLOB NOT NULL CHECK(typeof(slots)='blob' AND length(slots)=5));
CREATE TABLE character_weekly_dungeon(
  character_id INTEGER NOT NULL,account TEXT NOT NULL,week INTEGER NOT NULL CHECK(week BETWEEN 0 AND 99999999),
  slot INTEGER NOT NULL CHECK(slot BETWEEN 0 AND 8),used INTEGER NOT NULL CHECK(used BETWEEN 0 AND 1000000),
  PRIMARY KEY(character_id,week,slot),
  FOREIGN KEY(character_id,account) REFERENCES characters(id,account) ON DELETE CASCADE);
CREATE INDEX character_weekly_dungeon_account ON character_weekly_dungeon(account,week);
CREATE TABLE account_adventure_expeditions(
  account TEXT NOT NULL REFERENCES account_adventures(account),area INTEGER NOT NULL CHECK(area BETWEEN 1 AND 255),
  run_id TEXT NOT NULL UNIQUE CHECK(length(run_id)=32),started INTEGER NOT NULL CHECK(started>=0),
  ends INTEGER NOT NULL CHECK(ends>started),reward INTEGER NOT NULL CHECK(reward BETWEEN 0 AND 4294967295),
  extra_reward INTEGER NOT NULL CHECK(extra_reward BETWEEN 0 AND 4294967295),
  attributes TEXT NOT NULL CHECK(json_valid(attributes)),PRIMARY KEY(account,area));
CREATE TABLE account_adventure_expedition_members(
  account TEXT NOT NULL,area INTEGER NOT NULL,ordinal INTEGER NOT NULL CHECK(ordinal BETWEEN 0 AND 3),
  character_id INTEGER NOT NULL,member_snapshot TEXT NOT NULL CHECK(json_valid(member_snapshot)),
  PRIMARY KEY(account,area,ordinal),UNIQUE(account,character_id),
  FOREIGN KEY(account,area) REFERENCES account_adventure_expeditions(account,area) ON DELETE CASCADE,
  FOREIGN KEY(character_id,account) REFERENCES characters(id,account));
CREATE TABLE account_adventure_receipts(
  account TEXT NOT NULL REFERENCES account_adventures(account),event_id TEXT NOT NULL,
  character_id INTEGER,action TEXT NOT NULL,amount INTEGER NOT NULL CHECK(amount>=0),
  before_revision INTEGER NOT NULL CHECK(before_revision>=0),revision INTEGER NOT NULL CHECK(revision>before_revision),
  PRIMARY KEY(account,event_id),FOREIGN KEY(character_id,account) REFERENCES characters(id,account));
CREATE TABLE character_adventure_credits(
  character_id INTEGER PRIMARY KEY,account TEXT NOT NULL,
  baseline_experience INTEGER NOT NULL CHECK(baseline_experience>=0),
  position_experience INTEGER NOT NULL CHECK(position_experience>=0),
  FOREIGN KEY(character_id,account) REFERENCES characters(id,account) ON DELETE CASCADE
);
CREATE TABLE adventure_ledger_meta(
  key TEXT NOT NULL PRIMARY KEY,value TEXT NOT NULL
);

CREATE TABLE account_hotkey_settings(
  account TEXT NOT NULL REFERENCES accounts(account_id),profile INTEGER NOT NULL CHECK(profile IN (0,1)),
  hotkeys BLOB NOT NULL CHECK(typeof(hotkeys)='blob' AND length(hotkeys) BETWEEN 1 AND 1399),
  PRIMARY KEY(account,profile));
CREATE TABLE account_quick_chat(
  account TEXT NOT NULL REFERENCES accounts(account_id),bank INTEGER NOT NULL CHECK(bank IN (0,1)),
  quick_chat BLOB NOT NULL CHECK(typeof(quick_chat)='blob' AND length(quick_chat)=300),
  PRIMARY KEY(account,bank));
CREATE TABLE account_chat_emoticons(
  account TEXT NOT NULL REFERENCES accounts(account_id),
  emoticon_id INTEGER NOT NULL CHECK(emoticon_id BETWEEN 1 AND 32767),
  macro BLOB NOT NULL CHECK(typeof(macro)='blob' AND length(macro)<=259),
  PRIMARY KEY(account,emoticon_id));
CREATE TABLE account_recent_emoticons(
  account TEXT PRIMARY KEY NOT NULL REFERENCES accounts(account_id),
  entries BLOB NOT NULL CHECK(typeof(entries)='blob' AND length(entries)=72));
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
  PRIMARY KEY(character_id,instance_id,ground_key),
  FOREIGN KEY(character_id,account) REFERENCES characters(id,account) ON DELETE CASCADE);
CREATE TABLE character_quest_effect_receipts(
  character_id INTEGER NOT NULL REFERENCES characters(id),operation_id TEXT NOT NULL CHECK(length(operation_id)=32),
  before_revision INTEGER NOT NULL CHECK(before_revision>=0),revision INTEGER NOT NULL CHECK(revision=before_revision+1),
  claimed_quests TEXT NOT NULL CHECK(json_valid(claimed_quests) AND json_type(claimed_quests)='array'),
  changed_items TEXT NOT NULL CHECK(json_valid(changed_items) AND json_type(changed_items)='array'),
  PRIMARY KEY(character_id,operation_id));
CREATE TABLE character_quest_special_reward_claims(
  character_id INTEGER NOT NULL,quest_id INTEGER NOT NULL CHECK(quest_id BETWEEN 1 AND 29999),operation_id TEXT NOT NULL,
  PRIMARY KEY(character_id,quest_id),
  FOREIGN KEY(character_id,operation_id) REFERENCES character_quest_effect_receipts(character_id,operation_id));
-- 公会不归属单个角色：guilds 不建立指向 characters 的外键，会长删角由删除业务同事务解散公会，
-- guild_members.character_id 才外键到 characters 并在角色删除时随行清理。
-- name 的唯一约束就是「公会名重复确认」的唯一依据。
CREATE TABLE guilds(
  guild_id INTEGER PRIMARY KEY AUTOINCREMENT CHECK(guild_id BETWEEN 1 AND 2147483647),
  name TEXT NOT NULL UNIQUE CHECK(length(name) BETWEEN 1 AND 14),
  address TEXT NOT NULL UNIQUE CHECK(length(address) BETWEEN 1 AND 24),
  master_character_id INTEGER NOT NULL CHECK(master_character_id>0),
  tier INTEGER NOT NULL DEFAULT 0 CHECK(tier BETWEEN 0 AND 4),
  level INTEGER NOT NULL DEFAULT 1 CHECK(level BETWEEN 1 AND 50),
  experience INTEGER NOT NULL DEFAULT 0 CHECK(experience>=0),
  public_funds INTEGER NOT NULL DEFAULT 0 CHECK(public_funds BETWEEN 0 AND 2147483647),
  guild_crystal INTEGER NOT NULL DEFAULT 0 CHECK(guild_crystal BETWEEN 0 AND 2147483647),
  notice TEXT NOT NULL DEFAULT '',
  memo TEXT NOT NULL DEFAULT '',
  created_at_unix INTEGER NOT NULL CHECK(created_at_unix>=0));
CREATE TABLE guild_members(
  guild_id INTEGER NOT NULL REFERENCES guilds(guild_id) ON DELETE CASCADE,
  character_id INTEGER NOT NULL UNIQUE CHECK(character_id>0) REFERENCES characters(id) ON DELETE CASCADE,
  grade INTEGER NOT NULL DEFAULT 0 CHECK(grade BETWEEN 0 AND 255),
  contribution INTEGER NOT NULL DEFAULT 0 CHECK(contribution BETWEEN 0 AND 2147483647),
  member_memo TEXT NOT NULL DEFAULT '',
  joined_at_unix INTEGER NOT NULL CHECK(joined_at_unix>=0),
  last_login_unix INTEGER NOT NULL DEFAULT 0 CHECK(last_login_unix>=0),
  PRIMARY KEY(guild_id,character_id));
CREATE INDEX guild_members_grade ON guild_members(guild_id,grade);

CREATE TABLE character_quest_stage_witnesses(
  character_id INTEGER NOT NULL,
  account TEXT NOT NULL,
  source_acceptance_id TEXT NOT NULL CHECK(length(source_acceptance_id)=32 AND source_acceptance_id NOT GLOB '*[^0-9a-f]*'),
  source_quest_id INTEGER NOT NULL CHECK(source_quest_id BETWEEN 1 AND 29999),
  source_revision INTEGER NOT NULL CHECK(source_revision>0),
  target_level INTEGER NOT NULL CHECK(target_level BETWEEN 1 AND 95),
  rule_version INTEGER NOT NULL CHECK(rule_version>0),
  status TEXT NOT NULL CHECK(status IN('Deferred','Granted','Satisfied','Invalidated')),
  proof TEXT NOT NULL CHECK(json_valid(proof) AND json_type(proof)='object'),
  PRIMARY KEY(character_id,source_acceptance_id),
  FOREIGN KEY(character_id,account) REFERENCES characters(id,account),
  FOREIGN KEY(character_id,source_acceptance_id) REFERENCES character_quest_completion_receipts(character_id,acceptance_id)
) STRICT;

CREATE TABLE character_quest_stage_claims(
  character_id INTEGER NOT NULL,
  account TEXT NOT NULL,
  source_acceptance_id TEXT NOT NULL,
  trigger_acceptance_id TEXT NOT NULL,
  target_level INTEGER NOT NULL CHECK(target_level BETWEEN 1 AND 95),
  before_experience INTEGER NOT NULL CHECK(before_experience>=0),
  after_experience INTEGER NOT NULL CHECK(after_experience>=before_experience),
  amount INTEGER NOT NULL CHECK(amount BETWEEN 0 AND 2147483647 AND after_experience-before_experience=amount),
  revision INTEGER NOT NULL CHECK(revision>0),
  UNIQUE(character_id,target_level),
  UNIQUE(character_id,source_acceptance_id),
  FOREIGN KEY(character_id,account) REFERENCES characters(id,account),
  FOREIGN KEY(character_id,source_acceptance_id) REFERENCES character_quest_stage_witnesses(character_id,source_acceptance_id),
  FOREIGN KEY(character_id,trigger_acceptance_id) REFERENCES character_quest_completion_receipts(character_id,acceptance_id)
) STRICT;

CREATE TABLE account_grief_tower(
  account TEXT PRIMARY KEY,game_day INTEGER NOT NULL CHECK(game_day>0),
  used INTEGER NOT NULL CHECK(used>=0));
CREATE TABLE character_grief_tower(
  character_id INTEGER PRIMARY KEY,account TEXT NOT NULL,
  game_day INTEGER NOT NULL CHECK(game_day>0),cleared INTEGER NOT NULL CHECK(cleared IN(0,1)),
  layer INTEGER NOT NULL CHECK(layer BETWEEN 1 AND 101),
  instance_id TEXT NOT NULL CHECK(length(instance_id)=32),task_attempt INTEGER NOT NULL CHECK(task_attempt>0),
  FOREIGN KEY(character_id,account) REFERENCES characters(id,account) ON DELETE CASCADE);
CREATE TABLE character_grief_tower_clears(
  character_id INTEGER NOT NULL,account TEXT NOT NULL,instance_id TEXT NOT NULL CHECK(length(instance_id)=32),
  event_id TEXT NOT NULL CHECK(length(event_id)=32),task_attempt INTEGER NOT NULL CHECK(task_attempt>0),
  game_day INTEGER NOT NULL CHECK(game_day>0),layer INTEGER NOT NULL CHECK(layer BETWEEN 1 AND 100),
  dungeon_id INTEGER NOT NULL CHECK(dungeon_id>0),reward_json TEXT NOT NULL CHECK(json_valid(reward_json)),
  mail_id INTEGER,revision INTEGER NOT NULL CHECK(revision>0),
  PRIMARY KEY(character_id,instance_id),UNIQUE(character_id,event_id),
  FOREIGN KEY(character_id,instance_id) REFERENCES character_grief_tower_entries(character_id,instance_id),
  FOREIGN KEY(character_id,account) REFERENCES characters(id,account),
  FOREIGN KEY(mail_id) REFERENCES mailbox_messages(mail_id));
CREATE TABLE character_grief_tower_entries(
  character_id INTEGER NOT NULL,account TEXT NOT NULL,instance_id TEXT NOT NULL CHECK(length(instance_id)=32),
  task_attempt INTEGER NOT NULL CHECK(task_attempt>0),game_day INTEGER NOT NULL CHECK(game_day>0),
  layer INTEGER NOT NULL CHECK(layer BETWEEN 1 AND 100),dungeon_id INTEGER NOT NULL CHECK(dungeon_id>0),
  invitation INTEGER NOT NULL CHECK(invitation IN(0,1)),account_used INTEGER NOT NULL CHECK(account_used>=0),
  account_limit INTEGER NOT NULL CHECK(account_limit>0),revision INTEGER NOT NULL CHECK(revision>0),
  PRIMARY KEY(character_id,instance_id),UNIQUE(character_id,task_attempt),
  FOREIGN KEY(character_id,account) REFERENCES characters(id,account) ON DELETE CASCADE);
CREATE TABLE character_social_links(
          owner_id INTEGER NOT NULL REFERENCES characters(id) ON DELETE CASCADE,
          target_id INTEGER NOT NULL REFERENCES characters(id) ON DELETE CASCADE,
          kind INTEGER NOT NULL CHECK(kind IN(0,1)),created INTEGER NOT NULL CHECK(created>=0),memo TEXT NOT NULL DEFAULT '',
          PRIMARY KEY(owner_id,target_id,kind),CHECK(owner_id<>target_id)
        ) STRICT;
        CREATE INDEX character_social_target ON character_social_links(target_id,kind);
        CREATE TABLE account_social_links(
          owner_account TEXT NOT NULL REFERENCES accounts(account_id),
          target_account TEXT NOT NULL REFERENCES accounts(account_id),
          created INTEGER NOT NULL CHECK(created>=0),memo TEXT NOT NULL DEFAULT '',group_id INTEGER NOT NULL DEFAULT 0 CHECK(group_id BETWEEN 0 AND 255),
          PRIMARY KEY(owner_account,target_account),CHECK(owner_account<>target_account)
        ) STRICT;
        CREATE TABLE account_social_requests(
          source_account TEXT NOT NULL REFERENCES accounts(account_id),
          target_account TEXT NOT NULL REFERENCES accounts(account_id),
          message TEXT NOT NULL,created INTEGER NOT NULL CHECK(created>=0),
          memo TEXT NOT NULL DEFAULT '',group_id INTEGER NOT NULL DEFAULT 0 CHECK(group_id BETWEEN 0 AND 4),
          PRIMARY KEY(source_account,target_account),CHECK(source_account<>target_account)
        ) STRICT;

CREATE TABLE character_epic_production(
    character_id INTEGER PRIMARY KEY REFERENCES characters(id) ON DELETE CASCADE,
    weapon_id INTEGER NOT NULL CHECK(weapon_id>=0),
    charge INTEGER NOT NULL CHECK(charge>=0 AND (weapon_id<>0 OR charge=0)),
    service_week INTEGER NOT NULL CHECK(service_week>=-1),
    weekly_uses INTEGER NOT NULL CHECK(weekly_uses BETWEEN 0 AND 255),
    since_big_chance INTEGER NOT NULL CHECK(since_big_chance>=0),
    special_uses INTEGER NOT NULL CHECK(special_uses>=0),
    additional_weekly_uses INTEGER NOT NULL CHECK(additional_weekly_uses>=0),
            intermediate_material_remainder INTEGER NOT NULL CHECK(intermediate_material_remainder>=0),
            ordinary_material_remainder INTEGER NOT NULL CHECK(ordinary_material_remainder>=0)
);

CREATE TABLE quest_eligibility_mail_dispatches(
    account TEXT NOT NULL,
    scope_character_id INTEGER NOT NULL CHECK(scope_character_id>=0),
    quest_id INTEGER NOT NULL CHECK(quest_id>0),
    recipient_id INTEGER NOT NULL CHECK(recipient_id>0),
    mail_id INTEGER NOT NULL CHECK(mail_id>0),
    resource_path TEXT NOT NULL,
    dispatched_at INTEGER NOT NULL CHECK(dispatched_at>=0),
    PRIMARY KEY(account,scope_character_id,quest_id)
);

CREATE TABLE account_pvp_total_teams(
    account TEXT NOT NULL PRIMARY KEY,
    team_id TEXT NOT NULL UNIQUE CHECK(length(team_id)=32 AND team_id NOT GLOB '*[^0-9a-f]*' AND team_id<>'00000000000000000000000000000000'),
    revision INTEGER NOT NULL CHECK(typeof(revision)='integer' AND revision>0),
    name TEXT NOT NULL UNIQUE CHECK(length(name) BETWEEN 2 AND 12),
    first_id INTEGER,
    second_id INTEGER,
    third_id INTEGER,
    CHECK(first_id IS NULL OR second_id IS NULL OR first_id<>second_id),
    CHECK(first_id IS NULL OR third_id IS NULL OR first_id<>third_id),
    CHECK(second_id IS NULL OR third_id IS NULL OR second_id<>third_id),
    FOREIGN KEY(first_id,account) REFERENCES characters(id,account),
    FOREIGN KEY(second_id,account) REFERENCES characters(id,account),
    FOREIGN KEY(third_id,account) REFERENCES characters(id,account)
);

CREATE TABLE pvp_arena_match_results(
    match_id TEXT NOT NULL PRIMARY KEY CHECK(length(match_id)=32 AND match_id NOT GLOB '*[^0-9a-f]*' AND match_id<>'00000000000000000000000000000000'),
    result_json TEXT NOT NULL CHECK(json_valid(result_json) AND json_type(result_json)='object'),
    finished_at INTEGER NOT NULL CHECK(finished_at>=0)
);

CREATE TABLE character_pvp_ratings(
    character_id INTEGER NOT NULL PRIMARY KEY,
    account TEXT NOT NULL,
    revision INTEGER NOT NULL CHECK(typeof(revision)='integer' AND revision>0),
    state_json TEXT NOT NULL CHECK(json_valid(state_json) AND json_type(state_json)='object'),
    FOREIGN KEY(character_id,account) REFERENCES characters(id,account)
);

CREATE TABLE character_pvp_rating_claims(
    sequence INTEGER PRIMARY KEY AUTOINCREMENT,
    match_id TEXT NOT NULL REFERENCES pvp_arena_match_results(match_id),
    character_id INTEGER NOT NULL,
    account TEXT NOT NULL,
    rating_mode INTEGER NOT NULL CHECK(typeof(rating_mode)='integer' AND rating_mode BETWEEN 0 AND 2),
    outcome INTEGER NOT NULL CHECK(typeof(outcome)='integer' AND outcome BETWEEN 0 AND 3),
    receipt_json TEXT CHECK(receipt_json IS NULL OR json_valid(receipt_json) AND json_type(receipt_json)='object'),
    UNIQUE(match_id,character_id),
    FOREIGN KEY(character_id,account) REFERENCES characters(id,account)
);
CREATE INDEX ix_pvp_rating_pending ON character_pvp_rating_claims(account,character_id,rating_mode,sequence) WHERE receipt_json IS NULL;

CREATE TABLE character_pvp_training_skills(
    character_id INTEGER PRIMARY KEY REFERENCES characters(id) ON DELETE CASCADE,
    state_json TEXT NOT NULL CHECK(json_valid(state_json) AND json_type(state_json)='object'),
    operation_id TEXT NOT NULL CHECK(length(operation_id)=32),
    before_revision INTEGER NOT NULL CHECK(before_revision>=0)
);

CREATE TABLE account_dazzlement_tower_bindings(
  account TEXT NOT NULL,
  slot INTEGER NOT NULL CHECK(typeof(slot)='integer' AND slot BETWEEN 1 AND 5),
  week INTEGER NOT NULL CHECK(typeof(week)='integer' AND week BETWEEN 10000101 AND 99991231),
  character_id INTEGER UNIQUE REFERENCES characters(id) ON DELETE SET NULL,
  last_entry_day INTEGER NOT NULL CHECK(typeof(last_entry_day)='integer' AND last_entry_day>=0),
  PRIMARY KEY(account,slot)
);

CREATE TABLE account_pvp_private_characters(
    account TEXT NOT NULL,
    job INTEGER NOT NULL CHECK(job BETWEEN 0 AND 15),
    grow_type INTEGER NOT NULL CHECK(grow_type BETWEEN 0 AND 5),
    character_id INTEGER UNIQUE REFERENCES characters(id) ON DELETE SET NULL,
    blocked_until INTEGER NOT NULL CHECK(blocked_until>=0),
    recreate_days INTEGER NOT NULL CHECK(recreate_days BETWEEN 0 AND 36500),
    PRIMARY KEY(account,job,grow_type)
);

CREATE TABLE marriage_records(
  marriage_id TEXT PRIMARY KEY CHECK(length(marriage_id)=32 AND marriage_id NOT GLOB '*[^0-9a-f]*'),
  context_id INTEGER NOT NULL UNIQUE CHECK(typeof(context_id)='integer' AND context_id BETWEEN 1 AND 4294967295),
  revision INTEGER NOT NULL CHECK(typeof(revision)='integer' AND revision>0),
  first_id INTEGER NOT NULL CHECK(typeof(first_id)='integer' AND first_id BETWEEN 1 AND 65535),
  second_id INTEGER NOT NULL CHECK(typeof(second_id)='integer' AND second_id BETWEEN 1 AND 65535 AND second_id<>first_id),
  first_account TEXT NOT NULL CHECK(length(first_account)>0),
  second_account TEXT NOT NULL CHECK(length(second_account)>0),
  state INTEGER NOT NULL CHECK(typeof(state)='integer' AND state BETWEEN 0 AND 4),
  created_at INTEGER NOT NULL CHECK(typeof(created_at)='integer' AND created_at>=0),
  ended_at INTEGER NOT NULL CHECK(typeof(ended_at)='integer' AND ended_at>=0),
  aggregate_json TEXT NOT NULL CHECK(json_valid(aggregate_json) AND json_type(aggregate_json)='object'),
  CHECK((state=0 AND ended_at>=created_at) OR (state<>0 AND ended_at=0))
);
CREATE TABLE marriage_active_members(
  character_id INTEGER PRIMARY KEY REFERENCES characters(id),
  marriage_id TEXT NOT NULL REFERENCES marriage_records(marriage_id)
);
CREATE INDEX marriage_active_relation ON marriage_active_members(marriage_id);
CREATE INDEX marriage_first_history ON marriage_records(first_id,created_at DESC,marriage_id);
CREATE INDEX marriage_second_history ON marriage_records(second_id,created_at DESC,marriage_id);

CREATE TABLE character_operation_hope_reward_receipts(
    character_id INTEGER NOT NULL,account TEXT NOT NULL,
    instance_id TEXT NOT NULL CHECK(length(instance_id)=32 AND instance_id NOT GLOB '*[^0-9a-f]*' AND instance_id<>'00000000000000000000000000000000'),
    acceptance_id TEXT NOT NULL CHECK(length(acceptance_id)=32 AND acceptance_id NOT GLOB '*[^0-9a-f]*' AND acceptance_id<>'00000000000000000000000000000000'),
    task_attempt INTEGER NOT NULL CHECK(task_attempt>0),
    maze_ordinal INTEGER NOT NULL CHECK(maze_ordinal>=0),
    quest_id INTEGER NOT NULL CHECK(quest_id BETWEEN 1 AND 29999),
    clear_epoch INTEGER NOT NULL CHECK(clear_epoch>0),
    generation INTEGER NOT NULL CHECK(generation>0),
    before_revision INTEGER NOT NULL CHECK(before_revision>=0),
    revision INTEGER NOT NULL CHECK(revision=before_revision+1),
    before_level INTEGER NOT NULL CHECK(before_level BETWEEN 1 AND 95),
    after_level INTEGER NOT NULL CHECK(after_level BETWEEN before_level AND 95),
    before_experience INTEGER NOT NULL CHECK(before_experience>=0),
    requested_experience INTEGER NOT NULL CHECK(requested_experience BETWEEN 0 AND 2147483647),
    after_experience INTEGER NOT NULL CHECK(after_experience>=before_experience AND after_experience<=before_experience+requested_experience),
    plan_snapshot TEXT NOT NULL CHECK(json_valid(plan_snapshot) AND json_type(plan_snapshot)='object'),
    rewards TEXT NOT NULL CHECK(json_valid(rewards) AND json_type(rewards)='array'),
    changed_items TEXT NOT NULL CHECK(json_valid(changed_items) AND json_type(changed_items)='array'),
    created_at INTEGER NOT NULL CHECK(created_at>=0),
    PRIMARY KEY(character_id,instance_id),
    UNIQUE(character_id,acceptance_id),
    FOREIGN KEY(character_id,account) REFERENCES characters(id,account) ON DELETE CASCADE
);

CREATE TABLE guild_cargo_state(
  guild_id INTEGER PRIMARY KEY REFERENCES guilds(guild_id) ON DELETE CASCADE,
  revision INTEGER NOT NULL DEFAULT 0 CHECK(revision>=0),
  capacity INTEGER NOT NULL DEFAULT 8 CHECK(capacity BETWEEN 8 AND 56)
);
CREATE TABLE guild_cargo_slots(
  guild_id INTEGER NOT NULL REFERENCES guild_cargo_state(guild_id) ON DELETE RESTRICT,
  slot_index INTEGER NOT NULL CHECK(slot_index BETWEEN 0 AND 55),
  item_uid INTEGER NOT NULL UNIQUE,
  account TEXT GENERATED ALWAYS AS ('guild:' || guild_id) STORED,
  owner_character_id INTEGER NOT NULL DEFAULT 0 CHECK(owner_character_id=0),
  PRIMARY KEY(guild_id,slot_index),
  FOREIGN KEY(account,owner_character_id,item_uid)
    REFERENCES character_inventory_items(account,owner_character_id,item_uid) ON DELETE RESTRICT
);
CREATE TABLE guild_cargo_history(
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  guild_id INTEGER NOT NULL REFERENCES guilds(guild_id) ON DELETE CASCADE,
  created_at_unix INTEGER NOT NULL CHECK(created_at_unix BETWEEN 0 AND 4294967295),
  kind INTEGER NOT NULL CHECK(kind IN(1,2)),
  name TEXT NOT NULL,
  item_core TEXT NOT NULL CHECK(json_valid(item_core)),
  moved_count INTEGER NOT NULL CHECK(moved_count>0)
);
CREATE INDEX guild_cargo_history_recent ON guild_cargo_history(guild_id,id DESC);
CREATE TABLE guild_cargo_receipts(
  request_id TEXT PRIMARY KEY CHECK(length(request_id)=64 AND request_id NOT GLOB '*[^0-9A-Fa-f]*'),
  fingerprint TEXT NOT NULL CHECK(length(fingerprint)=64),
  account TEXT NOT NULL,
  character_id INTEGER NOT NULL,
  guild_id INTEGER NOT NULL,
  revision INTEGER NOT NULL CHECK(revision>0),
  destination_slot INTEGER NOT NULL CHECK(destination_slot>=0),
  created_at_unix INTEGER NOT NULL
);
CREATE TRIGGER guild_cargo_exclusive_insert BEFORE INSERT ON guild_cargo_slots
WHEN EXISTS(SELECT 1 FROM character_inventory_slots WHERE item_uid=NEW.item_uid) OR
     EXISTS(SELECT 1 FROM character_title_book_slots WHERE item_uid=NEW.item_uid) OR
     EXISTS(SELECT 1 FROM mailbox_attachments WHERE item_uid=NEW.item_uid)
BEGIN SELECT RAISE(ABORT,'guild cargo item already has a position'); END;
CREATE TRIGGER guild_cargo_exclusive_update BEFORE UPDATE ON guild_cargo_slots
WHEN EXISTS(SELECT 1 FROM character_inventory_slots WHERE item_uid=NEW.item_uid) OR
     EXISTS(SELECT 1 FROM character_title_book_slots WHERE item_uid=NEW.item_uid) OR
     EXISTS(SELECT 1 FROM mailbox_attachments WHERE item_uid=NEW.item_uid)
BEGIN SELECT RAISE(ABORT,'guild cargo item already has a position'); END;
CREATE TRIGGER inventory_not_guild_cargo_insert BEFORE INSERT ON character_inventory_slots
WHEN EXISTS(SELECT 1 FROM guild_cargo_slots WHERE item_uid=NEW.item_uid)
BEGIN SELECT RAISE(ABORT,'item is in guild cargo'); END;
CREATE TRIGGER inventory_not_guild_cargo_update BEFORE UPDATE ON character_inventory_slots
WHEN EXISTS(SELECT 1 FROM guild_cargo_slots WHERE item_uid=NEW.item_uid)
BEGIN SELECT RAISE(ABORT,'item is in guild cargo'); END;
CREATE TRIGGER title_not_guild_cargo_insert BEFORE INSERT ON character_title_book_slots
WHEN EXISTS(SELECT 1 FROM guild_cargo_slots WHERE item_uid=NEW.item_uid)
BEGIN SELECT RAISE(ABORT,'item is in guild cargo'); END;
CREATE TRIGGER title_not_guild_cargo_update BEFORE UPDATE ON character_title_book_slots
WHEN EXISTS(SELECT 1 FROM guild_cargo_slots WHERE item_uid=NEW.item_uid)
BEGIN SELECT RAISE(ABORT,'item is in guild cargo'); END;
CREATE TRIGGER mail_not_guild_cargo_insert BEFORE INSERT ON mailbox_attachments
WHEN EXISTS(SELECT 1 FROM guild_cargo_slots WHERE item_uid=NEW.item_uid)
BEGIN SELECT RAISE(ABORT,'item is in guild cargo'); END;
CREATE TRIGGER mail_not_guild_cargo_update BEFORE UPDATE ON mailbox_attachments
WHEN EXISTS(SELECT 1 FROM guild_cargo_slots WHERE item_uid=NEW.item_uid)
BEGIN SELECT RAISE(ABORT,'item is in guild cargo'); END;

CREATE TABLE guild_economy(
    guild_id INTEGER PRIMARY KEY REFERENCES guilds(guild_id) ON DELETE CASCADE,
    member_limit INTEGER NOT NULL DEFAULT 300 CHECK(member_limit IN (300,500,700,1000))
);
CREATE TABLE guild_benefits(
    guild_id INTEGER NOT NULL REFERENCES guilds(guild_id) ON DELETE CASCADE,
    type INTEGER NOT NULL CHECK(type IN (1,3,5,7)),
    value INTEGER NOT NULL CHECK(value BETWEEN 0 AND 4294967295),
    expires_unix INTEGER NOT NULL CHECK(expires_unix BETWEEN 0 AND 2147483647),
    PRIMARY KEY(guild_id,type)
);
CREATE TABLE guild_economy_receipts(
    request_id TEXT PRIMARY KEY CHECK(length(request_id)=64),
    account TEXT NOT NULL,
    character_id INTEGER NOT NULL,
    guild_id INTEGER NOT NULL,
    operation INTEGER NOT NULL CHECK(operation IN (1,2)),
    content_id INTEGER NOT NULL,
    source_uid INTEGER NOT NULL DEFAULT 0,
    source_slot INTEGER NOT NULL DEFAULT 0,
    amount INTEGER NOT NULL CHECK(amount>0),
    created_at_unix INTEGER NOT NULL CHECK(created_at_unix BETWEEN 1 AND 2147483647)
);
CREATE TABLE guild_mileage_history(
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    guild_id INTEGER NOT NULL REFERENCES guilds(guild_id) ON DELETE CASCADE,
    character_id INTEGER NOT NULL,
    character_name TEXT NOT NULL,
    kind INTEGER NOT NULL CHECK(kind IN (3,5,8)),
    content_id INTEGER NOT NULL,
    amount INTEGER NOT NULL CHECK(amount BETWEEN 1 AND 2147483647),
    created_at_unix INTEGER NOT NULL CHECK(created_at_unix BETWEEN 1 AND 2147483647)
);
CREATE INDEX guild_mileage_history_recent ON guild_mileage_history(guild_id,id DESC);
CREATE TABLE guild_economy_events(
    event_id TEXT PRIMARY KEY,
    character_id INTEGER NOT NULL,
    guild_id INTEGER NOT NULL,
    amount INTEGER NOT NULL CHECK(amount BETWEEN 0 AND 2147483647)
);

CREATE TABLE guild_appearance(
  guild_id INTEGER PRIMARY KEY REFERENCES guilds(guild_id) ON DELETE CASCADE,
  mark INTEGER NOT NULL DEFAULT 0 CHECK(mark BETWEEN 0 AND 4294967295),
  aura_mark INTEGER NOT NULL DEFAULT 4294967295 CHECK(aura_mark BETWEEN 0 AND 4294967295),
  aura_frame INTEGER NOT NULL DEFAULT 4294967295 CHECK(aura_frame BETWEEN 0 AND 4294967295),
  CHECK((aura_mark=4294967295)=(aura_frame=4294967295))
);

CREATE TABLE guild_attendance(
  character_id INTEGER NOT NULL REFERENCES characters(id) ON DELETE CASCADE,
  guild_id INTEGER NOT NULL REFERENCES guilds(guild_id) ON DELETE CASCADE,
  joined_at_unix INTEGER NOT NULL CHECK(joined_at_unix>=0),
  business_day_unix INTEGER NOT NULL,
  observed_at_unix INTEGER NOT NULL CHECK(observed_at_unix>0 AND observed_at_unix>=joined_at_unix),
  PRIMARY KEY(guild_id,business_day_unix,character_id,joined_at_unix)
);

CREATE TABLE guild_teleport_receipts(
    request_id TEXT PRIMARY KEY CHECK(length(request_id)=64),
    account TEXT NOT NULL,
    character_id INTEGER NOT NULL,
    guild_id INTEGER NOT NULL,
    joined_at_unix INTEGER NOT NULL,
    participants TEXT NOT NULL,
    destination TEXT NOT NULL
);

CREATE TABLE guild_request_claims(
    request_id TEXT PRIMARY KEY CHECK(length(request_id)=64),
    kind INTEGER NOT NULL CHECK(kind BETWEEN 1 AND 7),
    account TEXT NOT NULL,
    character_id INTEGER NOT NULL
);
INSERT INTO guild_request_claims SELECT request_id,1,account,character_id FROM guild_cargo_receipts;
INSERT INTO guild_request_claims SELECT request_id,2,account,character_id FROM guild_economy_receipts;
INSERT INTO guild_request_claims SELECT request_id,3,account,character_id FROM guild_teleport_receipts;

CREATE TABLE guild_experience_receipts(
    character_id INTEGER NOT NULL,
    event_id TEXT NOT NULL,
    bonuses TEXT NOT NULL,
    PRIMARY KEY(character_id,event_id)
);

CREATE TABLE guild_contribution_events(
  event_id TEXT PRIMARY KEY,
  character_id INTEGER NOT NULL REFERENCES characters(id) ON DELETE CASCADE,
  guild_id INTEGER REFERENCES guilds(guild_id) ON DELETE SET NULL,
  joined_at_unix INTEGER NOT NULL CHECK(joined_at_unix>=0),
  source INTEGER NOT NULL CHECK(source BETWEEN 1 AND 7),
  business_day_unix INTEGER NOT NULL,
  week_start_unix INTEGER NOT NULL,
  points INTEGER NOT NULL CHECK(points>=0),
  created_at_unix INTEGER NOT NULL CHECK(created_at_unix>0)
);
CREATE INDEX ix_guild_contribution_week ON guild_contribution_events(guild_id,character_id,joined_at_unix,week_start_unix);
CREATE INDEX ix_guild_contribution_daily_cap ON guild_contribution_events(character_id,business_day_unix,source);
CREATE UNIQUE INDEX ux_guild_contribution_daily ON guild_contribution_events(character_id,business_day_unix,source) WHERE source IN (1,2);
CREATE TABLE guild_contribution_settlements(
  guild_id INTEGER NOT NULL REFERENCES guilds(guild_id) ON DELETE CASCADE,
  character_id INTEGER NOT NULL REFERENCES characters(id) ON DELETE CASCADE,
  joined_at_unix INTEGER NOT NULL CHECK(joined_at_unix>=0),
  week_start_unix INTEGER NOT NULL,
  points INTEGER NOT NULL CHECK(points>0),
  coins INTEGER NOT NULL CHECK(coins>0),
  settled_at_unix INTEGER NOT NULL CHECK(settled_at_unix>0),
  PRIMARY KEY(guild_id,character_id,joined_at_unix,week_start_unix)
);

CREATE TABLE guild_npc_progress(
    guild_id INTEGER NOT NULL REFERENCES guilds(guild_id) ON DELETE CASCADE,
    npc_id INTEGER NOT NULL,
    level INTEGER NOT NULL CHECK(level BETWEEN 1 AND 5),
    upgrade_target INTEGER NOT NULL DEFAULT 0 CHECK(upgrade_target BETWEEN 0 AND 5),
    upgrade_until INTEGER NOT NULL DEFAULT 0 CHECK(upgrade_until BETWEEN 0 AND 2147483647),
    contract_until INTEGER NOT NULL DEFAULT 0 CHECK(contract_until BETWEEN 0 AND 2147483647),
    PRIMARY KEY(guild_id,npc_id),
    CHECK((upgrade_target=0 AND upgrade_until=0) OR (upgrade_target=level+1 AND upgrade_until>0))
);
CREATE INDEX guild_npc_due ON guild_npc_progress(upgrade_until) WHERE upgrade_until>0;
CREATE TABLE guild_npc_receipts(
    request_id TEXT PRIMARY KEY CHECK(length(request_id)=64),
    account TEXT NOT NULL,
    character_id INTEGER NOT NULL,
    guild_id INTEGER NOT NULL,
    joined_at INTEGER NOT NULL,
    npc_id INTEGER NOT NULL,
    action INTEGER NOT NULL,
    argument INTEGER NOT NULL,
    deadline INTEGER NOT NULL
);

CREATE TABLE guild_growth_item_receipts(
  request_id TEXT PRIMARY KEY CHECK(length(request_id)=64),
  account TEXT NOT NULL,character_id INTEGER NOT NULL,
  guild_id INTEGER NOT NULL CHECK(guild_id>0),joined_at_unix INTEGER NOT NULL CHECK(joined_at_unix>=0),
  source_uid INTEGER NOT NULL CHECK(source_uid>0),source_slot INTEGER NOT NULL CHECK(source_slot>=0),
  item_id INTEGER NOT NULL CHECK(item_id>0),amount INTEGER NOT NULL CHECK(amount>0),
  before_experience INTEGER NOT NULL CHECK(before_experience BETWEEN 0 AND 4294967295),
  after_experience INTEGER NOT NULL CHECK(after_experience BETWEEN 0 AND 4294967295),
  before_level INTEGER NOT NULL CHECK(before_level BETWEEN 1 AND 10),
  after_level INTEGER NOT NULL CHECK(after_level BETWEEN 1 AND 10),
  created_at_unix INTEGER NOT NULL CHECK(created_at_unix BETWEEN 1 AND 2147483647),
  CHECK(after_experience=before_experience+amount)
);

CREATE TABLE guild_dungeon_scroll_receipts(
  request_id TEXT PRIMARY KEY CHECK(length(request_id)=64),
  account TEXT NOT NULL,character_id INTEGER NOT NULL,
  guild_id INTEGER NOT NULL CHECK(guild_id>0),joined_at_unix INTEGER NOT NULL CHECK(joined_at_unix>=0),
  source_uid INTEGER NOT NULL CHECK(source_uid>0),source_slot INTEGER NOT NULL CHECK(source_slot>=0),
  item_id INTEGER NOT NULL CHECK(item_id>0),
  dungeon_id INTEGER NOT NULL CHECK(dungeon_id BETWEEN 1 AND 4294967295),
  increment INTEGER NOT NULL CHECK(increment>0),
  before_clears INTEGER NOT NULL CHECK(before_clears BETWEEN 0 AND 4294967295),
  after_clears INTEGER NOT NULL CHECK(after_clears BETWEEN 0 AND 4294967295),
  before_units INTEGER NOT NULL CHECK(before_units BETWEEN 0 AND 4294967295),
  after_units INTEGER NOT NULL CHECK(after_units BETWEEN 0 AND 4294967295),
  created_at_unix INTEGER NOT NULL CHECK(created_at_unix BETWEEN 1 AND 2147483647),
  CHECK(after_clears>=before_clears),
  CHECK(after_units>=before_units)
);

CREATE TABLE guild_npc_crafts(
  guild_id INTEGER PRIMARY KEY REFERENCES guilds(guild_id) ON DELETE CASCADE,
  npc_id INTEGER NOT NULL CHECK(npc_id=400001060),
  benefit_id INTEGER NOT NULL CHECK(benefit_id>0),
  public_funds INTEGER NOT NULL CHECK(public_funds>0),
  reward_count INTEGER NOT NULL CHECK(reward_count>0),
  deadline INTEGER NOT NULL CHECK(deadline BETWEEN 1 AND 2147483647),
  craft_id TEXT NOT NULL UNIQUE CHECK(length(craft_id)=64)
);
CREATE INDEX guild_npc_crafts_due ON guild_npc_crafts(deadline,guild_id);
CREATE TABLE guild_npc_craft_receipts(
  request_id TEXT PRIMARY KEY CHECK(length(request_id)=64),
  account TEXT NOT NULL,character_id INTEGER NOT NULL,guild_id INTEGER NOT NULL,
  joined_at INTEGER NOT NULL,action INTEGER NOT NULL CHECK(action IN (4,5)),
  benefit_id INTEGER NOT NULL,craft_id TEXT NOT NULL CHECK(length(craft_id)=64),
  deadline INTEGER NOT NULL CHECK(deadline BETWEEN 1 AND 2147483647)
);
CREATE TABLE guild_recommended_channels(
  guild_id INTEGER PRIMARY KEY REFERENCES guilds(guild_id) ON DELETE CASCADE,
  region INTEGER NOT NULL CHECK(typeof(region)='integer' AND region BETWEEN 0 AND 249),
  channel INTEGER NOT NULL CHECK(typeof(channel)='integer' AND channel BETWEEN 1 AND 255)
);

CREATE TABLE character_beast_dungeon_entries(
    character_id INTEGER NOT NULL,
    account TEXT NOT NULL,
    day INTEGER NOT NULL CHECK(typeof(day)='integer' AND day>0),
    dungeon_id INTEGER NOT NULL CHECK(dungeon_id BETWEEN 310 AND 314),
    used INTEGER NOT NULL CHECK(used BETWEEN 0 AND 2),
    PRIMARY KEY(character_id,day,dungeon_id),
    FOREIGN KEY(character_id,account) REFERENCES characters(id,account) ON DELETE CASCADE
);
CREATE TABLE guild_npc_craft_completions(
  craft_id TEXT PRIMARY KEY CHECK(length(craft_id)=64),guild_id INTEGER NOT NULL,
  benefit_id INTEGER NOT NULL,reward_count INTEGER NOT NULL CHECK(reward_count>0),
  before_crystal INTEGER NOT NULL CHECK(before_crystal>=0),
  after_crystal INTEGER NOT NULL CHECK(after_crystal=before_crystal+reward_count),
  completed_at INTEGER NOT NULL CHECK(completed_at BETWEEN 1 AND 2147483647)
);

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
    raid_type INTEGER NOT NULL DEFAULT 0 CHECK(raid_type IN(0,1,2,3)),
    reward_epoch INTEGER NOT NULL DEFAULT 0 CHECK(typeof(reward_epoch)='integer' AND reward_epoch>=0),
    PRIMARY KEY(character_id,instance_id,phase,kind),
    FOREIGN KEY(character_id,account) REFERENCES characters(id,account) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS character_raid_rewards_week ON character_raid_reward_receipts(character_id,week_key,final_phase,instance_id);

CREATE TABLE character_raid_runs(
    character_id INTEGER NOT NULL, account TEXT NOT NULL,
    instance_id TEXT NOT NULL CHECK(length(instance_id)=32 AND instance_id NOT GLOB '*[^0-9a-f]*' AND instance_id<>'00000000000000000000000000000000'),
    raid_type INTEGER NOT NULL CHECK(raid_type IN(0,1,2,3)),
    source TEXT NOT NULL CHECK(length(source)>0), admitted_at INTEGER NOT NULL CHECK(admitted_at>=0),
    state INTEGER NOT NULL CHECK(state IN(0,1,2)),
    phase0_event TEXT, phase0_anchor INTEGER, phase0_receipt TEXT,
    phase1_event TEXT, phase1_anchor INTEGER, phase1_receipt TEXT,
    clear_week INTEGER,
    guild_id INTEGER CHECK(guild_id BETWEEN 0 AND 4294967295),
    guild_joined_at INTEGER CHECK((guild_id IS NULL AND guild_joined_at IS NULL) OR (guild_id IS NOT NULL AND guild_joined_at IS NOT NULL AND ((guild_id=0 AND guild_joined_at=0) OR (guild_id>0 AND guild_joined_at>=0)))),
    entry_epoch INTEGER NOT NULL DEFAULT 0 CHECK(typeof(entry_epoch)='integer' AND entry_epoch>=0),
    reward_epoch INTEGER NOT NULL DEFAULT 0 CHECK(typeof(reward_epoch)='integer' AND reward_epoch>=0),
    PRIMARY KEY(character_id,instance_id),
    CHECK((phase0_event IS NULL AND phase0_anchor IS NULL AND phase0_receipt IS NULL) OR
        (phase0_event IS NOT NULL AND phase0_anchor IS NOT NULL AND phase0_receipt IS NOT NULL AND length(phase0_event)=32 AND phase0_anchor>=0 AND json_valid(phase0_receipt) AND json_type(phase0_receipt)='object')),
    CHECK((phase1_event IS NULL AND phase1_anchor IS NULL AND phase1_receipt IS NULL) OR
        (phase1_event IS NOT NULL AND phase1_anchor IS NOT NULL AND phase1_receipt IS NOT NULL AND length(phase1_event)=32 AND phase1_anchor>=0 AND json_valid(phase1_receipt) AND json_type(phase1_receipt)='object')),
    CHECK((state=2 AND clear_week IS NOT NULL AND clear_week>0 AND ((raid_type=2 AND phase0_event IS NOT NULL AND phase1_event IS NULL) OR (raid_type<>2 AND phase1_event IS NOT NULL))) OR (state<>2 AND clear_week IS NULL)),
    FOREIGN KEY(character_id,account) REFERENCES characters(id,account) ON DELETE CASCADE
);
CREATE UNIQUE INDEX character_raid_active ON character_raid_runs(character_id,raid_type) WHERE state=0;
CREATE INDEX character_raid_clear_week ON character_raid_runs(character_id,raid_type,clear_week) WHERE state=2;
CREATE INDEX character_raid_account_admission ON character_raid_runs(account,raid_type,admitted_at);
CREATE INDEX character_raid_rewards_type_week ON character_raid_reward_receipts(character_id,raid_type,week_key,final_phase,instance_id);
CREATE INDEX character_raid_entry_clear ON character_raid_runs(account,raid_type,entry_epoch,clear_week) WHERE state=2;
CREATE INDEX character_raid_reward_epoch ON character_raid_reward_receipts(account,raid_type,reward_epoch,week_key,final_phase,instance_id);

CREATE TABLE character_evildom_war_rewards(
  character_id INTEGER NOT NULL,account TEXT NOT NULL,battle_id TEXT NOT NULL CHECK(length(battle_id)=32),
  instance_id TEXT NOT NULL CHECK(length(instance_id)=32),task_attempt INTEGER NOT NULL CHECK(task_attempt>0),
  dungeon_id INTEGER NOT NULL CHECK(dungeon_id>0),mode INTEGER NOT NULL CHECK(mode BETWEEN 0 AND 2),
  created_at INTEGER NOT NULL CHECK(created_at>=0),reward_json TEXT NOT NULL CHECK(json_valid(reward_json)),
  mail_ids TEXT NOT NULL CHECK(json_valid(mail_ids)),delivered INTEGER NOT NULL DEFAULT 0 CHECK(delivered IN(0,1)),
  PRIMARY KEY(character_id,battle_id),UNIQUE(character_id,instance_id),
  FOREIGN KEY(character_id,account) REFERENCES characters(id,account));

CREATE TABLE evildom_war_battles(
  battle_id TEXT PRIMARY KEY NOT NULL CHECK(length(battle_id)=32),revision INTEGER NOT NULL CHECK(revision>=0),
  deadline INTEGER NOT NULL CHECK(deadline>=0),checkpoint_json TEXT NOT NULL CHECK(json_valid(checkpoint_json)));
CREATE TABLE character_evildom_war_hidden_rewards(
  character_id INTEGER NOT NULL,account TEXT NOT NULL,battle_id TEXT NOT NULL CHECK(length(battle_id)=32),
  reward_json TEXT NOT NULL CHECK(json_valid(reward_json)),mail_ids TEXT NOT NULL CHECK(json_valid(mail_ids)),
  PRIMARY KEY(character_id,battle_id),
  FOREIGN KEY(character_id,account) REFERENCES characters(id,account));

CREATE TABLE account_fiendwar_guides(
  account TEXT NOT NULL,slot INTEGER NOT NULL CHECK(slot BETWEEN 0 AND 1),
  week INTEGER NOT NULL CHECK(week>0),entry_epoch INTEGER NOT NULL DEFAULT 0 CHECK(typeof(entry_epoch)='integer' AND entry_epoch>=0),
  character_id INTEGER NOT NULL CHECK(character_id BETWEEN 1 AND 65535),
  PRIMARY KEY(account,week,entry_epoch,slot),UNIQUE(account,week,entry_epoch,character_id),
  FOREIGN KEY(account) REFERENCES accounts(account_id));

CREATE TABLE account_raid_quota_epochs(
  account TEXT NOT NULL,raid_type INTEGER NOT NULL CHECK(raid_type BETWEEN 0 AND 3),
  entry_epoch INTEGER NOT NULL DEFAULT 0 CHECK(typeof(entry_epoch)='integer' AND entry_epoch>=0),
  reward_epoch INTEGER NOT NULL DEFAULT 0 CHECK(typeof(reward_epoch)='integer' AND reward_epoch>=0),
  PRIMARY KEY(account,raid_type),FOREIGN KEY(account) REFERENCES accounts(account_id));

CREATE TABLE account_evildom_war_guides(
  account TEXT NOT NULL,slot INTEGER NOT NULL CHECK(slot BETWEEN 0 AND 1),
  week INTEGER NOT NULL CHECK(week>0),character_id INTEGER NOT NULL CHECK(character_id BETWEEN 1 AND 65535),
  PRIMARY KEY(account,slot),UNIQUE(account,character_id),
  FOREIGN KEY(account) REFERENCES accounts(account_id));
CREATE TABLE character_evildom_war_seats(
  character_id INTEGER NOT NULL,account TEXT NOT NULL,battle_id TEXT NOT NULL,binding_token TEXT NOT NULL CHECK(length(binding_token)=32),
  state INTEGER NOT NULL DEFAULT 0 CHECK(state BETWEEN 0 AND 3),reconnect_until INTEGER CHECK(reconnect_until>=0),
  payment_json TEXT NOT NULL CHECK(json_valid(payment_json)),latest_instance TEXT NOT NULL CHECK(length(latest_instance)=32),
  PRIMARY KEY(character_id,battle_id),
  FOREIGN KEY(character_id,account) REFERENCES characters(id,account),FOREIGN KEY(battle_id) REFERENCES evildom_war_battles(battle_id));

CREATE TABLE account_pvp_total_ratings(
    account TEXT NOT NULL PRIMARY KEY,
    team_id TEXT NOT NULL UNIQUE REFERENCES account_pvp_total_teams(team_id),
    revision INTEGER NOT NULL CHECK(typeof(revision)='integer' AND revision>0),
    state_json TEXT NOT NULL CHECK(json_valid(state_json) AND json_type(state_json)='object')
);
CREATE TABLE account_pvp_total_rating_claims(
    sequence INTEGER PRIMARY KEY AUTOINCREMENT,
    match_id TEXT NOT NULL REFERENCES pvp_arena_match_results(match_id),
    account TEXT NOT NULL,
    team_id TEXT NOT NULL REFERENCES account_pvp_total_teams(team_id),
    outcome INTEGER NOT NULL CHECK(typeof(outcome)='integer' AND outcome BETWEEN 0 AND 3),
    receipt_json TEXT CHECK(receipt_json IS NULL OR json_valid(receipt_json) AND json_type(receipt_json)='object'),
    UNIQUE(match_id,account)
);
CREATE INDEX ix_pvp_total_rating_pending ON account_pvp_total_rating_claims(account,sequence) WHERE receipt_json IS NULL;

CREATE TABLE account_tag_evildom_challenges(
  account TEXT NOT NULL,
  day INTEGER NOT NULL CHECK(day>0),
  week INTEGER NOT NULL CHECK(week>0),
  team_index INTEGER NOT NULL CHECK(team_index BETWEEN 0 AND 2),
  maze_ordinal INTEGER NOT NULL CHECK(maze_ordinal BETWEEN 0 AND 4),
  dungeon_id INTEGER NOT NULL CHECK(dungeon_id IN(9608,400000095)),
  instance_id TEXT NOT NULL CHECK(length(instance_id)=32),
  first INTEGER NOT NULL CHECK(first BETWEEN 1 AND 65535),
  second INTEGER NOT NULL CHECK(second BETWEEN 1 AND 65535 AND second<>first),
  admitted_at INTEGER NOT NULL CHECK(admitted_at>=0),
  sequence INTEGER NOT NULL CHECK(sequence>0),
  state INTEGER NOT NULL CHECK(state BETWEEN 0 AND 2),
  PRIMARY KEY(account,day,team_index)
);

CREATE TABLE imperial_arena_daily_routes(
  day INTEGER NOT NULL PRIMARY KEY CHECK(day>=20000101),
  route_json TEXT NOT NULL CHECK(json_valid(route_json) AND json_type(route_json)='object')
);

CREATE TABLE account_imperial_arena_configuration(
  account TEXT NOT NULL PRIMARY KEY,
  revision INTEGER NOT NULL CHECK(typeof(revision)='integer' AND revision>0),
  first INTEGER NOT NULL CHECK(first BETWEEN 0 AND 65535),
  second INTEGER NOT NULL CHECK(second BETWEEN 0 AND 65535),
  CHECK((first=0 AND second=0) OR (first>0 AND second>0 AND first<>second))
);

CREATE TABLE account_imperial_arena_days(
  account TEXT NOT NULL,day INTEGER NOT NULL CHECK(day>=20000101),
  mode INTEGER NOT NULL CHECK(mode IN(1,2)),
  hard_round INTEGER NOT NULL DEFAULT 0 CHECK(hard_round BETWEEN 0 AND 7),
  PRIMARY KEY(account,day)
);

CREATE TABLE account_imperial_arena_runs(
  account TEXT NOT NULL,instance_id TEXT NOT NULL CHECK(length(instance_id)=32),
  day INTEGER NOT NULL,week INTEGER NOT NULL,mode INTEGER NOT NULL CHECK(mode IN(1,2)),
  first INTEGER NOT NULL CHECK(first BETWEEN 1 AND 65535),
  second INTEGER NOT NULL CHECK(second BETWEEN 1 AND 65535 AND second<>first),
  configuration_revision INTEGER NOT NULL CHECK(configuration_revision>0),
  task_attempt INTEGER NOT NULL CHECK(task_attempt>0),
  admitted_at INTEGER NOT NULL CHECK(admitted_at>=0),
  minimum_clear_ms INTEGER NOT NULL CHECK(minimum_clear_ms>0),
  team_json TEXT NOT NULL CHECK(json_valid(team_json) AND json_array_length(team_json)=4),
  route_json TEXT NOT NULL CHECK(json_valid(route_json)),
  cleared_round INTEGER NOT NULL DEFAULT 0 CHECK(cleared_round BETWEEN 0 AND 7),
  events_json TEXT NOT NULL DEFAULT '[]' CHECK(json_valid(events_json)),
  elapsed_ms INTEGER CHECK(elapsed_ms IS NULL OR elapsed_ms>=0),
  closed INTEGER NOT NULL DEFAULT 0 CHECK(closed IN(0,1)),
  ranked_week INTEGER,
  PRIMARY KEY(account,instance_id),UNIQUE(first,task_attempt),
  FOREIGN KEY(account,day) REFERENCES account_imperial_arena_days(account,day)
);

CREATE TABLE account_imperial_arena_challenge_claims(
  account TEXT NOT NULL,week INTEGER NOT NULL CHECK(week>=20000101),
  instance_id TEXT NOT NULL CHECK(length(instance_id)=32),
  mail_id INTEGER NOT NULL CHECK(mail_id>0),
  reward_json TEXT NOT NULL CHECK(json_valid(reward_json)),
  PRIMARY KEY(account,week),
  FOREIGN KEY(account,instance_id) REFERENCES account_imperial_arena_runs(account,instance_id)
);

CREATE TABLE account_imperial_arena_rewards(
  account TEXT NOT NULL,day INTEGER NOT NULL,monster INTEGER NOT NULL CHECK(monster>0),
  instance_id TEXT NOT NULL,event_id TEXT NOT NULL CHECK(length(event_id)=32),
  reward_json TEXT NOT NULL CHECK(json_valid(reward_json)),
  mail_id INTEGER NOT NULL CHECK(mail_id>=0),
  PRIMARY KEY(account,day,monster),
  FOREIGN KEY(account,instance_id) REFERENCES account_imperial_arena_runs(account,instance_id)
);

CREATE TABLE guild_ranking_party_clears(
  guild_id INTEGER NOT NULL REFERENCES guilds(guild_id) ON DELETE CASCADE,
  instance_id TEXT NOT NULL CHECK(length(instance_id)=32),dungeon_id INTEGER NOT NULL CHECK(dungeon_id>0),
  members INTEGER NOT NULL CHECK(members BETWEEN 2 AND 4),created_at INTEGER NOT NULL CHECK(created_at>0),
  PRIMARY KEY(guild_id,instance_id)
);
CREATE TABLE guild_ranking_months(
  month INTEGER PRIMARY KEY CHECK(month BETWEEN 190001 AND 999912),
  observed_at INTEGER NOT NULL CHECK(observed_at>0),rule_version TEXT NOT NULL,
  entries TEXT NOT NULL CHECK(json_valid(entries) AND json_type(entries)='array')
);
CREATE INDEX guild_ranking_party_period ON guild_ranking_party_clears(created_at,guild_id);

CREATE TABLE guild_dungeon_reward_receipts(
  character_id INTEGER NOT NULL CHECK(character_id BETWEEN 1 AND 65535),account TEXT NOT NULL,
  guild_id INTEGER NOT NULL CHECK(guild_id>0),instance_id TEXT NOT NULL CHECK(length(instance_id)=32),
  event_id TEXT NOT NULL CHECK(length(event_id)=32),dungeon_id INTEGER NOT NULL CHECK(dungeon_id>0),
  reward_json TEXT NOT NULL CHECK(json_valid(reward_json) AND json_type(reward_json)='object'),
  mail_id INTEGER NOT NULL CHECK(mail_id>0),PRIMARY KEY(character_id,instance_id)
);
CREATE INDEX guild_dungeon_reward_instance ON guild_dungeon_reward_receipts(guild_id,instance_id);

CREATE TABLE guild_dungeon_effective_progress(
  guild_id INTEGER NOT NULL,
  dungeon_id INTEGER NOT NULL,
  effective_clear_units INTEGER NOT NULL CHECK(effective_clear_units BETWEEN 0 AND 4294967295),
  decay_anchor_unix INTEGER NOT NULL CHECK(decay_anchor_unix>0),
  PRIMARY KEY(guild_id,dungeon_id),
  FOREIGN KEY(guild_id,dungeon_id) REFERENCES guild_dungeon_progress(guild_id,dungeon_id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS guild_npc_common_benefits(
  guild_id INTEGER NOT NULL REFERENCES guilds(guild_id) ON DELETE CASCADE,
  effect TEXT NOT NULL,benefit_id INTEGER NOT NULL CHECK(benefit_id>0),
  remaining INTEGER NOT NULL CHECK(remaining BETWEEN 0 AND 32767),
  deadline INTEGER NOT NULL CHECK(deadline BETWEEN 1 AND 2147483647),
  PRIMARY KEY(guild_id,effect)
);
CREATE TABLE IF NOT EXISTS character_guild_npc_benefits(
  character_id INTEGER NOT NULL REFERENCES characters(id) ON DELETE CASCADE,
  guild_id INTEGER NOT NULL REFERENCES guilds(guild_id) ON DELETE CASCADE,
  joined_at INTEGER NOT NULL CHECK(joined_at>=0),
  effect TEXT NOT NULL,benefit_id INTEGER NOT NULL CHECK(benefit_id>0),
  remaining INTEGER NOT NULL CHECK(remaining BETWEEN 0 AND 32767),
  deadline INTEGER NOT NULL CHECK(deadline BETWEEN 0 AND 2147483647),
  selected INTEGER NOT NULL DEFAULT 0 CHECK(selected IN(0,1)),
  PRIMARY KEY(character_id,guild_id,joined_at,benefit_id)
);
CREATE TABLE IF NOT EXISTS guild_npc_training_receipts(
  request_id TEXT PRIMARY KEY REFERENCES guild_npc_receipts(request_id),
  selection TEXT NOT NULL CHECK(json_valid(selection) AND json_type(selection)='array')
);
CREATE TABLE character_guild_npc_item_crafts(
  craft_id TEXT PRIMARY KEY CHECK(length(craft_id)=64),
  character_id INTEGER NOT NULL REFERENCES characters(id) ON DELETE CASCADE,
  guild_id INTEGER NOT NULL,joined_at INTEGER NOT NULL CHECK(joined_at>=0),
  npc_id INTEGER NOT NULL CHECK(npc_id IN(400001065,400001061)),
  benefit_id INTEGER NOT NULL CHECK(benefit_id>0),
  deadline INTEGER NOT NULL CHECK(deadline BETWEEN 1 AND 2147483647),
  item_core TEXT NOT NULL CHECK(json_valid(item_core)),
  state INTEGER NOT NULL DEFAULT 0 CHECK(state IN(0,1,2)),
  mail_id INTEGER NOT NULL DEFAULT 0 CHECK(mail_id>=0),
  CHECK((state=1 AND mail_id>0) OR (state IN(0,2) AND mail_id=0))
);
CREATE UNIQUE INDEX guild_npc_item_craft_active ON character_guild_npc_item_crafts(character_id,npc_id) WHERE state=0;
CREATE INDEX guild_npc_item_craft_due ON character_guild_npc_item_crafts(deadline) WHERE state=0;
CREATE TABLE character_guild_npc_craft_weeks(
  character_id INTEGER NOT NULL REFERENCES characters(id) ON DELETE CASCADE,
  npc_id INTEGER NOT NULL CHECK(npc_id=400001061),
  week_start INTEGER NOT NULL CHECK(week_start BETWEEN 1 AND 2147483647),
  craft_id TEXT NOT NULL CHECK(length(craft_id)=64),
  PRIMARY KEY(character_id,npc_id,week_start)
);

CREATE TABLE character_beast_dungeon_reservations(
            character_id INTEGER NOT NULL,
            account TEXT NOT NULL,
            instance_id TEXT NOT NULL CHECK(length(instance_id)=32),
            attempt INTEGER NOT NULL CHECK(attempt>0),
            entry_epoch INTEGER NOT NULL CHECK(entry_epoch>0),
            day INTEGER NOT NULL CHECK(day>0),
            dungeon_id INTEGER NOT NULL CHECK(dungeon_id BETWEEN 310 AND 314),
            expected_used INTEGER NOT NULL CHECK(expected_used BETWEEN 0 AND 1),
            state INTEGER NOT NULL CHECK(state BETWEEN 0 AND 2),
            PRIMARY KEY(character_id,instance_id),
            FOREIGN KEY(character_id,account) REFERENCES characters(id,account) ON DELETE CASCADE
        );
        CREATE UNIQUE INDEX beast_pending_character ON character_beast_dungeon_reservations(character_id) WHERE state=0;

CREATE TABLE character_beast_stage_rewards(
    character_id INTEGER NOT NULL,
    account TEXT NOT NULL,
    instance_id TEXT NOT NULL CHECK(length(instance_id)=32),
    stage INTEGER NOT NULL CHECK(stage BETWEEN 0 AND 5),
    event_id TEXT NOT NULL CHECK(length(event_id)=32),
    map_id INTEGER NOT NULL,
    monster_key INTEGER NOT NULL CHECK(monster_key BETWEEN 1 AND 65535),
    item_id INTEGER NOT NULL,
    PRIMARY KEY(character_id,instance_id,stage),
    UNIQUE(character_id,event_id),
    FOREIGN KEY(character_id,account) REFERENCES characters(id,account) ON DELETE CASCADE
);

        CREATE TABLE character_pvp_mission_facts(
            character_id INTEGER NOT NULL,account TEXT NOT NULL,
            kind INTEGER NOT NULL CHECK(typeof(kind)='integer' AND kind IN(0,1)),
            job INTEGER NOT NULL CHECK(typeof(job)='integer' AND job BETWEEN 0 AND 15),
            parameter INTEGER NOT NULL CHECK(typeof(parameter)='integer' AND
                (kind=0 AND parameter BETWEEN 0 AND 11 OR kind=1 AND parameter BETWEEN 0 AND 2)),
            instance_id TEXT NOT NULL CHECK(length(instance_id)=32 AND instance_id NOT GLOB '*[^0-9a-f]*' AND instance_id<>'00000000000000000000000000000000'),
            PRIMARY KEY(character_id,kind,job,parameter),
            FOREIGN KEY(character_id,account) REFERENCES characters(id,account)
        );
        CREATE TABLE character_pvp_mission_rewards(
            character_id INTEGER NOT NULL,account TEXT NOT NULL,
            mission_id INTEGER NOT NULL CHECK(typeof(mission_id)='integer' AND mission_id BETWEEN 1 AND 32767),
            rewards_json TEXT NOT NULL CHECK(json_valid(rewards_json) AND json_type(rewards_json)='array'),
            PRIMARY KEY(character_id,mission_id),
            FOREIGN KEY(character_id,account) REFERENCES characters(id,account)
        );

        CREATE TABLE character_pvp_daily_missions(
            character_id INTEGER NOT NULL,account TEXT NOT NULL,
            service_day INTEGER NOT NULL CHECK(typeof(service_day)='integer' AND service_day BETWEEN 1 AND 3652058),
            state_json TEXT NOT NULL CHECK(json_valid(state_json) AND json_type(state_json)='object'),
            PRIMARY KEY(character_id,service_day),
            FOREIGN KEY(character_id,account) REFERENCES characters(id,account)
        );

CREATE TABLE IF NOT EXISTS character_rename_pending(
  character_id INTEGER NOT NULL,account TEXT NOT NULL,source_uid INTEGER NOT NULL CHECK(source_uid>=0),
  PRIMARY KEY(character_id),FOREIGN KEY(character_id,account) REFERENCES characters(id,account));

CREATE TABLE IF NOT EXISTS character_name_history(
  character_id INTEGER NOT NULL,account TEXT NOT NULL,revision INTEGER NOT NULL CHECK(revision>=0),
  former_name TEXT NOT NULL,changed_at INTEGER NOT NULL CHECK(changed_at BETWEEN 0 AND 4294967295),
  PRIMARY KEY(character_id,revision),FOREIGN KEY(character_id,account) REFERENCES characters(id,account));

CREATE TABLE IF NOT EXISTS character_ekern_cycle(
  character_id INTEGER NOT NULL,account TEXT NOT NULL,service_day INTEGER NOT NULL CHECK(service_day>=0),
  normal_clears INTEGER NOT NULL CHECK(normal_clears BETWEEN 0 AND 3),
  trial_clears INTEGER NOT NULL CHECK(trial_clears BETWEEN 0 AND 1),
  PRIMARY KEY(character_id),FOREIGN KEY(character_id,account) REFERENCES characters(id,account));

CREATE TABLE IF NOT EXISTS cash_shop_promotion_rules(
    campaign TEXT NOT NULL,
    period TEXT NOT NULL,
    realm TEXT NOT NULL,
    promotion_group TEXT NOT NULL,
    rules_version TEXT NOT NULL,
    rules_snapshot TEXT NOT NULL CHECK(json_valid(rules_snapshot)),
    PRIMARY KEY(campaign,period,realm,promotion_group)
) STRICT;
CREATE TABLE IF NOT EXISTS cash_shop_promotion_orders(
    order_id TEXT NOT NULL PRIMARY KEY CHECK(length(order_id)=32),
    account TEXT NOT NULL,
    recipient_id INTEGER NOT NULL CHECK(recipient_id BETWEEN 1 AND 65535),
    campaign TEXT NOT NULL,
    period TEXT NOT NULL,
    realm TEXT NOT NULL,
    promotion_group TEXT NOT NULL,
    rules_version TEXT NOT NULL,
    business_snapshot TEXT NOT NULL CHECK(json_valid(business_snapshot)),
    count INTEGER NOT NULL CHECK(count>=0),
    revision INTEGER NOT NULL CHECK(revision>0),
    mail_ids TEXT NOT NULL CHECK(json_valid(mail_ids)),
    committed_at INTEGER NOT NULL CHECK(committed_at>=0),
    FOREIGN KEY(campaign,period,realm,promotion_group) REFERENCES cash_shop_promotion_rules(campaign,period,realm,promotion_group)
) STRICT;
CREATE TABLE IF NOT EXISTS cash_shop_promotion_progress(
    account TEXT NOT NULL,
    campaign TEXT NOT NULL,
    period TEXT NOT NULL,
    realm TEXT NOT NULL,
    promotion_group TEXT NOT NULL,
    count INTEGER NOT NULL CHECK(count>=0),
    revision INTEGER NOT NULL CHECK(revision>=0),
    PRIMARY KEY(account,campaign,period,realm,promotion_group),
    FOREIGN KEY(campaign,period,realm,promotion_group) REFERENCES cash_shop_promotion_rules(campaign,period,realm,promotion_group)
) STRICT;
CREATE TABLE IF NOT EXISTS cash_shop_promotion_awards(
    account TEXT NOT NULL,
    campaign TEXT NOT NULL,
    period TEXT NOT NULL,
    realm TEXT NOT NULL,
    promotion_group TEXT NOT NULL,
    rules_version TEXT NOT NULL,
    global_purchase INTEGER NOT NULL CHECK(global_purchase>0),
    order_id TEXT NOT NULL REFERENCES cash_shop_promotion_orders(order_id),
    reward_snapshot TEXT NOT NULL CHECK(json_valid(reward_snapshot)),
    mail_id INTEGER NOT NULL CHECK(mail_id BETWEEN 1 AND 1073741823),
    PRIMARY KEY(account,campaign,period,realm,promotion_group,rules_version,global_purchase)
) STRICT;
CREATE TRIGGER IF NOT EXISTS cash_shop_promotion_rules_immutable_update BEFORE UPDATE ON cash_shop_promotion_rules
BEGIN SELECT RAISE(ABORT,'promotion rules are immutable'); END;
CREATE TRIGGER IF NOT EXISTS cash_shop_promotion_rules_immutable_delete BEFORE DELETE ON cash_shop_promotion_rules
BEGIN SELECT RAISE(ABORT,'promotion rules are immutable'); END;

-- 常驻累计规则只扩展冻结规则表；不搬迁订单、资格或账号累计。
ALTER TABLE cash_shop_promotion_rules ADD COLUMN rule_kind INTEGER NOT NULL DEFAULT 0 CHECK(rule_kind IN(0,1));
ALTER TABLE cash_shop_promotion_rules ADD COLUMN carry_campaign TEXT;
ALTER TABLE cash_shop_promotion_rules ADD COLUMN carry_period TEXT;
ALTER TABLE cash_shop_promotion_rules ADD COLUMN carry_rules_version TEXT;
ALTER TABLE cash_shop_promotion_rules ADD COLUMN carry_realm TEXT;
ALTER TABLE cash_shop_promotion_rules ADD COLUMN carry_group TEXT;
ALTER TABLE cash_shop_promotion_rules ADD COLUMN carry_rules_snapshot TEXT CHECK((rule_kind=0 AND carry_campaign IS NULL AND carry_period IS NULL AND carry_rules_version IS NULL AND carry_realm IS NULL AND carry_group IS NULL AND carry_rules_snapshot IS NULL) OR (rule_kind=1 AND carry_campaign IS NOT NULL AND carry_period IS NOT NULL AND carry_rules_version IS NOT NULL AND carry_realm IS NOT NULL AND carry_group IS NOT NULL AND carry_rules_snapshot IS NOT NULL AND json_valid(carry_rules_snapshot) AND carry_campaign=campaign AND carry_realm=realm AND carry_group=promotion_group AND carry_period<>period));
CREATE UNIQUE INDEX IF NOT EXISTS cash_shop_promotion_single_successor
    ON cash_shop_promotion_rules(carry_campaign,carry_period,carry_rules_version,carry_realm,carry_group) WHERE rule_kind=1;
CREATE TRIGGER IF NOT EXISTS cash_shop_promotion_valid_predecessor BEFORE INSERT ON cash_shop_promotion_rules
    WHEN NEW.rule_kind=1
    BEGIN
        SELECT CASE WHEN NOT EXISTS(SELECT 1 FROM cash_shop_promotion_rules
            WHERE campaign=NEW.carry_campaign AND period=NEW.carry_period AND rules_version=NEW.carry_rules_version
              AND realm=NEW.carry_realm AND promotion_group=NEW.carry_group AND rules_snapshot=NEW.carry_rules_snapshot AND rule_kind=0)
            THEN RAISE(ABORT,'promotion predecessor must be frozen and exact') END;
    END;

-- 漩涡原票与断线资格仅支持系统中断退票，不重建战斗。

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

CREATE TABLE character_fiendwar_title_facts(
  character_id INTEGER NOT NULL, account TEXT NOT NULL,
  quest_id INTEGER NOT NULL CHECK(quest_id BETWEEN 7003 AND 7008),
  total INTEGER NOT NULL CHECK(total BETWEEN 1 AND 65535),
  applied INTEGER NOT NULL DEFAULT 0 CHECK(applied>=0 AND applied<=total),
  PRIMARY KEY(character_id,quest_id),
  FOREIGN KEY(character_id,account) REFERENCES characters(id,account)
);
CREATE TABLE character_fiendwar_counter_stream(
  character_id INTEGER PRIMARY KEY, account TEXT NOT NULL,
  stream TEXT NOT NULL CHECK(length(stream) BETWEEN 1 AND 256),
  sequence INTEGER NOT NULL CHECK(sequence BETWEEN 1 AND 4294967295),
  FOREIGN KEY(character_id,account) REFERENCES characters(id,account)
);

CREATE INDEX character_raid_rewards_account_time ON character_raid_reward_receipts(account,created_at);

ALTER TABLE characters ADD COLUMN inventory_items_revision INTEGER NOT NULL DEFAULT 0 CHECK(inventory_items_revision>=0);
ALTER TABLE account_cargo ADD COLUMN inventory_items_revision INTEGER NOT NULL DEFAULT 0 CHECK(inventory_items_revision>=0);
CREATE TRIGGER inventory_version_character_inventory_items_insert AFTER INSERT ON character_inventory_items
BEGIN
    UPDATE characters SET inventory_items_revision=inventory_items_revision+1 WHERE (id=NEW.character_id AND account=NEW.account);
    UPDATE account_cargo SET inventory_items_revision=inventory_items_revision+1 WHERE (NEW.character_id IS NULL AND account=NEW.account);
END;
CREATE TRIGGER inventory_version_character_inventory_items_update AFTER UPDATE ON character_inventory_items
BEGIN
    UPDATE characters SET inventory_items_revision=inventory_items_revision+1 WHERE (id=OLD.character_id AND account=OLD.account) OR (id=NEW.character_id AND account=NEW.account);
    UPDATE account_cargo SET inventory_items_revision=inventory_items_revision+1 WHERE (OLD.character_id IS NULL AND account=OLD.account) OR (NEW.character_id IS NULL AND account=NEW.account);
END;
CREATE TRIGGER inventory_version_character_inventory_items_delete AFTER DELETE ON character_inventory_items
BEGIN
    UPDATE characters SET inventory_items_revision=inventory_items_revision+1 WHERE (id=OLD.character_id AND account=OLD.account);
    UPDATE account_cargo SET inventory_items_revision=inventory_items_revision+1 WHERE (OLD.character_id IS NULL AND account=OLD.account);
END;
CREATE TRIGGER inventory_version_character_inventory_slots_insert AFTER INSERT ON character_inventory_slots
BEGIN
    UPDATE characters SET inventory_items_revision=inventory_items_revision+1 WHERE (id=NEW.character_id AND account=NEW.account);
    UPDATE account_cargo SET inventory_items_revision=inventory_items_revision+1 WHERE (NEW.character_id IS NULL AND account=NEW.account);
END;
CREATE TRIGGER inventory_version_character_inventory_slots_update AFTER UPDATE ON character_inventory_slots
BEGIN
    UPDATE characters SET inventory_items_revision=inventory_items_revision+1 WHERE (id=OLD.character_id AND account=OLD.account) OR (id=NEW.character_id AND account=NEW.account);
    UPDATE account_cargo SET inventory_items_revision=inventory_items_revision+1 WHERE (OLD.character_id IS NULL AND account=OLD.account) OR (NEW.character_id IS NULL AND account=NEW.account);
END;
CREATE TRIGGER inventory_version_character_inventory_slots_delete AFTER DELETE ON character_inventory_slots
BEGIN
    UPDATE characters SET inventory_items_revision=inventory_items_revision+1 WHERE (id=OLD.character_id AND account=OLD.account);
    UPDATE account_cargo SET inventory_items_revision=inventory_items_revision+1 WHERE (OLD.character_id IS NULL AND account=OLD.account);
END;
CREATE TRIGGER inventory_version_character_title_book_slots_insert AFTER INSERT ON character_title_book_slots
BEGIN
    UPDATE characters SET inventory_items_revision=inventory_items_revision+1 WHERE (id=NEW.character_id AND account=NEW.account);
    UPDATE account_cargo SET inventory_items_revision=inventory_items_revision+1 WHERE (NEW.character_id IS NULL AND account=NEW.account);
END;
CREATE TRIGGER inventory_version_character_title_book_slots_update AFTER UPDATE ON character_title_book_slots
BEGIN
    UPDATE characters SET inventory_items_revision=inventory_items_revision+1 WHERE (id=OLD.character_id AND account=OLD.account) OR (id=NEW.character_id AND account=NEW.account);
    UPDATE account_cargo SET inventory_items_revision=inventory_items_revision+1 WHERE (OLD.character_id IS NULL AND account=OLD.account) OR (NEW.character_id IS NULL AND account=NEW.account);
END;
CREATE TRIGGER inventory_version_character_title_book_slots_delete AFTER DELETE ON character_title_book_slots
BEGIN
    UPDATE characters SET inventory_items_revision=inventory_items_revision+1 WHERE (id=OLD.character_id AND account=OLD.account);
    UPDATE account_cargo SET inventory_items_revision=inventory_items_revision+1 WHERE (OLD.character_id IS NULL AND account=OLD.account);
END;
CREATE TRIGGER inventory_version_inventory_avatar_details_insert AFTER INSERT ON inventory_avatar_details
BEGIN
    UPDATE characters SET inventory_items_revision=inventory_items_revision+1 WHERE (id,account) IN(SELECT character_id,account FROM character_inventory_items WHERE item_uid IN(NEW.item_uid));
    UPDATE account_cargo SET inventory_items_revision=inventory_items_revision+1 WHERE account IN(SELECT account FROM character_inventory_items WHERE character_id IS NULL AND item_uid IN(NEW.item_uid));
END;
CREATE TRIGGER inventory_version_inventory_avatar_details_update AFTER UPDATE ON inventory_avatar_details
BEGIN
    UPDATE characters SET inventory_items_revision=inventory_items_revision+1 WHERE (id,account) IN(SELECT character_id,account FROM character_inventory_items WHERE item_uid IN(OLD.item_uid,NEW.item_uid));
    UPDATE account_cargo SET inventory_items_revision=inventory_items_revision+1 WHERE account IN(SELECT account FROM character_inventory_items WHERE character_id IS NULL AND item_uid IN(OLD.item_uid,NEW.item_uid));
END;
CREATE TRIGGER inventory_version_inventory_avatar_details_delete AFTER DELETE ON inventory_avatar_details
BEGIN
    UPDATE characters SET inventory_items_revision=inventory_items_revision+1 WHERE (id,account) IN(SELECT character_id,account FROM character_inventory_items WHERE item_uid IN(OLD.item_uid));
    UPDATE account_cargo SET inventory_items_revision=inventory_items_revision+1 WHERE account IN(SELECT account FROM character_inventory_items WHERE character_id IS NULL AND item_uid IN(OLD.item_uid));
END;
CREATE TRIGGER inventory_version_inventory_creature_details_insert AFTER INSERT ON inventory_creature_details
BEGIN
    UPDATE characters SET inventory_items_revision=inventory_items_revision+1 WHERE (id,account) IN(SELECT character_id,account FROM character_inventory_items WHERE item_uid IN(NEW.item_uid));
    UPDATE account_cargo SET inventory_items_revision=inventory_items_revision+1 WHERE account IN(SELECT account FROM character_inventory_items WHERE character_id IS NULL AND item_uid IN(NEW.item_uid));
END;
CREATE TRIGGER inventory_version_inventory_creature_details_update AFTER UPDATE ON inventory_creature_details
BEGIN
    UPDATE characters SET inventory_items_revision=inventory_items_revision+1 WHERE (id,account) IN(SELECT character_id,account FROM character_inventory_items WHERE item_uid IN(OLD.item_uid,NEW.item_uid));
    UPDATE account_cargo SET inventory_items_revision=inventory_items_revision+1 WHERE account IN(SELECT account FROM character_inventory_items WHERE character_id IS NULL AND item_uid IN(OLD.item_uid,NEW.item_uid));
END;
CREATE TRIGGER inventory_version_inventory_creature_details_delete AFTER DELETE ON inventory_creature_details
BEGIN
    UPDATE characters SET inventory_items_revision=inventory_items_revision+1 WHERE (id,account) IN(SELECT character_id,account FROM character_inventory_items WHERE item_uid IN(OLD.item_uid));
    UPDATE account_cargo SET inventory_items_revision=inventory_items_revision+1 WHERE account IN(SELECT account FROM character_inventory_items WHERE character_id IS NULL AND item_uid IN(OLD.item_uid));
END;
CREATE TRIGGER inventory_version_mailbox_attachments_insert AFTER INSERT ON mailbox_attachments
BEGIN
    UPDATE characters SET inventory_items_revision=inventory_items_revision+1 WHERE (id,account) IN(SELECT character_id,account FROM character_inventory_items WHERE item_uid IN(NEW.item_uid));
    UPDATE account_cargo SET inventory_items_revision=inventory_items_revision+1 WHERE account IN(SELECT account FROM character_inventory_items WHERE character_id IS NULL AND item_uid IN(NEW.item_uid));
END;
CREATE TRIGGER inventory_version_mailbox_attachments_update AFTER UPDATE ON mailbox_attachments
BEGIN
    UPDATE characters SET inventory_items_revision=inventory_items_revision+1 WHERE (id,account) IN(SELECT character_id,account FROM character_inventory_items WHERE item_uid IN(OLD.item_uid,NEW.item_uid));
    UPDATE account_cargo SET inventory_items_revision=inventory_items_revision+1 WHERE account IN(SELECT account FROM character_inventory_items WHERE character_id IS NULL AND item_uid IN(OLD.item_uid,NEW.item_uid));
END;
CREATE TRIGGER inventory_version_mailbox_attachments_delete AFTER DELETE ON mailbox_attachments
BEGIN
    UPDATE characters SET inventory_items_revision=inventory_items_revision+1 WHERE (id,account) IN(SELECT character_id,account FROM character_inventory_items WHERE item_uid IN(OLD.item_uid));
    UPDATE account_cargo SET inventory_items_revision=inventory_items_revision+1 WHERE account IN(SELECT account FROM character_inventory_items WHERE character_id IS NULL AND item_uid IN(OLD.item_uid));
END;

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

CREATE TABLE account_battle_pass_seasons(
  account TEXT NOT NULL,
  season_key TEXT NOT NULL CHECK(length(trim(season_key))>0),
  total_experience INTEGER NOT NULL DEFAULT 0 CHECK(typeof(total_experience)='integer' AND total_experience>=0),
  pass_type INTEGER NOT NULL DEFAULT 0 CHECK(typeof(pass_type)='integer' AND pass_type BETWEEN 0 AND 255),
  revision INTEGER NOT NULL DEFAULT 0 CHECK(typeof(revision)='integer' AND revision>=0),
  PRIMARY KEY(account,season_key)
);

CREATE TABLE account_battle_pass_claims(
  account TEXT NOT NULL,
  season_key TEXT NOT NULL,
  level INTEGER NOT NULL CHECK(typeof(level)='integer' AND level BETWEEN 1 AND 80),
  track INTEGER NOT NULL CHECK(typeof(track)='integer' AND track BETWEEN 0 AND 1),
  recipient_character_id INTEGER NOT NULL,
  PRIMARY KEY(account,season_key,level,track),
  FOREIGN KEY(account,season_key) REFERENCES account_battle_pass_seasons(account,season_key)
);

CREATE TABLE battle_pass_season_rules(
  season_key TEXT PRIMARY KEY NOT NULL CHECK(length(trim(season_key))>0),
  start_utc INTEGER NOT NULL CHECK(typeof(start_utc)='integer' AND start_utc>=0),
  end_utc INTEGER NOT NULL CHECK(typeof(end_utc)='integer' AND end_utc>start_utc),
  rules_json TEXT NOT NULL
);

CREATE TABLE account_battle_pass_mission_progress(
  account TEXT NOT NULL, season_key TEXT NOT NULL,
  category INTEGER NOT NULL CHECK(typeof(category)='integer' AND category BETWEEN 0 AND 2),
  period_key INTEGER NOT NULL CHECK(typeof(period_key)='integer' AND period_key>=0),
  mission_id INTEGER NOT NULL CHECK(typeof(mission_id)='integer' AND mission_id BETWEEN 1 AND 65535),
  current_count INTEGER NOT NULL CHECK(typeof(current_count)='integer' AND current_count>=0),
  PRIMARY KEY(account,season_key,category,period_key,mission_id),
  FOREIGN KEY(account,season_key) REFERENCES account_battle_pass_seasons(account,season_key)
);

CREATE TABLE account_battle_pass_mission_claims(
  account TEXT NOT NULL, season_key TEXT NOT NULL,
  category INTEGER NOT NULL CHECK(typeof(category)='integer' AND category BETWEEN 0 AND 2),
  period_key INTEGER NOT NULL CHECK(typeof(period_key)='integer' AND period_key>=0),
  mission_id INTEGER NOT NULL CHECK(typeof(mission_id)='integer' AND mission_id BETWEEN 1 AND 65535),
  experience INTEGER NOT NULL CHECK(typeof(experience)='integer' AND experience>0),
  claimed_at INTEGER NOT NULL CHECK(typeof(claimed_at)='integer' AND claimed_at>=0),
  resulting_season_revision INTEGER NOT NULL CHECK(typeof(resulting_season_revision)='integer' AND resulting_season_revision>0),
  PRIMARY KEY(account,season_key,category,period_key,mission_id),
  FOREIGN KEY(account,season_key) REFERENCES account_battle_pass_seasons(account,season_key)
);

CREATE TABLE account_battle_pass_source_events(
  account TEXT NOT NULL, source_kind TEXT NOT NULL, source_id TEXT NOT NULL CHECK(length(source_id)=32),
  season_key TEXT NOT NULL, occurred_at INTEGER NOT NULL CHECK(typeof(occurred_at)='integer' AND occurred_at>=0),
  PRIMARY KEY(account,source_kind,source_id),
  FOREIGN KEY(account,season_key) REFERENCES account_battle_pass_seasons(account,season_key)
);

CREATE TABLE account_battle_pass_certificate_orders(
  account TEXT NOT NULL, season_key TEXT NOT NULL,
  order_id TEXT NOT NULL UNIQUE CHECK(length(order_id)=32),
  commodity_id INTEGER NOT NULL CHECK(commodity_id=105659),
  item_id INTEGER NOT NULL CHECK(item_id=490705043),
  cera INTEGER NOT NULL CHECK(cera=4900),
  recipient_character_id INTEGER NOT NULL,
  before_season_revision INTEGER NOT NULL CHECK(typeof(before_season_revision)='integer' AND before_season_revision>=0),
  resulting_season_revision INTEGER NOT NULL CHECK(typeof(resulting_season_revision)='integer' AND resulting_season_revision>before_season_revision),
  business_snapshot TEXT NOT NULL CHECK(json_valid(business_snapshot)),
  committed_at INTEGER NOT NULL CHECK(typeof(committed_at)='integer' AND committed_at>=0),
  PRIMARY KEY(account,season_key),
  FOREIGN KEY(account,season_key) REFERENCES account_battle_pass_seasons(account,season_key)
);

CREATE TABLE account_battle_pass_deluxe_orders(
  account TEXT NOT NULL, season_key TEXT NOT NULL,
  order_id TEXT NOT NULL UNIQUE CHECK(length(order_id)=32),
  commodity_id INTEGER NOT NULL CHECK(commodity_id>0),
  item_id INTEGER NOT NULL CHECK(item_id=490705044),
  cera INTEGER NOT NULL CHECK(typeof(cera)='integer' AND cera BETWEEN 1 AND 2147483647),
  recipient_character_id INTEGER NOT NULL,
  before_season_revision INTEGER NOT NULL CHECK(typeof(before_season_revision)='integer' AND before_season_revision>=0),
  resulting_season_revision INTEGER NOT NULL CHECK(typeof(resulting_season_revision)='integer' AND resulting_season_revision>before_season_revision),
  before_total_experience INTEGER NOT NULL CHECK(typeof(before_total_experience)='integer' AND before_total_experience>=0),
  paid_total_experience INTEGER NOT NULL CHECK(typeof(paid_total_experience)='integer' AND paid_total_experience>=0),
  resulting_total_experience INTEGER NOT NULL CHECK(typeof(resulting_total_experience)='integer' AND resulting_total_experience>=paid_total_experience),
  business_snapshot TEXT NOT NULL CHECK(json_valid(business_snapshot)),
  committed_at INTEGER NOT NULL CHECK(typeof(committed_at)='integer' AND committed_at>=0),
  PRIMARY KEY(account,item_id),
  FOREIGN KEY(account,season_key) REFERENCES account_battle_pass_seasons(account,season_key)
);

CREATE TABLE account_battle_pass_cera_consumption(
  account TEXT NOT NULL, season_key TEXT NOT NULL,
  total_cera INTEGER NOT NULL DEFAULT 0 CHECK(typeof(total_cera)='integer' AND total_cera>=0),
  numerator_remainder INTEGER NOT NULL DEFAULT 0 CHECK(typeof(numerator_remainder)='integer' AND numerator_remainder BETWEEN 0 AND 99),
  PRIMARY KEY(account,season_key),
  FOREIGN KEY(account,season_key) REFERENCES account_battle_pass_seasons(account,season_key)
);

CREATE TABLE account_battle_pass_cera_orders(
  account TEXT NOT NULL, season_key TEXT NOT NULL, order_id TEXT NOT NULL CHECK(length(order_id)=32),
  recipient_character_id INTEGER NOT NULL,
  cera INTEGER NOT NULL CHECK(typeof(cera)='integer' AND cera>0),
  eligible_cera INTEGER NOT NULL CHECK(typeof(eligible_cera)='integer' AND eligible_cera BETWEEN 1 AND cera),
  experience INTEGER NOT NULL CHECK(typeof(experience)='integer' AND experience>=0),
  percentage INTEGER NOT NULL CHECK(percentage BETWEEN 0 AND 65535),
  before_remainder INTEGER NOT NULL CHECK(before_remainder BETWEEN 0 AND 99),
  resulting_remainder INTEGER NOT NULL CHECK(resulting_remainder BETWEEN 0 AND 99),
  before_season_revision INTEGER NOT NULL CHECK(typeof(before_season_revision)='integer' AND before_season_revision>=0),
  resulting_season_revision INTEGER NOT NULL CHECK(typeof(resulting_season_revision)='integer' AND resulting_season_revision>before_season_revision),
  business_snapshot TEXT NOT NULL CHECK(json_valid(business_snapshot)),
  committed_at INTEGER NOT NULL CHECK(typeof(committed_at)='integer' AND committed_at>=0),
  PRIMARY KEY(account,season_key,order_id),
  FOREIGN KEY(account,season_key) REFERENCES account_battle_pass_seasons(account,season_key)
);

CREATE TABLE account_strongway2019_rewards(
  account TEXT NOT NULL,
  instance_id TEXT NOT NULL CHECK(length(instance_id)=32),
  character_id INTEGER NOT NULL CHECK(typeof(character_id)='integer' AND character_id BETWEEN 1 AND 65535),
  task_attempt INTEGER NOT NULL CHECK(typeof(task_attempt)='integer' AND task_attempt>0),
  run_epoch INTEGER NOT NULL CHECK(typeof(run_epoch)='integer' AND run_epoch>=0),
  dungeon_id INTEGER NOT NULL CHECK(typeof(dungeon_id)='integer' AND dungeon_id IN(5359,5363,5360)),
  bucket INTEGER NOT NULL CHECK(typeof(bucket)='integer' AND bucket IN(0,1)),
  day INTEGER NOT NULL CHECK(typeof(day)='integer' AND day>0),
  week INTEGER NOT NULL CHECK(typeof(week)='integer' AND week>0),
  created_at INTEGER NOT NULL CHECK(typeof(created_at)='integer' AND created_at>=0),
  reward_json TEXT NOT NULL CHECK(json_valid(reward_json) AND json_type(reward_json)='object'),
  fact_json TEXT NOT NULL CHECK(json_valid(fact_json) AND json_type(fact_json)='object'),
  mail_id INTEGER NOT NULL CHECK(typeof(mail_id)='integer' AND mail_id>0),
  PRIMARY KEY(account,instance_id),
  UNIQUE(account,bucket,day),
  CHECK((bucket=0 AND dungeon_id IN(5359,5363)) OR (bucket=1 AND dungeon_id=5360))
);
CREATE INDEX account_strongway2019_rewards_week ON account_strongway2019_rewards(account,bucket,week);

CREATE TABLE character_strongway2019_statistics(
  account TEXT NOT NULL,
  character_id INTEGER NOT NULL CHECK(typeof(character_id)='integer' AND character_id BETWEEN 1 AND 65535),
  instance_id TEXT NOT NULL CHECK(length(instance_id)=32),
  task_attempt INTEGER NOT NULL CHECK(typeof(task_attempt)='integer' AND task_attempt>0),
  run_epoch INTEGER NOT NULL CHECK(typeof(run_epoch)='integer' AND run_epoch>=0),
  dungeon_id INTEGER NOT NULL CHECK(typeof(dungeon_id)='integer' AND dungeon_id IN(5359,5363,5360)),
  mode INTEGER NOT NULL CHECK(typeof(mode)='integer' AND mode IN(0,2,3)),
  elapsed_ms INTEGER CHECK(elapsed_ms IS NULL OR (typeof(elapsed_ms)='integer' AND elapsed_ms BETWEEN 0 AND 4294967295)),
  fact_json TEXT NOT NULL CHECK(json_valid(fact_json) AND json_type(fact_json)='object'),
  PRIMARY KEY(character_id,instance_id),
  CHECK((dungeon_id=5359 AND mode=0 AND elapsed_ms IS NOT NULL) OR
    (dungeon_id=5363 AND mode=2 AND elapsed_ms IS NOT NULL) OR (dungeon_id=5360 AND mode=3))
);

CREATE TABLE character_strongway2019_completions(
  account TEXT NOT NULL,
  instance_id TEXT NOT NULL CHECK(length(instance_id)=32),
  character_id INTEGER NOT NULL CHECK(typeof(character_id)='integer' AND character_id BETWEEN 1 AND 65535),
  task_attempt INTEGER NOT NULL CHECK(typeof(task_attempt)='integer' AND task_attempt>0),
  run_epoch INTEGER NOT NULL CHECK(typeof(run_epoch)='integer' AND run_epoch>=0),
  dungeon_id INTEGER NOT NULL CHECK(typeof(dungeon_id)='integer' AND dungeon_id IN(5359,5363,5360)),
  bucket INTEGER NOT NULL CHECK(typeof(bucket)='integer' AND bucket IN(0,1)),
  day INTEGER NOT NULL CHECK(typeof(day)='integer' AND day>0),
  week INTEGER NOT NULL CHECK(typeof(week)='integer' AND week>0),
  created_at INTEGER NOT NULL CHECK(typeof(created_at)='integer' AND created_at>=0),
  reward_json TEXT NOT NULL CHECK(json_valid(reward_json) AND json_type(reward_json)='object'),
  fact_json TEXT NOT NULL CHECK(json_valid(fact_json) AND json_type(fact_json)='object'),
  state INTEGER NOT NULL CHECK(typeof(state)='integer' AND state IN(0,1,2)),
  mail_id INTEGER CHECK(mail_id IS NULL OR (typeof(mail_id)='integer' AND mail_id>0)),
  PRIMARY KEY(account,instance_id),
  CHECK((state=1 AND mail_id IS NOT NULL) OR (state<>1 AND mail_id IS NULL)),
  CHECK((bucket=0 AND dungeon_id IN(5359,5363)) OR (bucket=1 AND dungeon_id=5360))
);
CREATE INDEX character_strongway2019_completions_pending ON character_strongway2019_completions(account,state,character_id,created_at);

CREATE TABLE character_strongway2019_match_facts(
  account TEXT NOT NULL,
  character_id INTEGER NOT NULL CHECK(typeof(character_id)='integer' AND character_id BETWEEN 1 AND 65535),
  instance_id TEXT NOT NULL CHECK(length(instance_id)=32),
  competition_id TEXT NOT NULL CHECK(length(competition_id)=32),
  bucket INTEGER NOT NULL CHECK(bucket=1),
  day INTEGER NOT NULL CHECK(typeof(day)='integer' AND day>0),
  week INTEGER NOT NULL CHECK(typeof(week)='integer' AND week>0),
  created_at INTEGER NOT NULL CHECK(typeof(created_at)='integer' AND created_at>=0),
  state INTEGER NOT NULL CHECK(typeof(state)='integer' AND state IN(0,2)),
  snapshot_json TEXT NOT NULL CHECK(json_valid(snapshot_json) AND json_type(snapshot_json)='object'),
  PRIMARY KEY(account,instance_id)
);
CREATE INDEX character_strongway2019_match_facts_owner ON character_strongway2019_match_facts(account,character_id,created_at);

CREATE TABLE account_pcroom_claims(
    account TEXT PRIMARY KEY REFERENCES accounts(account_id) ON DELETE CASCADE,
    claim_day INTEGER NOT NULL CHECK(typeof(claim_day)='integer' AND claim_day>=0)
);
CREATE TABLE character_pcroom_claims(
    character_id INTEGER PRIMARY KEY,
    account TEXT NOT NULL,
    claim_day INTEGER NOT NULL CHECK(typeof(claim_day)='integer' AND claim_day>=0),
    FOREIGN KEY(character_id,account) REFERENCES characters(id,account) ON DELETE CASCADE
);
CREATE TABLE character_fatigue_acceleration(
    character_id INTEGER PRIMARY KEY,
    account TEXT NOT NULL,
    enabled INTEGER NOT NULL CHECK(typeof(enabled)='integer' AND enabled IN(0,1)),
    FOREIGN KEY(character_id,account) REFERENCES characters(id,account) ON DELETE CASCADE
);
CREATE TABLE account_endless_adventure_days(
  account TEXT NOT NULL,
  day INTEGER NOT NULL CHECK(day BETWEEN 0 AND 3652058),
  earned INTEGER NOT NULL CHECK(earned BETWEEN 0 AND 200),
  PRIMARY KEY(account,day)
) STRICT;
CREATE TABLE account_pcroom_timepoint_days(
  account TEXT NOT NULL,
  day INTEGER NOT NULL CHECK(day BETWEEN 10101 AND 99991231),
  elapsed_ms INTEGER NOT NULL CHECK(elapsed_ms>=0),
  qualified INTEGER NOT NULL CHECK(qualified IN(0,1)),
  PRIMARY KEY(account,day)
) STRICT;
CREATE TABLE account_pcroom_timepoint_segments(
  account TEXT NOT NULL,
  segment_id TEXT NOT NULL CHECK(length(segment_id)=32 AND segment_id NOT GLOB '*[^0-9a-f]*'),
  day INTEGER NOT NULL CHECK(day BETWEEN 10101 AND 99991231),
  total_ms INTEGER NOT NULL CHECK(total_ms>=0),
  PRIMARY KEY(account,segment_id,day)
) STRICT;
CREATE TABLE account_pcroom_timepoint_claims(
  account TEXT NOT NULL,
  kind INTEGER NOT NULL CHECK(kind IN(0,1)),
  day INTEGER NOT NULL CHECK((kind=0 AND day BETWEEN 10101 AND 99991231) OR (kind=1 AND day=0)),
  reward_index INTEGER NOT NULL CHECK(reward_index BETWEEN 0 AND 3),
  recipient_id INTEGER NOT NULL CHECK(recipient_id BETWEEN 1 AND 65535),
  mail_id INTEGER NOT NULL CHECK(mail_id>0),
  item_id INTEGER NOT NULL CHECK(item_id>0),
  item_count INTEGER NOT NULL CHECK(item_count>0),
  claimed_at INTEGER NOT NULL CHECK(claimed_at>=0),
  PRIMARY KEY(account,kind,day,reward_index)
) STRICT;

CREATE TABLE character_epic_wishes (
    character_id INTEGER PRIMARY KEY,
    account TEXT NOT NULL,
    item_ids TEXT NOT NULL CHECK(json_valid(item_ids) AND json_type(item_ids)='array' AND json_array_length(item_ids)<=5),
    FOREIGN KEY(character_id,account) REFERENCES characters(id,account) ON DELETE CASCADE
);

CREATE TABLE account_daily_attendance_days(
  account TEXT NOT NULL,
  day INTEGER NOT NULL CHECK(day BETWEEN 10101 AND 99991231),
  clears INTEGER NOT NULL CHECK(clears BETWEEN 0 AND 2),
  PRIMARY KEY(account,day)
) STRICT;
CREATE TABLE account_daily_attendance_claims(
  account TEXT NOT NULL,
  kind INTEGER NOT NULL CHECK(kind IN(0,1)),
  reward_index INTEGER NOT NULL CHECK((kind=0 AND reward_index BETWEEN 0 AND 27) OR (kind=1 AND reward_index BETWEEN 0 AND 2)),
  day INTEGER NOT NULL CHECK(day BETWEEN 10101 AND 99991231),
  recipient_id INTEGER NOT NULL CHECK(recipient_id BETWEEN 1 AND 65535),
  mail_id INTEGER NOT NULL CHECK(mail_id>0),
  item_id INTEGER NOT NULL CHECK(item_id>0),
  item_count INTEGER NOT NULL CHECK(item_count>0),
  claimed_at INTEGER NOT NULL CHECK(claimed_at>=0),
  PRIMARY KEY(account,kind,reward_index)
) STRICT;

CREATE TABLE weekly_attendance_season(
  id INTEGER PRIMARY KEY CHECK(id=1),
  starts_at INTEGER NOT NULL CHECK(starts_at>=0),
  ends_at INTEGER NOT NULL CHECK(ends_at>starts_at),
  activated_at INTEGER NOT NULL CHECK(activated_at>=starts_at AND activated_at<ends_at),
  weeks INTEGER NOT NULL CHECK(weeks BETWEEN 1 AND 20),
  CHECK(ends_at-starts_at=weeks*604800)
) STRICT;
CREATE TABLE account_weekly_attendance(
  account TEXT NOT NULL,
  season_start INTEGER NOT NULL CHECK(season_start>=0),
  state TEXT NOT NULL CHECK(json_valid(state) AND json_type(state)='object'),
  PRIMARY KEY(account,season_start)
) STRICT;

CREATE TABLE account_monster_mock_battle(
  account TEXT PRIMARY KEY,
  revision INTEGER NOT NULL CHECK(revision>0),
  display_name TEXT NOT NULL,
  lineup_stars INTEGER NOT NULL CHECK(lineup_stars BETWEEN 0 AND 25),
  state TEXT NOT NULL CHECK(json_valid(state) AND json_type(state)='object')
) STRICT;
CREATE INDEX account_mock_battle_matching ON account_monster_mock_battle(lineup_stars,account);
CREATE INDEX account_mock_battle_name ON account_monster_mock_battle(display_name);
CREATE TABLE account_monster_mock_battle_runs(
  run_id TEXT PRIMARY KEY CHECK(length(run_id)=32),
  account TEXT NOT NULL,
  character_id INTEGER NOT NULL CHECK(character_id BETWEEN 1 AND 65535),
  world_id INTEGER NOT NULL CHECK(world_id>=0),
  opened_at INTEGER NOT NULL CHECK(opened_at>=0),
  paid_revision INTEGER NOT NULL CHECK(paid_revision>0),
  state INTEGER NOT NULL CHECK(state BETWEEN 0 AND 2)
) STRICT;
CREATE UNIQUE INDEX account_mock_battle_active ON account_monster_mock_battle_runs(account) WHERE state=0;


        CREATE TABLE character_revised_beast_rewards(
            character_id INTEGER NOT NULL,account TEXT NOT NULL,
            instance_id TEXT NOT NULL CHECK(length(instance_id)=32),
            task_attempt INTEGER NOT NULL CHECK(task_attempt>0),epoch INTEGER NOT NULL CHECK(epoch>0),
            dungeon_id INTEGER NOT NULL CHECK(dungeon_id BETWEEN 5335 AND 5339),
            difficulty INTEGER NOT NULL CHECK(difficulty BETWEEN 0 AND 2),
            success INTEGER NOT NULL CHECK(success IN (0,1)),
            day_key INTEGER NOT NULL,week_key INTEGER NOT NULL,
            awarded INTEGER NOT NULL CHECK(awarded IN (0,1)),
            before_revision INTEGER NOT NULL,revision INTEGER NOT NULL,
            plan_snapshot TEXT NOT NULL CHECK(json_valid(plan_snapshot)),
            PRIMARY KEY(character_id,instance_id),
            FOREIGN KEY(character_id,account) REFERENCES characters(id,account) ON DELETE CASCADE
        );


        CREATE TABLE character_revised_beast_stage_rewards(
            character_id INTEGER NOT NULL,account TEXT NOT NULL,
            instance_id TEXT NOT NULL CHECK(length(instance_id)=32),
            task_attempt INTEGER NOT NULL CHECK(task_attempt>0),epoch INTEGER NOT NULL CHECK(epoch>0),
            dungeon_id INTEGER NOT NULL CHECK(dungeon_id BETWEEN 5335 AND 5339),
            difficulty INTEGER NOT NULL CHECK(difficulty BETWEEN 0 AND 2),
            success INTEGER NOT NULL CHECK(success IN (0,1)),
            day_key INTEGER NOT NULL,week_key INTEGER NOT NULL,
            awarded INTEGER NOT NULL CHECK(awarded IN (0,1)),
            before_revision INTEGER NOT NULL,revision INTEGER NOT NULL,
            plan_snapshot TEXT NOT NULL CHECK(json_valid(plan_snapshot)),
            PRIMARY KEY(character_id,instance_id),
            FOREIGN KEY(character_id,account) REFERENCES characters(id,account) ON DELETE CASCADE
        );

CREATE TABLE character_various_buffs(
    character_id INTEGER PRIMARY KEY,
    account TEXT NOT NULL,
    buffs TEXT NOT NULL CHECK(json_valid(buffs)),
    FOREIGN KEY(character_id,account) REFERENCES characters(id,account) ON DELETE CASCADE
) STRICT;
CREATE TABLE IF NOT EXISTS character_npc_bookmarks(
            character_id INTEGER PRIMARY KEY,
            account TEXT NOT NULL,
            entries BLOB NOT NULL CHECK(typeof(entries)='blob' AND length(entries)<=4096 AND length(entries)%4=0),
            FOREIGN KEY(character_id,account) REFERENCES characters(id,account)
        );
