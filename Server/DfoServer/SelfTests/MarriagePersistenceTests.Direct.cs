using DfoServer.Game.Marriage;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class MarriagePersistenceTests
{
    public static void RunDirectResources(Development.DevelopmentCatalog catalog)
    {
        foreach (bool sameAccount in new[] { false, true })
        {
            using var f = new Fixture();
            using (var db = f.Open())
            using (var write = db.BeginWrite("MarriagePersistenceTests.DirectResourceSchema"))
            {
                MarriageSameAccountSchema.Upgrade(db, write.Transaction);
                write.Transaction.Commit();
            }
            var second = sameAccount ? f.Create(f.FirstOwner, "SameMale", 0) : f.Second;
            string secondAccount = sameAccount ? f.FirstOwner.Account : f.SecondOwner.Account;
            string line = "account " + f.FirstOwner.Account + " direct-marriage " + f.First.Name + " " + secondAccount + " " + second.Name;
            string Answer() => Development.DevelopmentGm.Answer(line, "unused", f.Repository, f.Owners, static _ => null, catalog: catalog);
            That(Answer().StartsWith("ERR ", StringComparison.Ordinal) && f.Count("marriage_records") == 0,
                "真实GM命令拒绝已持有账号owner，不踢线或写库");
            f.FirstOwner.Dispose();
            f.SecondOwner.Dispose();
            string result = Answer();
            That(result.StartsWith("OK ", StringComparison.Ordinal), "真实GM命令允许离线跨账号及同账号异性结婚: " + result);
            That(f.Repository.FindMarriage(f.First.Id)?.State == 4 && f.Repository.FindMarriage(second.Id)?.FirstId == f.First.Id,
                "真实GM命令解析、PVF职业性别与存储回读闭合");
            That(Answer().StartsWith("ERR ", StringComparison.Ordinal) && f.Count("marriage_records") == 1,
                "真实GM重复命令拒绝已婚，不返回伪成功");
        }
    }

    static void DirectMarriageCases()
    {
        foreach (string job in new[] { "swordman", "gunner", "priest", "at fighter", "at mage", "demonic swordman", "demonic lancer", "gun blader" })
            That(MarriageDirectRules.Male(job), "GM结婚按职业身份识别男性");
        foreach (string job in new[] { "fighter", "mage", "at gunner", "thief", "creator mage", "at swordman", "knight", "at priest" })
            That(!MarriageDirectRules.Male(job), "GM结婚按职业身份识别女性");
        Reject<InvalidDataException>(() => MarriageDirectRules.Male("unknown"), "未知职业不猜测性别");
        Reject<InvalidDataException>(() => MarriageDirectRules.Create(Guid.NewGuid(), 1, 1, 2, true, true, 1000), "GM拒绝同性关系");

        foreach (bool sameAccount in new[] { false, true })
        {
            using var f = new Fixture();
            using (var db = f.Open())
            using (var write = db.BeginWrite("MarriagePersistenceTests.DirectSchema"))
            {
                MarriageSameAccountSchema.Upgrade(db, write.Transaction);
                MarriageSameAccountSchema.Upgrade(db, write.Transaction);
                write.Transaction.Commit();
            }
            var first = f.First;
            var secondOwner = sameAccount ? f.FirstOwner : f.SecondOwner;
            var second = sameAccount ? f.Create(f.FirstOwner, "SameMale", 0) : f.Second;
            var plan = MarriageDirectRules.Create(Guid.NewGuid(), 201, first.Id, second.Id, false, true, 1000);
            f.Sql($"CREATE TRIGGER fail_direct_member BEFORE INSERT ON marriage_active_members WHEN NEW.character_id={second.Id} BEGIN SELECT RAISE(ABORT,'direct marriage rollback'); END");
            Reject<SqliteException>(() => f.Repository.CommitDirectMarriage(f.FirstOwner, secondOwner, first, second, plan), "第二成员失败回滚直接结婚");
            That(f.Count("marriage_records") == 0 && f.Count("marriage_active_members") == 0 &&
                f.ReadFirst().Revision == first.Revision && f.ReadFirst().Inventory.Gold == 1000,
                "直接结婚晚失败不残留单边关系、不修改角色库存");
            f.Sql("DROP TRIGGER fail_direct_member");
            var saved = f.Repository.CommitDirectMarriage(f.FirstOwner, secondOwner, first, second, plan);
            var rereadSecond = f.Repository.LoadCharacter(secondOwner, second.Slot);
            That(saved.State == 4 && saved.Revision == 1 && saved.MarriedAt == 1000 && saved.Members.Count == 2 &&
                f.Repository.FindMarriage(first.Id)?.Id == saved.Id && f.Repository.FindMarriage(second.Id)?.Id == saved.Id &&
                f.ReadFirst().Marriage?.Id == saved.Id && rereadSecond.Marriage?.Id == saved.Id,
                "跨账号与同账号直接结婚双方索引和重载一致");
            That(f.ReadFirst().Revision == first.Revision && rereadSecond.Revision == second.Revision &&
                f.ReadFirst().Inventory.Gold == 1000 && rereadSecond.Inventory.Gold == 1000,
                "直接结婚不伪造扣费、奖励或角色资产版本");
            Reject<InvalidOperationException>(() => f.Repository.CommitDirectMarriage(f.FirstOwner, secondOwner, first, second, plan), "直接结婚重放不可重复建立关系");
            That(f.Count("marriage_records") == 1 && f.Count("marriage_active_members") == 2, "重放没有额外关系");
            var before = f.Repository.FindMarriage(first.Id)!;
            var updated = f.Repository.CommitMarriageSingle(f.FirstOwner, f.ReadFirst(), Noop(f.ReadFirst()), before,
                before with { Revision = 2, RingLevel = 1 });
            That(updated.Record.Revision == 2 && f.Repository.FindMarriage(second.Id)?.RingLevel == 1,
                "直接结婚后原有婚姻单方CAS仍能正常更新，包括同账号");
        }

        using (var f = new Fixture())
        {
            var plan = MarriageDirectRules.Create(Guid.NewGuid(), 202, f.First.Id, f.Second.Id, false, true, 1000);
            using var replacement = f.Owners.Acquire(f.FirstOwner.Account, static () => { });
            Reject<InvalidOperationException>(() => f.Repository.CommitDirectMarriage(f.FirstOwner, f.SecondOwner, f.First, f.Second, plan),
                "被替换的旧owner不能建立婚姻关系");
            That(f.Count("marriage_records") == 0, "旧世代拒绝没有存档副作用");
        }
    }
}
