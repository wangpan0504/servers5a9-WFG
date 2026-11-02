using System.Reflection;
using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Strongway2019;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class Strongway2019RewardTests
{
    static void CompletionTakeover()
    {
        var method = typeof(CharacterRepository).GetMethod("FreezeStrongway2019MatchingCheckpoint");
        That(method is not null, "匹配首次冻结必须接受原权威终局独立事实，不能卡在被替换的胜者旧Session");
        using var f = new Fixture();
        var original = f.Owner;
        var leases = new List<AccountOwners.Lease>();
        try
        {
            var clock = new Strongway2019MatchingTests.Clock();
            var at = clock.GetUtcNow();
            var rows = new List<(AccountOwners.Lease Owner, Strongway2019RewardPlan Plan)>();
            for (int i = 0; i < 8; i++)
            {
                f.Owner = f.Owners.Acquire("checkpoint-member-" + i, () => { }); leases.Add(f.Owner);
                var character = f.Repository.Create(f.Owner, new("SWCheck" + i, new(38, 1, 450, 234), null)
                    { Level = 95, DungeonState = new(256, 0, 0, 0, 0) });
                if (i == 3)
                {
                    var cap = f.Match(character.Slot, at, exit: true);
                    That(f.Repository.CommitStrongway2019Reward(f.Owner, cap) is { Granted: true },
                        "第四位胜者先经真实独立终局用尽匹配日额度，后续仍应保存本次胜场");
                }
                rows.Add((f.Owner, f.Start(character.Slot, 5360, 490012737, at).Plan));
            }
            var competition = new Strongway2019Competition(Guid.NewGuid(), rows.Take(4).Select(row => row.Plan.Run).ToArray(),
                rows.Skip(4).Select(row => row.Plan.Run).ToArray(), Strongway2019CompetitionTests.Catalog(30, 10000), TimeSpan.FromSeconds(600), clock);
            competition.Exit(rows[4].Plan.Run, rows[4].Plan.Run.Scene.Identity);
            var members = rows.Select(row => (row.Owner.Account, row.Plan.Expected.Id)).ToArray();
            var oldWinner = rows[0].Owner;
            using var newWinner = f.Owners.Acquire(oldWinner.Account, () => { });
            rows[0].Plan.Run.Return(rows[0].Plan.Run.Scene.Identity, Guid.NewGuid());
            f.Owner = newWinner;
            _ = f.Clear(rows[0].Plan.Expected.Slot, 5359, 490012735, at.AddDays(1));
            var item = rows[0].Plan.Item;
            void Save(AccountOwners.Lease reporter) => Invoke(method!, f.Repository, [reporter, competition, members, item]);
            Reject<InvalidOperationException>(() => Save(oldWinner), "旧owner不能签发共享checkpoint");
            f.Sql("CREATE TRIGGER checkpoint_fail BEFORE INSERT ON character_strongway2019_match_facts WHEN NEW.character_id=" + rows[3].Plan.Expected.Id + " BEGIN SELECT RAISE(ABORT,'checkpoint rollback'); END");
            Reject<SqliteException>(() => Save(rows[1].Owner), "首次checkpoint晚期失败保持完整原终局可重试");
            That(f.Scalar("SELECT count(*) FROM character_strongway2019_match_facts") is 0L, "四胜者共享checkpoint失败整笔回滚");
            f.Sql("DROP TRIGGER checkpoint_fail");
            var loser = rows[7];
            f.Repository.Delete(loser.Owner, f.Repository.LoadDeletionTarget(loser.Owner, loser.Plan.Expected.Slot));
            That(f.Scalar("SELECT count(*) FROM characters WHERE id=" + loser.Plan.Expected.Id) is 0L,
                "权威终局签发后败方经真实删除入口删角，不能因此否认其余胜者原权益");
            using var newReporter = f.Owners.Acquire(rows[1].Owner.Account, () => { });
            Save(newReporter);
            That(f.Scalar("SELECT count(*) FROM character_strongway2019_match_facts") is 4L &&
                f.Scalar("SELECT count(*) FROM character_strongway2019_statistics") is 0L,
                "新当前reporter无需旧Session即可冻结四名胜者，未冒旧owner写角色统计");
            var oldPeriod = Strongway2019RewardPeriod.At(DateTimeOffset.FromUnixTimeSeconds(
                (long)f.Scalar("SELECT created_at FROM character_strongway2019_match_facts LIMIT 1")!));
            That(f.Repository.ReadStrongway2019RewardState(newWinner, oldPeriod, Strongway2019RewardBucket.Matching) == new Strongway2019RewardState(1, 1),
                "共享checkpoint中的未消费权益也预留原日周额度");
            ushort[] ids = rows.Take(4).Select(row => row.Plan.Expected.Id).ToArray();
            rows.Clear();
            for (int i = 0; i < 4; i++)
            {
                var owner = i == 0 ? newWinner : i == 1 ? newReporter : leases[i];
                var recovery = typeof(CharacterRepository).GetMethod("RecoverStrongway2019Completion");
                That(recovery is not null, "恢复结果必须明确统计是否变化，不能只以邮件数量判断");
                var repo = new CharacterRepository(f.Path);
                var firstRecovery = Invoke(recovery!, repo, [owner, ids[i]])!;
                var mails = (ImmutableArray<uint>)firstRecovery.GetType().GetProperty("MailIds")!.GetValue(firstRecovery)!;
                That((bool)firstRecovery.GetType().GetProperty("Changed")!.GetValue(firstRecovery)! && mails.Length == (i == 3 ? 0 : 1),
                    "首次恢复真实新增统计，即使state2额度已满没有邮件也明确Changed");
                var secondRecovery = Invoke(recovery!, repo, [owner, ids[i]])!;
                That(!(bool)secondRecovery.GetType().GetProperty("Changed")!.GetValue(secondRecovery)! &&
                    ((ImmutableArray<uint>)secondRecovery.GetType().GetProperty("MailIds")!.GetValue(secondRecovery)!).IsEmpty &&
                    Recover(repo, owner, ids[i]).IsEmpty,
                    "重复Ready无新增统计或邮件，兼容旧恢复API并且不刷新普通初始1491/1753");
            }
            That(f.Scalar("SELECT count(*) FROM account_strongway2019_rewards") is 4L &&
                f.Scalar("SELECT count(*) FROM character_strongway2019_statistics") is 4L,
                "接管先于首次freeze不再卡死整队，也不重复奖励或统计");
        }
        finally { f.Owner = original; foreach (var lease in leases) lease.Dispose(); }
    }
}
