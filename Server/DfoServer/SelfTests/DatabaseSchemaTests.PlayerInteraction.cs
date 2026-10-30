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
        SocialNumberingUpgrade(root);
        PartyDungeonNumberingUpgrade(root);
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
                if (version == 78) RemoveEmptyPromotionFixtureSchema(db);
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
                    DROP TABLE character_dungeon_incount; DROP TABLE account_dungeon_incount;
                    """);
                Execute(db, """
                    CREATE TABLE player_trade_receipts(
                      trade_id TEXT NOT NULL PRIMARY KEY CHECK(length(trade_id)=32),
                      left_id INTEGER NOT NULL,right_id INTEGER NOT NULL CHECK(left_id<>right_id),
                      left_revision INTEGER NOT NULL,right_revision INTEGER NOT NULL,
                      created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
                    ) STRICT;
                    """);
                if (version != 80)
                {
                    using var rewind = db.BeginTransaction();
                    SqliteMigrations.ReplaceTableConstraint(db, rewind, "character_quest_lifecycle_receipts",
                        "CHECK(quest_id BETWEEN 0 AND 29999 AND (quest_id<>0 OR (action='Condition' AND scene_event IS NOT NULL AND before_active=after_active AND changed_items='[]')))", "CHECK(quest_id BETWEEN 1 AND 29999)");
                    SqliteMigrations.ReplaceTableConstraint(db, rewind, "character_quest_lifecycle_receipts",
                        "CHECK(length(acceptance_id)=32 AND acceptance_id NOT GLOB '*[^0-9a-f]*' AND ((quest_id=0 AND acceptance_id='00000000000000000000000000000000') OR (quest_id<>0 AND acceptance_id<>'00000000000000000000000000000000')))", "CHECK(length(acceptance_id)=32 AND acceptance_id NOT GLOB '*[^0-9a-f]*' AND acceptance_id<>'00000000000000000000000000000000')");
                    rewind.Commit();
                }
                if (version == 78)
                    Execute(db, "DROP TABLE account_social_requests;" + SocialSchema.LegacyRequestSql);
                RewindRaidLifecycle(db);
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

    static void PartyDungeonNumberingUpgrade(string root)
    {
        foreach (var (version, corrupt) in new[] { (92, false), (92, true), (97, false) })
        {
            string path = Path.Combine(root, $"party-numbering-{version}-{corrupt}", "game.db");
            _ = new CharacterRepository(path);
            using (var db = Open(path))
            {
                Execute(db, "INSERT INTO characters(account,slot,name,revision,town,area,x,y,direction,gold) VALUES('party-preview',0,'PartyPreview',7,38,1,450,234,0,123)");
                if (version == 92)
                {
                    Execute(db, "DROP TABLE character_epic_production; DROP TABLE quest_eligibility_mail_dispatches; DROP TABLE character_pvp_training_skills; DROP TABLE account_pvp_private_characters; DROP TABLE character_operation_hope_reward_receipts");
                    RewindTalismanInventory(db);
                    Execute(db, $"""
                        INSERT INTO character_quest_lifecycle_receipts VALUES(1,'party-preview','{new string('c', 32)}','Condition',0,
                        '{new string('0', 32)}',6,7,'[]','[]','[]','null',NULL,NULL);
                        """);
                }
                else
                {
                    using var tx = db.BeginTransaction();
                    SqliteMigrations.ReplaceTableConstraint(db, tx, "character_quest_lifecycle_receipts",
                        "CHECK(quest_id BETWEEN 0 AND 29999 AND (quest_id<>0 OR (action='Condition' AND scene_event IS NOT NULL AND before_active=after_active AND changed_items='[]')))", "CHECK(quest_id BETWEEN 1 AND 29999)");
                    SqliteMigrations.ReplaceTableConstraint(db, tx, "character_quest_lifecycle_receipts",
                        "CHECK(length(acceptance_id)=32 AND acceptance_id NOT GLOB '*[^0-9a-f]*' AND ((quest_id=0 AND acceptance_id='00000000000000000000000000000000') OR (quest_id<>0 AND acceptance_id<>'00000000000000000000000000000000')))", "CHECK(length(acceptance_id)=32 AND acceptance_id NOT GLOB '*[^0-9a-f]*' AND acceptance_id<>'00000000000000000000000000000000')");
                    tx.Commit();
                }
                if (corrupt) Execute(db, "ALTER TABLE character_quest_lifecycle_receipts ADD COLUMN unexpected INTEGER");
                RemoveSkillTreeExpansionSchema(db);
                Execute(db, $"PRAGMA user_version={version}");
            }
            if (corrupt)
            {
                string before = Snapshot(path);
                Reject<InvalidDataException>(() => _ = new CharacterRepository(path), "旧组队v92拒绝未知回执结构");
                Check(Snapshot(path) == before, "拒绝未知组队预览库时不修改资产、DDL或版本");
                continue;
            }
            _ = new CharacterRepository(path);
            using (var db = Open(path))
            {
                Check(Query(db, "PRAGMA user_version") is long actual && actual == SqliteMigrations.CurrentVersion &&
                    Query(db, "SELECT revision||':'||gold FROM characters") is "7:123" &&
                    Query(db, "SELECT count(*) FROM character_quest_lifecycle_receipts") is long scenes && scenes == (version == 92 ? 1 : 0) &&
                    Query(db, "SELECT count(*) FROM sqlite_schema WHERE name IN ('character_epic_production','quest_eligibility_mail_dispatches','character_pvp_training_skills','account_pvp_private_characters','character_operation_hope_reward_receipts')") is 5L,
                    "主线v97与组队预览v92补齐正式迁移，保留原角色资产及助战回执");
                Check(Query(db, "PRAGMA integrity_check") is "ok" && Query(db, "SELECT count(*) FROM pragma_foreign_key_check") is 0L,
                    "组队迁移后结构完整且外键正确");
            }
            string once = Snapshot(path);
            _ = new CharacterRepository(path);
            Check(Snapshot(path) == once, "组队迁移后重开不重复改写");
        }
    }

    static void SocialNumberingUpgrade(string root)
    {
        foreach (var (version, social) in new[] { (87, false), (88, false), (89, false), (88, true), (89, true), (90, true) })
        {
            string path = Path.Combine(root, $"social-numbering-{version}-{social}", "game.db");
            var repository = new CharacterRepository(path);
            var auth = new Game.Auth.AuthenticationService(new AuthenticationRepository(path));
            auth.Create("numbering-a", "fixture-password"); auth.Create("numbering-b", "fixture-password");
            using var owner = new AccountOwners().Acquire("numbering-a", () => { });
            var role = repository.Create(owner, new NewCharacter("Numbering", new(38, 1, 450, 234), null));
            using (var db = Open(path))
            {
                Execute(db,"DROP TABLE character_epic_production; DROP TABLE quest_eligibility_mail_dispatches; DROP TABLE character_pvp_training_skills; DROP TABLE account_pvp_private_characters; DROP TABLE character_operation_hope_reward_receipts");
                if (social)
                {
                    Execute(db, "INSERT INTO account_social_links VALUES('numbering-a','numbering-b',123,'备注',2);");
                    if (version == 88) Execute(db, "DROP TABLE account_social_requests;" + SocialSchema.LegacyRequestSql);
                    Execute(db, "INSERT INTO account_social_requests(source_account,target_account,message,created) VALUES('numbering-b','numbering-a','申请',456)");
                }
                else Execute(db, "DROP TABLE character_social_links; DROP TABLE account_social_links; DROP TABLE account_social_requests");
                if (social || version < 88) Execute(db, "DROP TABLE character_dungeon_incount");
                else Execute(db, $"INSERT INTO character_dungeon_incount VALUES({role.Id},'numbering-a',400000016,20261024,2,20261022,3)");
                if (social || version < 89) Execute(db, "DROP TABLE account_dungeon_incount");
                else Execute(db, "INSERT INTO account_dungeon_incount VALUES('numbering-a',400000016,20261024,4)");
                RemoveSkillTreeExpansionSchema(db);
                Execute(db, $"PRAGMA user_version={version}");
            }
            if (version == 90)
            {
                string original = Snapshot(path);
                Reject<InvalidDataException>(() => _ = new CharacterRepository(path), "完整交互旧v90不能按新社交v90打开");
                Check(Snapshot(path) == original, "拒绝同号完整交互库后结构、关系、资产和版本均不变");
                continue;
            }
            if (social)
            {
                using (var db = Open(path)) Execute(db, "ALTER TABLE account_social_links ADD COLUMN unexpected INTEGER");
                string invalid = Snapshot(path);
                Reject<InvalidDataException>(() => _ = new CharacterRepository(path), "旧社交同号库拒绝未知结构");
                Check(Snapshot(path) == invalid, "拒绝前不写新次数表或改版本");
                using var restoredDb = Open(path); Execute(restoredDb, "ALTER TABLE account_social_links DROP COLUMN unexpected");
            }
            _ = new CharacterRepository(path);
            using (var db = Open(path))
            {
                Check(Query(db, "PRAGMA user_version") is long actual && actual == SqliteMigrations.CurrentVersion &&
                    Query(db, "SELECT count(*) FROM sqlite_schema WHERE name IN('character_dungeon_incount','account_dungeon_incount')") is 2L,
                    "主线或社交同号旧库补齐次数与社交迁移");
                Check(Query(db, "SELECT count(*) FROM account_social_links") is long rows && rows == (social ? 1 : 0) &&
                    Query(db, "SELECT count(*) FROM account_social_requests WHERE created=456 AND message='申请'") is long requests && requests == (social ? 1 : 0),
                    "社交关系与申请原样保留，主线升级不造好友");
                if (!social && version >= 88) Check(Query(db, "SELECT daily_used FROM character_dungeon_incount") is 2L, "保留主线角色次数");
                if (!social && version >= 89) Check(Query(db, "SELECT used FROM account_dungeon_incount") is 4L, "保留主线账号次数");
                Check(Query(db, "PRAGMA integrity_check") is "ok" && Query(db, "SELECT count(*) FROM pragma_foreign_key_check") is 0L,
                    "顺延迁移后完整性和外键正确");
            }
            string upgraded = Snapshot(path);
            _ = new CharacterRepository(path);
            Check(Snapshot(path) == upgraded, "顺延迁移重开不重复修改");
        }
    }
}
