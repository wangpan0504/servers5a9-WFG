using DfoServer.Persistence;

namespace DfoServer;

internal static partial class DatabaseSchemaTests
{
    static void Quest704NumberingUpgrade(string root)
    {
        root=Path.Combine(root,"quest704-numbering");
        Directory.CreateDirectory(root);
        (string Kind, int[] Versions)[] histories =
        [
            ("main", [88,89,90,91,92]),
            ("preview", [88,89,90,91,92,93,94]),
            ("combined", [92,93,94,95,96]),
            ("current", [93,94,95,96,97])
        ];
        string[] taskTables = ["character_epic_production", "quest_eligibility_mail_dispatches", "character_pvp_training_skills",
            "account_pvp_private_characters", "character_operation_hope_reward_receipts"];
        foreach (var (kind, versions) in histories)
        foreach (int version in versions)
        {
            bool preview = kind == "preview";
            int taskStage = kind switch { "main" => -1, "combined" => version - 92, "current" => version - 93,
                _ => version <= 89 ? version - 88 : version - 90 };
            string path=Path.Combine(root,$"quest704-{kind}-{version}.db");
            _=new CharacterRepository(path);
            var preserved = new Dictionary<string, string>();
            using(var db=Open(path))
            {
                RemoveSkillTreeExpansionSchema(db);
                Execute(db,"INSERT INTO characters(account,slot,name,revision,town,area,x,y,direction) VALUES('preview',0,'Preview',7,1,0,100,100,0)");
                if(preview)
                {
                    Execute(db,"DROP TABLE character_social_links; DROP TABLE account_social_links; DROP TABLE account_social_requests");
                    if (version <= 89) Execute(db,"DROP TABLE character_dungeon_incount; DROP TABLE account_dungeon_incount");
                }
                else if (kind == "main")
                {
                    if (version <= 89) Execute(db,"DROP TABLE character_social_links; DROP TABLE account_social_links; DROP TABLE account_social_requests");
                    else if (version == 90) Execute(db,"DROP TABLE account_social_requests;" + SocialSchema.LegacyRequestSql);
                    if(version==88) Execute(db,"DROP TABLE account_dungeon_incount");
                }
                if (kind != "current" && !(kind == "main" && version == 92)) RewindTalismanInventory(db);
                for (int index = taskTables.Length - 1; index > taskStage; index--)
                    Execute(db,"DROP TABLE " + taskTables[index]);
                if (taskStage >= 0)
                    Execute(db,"INSERT INTO character_epic_production VALUES(1,5000,123,20261024,2,3,4,5,6,7)");
                if (taskStage >= 1)
                    Execute(db,"INSERT INTO quest_eligibility_mail_dispatches VALUES('preview',0,101,1,9,'fixture/101.qst',1234)");
                if (taskStage >= 2)
                    Execute(db,"INSERT INTO character_pvp_training_skills VALUES(1,'{}','aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',6)");
                if (taskStage >= 3)
                    Execute(db,"INSERT INTO account_pvp_private_characters VALUES('preview',0,0,1,123456,30); INSERT INTO account_pvp_private_characters VALUES('preview',0,1,NULL,654321,30)");
                if (taskStage >= 4)
                    Execute(db,"INSERT INTO character_operation_hope_reward_receipts VALUES(1,'preview','aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa','bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb',1,0,12001,1,1,6,7,95,95,0,0,0,'{}','[]','[]',1234)");
                foreach (string table in taskTables)
                    if (TableNames(db).Contains(table)) preserved.Add(table, Rows(db, table, ColumnNames(db, table)));
                RewindRaidLifecycle(db);
                Execute(db,$"PRAGMA user_version={version}");
            }
            // 连续两次打开覆盖真实升级以及升级后重启，不重复制作或邮件。
            _=new CharacterRepository(path);
            string upgraded = Snapshot(path);
            _=new CharacterRepository(path);
            Check(Snapshot(path) == upgraded, "顺延后重开保持结构、版本及全部数据不变");
            using(var db=Open(path))
            {
                foreach (var (table, rows) in preserved)
                    Check(Rows(db, table, ColumnNames(db, table)) == rows, "迁移逐值保留已有账本：" + table);
                Check(Query(db,"SELECT count(*) FROM pragma_foreign_key_check") is 0L, "迁移后全部外键有效");
                Check(Query(db,"PRAGMA user_version") is long current && current==SqliteMigrations.CurrentVersion &&
                    Query(db,"SELECT count(*) FROM sqlite_schema WHERE type='table' AND name IN('character_dungeon_incount','account_dungeon_incount','character_epic_production','quest_eligibility_mail_dispatches')") is 4L,
                    "主线88–92、任务预览88–94及整合预览92–96按历史结构补齐且正式93–97保持幂等");
                Check(Query(db,"SELECT revision FROM characters WHERE id=1") is 7L && Query(db,"PRAGMA integrity_check") is "ok",
                    "迁移不改角色revision或业务状态");
                Check(Query(db,"SELECT count(*) FROM character_epic_production") is long count && count==(taskStage >= 0 ? 1 : 0),
                    "主线升级不凭空生成制作进度，预览进度不丢失");
                if(taskStage >= 0) Check(Query(db,"SELECT weapon_id||':'||charge||':'||service_week||':'||weekly_uses||':'||since_big_chance||':'||special_uses||':'||additional_weekly_uses||':'||intermediate_material_remainder||':'||ordinary_material_remainder FROM character_epic_production") is "5000:123:20261024:2:3:4:5:6:7",
                    "预览制作武器、进度和周次数原样保留");
                Check(Query(db,"SELECT count(*) FROM quest_eligibility_mail_dispatches") is long mails && mails==(taskStage >= 1 ? 1 : 0),
                    "首次邀请账本迁移不补发或清空原账号已投递事实");
            }
        }
        foreach (string fault in new[]{"unknown-epic", "partial-social", "future-table", "unknown-incount", "missing-epic", "full-social", "mixed-incount", "mixed-talisman", "late-failure"})
        {
            string path = Path.Combine(root, "quest704-reject-" + fault + ".db");
            _ = new CharacterRepository(path);
            using (var db = Open(path))
            {
                RemoveSkillTreeExpansionSchema(db);
                if (fault != "mixed-talisman") RewindTalismanInventory(db);
                RewindRaidLifecycle(db);
                Execute(db,"DROP TABLE character_social_links; DROP TABLE account_social_links; DROP TABLE account_social_requests; DROP TABLE character_operation_hope_reward_receipts; DROP TABLE account_pvp_private_characters; DROP TABLE character_pvp_training_skills; DROP TABLE quest_eligibility_mail_dispatches; PRAGMA user_version=90");
                Execute(db,"INSERT INTO characters(account,slot,name,revision,town,area,x,y,direction) VALUES('preview',0,'Preview',7,1,0,100,100,0); INSERT INTO character_epic_production VALUES(1,5000,123,20261024,2,3,4,5,6,7)");
                if (fault == "unknown-epic") Execute(db,"ALTER TABLE character_epic_production ADD COLUMN unexpected INTEGER");
                if (fault == "full-social") Execute(db, SocialSchema.Sql);
                if (fault == "mixed-incount") Execute(db, SqliteMigrations.QuestEligibilityMailSchema + "; PRAGMA user_version=89");
                if (fault == "partial-social") Execute(db,SocialSchema.LegacyRequestSql);
                if (fault == "missing-epic") Execute(db,"DROP TABLE character_epic_production;" + SqliteMigrations.QuestEligibilityMailSchema);
                if (fault == "future-table") Execute(db,SqliteMigrations.PvpTrainingSkillsSchema);
                if (fault == "unknown-incount") Execute(db,"ALTER TABLE character_dungeon_incount ADD COLUMN unexpected INTEGER");
                // 先通过旧编号识别并执行新DDL，再于末段Validate失败，证明整笔事务回滚。
                if (fault == "late-failure") Execute(db,"ALTER TABLE character_quest_daily_clear ADD COLUMN unexpected INTEGER");
            }
            string before = Snapshot(path);
            DfoServer.Testing.TestCheck.Reject<InvalidDataException>(() => _ = new CharacterRepository(path), "未知任务预览拒绝：" + fault);
            Check(Snapshot(path) == before, "拒绝或迁移末段失败保留全部DDL、版本、进度与原行：" + fault);
        }
    }

    // 回退合成夹具到护石发布前的真实容器约束，不能只改版本号伪造历史库。
    static void RewindTalismanInventory(Microsoft.Data.Sqlite.SqliteConnection db)
    {
        using var tx = db.BeginTransaction();
        SqliteMigrations.ReplaceTableConstraint(db, tx, "character_inventory_slots",
            "CHECK(list_type IN (0,1,2,3,7,12,38,46))", "CHECK(list_type IN (0,1,2,3,7,12,38))");
        tx.Commit();
    }
}
