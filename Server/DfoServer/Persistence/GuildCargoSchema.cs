namespace DfoServer.Persistence;

internal static class GuildCargoSchema
{
    public const string Sql = """
        CREATE TABLE IF NOT EXISTS guild_cargo_state(
          guild_id INTEGER PRIMARY KEY REFERENCES guilds(guild_id) ON DELETE CASCADE,
          revision INTEGER NOT NULL DEFAULT 0 CHECK(revision>=0),
          capacity INTEGER NOT NULL DEFAULT 8 CHECK(capacity BETWEEN 8 AND 56)
        );
        CREATE TABLE IF NOT EXISTS guild_cargo_slots(
          guild_id INTEGER NOT NULL REFERENCES guild_cargo_state(guild_id) ON DELETE RESTRICT,
          slot_index INTEGER NOT NULL CHECK(slot_index BETWEEN 0 AND 55),
          item_uid INTEGER NOT NULL UNIQUE,
          account TEXT GENERATED ALWAYS AS ('guild:' || guild_id) STORED,
          owner_character_id INTEGER NOT NULL DEFAULT 0 CHECK(owner_character_id=0),
          PRIMARY KEY(guild_id,slot_index),
          FOREIGN KEY(account,owner_character_id,item_uid)
            REFERENCES character_inventory_items(account,owner_character_id,item_uid) ON DELETE RESTRICT
        );
        CREATE TABLE IF NOT EXISTS guild_cargo_history(
          id INTEGER PRIMARY KEY AUTOINCREMENT,
          guild_id INTEGER NOT NULL REFERENCES guilds(guild_id) ON DELETE CASCADE,
          created_at_unix INTEGER NOT NULL CHECK(created_at_unix BETWEEN 0 AND 4294967295),
          kind INTEGER NOT NULL CHECK(kind IN(1,2)),
          name TEXT NOT NULL,
          item_core TEXT NOT NULL CHECK(json_valid(item_core)),
          moved_count INTEGER NOT NULL CHECK(moved_count>0)
        );
        CREATE INDEX IF NOT EXISTS guild_cargo_history_recent ON guild_cargo_history(guild_id,id DESC);
        CREATE TABLE IF NOT EXISTS guild_cargo_receipts(
          request_id TEXT PRIMARY KEY CHECK(length(request_id)=64 AND request_id NOT GLOB '*[^0-9A-Fa-f]*'),
          fingerprint TEXT NOT NULL CHECK(length(fingerprint)=64),
          account TEXT NOT NULL,
          character_id INTEGER NOT NULL,
          guild_id INTEGER NOT NULL,
          revision INTEGER NOT NULL CHECK(revision>0),
          destination_slot INTEGER NOT NULL CHECK(destination_slot>=0),
          created_at_unix INTEGER NOT NULL
        );
        CREATE TRIGGER IF NOT EXISTS guild_cargo_exclusive_insert BEFORE INSERT ON guild_cargo_slots
        WHEN EXISTS(SELECT 1 FROM character_inventory_slots WHERE item_uid=NEW.item_uid) OR
             EXISTS(SELECT 1 FROM character_title_book_slots WHERE item_uid=NEW.item_uid) OR
             EXISTS(SELECT 1 FROM mailbox_attachments WHERE item_uid=NEW.item_uid)
        BEGIN SELECT RAISE(ABORT,'guild cargo item already has a position'); END;
        CREATE TRIGGER IF NOT EXISTS guild_cargo_exclusive_update BEFORE UPDATE ON guild_cargo_slots
        WHEN EXISTS(SELECT 1 FROM character_inventory_slots WHERE item_uid=NEW.item_uid) OR
             EXISTS(SELECT 1 FROM character_title_book_slots WHERE item_uid=NEW.item_uid) OR
             EXISTS(SELECT 1 FROM mailbox_attachments WHERE item_uid=NEW.item_uid)
        BEGIN SELECT RAISE(ABORT,'guild cargo item already has a position'); END;
        CREATE TRIGGER IF NOT EXISTS inventory_not_guild_cargo_insert BEFORE INSERT ON character_inventory_slots
        WHEN EXISTS(SELECT 1 FROM guild_cargo_slots WHERE item_uid=NEW.item_uid)
        BEGIN SELECT RAISE(ABORT,'item is in guild cargo'); END;
        CREATE TRIGGER IF NOT EXISTS inventory_not_guild_cargo_update BEFORE UPDATE ON character_inventory_slots
        WHEN EXISTS(SELECT 1 FROM guild_cargo_slots WHERE item_uid=NEW.item_uid)
        BEGIN SELECT RAISE(ABORT,'item is in guild cargo'); END;
        CREATE TRIGGER IF NOT EXISTS title_not_guild_cargo_insert BEFORE INSERT ON character_title_book_slots
        WHEN EXISTS(SELECT 1 FROM guild_cargo_slots WHERE item_uid=NEW.item_uid)
        BEGIN SELECT RAISE(ABORT,'item is in guild cargo'); END;
        CREATE TRIGGER IF NOT EXISTS title_not_guild_cargo_update BEFORE UPDATE ON character_title_book_slots
        WHEN EXISTS(SELECT 1 FROM guild_cargo_slots WHERE item_uid=NEW.item_uid)
        BEGIN SELECT RAISE(ABORT,'item is in guild cargo'); END;
        CREATE TRIGGER IF NOT EXISTS mail_not_guild_cargo_insert BEFORE INSERT ON mailbox_attachments
        WHEN EXISTS(SELECT 1 FROM guild_cargo_slots WHERE item_uid=NEW.item_uid)
        BEGIN SELECT RAISE(ABORT,'item is in guild cargo'); END;
        CREATE TRIGGER IF NOT EXISTS mail_not_guild_cargo_update BEFORE UPDATE ON mailbox_attachments
        WHEN EXISTS(SELECT 1 FROM guild_cargo_slots WHERE item_uid=NEW.item_uid)
        BEGIN SELECT RAISE(ABORT,'item is in guild cargo'); END;
        """;
}
