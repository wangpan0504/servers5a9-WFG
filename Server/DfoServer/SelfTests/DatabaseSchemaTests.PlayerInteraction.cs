using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class DatabaseSchemaTests
{
    static void PlayerInteractionPreviewUpgrade(string root)
    {
        foreach (int version in new[] { 78, 79, 80 })
        {
            string path = Path.Combine(root, "player-preview-" + version, "game.db");
            var repository = new CharacterRepository(path);
            var auth = new Game.Auth.AuthenticationService(new AuthenticationRepository(path));
            auth.Create("key-schema", "fixture-password");
            auth.Create("key-target", "fixture-password");
            var owners = new AccountOwners();
            using var owner = owners.Acquire("key-schema", () => { });
            using var peerOwner = owners.Acquire("key-target", () => { });
            var role = repository.Create(owner, new NewCharacter("PreviewA", new(38, 1, 450, 234), null)
                { DungeonState = CharacterDungeonState.NewOrdinary });
            var peer = repository.Create(peerOwner, new NewCharacter("PreviewB", new(38, 1, 450, 234), null));
            using (var db = Open(path))
            {
                RewindObjectKeys(db);
                SeedObjectKeyReceipts(db, role.Id, "old-event", new string('a', 32), 1, hasObjectInstance: false);
                RewindZeroExperience(db);
                Execute(db, """
                    DROP TABLE character_quest_daily_clear;
                    ALTER TABLE character_expert_job DROP COLUMN learned_recipes;
                    ALTER TABLE character_teleport_cooldowns DROP COLUMN upgrade_aura_until;
                    ALTER TABLE character_teleport_cooldowns DROP COLUMN party_aura_until;
                    DROP TABLE account_quick_chat;
                    DROP TABLE character_grief_tower_clears; DROP TABLE character_grief_tower_entries;
                    DROP TABLE character_grief_tower; DROP TABLE account_grief_tower;
                    DROP TABLE dungeon_altar_clear_receipts;
                    DROP TABLE dungeon_despair_clear_receipts; DROP TABLE dungeon_despair_progress;
                    DROP TABLE dungeon_special_entry_receipts;
                    ALTER TABLE characters DROP COLUMN avatar_preset_state;
                    """);
                Execute(db, """
                    CREATE TABLE player_trade_receipts(
                      trade_id TEXT NOT NULL PRIMARY KEY CHECK(length(trade_id)=32),
                      left_id INTEGER NOT NULL,right_id INTEGER NOT NULL CHECK(left_id<>right_id),
                      left_revision INTEGER NOT NULL,right_revision INTEGER NOT NULL,
                      created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
                    ) STRICT;
                    """);
                if (version == 80)
                {
                    using var tx = db.BeginTransaction();
                    SqliteMigrations.ReplaceTableConstraint(db, tx, "character_quest_lifecycle_receipts",
                        "CHECK(quest_id BETWEEN 1 AND 29999)",
                        "CHECK(quest_id BETWEEN 0 AND 29999 AND (quest_id<>0 OR (action='Condition' AND scene_event IS NOT NULL AND before_active=after_active AND changed_items='[]')))");
                    SqliteMigrations.ReplaceTableConstraint(db, tx, "character_quest_lifecycle_receipts",
                        "CHECK(length(acceptance_id)=32 AND acceptance_id NOT GLOB '*[^0-9a-f]*' AND acceptance_id<>'00000000000000000000000000000000')",
                        "CHECK(length(acceptance_id)=32 AND acceptance_id NOT GLOB '*[^0-9a-f]*' AND ((quest_id=0 AND acceptance_id='00000000000000000000000000000000') OR (quest_id<>0 AND acceptance_id<>'00000000000000000000000000000000')))");
                    tx.Commit();
                }
                if (version == 78)
                    Execute(db, "DROP TABLE account_social_requests;" + SocialSchema.LegacyRequestSql);
                Execute(db, $"""
                    INSERT INTO character_social_links VALUES({role.Id},{peer.Id},0,123,'好友');
                    INSERT INTO account_social_links VALUES('key-schema','key-target',123,'备注',2);
                    INSERT INTO account_social_requests(source_account,target_account,message,created) VALUES('key-target','key-schema','旧申请',123);
                    INSERT INTO player_trade_receipts(trade_id,left_id,right_id,left_revision,right_revision) VALUES('{new string('b', 32)}',{role.Id},{peer.Id},3,4);
                    PRAGMA user_version={version};
                    """);
                if (version == 80)
                    Execute(db, $"""
                        INSERT INTO character_quest_lifecycle_receipts VALUES({role.Id},'key-schema','{new string('c', 32)}','Condition',0,
                        '{new string('0', 32)}',0,1,'[]','[]','[]','null',NULL,NULL);
                        """);
            }
            if (version == 80)
            {
                string preview = Snapshot(path);
                Reject<InvalidDataException>(() => _ = new CharacterRepository(path), "社交批次拒绝需要组队回执迁移的v80预览库");
                Check(Snapshot(path) == preview, "拒绝v80保持其助战回执、资产、好友、版本与结构原样");
                continue;
            }
            if (version == 79)
            {
                using (var db = Open(path)) Execute(db, "ALTER TABLE player_trade_receipts ADD COLUMN unexpected INTEGER");
                string invalid = Snapshot(path);
                Reject<InvalidDataException>(() => _ = new CharacterRepository(path), "未知旧库结构拒绝迁移");
                Check(Snapshot(path) == invalid, "升级失败保留旧关系、回执、版本和结构");
                using var restoredDb = Open(path);
                Execute(restoredDb, "ALTER TABLE player_trade_receipts DROP COLUMN unexpected");
            }
            _ = new CharacterRepository(path);
            using (var db = Open(path))
            {
                Check(Query(db, "PRAGMA user_version") is long actual && actual == SqliteMigrations.CurrentVersion &&
                    Query(db, "SELECT count(*) FROM pragma_table_info('character_expert_job') WHERE name='learned_recipes'") is 1L &&
                    Query(db, "SELECT count(*) FROM sqlite_schema WHERE name='character_quest_daily_clear'") is 1L,
                    $"本地v{version}补齐正式78–87及社交迁移，不跳过同号上游能力");
                Check(Query(db, "SELECT memo FROM character_social_links") is "好友" &&
                    Query(db, "SELECT memo FROM account_social_links") is "备注" &&
                    Query(db, "SELECT created FROM account_social_requests WHERE message='旧申请'") is 123L &&
                    Query(db, "SELECT left_revision FROM player_trade_receipts") is 3L,
                    "好友、申请方向和时间、交易历史原样保留");
                Check(Query(db, "SELECT instance_id FROM character_dungeon_drop_objects") is string instance && instance == new string('a', 32) &&
                    Query(db, "SELECT count(*) FROM character_quest_lifecycle_receipts") is long scenes && scenes == (version == 80 ? 1 : 0) &&
                    Query(db, "PRAGMA integrity_check") is "ok" && Query(db, "SELECT count(*) FROM pragma_foreign_key_check") is 0L,
                    "旧掉落实例与助战场景保留，库完整性及外键正确");
            }
            string current = Snapshot(path);
            _ = new CharacterRepository(path);
            Check(Snapshot(path) == current, "兼容升级后重开不重复修改资产和回执");
        }
    }
}
