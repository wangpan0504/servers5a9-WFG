using DfoServer.Persistence;

namespace DfoServer;

internal static partial class DatabaseSchemaTests
{
    internal static void RewindPvpRankCompletion(Microsoft.Data.Sqlite.SqliteConnection db)
    {
        const string table="character_quest_completion_receipts";
        if(Query(db,"SELECT sql FROM sqlite_schema WHERE name='"+table+"'") is not string sql ||
            !sql.Contains(",4,5,6,",StringComparison.Ordinal)) return;
        if(Query(db,"SELECT count(*) FROM "+table+" WHERE condition_type=5") is not 0L)
            throw new InvalidOperationException("旧版本夹具不能丢弃真实类型5领奖");
        ChangeReceiptDefinition(db,table,definition=>definition.Replace(",4,5,6,",",4,6,",StringComparison.Ordinal));
    }

    internal static void RunPvpRankCore()
    {
        string root=Path.Combine(Path.GetTempPath(),"S5A9-rank-schema-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try {PvpRankUpgrade(root);}
        finally {TestFiles.DeleteDirectory(root,true);}
    }

    static void PvpRankUpgrade(string root)
    {
        root=Path.Combine(root,"pvp-rank");
        Directory.CreateDirectory(root);
        const string table="character_quest_completion_receipts";
        foreach(string scenario in new[]{"ordinary","unknown","late-failure"})
        {
            string path=Path.Combine(root,"rank-"+scenario+".db");
            _=new CharacterRepository(path);
            using(var db=Open(path))
            {
                ChangeReceiptDefinition(db,table,sql=>sql.Replace(",4,5,6,",",4,6,",StringComparison.Ordinal));
                Execute(db,"CREATE INDEX rank_fixture_index ON character_quest_completion_receipts(quest_id)");
                Execute(db,"CREATE TRIGGER rank_fixture_trigger AFTER INSERT ON character_quest_completion_receipts BEGIN SELECT 1; END");
                Execute(db,"PRAGMA user_version=138");
                if(scenario=="unknown") ChangeReceiptDefinition(db,table,sql=>sql.Replace("native_index>=0","native_index>=0 AND native_index<999",StringComparison.Ordinal));
                if(scenario=="late-failure") Execute(db,"ALTER TABLE accounts DROP COLUMN disabled");
            }
            string before=Snapshot(path);
            if(scenario!="ordinary")
            {
                RejectDatabase(()=>new CharacterRepository(path));
                Check(Snapshot(path)==before,"段位迁移未知形状或末段失败回滚DDL和版本");
                continue;
            }
            _=new CharacterRepository(path);
            using(var db=Open(path))
            {
                Check(Query(db,"PRAGMA user_version") is long version && version==SqliteMigrations.CurrentVersion,
                    "138旧库升级到含段位任务类型5的实际版本");
                Check(Query(db,"SELECT count(*) FROM sqlite_schema WHERE name IN('rank_fixture_index','rank_fixture_trigger')") is 2L,
                    "只放宽领奖CHECK，保留原有索引与触发器");
                Check(Query(db,"SELECT instr(sql,'condition_type IN(0,2,4,5,6,')>0 FROM sqlite_schema WHERE name='character_quest_completion_receipts'") is 1L,
                    "type5仍要求冻结领奖计划并禁止伪造清图来源");
            }
            string saved=Snapshot(path);_=new CharacterRepository(path);
            Check(Snapshot(path)==saved,"段位迁移重开幂等");
        }
    }
}
