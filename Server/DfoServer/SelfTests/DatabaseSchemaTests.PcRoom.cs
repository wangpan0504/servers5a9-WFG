using Microsoft.Data.Sqlite;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class DatabaseSchemaTests
{
    static void PcRoomVersionCollisionUpgrade(string root)
    {
        string[] strongwayTables = [Strongway2019MatchingCheckpointSchema.Table, Strongway2019CompletionSchema.Table,
            Strongway2019StatisticsSchema.Table, Strongway2019RewardSchema.Table];
        foreach (int previousVersion in new[] { 162, 181 })
        {
            string directory = Path.Combine(root, "pcroom-v" + previousVersion);
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "game.db");
            _ = new CharacterRepository(path);
            Dictionary<string, (string[] Columns, string Rows)> beforeRows;
            using (var db = Open(path))
            {
                RewindReleasedBattlePassTables(db, previousVersion);
                foreach (string table in strongwayTables) Execute(db, "DROP TABLE " + table);
                if (previousVersion == 162) RewindFrozenLootSchema(db);
                Execute(db, """
                    INSERT INTO accounts(account_id,salt,password_hash,iterations) VALUES('pcroom-old',x'01',x'02',1);
                    INSERT INTO characters(account,slot,name,revision,town,area,x,y,direction,level,experience,gold)
                        VALUES('pcroom-old',0,'PcRoomOld',7,38,1,450,234,0,2,60,123);
                    INSERT INTO account_wallets(account,revision,rental_points) VALUES('pcroom-old',5,17);
                    INSERT INTO account_pcroom_claims VALUES('pcroom-old',20261031);
                    INSERT INTO character_pcroom_claims VALUES(1,'pcroom-old',20261031);
                    INSERT INTO character_fatigue_acceleration VALUES(1,'pcroom-old',1);
                    """);
                Execute(db, $"PRAGMA user_version={previousVersion}");
                beforeRows = TableNames(db).ToDictionary(table => table,
                    table => (ColumnNames(db, table), Rows(db, table, ColumnNames(db, table))));
                if (previousVersion == 181)
                    Execute(db, "CREATE TABLE account_strongway2019_rewards_week(value INTEGER)");
            }
            if (previousVersion == 181)
            {
                string failed = Snapshot(path);
                Reject<SqliteException>(() => new CharacterRepository(path), "旧网吧181补跑正式181时索引撞表，整笔升级失败");
                Check(Snapshot(path) == failed, "旧网吧181升级失败回滚已建结构及版本，保留全部历史行与领取偏好");
                using var db = Open(path);
                Execute(db, "DROP TABLE account_strongway2019_rewards_week");
            }
            _ = new CharacterRepository(path);
            using (var db = Open(path))
            {
                Check(Query(db, "PRAGMA user_version") is long version && version == SqliteMigrations.CurrentVersion,
                    $"旧网吧{previousVersion}按精确结构补跑正式迁移并升级当前版本");
                Check(strongwayTables.All(table => Query(db, "SELECT count(*) FROM sqlite_schema WHERE type='table' AND name='" + table + "'") is 1L),
                    "网吧旧库补齐正式181–184四张强者之路表");
                Check(Query(db, "SELECT count(*) FROM pragma_table_info('character_dungeon_drop_batches') WHERE name='freeze_format'") is 1L &&
                    Query(db, "SELECT count(*) FROM pragma_table_info('character_dungeon_drop_pickups') WHERE name='epic_piece'") is 1L,
                    "网吧旧库补齐正式死亡冻结和拾取结构");
                foreach (var (table, original) in beforeRows)
                    Check(Rows(db, table, original.Columns) == original.Rows,
                        "网吧编号碰撞升级保留领取、偏好和原表全部历史行：" + table);
                Check(Query(db, "PRAGMA integrity_check") is "ok" && Query(db, "SELECT count(*) FROM pragma_foreign_key_check") is 0L,
                    "网吧编号碰撞升级保持完整性与归属外键");
            }
            string saved = Snapshot(path);
            _ = new CharacterRepository(path);
            Check(Snapshot(path) == saved, "网吧旧库升级后重开幂等，不改领取事实或燃烧选择");
        }
    }

    // 当前库构造185之前的迁移夹具时，先移除新增空表；不能丢弃真实领取事实或角色偏好。
    internal static void RemovePcRoomSchema(SqliteConnection db)
    {
        foreach (string table in new[] { "character_fatigue_acceleration",
            "character_pcroom_claims", "account_pcroom_claims" })
        {
            if (Query(db, "SELECT count(*) FROM sqlite_schema WHERE type='table' AND name='" + table + "'") is not 1L) continue;
            if (Query(db, "SELECT count(*) FROM " + table) is not 0L)
                throw new InvalidOperationException("旧库迁移夹具不能丢弃网吧领取事实或燃烧偏好：" + table);
            Execute(db, "DROP TABLE " + table);
        }
    }
}
