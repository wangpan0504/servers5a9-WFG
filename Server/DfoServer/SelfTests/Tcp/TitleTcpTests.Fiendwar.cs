using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Titles;
using DfoServer.GameWorld.Quests;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static partial class TitleTcpTests
{
    static void FiendwarFacts(TcpTestResources resources, string root)
    {
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "game.db");
        var repo = new CharacterRepository(path);
        using var owner = new AccountOwners().Acquire("fw-title-facts", () => { });
        var current = repo.Create(owner, new("FwTitleFacts", resources.Towns.Room, resources.Towns.InitialReturn)
            { Level = 95, AcceptedStoryLevel = 95 });
        var book = new TitleCatalog(resources.Archive, resources.Quests);
        var items = resources.ItemResources;
        var service = new TitleAchievementService(book, resources.Quests, items.Items, items.GoldLimits, items.EpicPieces);
        var now = DateTimeOffset.UtcNow;
        foreach (int id in Enumerable.Range(7003, 6))
            That(!service.Trigger(current, new(id, new(ushort.MaxValue, ushort.MaxValue, ushort.MaxValue)), now).Accepted,
                "六项漩涡成就不接受通用417自报累计：" + id);
        using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False;Foreign Keys=True"); db.Open();
        // 本处为消费事务合成事实，不能当成7004/7006自然生产者已经实现。
        Execute($"INSERT INTO character_fiendwar_title_facts VALUES({current.Id},'fw-title-facts',7003,29,0)");
        var first = service.Reconcile(current, now, repo.LoadFiendwarTitleFacts(owner, current.Id), includeStateFacts: false);
        current = repo.CommitTitleAchievement(owner, first.Plan!);
        That(current.TitleProgress[7003].Remaining.First == 1 && Query("SELECT applied FROM character_fiendwar_title_facts") is 29L,
            "真实资源30次目标按来源水位部分推进，进度与applied同事务");
        Execute("UPDATE character_fiendwar_title_facts SET total=30");
        foreach (var row in new[] { (7004, 5), (7005, 10), (7006, 100), (7007, 100), (7008, 1) })
            Execute($"INSERT INTO character_fiendwar_title_facts VALUES({current.Id},'fw-title-facts',{row.Item1},{row.Item2},0)");
        var facts = repo.LoadFiendwarTitleFacts(owner, current.Id);
        var completed = service.Reconcile(current, now, facts, includeStateFacts: false);
        That(completed.Accepted && completed.Plan is { Completed.Length: 6 } plan && plan.Sections.Contains(270) &&
            plan.Postals.Any(row => row.ItemId == 100331746), "六真实资源称号及原章节总奖励共同规划");
        Execute("CREATE TRIGGER fail_fw_title_mail BEFORE INSERT ON mailbox_attachments BEGIN SELECT RAISE(ABORT,'late fiendwar title failure'); END");
        Reject<SqliteException>(() => repo.CommitTitleAchievement(owner, completed.Plan!), "章节邮件晚失败回滚全部称号、进度及消费水位");
        That(Query("SELECT sum(applied) FROM character_fiendwar_title_facts") is 29L &&
            Query("SELECT count(*) FROM character_title_book_slots") is 0L && Query("SELECT count(*) FROM mailbox_messages") is 0L,
            "晚失败保留原事实待消费，称号UID和章节邮件不残留");
        Execute("DROP TRIGGER fail_fw_title_mail");
        Execute("UPDATE character_fiendwar_title_facts SET total=101 WHERE quest_id=7007");
        current = repo.CommitTitleAchievement(owner, completed.Plan!);
        That(Enumerable.Range(7003, 6).All(id => current.TitleProgress[id].Remaining.Complete) &&
            Query("SELECT count(*) FROM character_title_book_slots") is 6L &&
            Query("SELECT count(*) FROM mailbox_messages") is 1L &&
            Query("SELECT total||':'||applied FROM character_fiendwar_title_facts WHERE quest_id=7007") is "101:100",
            "提交原消费快照不吞后到事实，六称号和总奖励只发一次");
        Reject<InvalidOperationException>(() => repo.CommitTitleAchievement(owner, completed.Plan!), "旧称号快照不能再次发奖或倒退水位");
        var late = service.Reconcile(current, now, repo.LoadFiendwarTitleFacts(owner, current.Id), includeStateFacts: false);
        long revision = current.Revision;
        current = repo.CommitTitleAchievement(owner, late.Plan!);
        That(current.Revision == revision && repo.LoadFiendwarTitleFacts(owner, current.Id).IsEmpty &&
            Query("SELECT count(*) FROM mailbox_messages") is 1L, "已完成称号仅消费晚到水位，不重发奖励或无谓增加revision");

        void Execute(string sql) { using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
        object? Query(string sql) { using var command = db.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar(); }
    }
}
