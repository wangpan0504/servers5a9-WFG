using System.Text.Json;
using DfoServer.Development;
using DfoServer.Game.Accounts;
using DfoServer.Game.Adventure;
using DfoServer.Game.Auth;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class AdventureGmTests
{
    public static void RunCore()
    {
        foreach (string command in new[] { "adventure status", "adventure level 35 0", "adventure name 测试团 1" })
        {
            var r = DevelopmentRequest.Parse("account gm " + command, "default");
            That(r.Action == DevelopmentAction.Adventure && r.Account == "gm" && r.Writes == !command.EndsWith("status"), "冒险团GM参数与读写标记");
        }
        foreach (string command in new[] { "adventure", "adventure level 0 0", "adventure level 36 -1", "adventure name A 0 extra", "adventure name A! 0" })
            Reject<InvalidDataException>(() => DevelopmentRequest.Parse(command, "gm"), "冒险团GM拒绝非法参数");
        string root = Path.Combine(Path.GetTempPath(), "adventure-gm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "game.db");
            var catalog = AdventureCoreTests.SyntheticCatalog();
            var repo = new CharacterRepository(path, adventureCatalog: catalog);
            var auth = new AuthenticationService(new AuthenticationRepository(path));
            auth.Create("gm", "fixture-password"); auth.Create("other", "fixture-password");
            var owners = new AccountOwners(); using var owner = owners.Acquire("gm", () => { }); using var other = owners.Acquire("other", () => { });
            var now = DateTimeOffset.UtcNow;
            repo.InitializeAdventure(owner, catalog, now); repo.InitializeAdventure(other, catalog, now);
            var before = repo.ReadAdventure(owner, catalog, now).State;
            var raised = repo.SetAdventure(owner, before.Revision, 3, null, now);
            That(raised.Level == 3 && raised.Experience == 0 && raised.ExcludedExperience == -30 && raised.OpeningExperience == before.OpeningExperience &&
                raised.Points.SequenceEqual(before.Points) && raised.CapsuleExperience == before.CapsuleExperience, "升级只调精确账本，不赠送余额/胶囊或改写开启事实");
            var credit = AdventureCredit.Apply(raised, AdventureCreditSource.Earned, 100, 100, 100, 5, 1000, catalog, 50, 50);
            That(AdventureCredit.ExactTotal(credit.After, catalog) == 35, "GM设置后自然经验仅加本次5，不恢复旧总量");
            var lowered = repo.SetAdventure(owner, raised.Revision, 1, null, now);
            That(lowered.Level == 1 && lowered.ExcludedExperience == 0 && lowered.Remainder == 0, "降级按相同恒等式回调账本");
            var renamed = repo.SetAdventure(owner, lowered.Revision, null, new string('测', 16), now);
            That(renamed.NameInitialized && renamed.Name.Length == 16 && renamed.Level == lowered.Level && renamed.Revision == lowered.Revision + 1,
                "GM改名使用原生16字符/32B上限且消费首次命名资格");
            Reject<InvalidOperationException>(() => repo.SetAdventure(other, repo.ReadAdventure(other, catalog, now).State.Revision, null, renamed.Name, now), "跨账号重名拒绝");
            Reject<InvalidOperationException>(() => repo.SetAdventure(owner, lowered.Revision, 2, null, now), "旧revision拒绝");
            Reject<InvalidDataException>(() => repo.SetAdventure(owner, renamed.Revision, 4, null, now), "按真实资源上限拒绝等级");
            using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False"); db.Open();
            using var sql = db.CreateCommand();
            using (var writer = db.BeginTransaction(deferred: false))
            {
                That(repo.SetAdventure(owner, renamed.Revision, null, renamed.Name, now).Revision == renamed.Revision,
                    "同名零变化在其他writer占用时仍只读，不重写版本");
                writer.Rollback();
            }
            string saved = JsonSerializer.Serialize(repo.ReadAdventure(owner, catalog, now));
            sql.CommandText = "CREATE TRIGGER fail_gm_adventure BEFORE INSERT ON account_adventure_receipts BEGIN SELECT RAISE(ABORT,'gm adventure rollback'); END"; sql.ExecuteNonQuery();
            Reject<SqliteException>(() => repo.SetAdventure(owner, renamed.Revision, 2, null, now), "晚期审计失败回滚等级、排除额与版本");
            That(JsonSerializer.Serialize(repo.ReadAdventure(owner, catalog, now)) == saved, "晚期失败保留完整账户快照");
            sql.CommandText = "DROP TRIGGER fail_gm_adventure"; sql.ExecuteNonQuery();
            That(new CharacterRepository(path, adventureCatalog: catalog).ReadAdventure(owner, catalog, now).State.Name == renamed.Name, "重开仓储保持设置");
            using var replacement = owners.Acquire("gm", () => { });
            Reject<InvalidOperationException>(() => repo.SetAdventure(owner, renamed.Revision, 2, null, now), "旧owner不能修改新会话账号");
        }
        finally { TestFiles.DeleteDirectory(root, true); }
    }
}
