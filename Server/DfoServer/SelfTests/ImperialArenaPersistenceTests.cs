using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.ImperialArena;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Persistence;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class ImperialArenaPersistenceTests
{
    internal static void RunResources(PvfArchive pvf)
    {
        int checks = Count;
        string root = Path.Combine(Path.GetTempPath(), "s5a9-imperial-arena-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var items = new ItemResources(pvf).Items;
            var catalog = new ImperialArenaCatalog(pvf, items);
            string path = Path.Combine(root, "game.db");
            var repository = new CharacterRepository(path);
            var owners = new AccountOwners();
            using var owner = owners.Acquire("imperial-persistence", () => { });
            var position = new Location(48, 0, 200, 200);
            Character Role(string name) => repository.Create(owner, new(name, position, position)
                { Level = 90, Inventory = InventorySnapshot.Empty with { Gold = 1_000_000 } });
            var primary = Role("EmpireMain");
            var standby = Role("EmpireOther");
            var now = DateTimeOffset.Parse("2026-10-27T08:00:00+08:00");
            var period = ImperialArenaPeriod.At(now);
            using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False");
            db.Open();
            object? Scalar(string text, params (string Name, object Value)[] parameters)
            {
                using var command = db.CreateCommand(); command.CommandText = text;
                foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
                return command.ExecuteScalar();
            }
            void Sql(string text, params (string Name, object Value)[] parameters) => Scalar(text, parameters);
            long Number(string text) => (long)Scalar(text)!;
            string Assets() => JsonSerializer.Serialize(repository.List(owner)) + Number("SELECT count(*) FROM account_imperial_arena_days");
            string unchanged = Assets();
            Reject<InvalidOperationException>(() => repository.PurchaseImperialArenaEntry(owner, primary, ImperialArenaMode.Normal, period, catalog.EntryGold),
                "持久层独立拒绝空编队购券");
            That(Assets() == unchanged, "空编队购券没有金币、revision或日券副作用");
            var configuration = repository.SaveImperialArenaConfiguration(owner, ImperialArenaConfiguration.Empty, new(0, primary.Id, standby.Id));
            Sql("UPDATE characters SET level=89 WHERE id=$id", ("$id", standby.Id));
            unchanged = Assets();
            Reject<InvalidOperationException>(() => repository.PurchaseImperialArenaEntry(owner, primary, ImperialArenaMode.Normal, period, catalog.EntryGold),
                "持久层重新核对第二角色实际等级");
            That(Assets() == unchanged, "备用资格失效不扣费或创建本日券");
            Sql("UPDATE characters SET level=90 WHERE id=$id", ("$id", standby.Id));
            primary = repository.PurchaseImperialArenaEntry(owner, primary, ImperialArenaMode.Normal, period, catalog.EntryGold);
            That(primary.Inventory.Gold == 850_000, "合法双角色首次购券准确扣费");
            unchanged = Assets();
            primary = repository.PurchaseImperialArenaEntry(owner, primary, ImperialArenaMode.Normal, period, catalog.EntryGold);
            That(Assets() == unchanged, "同模式重复购券不写角色或日券");
            Reject<InvalidOperationException>(() => repository.PurchaseImperialArenaEntry(owner, primary, ImperialArenaMode.Hard, period, catalog.EntryGold),
                "持久层拒绝本日换模式");

            int attempt = 0;
            Guid Run(ImperialArenaPeriod at, long elapsed, int? rankedWeek = null, bool complete = false)
            {
                Sql("INSERT OR IGNORE INTO account_imperial_arena_days(account,day,mode) VALUES($account,$day,2)",
                    ("$account", owner.Account), ("$day", at.Day));
                var instance = Guid.NewGuid();
                Sql("""
                    INSERT INTO account_imperial_arena_runs(account,instance_id,day,week,mode,first,second,
                      configuration_revision,task_attempt,admitted_at,minimum_clear_ms,team_json,route_json,cleared_round,elapsed_ms,ranked_week)
                    VALUES($account,$instance,$day,$week,2,$first,$second,$revision,$attempt,$created,30000,'[0,0,0,0]',$route,$round,$elapsed,$ranked)
                    """, ("$account", owner.Account), ("$instance", instance.ToString("N")), ("$day", at.Day), ("$week", at.Week),
                    ("$first", primary.Id), ("$second", standby.Id), ("$revision", configuration.Revision), ("$attempt", ++attempt),
                    ("$created", now.ToUnixTimeSeconds()), ("$route", JsonSerializer.Serialize(ImperialArenaDailyRoute.Create(catalog, at))),
                    ("$round", complete ? 7 : 1), ("$elapsed", elapsed), ("$ranked", rankedWeek is { } week ? week : DBNull.Value));
                return instance;
            }
            void Reward(Guid instance, ImperialArenaPeriod at)
            {
                var reward = ImmutableArray.Create(new ImperialArenaRewardItem(10304532, 3), new ImperialArenaRewardItem(10304533, 3));
                var frozen = ImperialArenaDeathPlan.Materialize(reward, items, now);
                Sql("""
                    INSERT INTO account_imperial_arena_rewards(account,day,monster,instance_id,event_id,reward_json,mail_id)
                    VALUES($account,$day,1,$instance,$event,$reward,0)
                    """, ("$account", owner.Account), ("$day", at.Day), ("$instance", instance.ToString("N")),
                    ("$event", Guid.NewGuid().ToString("N")), ("$reward", JsonSerializer.Serialize(new { Items = frozen })));
            }
            var hardPeriod = ImperialArenaPeriod.At(now.AddDays(1));
            var pending = Run(hardPeriod, 10000); Reward(pending, hardPeriod);
            Sql("CREATE TRIGGER imperial_mail_abort BEFORE INSERT ON mailbox_messages BEGIN SELECT RAISE(ABORT,'imperial mail rollback'); END");
            Reject<SqliteException>(() => repository.CompleteImperialArenaRuns(owner, pending), "困难统一邮件写入失败向上抛出");
            That(Number("SELECT count(*) FROM mailbox_messages") == 0 && Number("SELECT count(*) FROM account_imperial_arena_rewards WHERE mail_id=0") == 1 &&
                Number("SELECT closed FROM account_imperial_arena_runs LIMIT 1") == 0, "邮件失败回滚附件、奖励邮箱引用及实例关闭状态");
            Sql("DROP TRIGGER imperial_mail_abort");
            That(repository.CompleteImperialArenaRuns(owner, pending).Length == 1 && repository.CompleteImperialArenaRuns(owner, pending).IsEmpty &&
                Number("SELECT count(*) FROM mailbox_messages") == 1, "困难结算重试仅投递一次且重开可恢复");

            var eligible = Run(period, 80000, period.Week, complete: true);
            var challenge = repository.ReadImperialArenaChallenge(owner, catalog, period);
            That(challenge.InstanceId == eligible && challenge.Claimable && challenge.TargetMilliseconds == 240000, "只有本周有效完整成绩可领荣誉，缺上周名次时使用配置目标");
            var template = items.Get(catalog.ChallengeRewardItemId);
            var core = InventoryRewardGrantService.Create(template, 1, now);
            var item = InventoryRewardGrantService.CreateItem(template, new(InventoryListType.Main, 9), core);
            var claim = new ImperialArenaChallengeClaim(primary, owner.Generation, 1, challenge, item, now.ToUnixTimeSeconds());
            That(item.ExpireTime == now.AddDays(14).ToUnixTimeSeconds(), "每周荣誉邮件冻结真实14日道具期限");
            Sql("CREATE TRIGGER imperial_claim_abort BEFORE INSERT ON account_imperial_arena_challenge_claims BEGIN SELECT RAISE(ABORT,'imperial claim rollback'); END");
            Reject<SqliteException>(() => repository.ClaimImperialArenaChallenge(owner, catalog, claim), "邮件创建后周资格插入故障触发整笔回滚");
            That(Number("SELECT count(*) FROM mailbox_messages") == 1 && Number("SELECT count(*) FROM account_imperial_arena_challenge_claims") == 0 &&
                repository.ReadImperialArenaChallenge(owner, catalog, period).Claimable, "荣誉投递与领取资格原子回滚，仍可安全重试");
            Sql("DROP TRIGGER imperial_claim_abort");
            var claimed = repository.ClaimImperialArenaChallenge(owner, catalog, claim);
            That(claimed.MailId != 0 && !claimed.Duplicate && repository.ClaimImperialArenaChallenge(owner, catalog, claim) is { Duplicate: true } duplicate &&
                duplicate.MailId == claimed.MailId && Number("SELECT count(*) FROM mailbox_messages") == 2 &&
                !repository.ReadImperialArenaChallenge(owner, catalog, period).Claimable, "周奖提交后重复请求返回原邮件，不重投或重开资格");

            var previousPeriod = ImperialArenaPeriod.At(now.AddDays(-7));
            Run(previousPeriod, 90000, previousPeriod.Week, complete: true);
            var fastest = Run(previousPeriod, 70000, previousPeriod.Week, complete: true);
            using (var earlier = owners.Acquire("aaa-imperial", () => { }))
            {
                var rival = repository.Create(earlier, new("EmpireRival", position, position) { Level = 90 });
                var reserve = repository.Create(earlier, new("EmpireRival2", position, position) { Level = 90 });
                Sql("INSERT INTO account_imperial_arena_days(account,day,mode) VALUES($account,$day,2)", ("$account", earlier.Account), ("$day", previousPeriod.Day));
                Sql("""
                    INSERT INTO account_imperial_arena_runs(account,instance_id,day,week,mode,first,second,
                      configuration_revision,task_attempt,admitted_at,minimum_clear_ms,team_json,route_json,cleared_round,elapsed_ms,ranked_week)
                    SELECT $account,$instance,day,week,mode,$first,$second,configuration_revision,1,admitted_at,
                      minimum_clear_ms,team_json,route_json,cleared_round,elapsed_ms,ranked_week FROM account_imperial_arena_runs WHERE instance_id=$source
                    """, ("$account", earlier.Account), ("$instance", Guid.NewGuid().ToString("N")), ("$first", rival.Id),
                    ("$second", reserve.Id), ("$source", fastest.ToString("N")));
            }
            // 缩小名次只用于合成夹具，真实资源的20000名与240000毫秒不改写。
            var tokens = pvf.ReadTokens(ImperialArenaCatalog.ResourcePath).ToArray();
            int standard = Array.FindIndex(tokens, token => token.Type == 3 && pvf.ResolveString(token.Value) == "[tag tournament hard mode time attack standard rank]");
            That(standard >= 0, "周目标样本夹具命中真实配置字段");
            tokens[standard + 1] = new(0, 1);
            var firstPlace = new ImperialArenaCatalog(tokens, pvf.ResolveString);
            var ranked = repository.ReadImperialArenaChallenge(owner, firstPlace, period);
            That(ranked.TargetMilliseconds == 70000 && ranked.PreviousRank == 2,
                "上周每账号只取最快完整成绩，真实名次样本决定目标且同分按账号稳定排序");

            var deletePeriod = ImperialArenaPeriod.At(now.AddDays(3));
            var deleteRun = Run(deletePeriod, 10000); Reward(deleteRun, deletePeriod);
            repository.Delete(owner, repository.LoadDeletionTarget(owner, primary.Slot));
            That(repository.CompleteImperialArenaRuns(owner).IsEmpty && Number("SELECT count(*) FROM account_imperial_arena_rewards") == 2 &&
                Number("SELECT count(*) FROM account_imperial_arena_challenge_claims") == 1 &&
                repository.ReadImperialArenaState(owner, deletePeriod).Configuration.IsEmpty,
                "删主角先结清困难实例并清编队，账号每日资格及每周荣誉事实保留");
            var reopened = new CharacterRepository(path);
            That(!reopened.ReadImperialArenaChallenge(owner, catalog, period).Claimable && reopened.CompleteImperialArenaRuns(owner).IsEmpty,
                "删角后重开库可读取周成绩且不会卡在不存在的固定收件人");
            That(Scalar("PRAGMA integrity_check") is "ok" && Scalar("SELECT count(*) FROM pragma_foreign_key_check") is 0L,
                "故障、重试与删角后数据库及外键完整性通过");
        }
        finally { Directory.Delete(root, recursive: true); }
        Console.WriteLine($"PASS {Count - checks} 帝国购券、困难结算、每周荣誉故障事务与删角恢复断言");
    }
}
