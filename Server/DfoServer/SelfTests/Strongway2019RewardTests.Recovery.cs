using System.Collections.Immutable;
using System.Reflection;
using DfoServer.Game.Accounts;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Strongway2019;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class Strongway2019RewardTests
{
    // 反射只用于RED可编译：GREEN必须由真实仓储API执行，测试不代持Run来恢复。
    static void Freeze(CharacterRepository repository, (AccountOwners.Lease Owner, Strongway2019RewardPlan Plan)[] plans)
    {
        var method = typeof(CharacterRepository).GetMethod("FreezeStrongway2019Completions");
        That(method is not null, "真实终局必须有持久冻结入口，不能仅存Session计划");
        Invoke(method!, repository, [plans]);
    }
    static ImmutableArray<uint> Recover(CharacterRepository repository, AccountOwners.Lease owner, ushort character)
    {
        var method = typeof(CharacterRepository).GetMethod("RecoverStrongway2019Rewards");
        That(method is not null, "新owner必须可只凭持久完成快照恢复邮件");
        return (ImmutableArray<uint>)Invoke(method!, repository, [owner, character])!;
    }
    static object? Invoke(MethodInfo method, object target, object?[] arguments)
    {
        try { return method.Invoke(target, arguments); }
        catch (TargetInvocationException error) when (error.InnerException is { } inner)
        { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(inner).Throw(); throw; }
    }

    static void CompletionRecovery()
    {
        using var f = new Fixture();
        var at = DateTimeOffset.Parse("2026-11-04T05:59:59+08:00");
        var plan = f.Clear(f.First.Slot, 5359, 490012735, at);
        Freeze(f.Repository, [(f.Owner, plan)]);
        That(f.Scalar("SELECT count(*) FROM character_strongway2019_completions WHERE state=0") is 1L &&
            f.Scalar("SELECT count(*) FROM character_strongway2019_statistics") is 1L &&
            f.Scalar("SELECT count(*) FROM account_strongway2019_rewards") is 0L,
            "权威完成与统计先原子冻结，未交邮件也保留完整权益");
        That(f.Repository.ReadStrongway2019RewardState(f.Owner, new(20261103, 20261028), Strongway2019RewardBucket.Challenge) == new Strongway2019RewardState(1, 1),
            "pending权益预留原日周额度，不能被其它角色抢占");
        f.Sql("CREATE TRIGGER recovery_fail BEFORE INSERT ON account_strongway2019_rewards BEGIN SELECT RAISE(ABORT,'recovery rollback'); END");
        Reject<SqliteException>(() => Recover(new CharacterRepository(f.Path), f.Owner, plan.Expected.Id), "恢复邮件晚期失败整笔回滚");
        That(f.Snapshot() == "0:0:0:0" && f.Scalar("SELECT count(*) FROM character_strongway2019_completions WHERE state=0") is 1L,
            "失败不留下邮件或半回执，原pending可继续恢复");
        f.Sql("DROP TRIGGER recovery_fail");
        ushort character = plan.Expected.Id;
        string item = System.Text.Json.JsonSerializer.Serialize(plan.Item);
        var stale = f.Owner;
        f.Owner = f.Owners.Acquire(Fixture.Account, () => { });
        // 新尝试和更高revision不能抹掉已冻结权益；恢复不用旧Run或清图引用。
        _ = f.Clear(f.First.Slot, 5359, 490012735, at.AddDays(8));
        plan = null!;
        Reject<InvalidOperationException>(() => Recover(new CharacterRepository(f.Path), stale, character), "旧owner不能恢复");
        var recovered = Recover(new CharacterRepository(f.Path), f.Owner, character);
        That(recovered.Length == 1 && Recover(new CharacterRepository(f.Path), f.Owner, character).IsEmpty,
            "新仓储和新owner无旧Run重建，精确恢复一次");
        var mail = f.Repository.LoadMailbox(f.Owner, character, at.ToUnixTimeSeconds()).Entries.Single();
        That(mail.Id == recovered[0] && System.Text.Json.JsonSerializer.Serialize(mail.Attachments.Single().Item with { Uid = 0 }) == item &&
            f.Scalar("SELECT day FROM account_strongway2019_rewards") is 20261103L &&
            f.Scalar("SELECT week FROM account_strongway2019_rewards") is 20261028L &&
            f.Scalar("SELECT created_at FROM account_strongway2019_rewards") is long created && created == at.ToUnixTimeSeconds(),
            "跨06点、周切与新attempt恢复仍保原周期、时间及完整附件");
        MatchingCompletionRecovery();
    }

    static void MatchingCompletionRecovery()
    {
        using var f = new Fixture();
        var original = f.Owner;
        var leases = new List<AccountOwners.Lease>();
        try
        {
            var at = DateTimeOffset.Parse("2026-11-04T06:00:00+08:00");
            var rows = new List<(AccountOwners.Lease Owner, Strongway2019RewardPlan Plan)>();
            for (int i = 0; i < 4; i++)
            {
                f.Owner = f.Owners.Acquire("completion-winner-" + i, () => { }); leases.Add(f.Owner);
                var character = f.Repository.Create(f.Owner, new("SWRecovery" + i, new(38, 1, 450, 234), null)
                    { Level = 95, DungeonState = new(256, 0, 0, 0, 0) });
                rows.Add((f.Owner, f.Start(character.Slot, 5360, 490012737, at).Plan));
            }
            var opponents = Enumerable.Range(200, 4).Select(id => Strongway2019CompetitionTests.CreateRewardRun((ushort)id, Guid.NewGuid(), 1)).ToArray();
            var competition = new Strongway2019Competition(Guid.NewGuid(), rows.Select(row => row.Plan.Run).ToArray(), opponents,
                Strongway2019CompetitionTests.Catalog(30, 10000), TimeSpan.FromSeconds(600));
            competition.Exit(opponents[0], opponents[0].Scene.Identity);
            var plans = rows.Select(row => (row.Owner, row.Plan with { Competition = competition, Terminal = competition.Snapshot.Terminal })).ToArray();
            f.Sql("CREATE TRIGGER batch_fail BEFORE INSERT ON character_strongway2019_completions WHEN NEW.character_id=" + plans[3].Item2.Expected.Id + " BEGIN SELECT RAISE(ABORT,'batch rollback'); END");
            Reject<SqliteException>(() => Freeze(f.Repository, plans), "八成员原比赛胜队第四人冻结失败回滚全队");
            That(f.Scalar("SELECT count(*) FROM character_strongway2019_completions") is 0L &&
                f.Scalar("SELECT count(*) FROM character_strongway2019_statistics") is 0L, "不留下前三人的半套完成记录");
            f.Sql("DROP TRIGGER batch_fail");
            Freeze(f.Repository, plans);
            That(f.Scalar("SELECT count(*) FROM character_strongway2019_completions WHERE state=0") is 4L, "无个人clear的四名胜者同笔冻结");
            var first = f.Repository.CommitStrongway2019Reward(plans[0].Owner, plans[0].Item2);
            That(first is { Granted: true, MailId: > 0 }, "第一人邮件先提交");
            var identities = plans.Select(row => (row.Owner.Account, row.Item2.Expected.Id)).ToArray();
            plans = null!; rows.Clear();
            foreach (var (account, character) in identities)
            {
                using var owner = f.Owners.Acquire(account, () => { });
                var recovered = Recover(new CharacterRepository(f.Path), owner, character);
                That(recovered.Length == (character == identities[0].Id ? 0 : 1), "进程边界后其余三名胜者独立恢复且第一人不重发");
            }
            That(f.Scalar("SELECT count(*) FROM account_strongway2019_rewards") is 4L &&
                f.Scalar("SELECT count(*) FROM character_strongway2019_completions WHERE state=1") is 4L,
                "原八成员比赛最终四名胜者各一封邮件，未逐人冻结而丢掉尾部成员");
        }
        finally { f.Owner = original; foreach (var lease in leases) lease.Dispose(); }
    }
}
