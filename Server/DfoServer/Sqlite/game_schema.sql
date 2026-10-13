-- S5A9 当前完整数据库结构；账号与游戏数据统一保存。
CREATE TABLE account_adventures(
  account TEXT PRIMARY KEY REFERENCES accounts(account_id),revision INTEGER NOT NULL CHECK(revision>=0),
  name TEXT NOT NULL CHECK(length(name)>0),created_date INTEGER NOT NULL CHECK(created_date BETWEEN 19000101 AND 99991231),
  level INTEGER NOT NULL CHECK(level BETWEEN 1 AND 255),experience INTEGER NOT NULL CHECK(experience>=0),
  remainder TEXT NOT NULL,capsule_experience INTEGER NOT NULL CHECK(capsule_experience>=0),
  brave_experience INTEGER NOT NULL CHECK(brave_experience>=0),points BLOB NOT NULL CHECK(length(points)=20),
  month INTEGER NOT NULL CHECK(month BETWEEN 190001 AND 999912));

CREATE TABLE character_tutorial_state(
  character_id INTEGER PRIMARY KEY,account TEXT NOT NULL,dungeon_id INTEGER NOT NULL CHECK(dungeon_id>0),
  completed INTEGER NOT NULL CHECK(completed IN(0,1)),reward_received INTEGER NOT NULL CHECK(reward_received IN(0,1)),
  FOREIGN KEY(character_id,account) REFERENCES characters(id,account));
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
    FOREIGN KEY(character_id,account) REFERENCES characters(id,account)
);

CREATE TABLE accounts(
    account_id TEXT PRIMARY KEY,
    salt BLOB NOT NULL,
    password_hash BLOB NOT NULL,
    iterations INTEGER NOT NULL,
    disabled INTEGER NOT NULL DEFAULT 0,
    revision INTEGER NOT NULL DEFAULT 1
);

CREATE TABLE characters(
    id INTEGER PRIMARY KEY AUTOINCREMENT CHECK(id BETWEEN 1 AND 65535),
    account TEXT NOT NULL,
    slot INTEGER NOT NULL CHECK(slot BETWEEN 0 AND 31),
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

CREATE TABLE character_quest_clear_receipts(
    character_id INTEGER NOT NULL,account TEXT NOT NULL,
    acceptance_id TEXT NOT NULL CHECK(length(acceptance_id)=32 AND acceptance_id NOT GLOB '*[^0-9a-f]*' AND acceptance_id<>'00000000000000000000000000000000'),
    quest_id INTEGER NOT NULL CHECK(quest_id BETWEEN 1 AND 29999),
    quest_slot INTEGER NOT NULL CHECK(quest_slot BETWEEN 0 AND 29),
    event_id TEXT NOT NULL,instance_id TEXT NOT NULL,
    task_attempt INTEGER NOT NULL CHECK(task_attempt>0),epoch INTEGER NOT NULL CHECK(epoch>0),
    dungeon_id INTEGER NOT NULL,map_id INTEGER NOT NULL,
    generation INTEGER NOT NULL CHECK(generation>0),revision INTEGER NOT NULL CHECK(revision>0),
    PRIMARY KEY(character_id,acceptance_id),

    FOREIGN KEY(character_id,event_id) REFERENCES character_dungeon_growth_receipts(character_id,event_id),
    FOREIGN KEY(character_id,account) REFERENCES characters(id,account)
);

CREATE UNIQUE INDEX character_active_quest_acceptance ON character_active_main_quests(character_id,acceptance_id)
    WHERE acceptance_id<>'00000000000000000000000000000000';

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
          clear_acceptance_id TEXT,condition_type INTEGER NOT NULL DEFAULT 3 CHECK(condition_type IN(0,1,2,3,4,6,12,25,31,39,40,42,44,49,64,65,67,70)),
          selector INTEGER NOT NULL DEFAULT 65535 CHECK(selector BETWEEN 0 AND 65535),multiplier INTEGER NOT NULL DEFAULT 1 CHECK(multiplier BETWEEN 1 AND 65535),
          consumed TEXT NOT NULL DEFAULT '[]' CHECK(json_valid(consumed) AND json_type(consumed)='array' AND json_array_length(consumed)<=255),
          changed_items TEXT NOT NULL DEFAULT '[]' CHECK(json_valid(changed_items) AND json_type(changed_items)='array'),
          plan_snapshot TEXT CHECK(plan_snapshot IS NULL OR (json_valid(plan_snapshot) AND json_type(plan_snapshot)='object')),
          CHECK((condition_type IN(1,3) AND clear_acceptance_id=acceptance_id AND event_id IS NOT NULL AND instance_id IS NOT NULL AND task_attempt IS NOT NULL AND epoch IS NOT NULL)
            OR (condition_type IN(0,2,4,6,12,25,31,39,40,42,44,49,64,65,67,70) AND clear_acceptance_id IS NULL AND event_id IS NULL AND instance_id IS NULL AND task_attempt IS NULL AND epoch IS NULL AND plan_snapshot IS NOT NULL)),
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
    account TEXT PRIMARY KEY,
    revision INTEGER NOT NULL DEFAULT 0 CHECK(revision>=0),
    rental_points INTEGER NOT NULL DEFAULT 0 CHECK(rental_points BETWEEN 0 AND 2147483647),
    seria_luck INTEGER NOT NULL DEFAULT 0 CHECK(seria_luck BETWEEN 0 AND 8),
    premium_contracts TEXT NOT NULL DEFAULT '{}' CHECK(json_valid(premium_contracts) AND json_type(premium_contracts)='object')
);

CREATE TABLE account_currency_balances(
    account TEXT NOT NULL REFERENCES account_wallets(account),
    currency_kind INTEGER NOT NULL CHECK(currency_kind BETWEEN 3 AND 17),
    balance INTEGER NOT NULL CHECK(balance BETWEEN 1 AND 2147483647),
    PRIMARY KEY(account,currency_kind)
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
    quest_id INTEGER NOT NULL CHECK(quest_id BETWEEN 1 AND 29999),
    acceptance_id TEXT NOT NULL CHECK(length(acceptance_id)=32 AND acceptance_id NOT GLOB '*[^0-9a-f]*' AND acceptance_id<>'00000000000000000000000000000000'),
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
    list_type INTEGER NOT NULL CHECK(list_type IN (0,1,2,3,7,12,38)),
    owner_character_id INTEGER GENERATED ALWAYS AS (COALESCE(character_id,0)) STORED,
    slot_index INTEGER NOT NULL CHECK(slot_index BETWEEN 0 AND 351),
    item_uid INTEGER NOT NULL UNIQUE,
    CHECK((list_type=12 AND character_id IS NULL AND slot_index<240) OR (list_type<>12 AND character_id IS NOT NULL AND (list_type<>2 OR slot_index<264))),
    CHECK(list_type<>1 OR slot_index<210),
    CHECK(list_type<>7 OR slot_index<240),
    CHECK(list_type<>38 OR slot_index<98),
    UNIQUE(character_id,list_type,slot_index),
    FOREIGN KEY(account,owner_character_id,item_uid) REFERENCES character_inventory_items(account,owner_character_id,item_uid) ON DELETE CASCADE
);

CREATE UNIQUE INDEX account_cargo_slot ON character_inventory_slots(account,slot_index) WHERE list_type=12;

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
generation INTEGER NOT NULL CHECK(generation>0),amount INTEGER NOT NULL CHECK(amount>0),
rule_id TEXT NOT NULL,resource_source TEXT NOT NULL,
monster_id INTEGER NOT NULL,monster_level INTEGER NOT NULL CHECK(monster_level BETWEEN 1 AND 255),
before_level INTEGER NOT NULL CHECK(before_level BETWEEN 1 AND 95),
after_level INTEGER NOT NULL CHECK(after_level BETWEEN before_level AND 95),
before_experience INTEGER NOT NULL CHECK(before_experience>=0),
after_experience INTEGER NOT NULL CHECK(after_experience>=before_experience AND after_experience<=before_experience+amount),
revision INTEGER NOT NULL CHECK(revision>0),
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
  PRIMARY KEY(character_id,event_id),UNIQUE(character_id,ground_key),
  FOREIGN KEY(character_id,event_id) REFERENCES character_dungeon_growth_receipts(character_id,event_id),
  FOREIGN KEY(character_id,account) REFERENCES characters(id,account));

CREATE TABLE character_dungeon_pickup_receipts(
  character_id INTEGER NOT NULL,account TEXT NOT NULL,event_id TEXT NOT NULL,
  ground_key INTEGER NOT NULL CHECK(ground_key BETWEEN 1 AND 65535),
  instance_id TEXT NOT NULL,task_attempt INTEGER NOT NULL CHECK(task_attempt>0),
  epoch INTEGER NOT NULL CHECK(epoch>0),destination_list INTEGER NOT NULL,
  destination_slot INTEGER NOT NULL CHECK(destination_slot BETWEEN 0 AND 351),
  item_uid INTEGER NOT NULL CHECK(item_uid BETWEEN 1 AND 2147483647),
  after_core TEXT NOT NULL CHECK(json_valid(after_core) AND json_type(after_core)='object'),
  revision INTEGER NOT NULL CHECK(revision>0),
  PRIMARY KEY(character_id,event_id),UNIQUE(character_id,ground_key),
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
  death_events BLOB NOT NULL CHECK(typeof(death_events)='blob' AND length(death_events)>0 AND length(death_events)%16=0),
  report_body BLOB NOT NULL CHECK(typeof(report_body)='blob' AND length(report_body) BETWEEN 1 AND 4096),
  generation INTEGER NOT NULL CHECK(generation>0),
  base_experience INTEGER NOT NULL CHECK(base_experience BETWEEN 0 AND 4294967295),
  rank_experience INTEGER NOT NULL CHECK(rank_experience BETWEEN 0 AND 4294967295),
  total_experience INTEGER NOT NULL CHECK(total_experience BETWEEN 1 AND 4294967295 AND total_experience=base_experience+rank_experience),
  rank_bonus_index INTEGER NOT NULL CHECK(rank_bonus_index BETWEEN -1 AND 4),
  rule_id TEXT NOT NULL,resource_source TEXT NOT NULL,
  before_level INTEGER NOT NULL CHECK(before_level BETWEEN 1 AND 95),
  after_level INTEGER NOT NULL CHECK(after_level BETWEEN before_level AND 95),
  before_experience INTEGER NOT NULL CHECK(before_experience>=0),
  after_experience INTEGER NOT NULL CHECK(after_experience>=before_experience AND after_experience<=before_experience+total_experience),
  revision INTEGER NOT NULL CHECK(revision>0),
  PRIMARY KEY(character_id,event_id),UNIQUE(character_id,instance_id),
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
  PRIMARY KEY(character_id,event_id),UNIQUE(character_id,instance_id,monster_key),
  FOREIGN KEY(character_id,event_id) REFERENCES character_dungeon_growth_receipts(character_id,event_id),
  FOREIGN KEY(character_id,account) REFERENCES characters(id,account));
CREATE TABLE character_dungeon_drop_objects(
  character_id INTEGER NOT NULL,event_id TEXT NOT NULL,ordinal INTEGER NOT NULL CHECK(ordinal BETWEEN 0 AND 499),
  ground_key INTEGER NOT NULL CHECK(ground_key BETWEEN 1 AND 65535),
  core TEXT CHECK(core IS NULL OR (json_valid(core) AND json_type(core)='object')),
  gold_amount INTEGER NOT NULL CHECK(gold_amount BETWEEN 0 AND 2147483647),
  x INTEGER NOT NULL CHECK(x BETWEEN 0 AND 65535),y INTEGER NOT NULL CHECK(y BETWEEN 0 AND 65535),
  record_slot INTEGER NOT NULL CHECK(record_slot BETWEEN -32768 AND 32767),
  CHECK((core IS NULL AND gold_amount>0) OR (core IS NOT NULL AND gold_amount=0)),
  PRIMARY KEY(character_id,ground_key),UNIQUE(character_id,event_id,ordinal),UNIQUE(character_id,event_id,ground_key),
  FOREIGN KEY(character_id,event_id) REFERENCES character_dungeon_drop_batches(character_id,event_id));
CREATE TABLE character_dungeon_drop_pickups(
  character_id INTEGER NOT NULL,account TEXT NOT NULL,event_id TEXT NOT NULL,ground_key INTEGER NOT NULL,
  instance_id TEXT NOT NULL,task_attempt INTEGER NOT NULL CHECK(task_attempt>0),epoch INTEGER NOT NULL CHECK(epoch>0),
  destination_slot INTEGER,item_uid INTEGER,after_core TEXT,
  currency_kind INTEGER,amount INTEGER NOT NULL CHECK(amount>0),after_balance INTEGER,account_revision INTEGER,
  revision INTEGER NOT NULL CHECK(revision>0),
  CHECK((destination_slot IS NOT NULL AND item_uid IS NOT NULL AND destination_slot BETWEEN 0 AND 351 AND item_uid BETWEEN 1 AND 2147483647 AND
    after_core IS NOT NULL AND json_valid(after_core) AND json_type(after_core)='object' AND currency_kind IS NULL AND
    after_balance IS NULL AND account_revision IS NULL) OR
    (destination_slot IS NULL AND item_uid IS NULL AND after_core IS NULL AND currency_kind IS NOT NULL AND currency_kind BETWEEN 0 AND 18 AND
    after_balance IS NOT NULL AND after_balance>=0 AND account_revision IS NOT NULL AND account_revision>=0)),
  PRIMARY KEY(character_id,ground_key),
  FOREIGN KEY(character_id,event_id,ground_key) REFERENCES character_dungeon_drop_objects(character_id,event_id,ground_key),
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

CREATE TABLE account_hotkey_settings(
  account TEXT NOT NULL REFERENCES accounts(account_id),profile INTEGER NOT NULL CHECK(profile IN (0,1)),
  hotkeys BLOB NOT NULL CHECK(typeof(hotkeys)='blob' AND length(hotkeys) BETWEEN 1 AND 1399),
  PRIMARY KEY(account,profile));
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
