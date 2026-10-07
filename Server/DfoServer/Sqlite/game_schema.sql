-- S5A9 首次发布的完整数据库结构；账号与游戏数据统一保存。

CREATE TABLE schema_metadata(
    key TEXT NOT NULL PRIMARY KEY,
    value TEXT NOT NULL
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
    UNIQUE(account,slot)
);

CREATE TABLE character_completed_quests(
    character_id INTEGER NOT NULL REFERENCES characters(id),
    quest_id INTEGER NOT NULL CHECK(quest_id BETWEEN 1 AND 29999),
    PRIMARY KEY(character_id,quest_id)
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
    PRIMARY KEY(character_id,slot)
);

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
    rental_points INTEGER NOT NULL DEFAULT 0 CHECK(rental_points BETWEEN 0 AND 2147483647)
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
