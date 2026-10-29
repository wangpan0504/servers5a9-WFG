using DfoServer.Game.Accounts;
using DfoServer.Persistence;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class DungeonSettlementTests
{
    public static void RunEkernResources(PvfArchive pvf)
    {
        var rules = SelfTestCatalogs.Dungeons(pvf).EkernCycle;
        var growth = SelfTestCatalogs.Growth(pvf);
        string root = Path.Combine(Path.GetTempPath(), "S5A9-ekern-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root,"game.db");
        try
        {
            var repository = new CharacterRepository(path);
            var owners = new AccountOwners();
            using var owner = owners.Acquire("ekern", () => { });
            var current = repository.Create(owner,Fixture("EkernClear"));
            const int day = 739917;
            for (int clear = 1; clear <= 4; clear++)
            {
                uint dungeon = clear == 4 ? 5098u : 5097u;
                var before = repository.LoadEkernCycle(owner,current.Id,day);
                That(rules.CanEnter(dungeon,before.Normal,before.Trial),"循环资源允许当前实际进度进入");
                var (run, authority) = CreateRun(current,entry => entry with { DungeonId = dungeon, QuestId = 0, RouteQuestId = 0, ActiveQuestSlot = byte.MaxValue });
                run.AcceptConfirmedDeath(authority.Death(run.Snapshot.Identity,20));
                var pending = run.PrepareSettlement(run.ClearFact!,new byte[] {1,2,3},Reward).Pending!;
                pending.FreezeEkernDay(day);
                if (clear == 1)
                {
                    Sql("CREATE TRIGGER fail_ekern BEFORE INSERT ON character_dungeon_settlement_receipts BEGIN SELECT RAISE(ABORT,'ekern rollback'); END");
                    Reject<SqliteException>(() => repository.CommitDungeonSettlement(owner,current,run,pending,growth),"结算receipt故障回滚艾肯计数");
                    That(repository.LoadEkernCycle(owner,current.Id,day) == before,"故障后计数不提前增加");
                    Sql("DROP TRIGGER fail_ekern");
                }
                var committed = repository.CommitDungeonSettlement(owner,current,run,pending,growth);
                var replay = repository.CommitDungeonSettlement(owner,current,run,pending,growth);
                That(replay.Duplicate,"重复报告复用原receipt");
                current = committed.Character;
                var saved = new CharacterRepository(path).LoadEkernCycle(owner,current.Id,day);
                That(saved.Normal == Math.Min(clear,3) && saved.Trial == (clear == 4 ? 1 : 0),"通关计数原子持久化，重复报告不重复增加");
                That(rules.CanEnter(5098,saved.Normal,saved.Trial) == (clear == 3),"第三次通关开放试炼，试炼完成后日额度关闭");
            }
            var nextDay = repository.LoadEkernCycle(owner,current.Id,day+1);
            That(nextDay.Normal == 0 && nextDay.Trial == 0 && !rules.CanEnter(5098,nextDay.Normal,nextDay.Trial),"次日重新完成前置，旧日不继续开试炼");
            var other = repository.Create(owner,Fixture("EkernOther"));
            That(repository.LoadEkernCycle(owner,other.Id,day).Normal == 0,"同账号其他角色进度隔离");
        }
        finally { Directory.Delete(root,true); }
        Console.WriteLine("PASS 艾肯循环资源、第三次开图、事务回滚、幂等、仓储重建与日切");
        void Sql(string sql)
        {
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
            db.Open(); using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
        }
    }
}
