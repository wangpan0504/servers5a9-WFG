using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Raids;
using DfoServer.Game.Session;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Raids;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class FiendwarRecoveryTests
{
    internal static async Task RunAsync(ServerOptions options, string root, TcpTestResources resources)
    {
        RunRepository(Path.Combine(root, "fiendwar-recovery-sql"), resources);
        RunDepartureFailure(Path.Combine(root, "fiendwar-departure-failure"), resources);
        await RunProcessAsync(options, Path.Combine(root, "fiendwar-recovery-process"), resources);
    }

    static void RunRepository(string root, TcpTestResources resources)
    {
        int before = Count;
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "game.db");
        var repository = new CharacterRepository(path, resources.Quests);
        var owners = new AccountOwners(); var leases = new List<AccountOwners.Lease>();
        var sessions = new List<GameSession>(); var actors = new List<RaidActor>();
        var registry = new RaidRegistry(); var now = DateTimeOffset.Parse("2026-11-01T04:00:00Z");
        var clock = new LukeRaidClockTests.Clock(now);
        var definition = RaidCatalog.Load(resources.Archive).PrepareResources(resources.Archive,
            SelfTestCatalogs.Dungeons(resources.Archive), resources.ItemResources.Items).Find(2)!;
        // 合成双份费用只用于覆盖多格原票，不改正式资源或正式入场数量。
        var cost = definition.Resources!.Script.EntryCost! with { Count = 2 };
        definition = definition with { Resources = definition.Resources with { Script = definition.Resources.Script with { EntryCost = cost } } };
        Guid instance = Guid.NewGuid();
        try
        {
            for (int index = 0; index < 8; index++)
            {
                var lease = owners.Acquire("fwrecover" + index, () => { }); leases.Add(lease);
                var role = Seed(repository, lease, resources, "FwRecover" + index, cost.ItemId, now);
                var paid = role.Inventory.Items.Single(item => item.Core.ItemId == cost.ItemId);
                var split = role.Inventory.Items.Where(item => item != paid).ToImmutableArray()
                    .Add(paid with { Core = paid.Core with { Value = 1, ExpireTime = checked((int)now.AddDays(1).ToUnixTimeSeconds()), Extension110 = 123 } })
                    .Add(paid with { Uid = 0, Position = paid.Position with { Index = checked((short)(paid.Position.Index + 1)) },
                        Core = paid.Core with { Value = 1, ExpireTime = checked((int)now.AddDays(2).ToUnixTimeSeconds()), TradeRestriction = 1 } });
                InventoryService.Commit(repository, lease, role, new(true, 0, new(role.Inventory, role.Inventory with { Items = split }, "fixture_split_original_tickets")));
                var session = Session(repository, lease, resources, clock);
                sessions.Add(session); actors.Add(session.CaptureRaidActor(registry)!);
            }
            That(registry.Create(actors[0], 2, "Recovery", "Recovery"u8.ToArray(), 8) is not null, "恢复SQL夹具沿原目录建团");
            foreach (var actor in actors.Skip(1))
            {
                registry.Invite(actors[0], actor, 0);
                That(registry.Respond(actor, actors[0].Character, true).Accepted, "恢复SQL夹具沿原邀请入团");
            }
            var rows = AntonRaidStart.PlanReservations(registry.Find(actors[0])!, cost, definition.Resources.Script.WaitingRoom!,
                resources.Towns, resources.ItemResources.EpicPieces, now, out var reason)
                .Select(row => row with { Lifecycle = new("selftest/fiendwar-recovery", now) }).ToImmutableArray();
            That(reason is null && rows.Length == 8, "原费用规划保留8人成员和两格扣票");
            var expected = rows.Select(row => FiendwarPaymentSnapshot.Capture(row, cost)).ToArray();
            var committed = repository.CommitRaidStart(RaidKind.Fiendwar, definition, instance, rows);
            for (int index = 0; index < rows.Length; index++) sessions[index].InstallRaidStart(rows[index], committed[index]);
            That(Query("SELECT count(*) FROM character_fiendwar_recovery") is 8L &&
                Query("SELECT count(*) FROM character_raid_runs WHERE state=0") is 8L, "真实扣票原事务同时写全部生命周期与原票快照");
            string firstJson = (string)Query("SELECT payment_json FROM character_fiendwar_recovery ORDER BY character_id LIMIT 1")!;
            That(JsonSerializer.Deserialize<FiendwarPaymentSnapshot>(firstJson)!.Items.SequenceEqual(expected[0].Items), "两格原Core、期限和限制完整持久化");

            repository.RecordFiendwarDeparture(actors[1], instance, now.AddSeconds(300));
            repository.RecordFiendwarDeparture(actors[2], instance, null);
            repository.RecordFiendwarDeparture(actors[3], instance, now);
            repository.RecordFiendwarDeparture(actors[4], instance, now.AddSeconds(300));
            var replacementLease = owners.Acquire(actors[4].Owner.Account, () => { }); leases.Add(replacementLease);
            var replacementSession = Session(repository, replacementLease, resources, clock); sessions.Add(replacementSession);
            var replacement = replacementSession.CaptureRaidActor(new RaidRegistry())!;
            That(repository.ChangeFiendwarRejoin(actors[4], replacement, instance, now), "合法重返CAS原代次并绑定新owner");
            That(repository.ChangeFiendwarRejoin(actors[4], replacement, instance, now, undo: true), "安装失败可归还原断线资格");
            That(Query($"SELECT state||':'||rejoin_until FROM character_fiendwar_recovery WHERE character_id={replacement.Character}") is string original &&
                original == "1:" + now.AddSeconds(300).ToUnixTimeSeconds(), "归还保留原300秒期限，不续期");
            That(repository.ChangeFiendwarRejoin(actors[4], replacement, instance, now), "归还后原合法重返仍可兑现一次");
            repository.RecordFiendwarDeparture(actors[4], instance, null);
            That(Query($"SELECT state FROM character_fiendwar_recovery WHERE character_id={replacement.Character}") is 0L,
                "旧owner迟到离团不清新owner已绑定资格");
            That(!repository.ChangeFiendwarRejoin(actors[3], replacement, instance, now), "过期或错角色身份不取得重返资格");
            Sql($"DELETE FROM character_fiendwar_recovery WHERE character_id={actors[5].Character}");
            // 仅合成已完成存储守卫，不把手工状态当作真实战斗机制验收。
            Sql($"UPDATE character_raid_runs SET state=2,phase0_event='{Guid.NewGuid():N}',phase0_anchor={now.ToUnixTimeSeconds()},phase0_receipt='{{}}',clear_week=1 WHERE character_id={actors[6].Character}");
            Guid claimed = Guid.NewGuid();
            Sql($"""
                INSERT INTO character_raid_runs(character_id,account,instance_id,raid_type,source,admitted_at,state)
                VALUES({actors[6].Character},'{actors[6].Owner.Account}','{claimed:N}',2,'fixture/legacy-claimed',{now.ToUnixTimeSeconds()},0);
                INSERT INTO character_raid_reward_receipts(character_id,account,instance_id,phase,kind,card,week_key,final_phase,plan_json,mail_ids,created_at,raid_type)
                VALUES({actors[6].Character},'{actors[6].Owner.Account}','{claimed:N}',0,0,0,1,1,json_object(),'[]',{now.ToUnixTimeSeconds()},2);
                """);
            Sql($"CREATE TRIGGER fiendwar_recovery_last_failure BEFORE UPDATE OF state ON character_raid_runs WHEN OLD.character_id={actors[7].Character} BEGIN SELECT RAISE(ABORT,'fixture last recovery failure'); END");
            bool rejected = false;
            try { repository.RecoverInterruptedFiendwar(now); } catch (SqliteException) { rejected = true; }
            That(rejected && Query("SELECT count(*) FROM mailbox_messages") is 0L &&
                Query("SELECT count(*) FROM character_raid_runs WHERE state=0") is 8L, "末成员SQL失败回滚整批邮件、附件、审计和生命周期");
            Sql("DROP TRIGGER fiendwar_recovery_last_failure");
            var result = repository.RecoverInterruptedFiendwar(now);
            That(result == new FiendwarInterruptionResult(4, 2, 1), "只退活跃、未过期断线及已重返原票；放弃/到期/旧无快照分别处理");
            That(Query("SELECT count(*) FROM mailbox_messages") is 4L && Query("SELECT count(*) FROM character_raid_runs WHERE state=1") is 7L,
                "退票及无资格收尾共同落盘，不给未通关者补通关");
            That(Query($"SELECT state||':'||clear_week||':'||phase0_receipt FROM character_raid_runs WHERE character_id={actors[6].Character} AND instance_id='{instance:N}'") is "2:1:{}",
                "系统中断不修改原已完成未领奖事实");
            That(Query($"SELECT state FROM character_raid_runs WHERE instance_id='{claimed:N}'") is 0L &&
                Query($"SELECT plan_json||':'||mail_ids FROM character_raid_reward_receipts WHERE instance_id='{claimed:N}'") is "{}:[]",
                "即使旧run终态不完整，原领奖收据仍保护整笔事实，不改失败或另退票");
            foreach (int index in new[] { 0, 1, 4, 7 })
            {
                var actor = index == 4 ? replacement : actors[index];
                var mails = repository.LoadMailbox(actor.Owner, actor.Character, now.ToUnixTimeSeconds()).Entries;
                That(mails.Length == 1 && mails[0].Attachments.Select(item => item.Item.Core).SequenceEqual(expected[index].Items.Select(item => item.Core)),
                    "退款保留每格原Core/有效期/限制，不按当前资源重建或合并");
            }
            That(repository.RecoverInterruptedFiendwar(now.AddDays(8)) == new FiendwarInterruptionResult(0, 0, 0) &&
                Query("SELECT count(*) FROM mailbox_messages") is 4L, "跨日周重启重复恢复不补发、不重算资格");
            repository.Delete(actors[0].Owner, repository.LoadDeletionTarget(actors[0].Owner, 0));
            That(Query($"SELECT count(*) FROM character_fiendwar_recovery WHERE character_id={actors[0].Character}") is 0L &&
                Query($"SELECT count(*) FROM character_raid_runs WHERE character_id={actors[0].Character}") is 0L,
                "真实删角先清恢复快照再清父run，不遗留原票或阻断合法删除");
            Console.WriteLine($"PASS {Count-before} Fiendwar interruption SQL and original-payment assertions");
        }
        finally
        {
            foreach (var session in sessions) session.End();
            foreach (var lease in leases) lease.Dispose();
        }

        object? Query(string sql)
        { using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False"); db.Open(); using var command = db.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar(); }
        void Sql(string sql)
        { using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False"); db.Open(); using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
    }

    static GameSession Session(CharacterRepository repository, AccountOwners.Lease lease, TcpTestResources resources, TimeProvider? clock = null)
    {
        var session = new GameSession(repository, resources.Towns, lease, questCatalog: resources.Quests,
            entry: new(new(2, 39), 45, "[raid]"), timeProvider: clock);
        session.Select(0); session.MarkReady(); return session;
    }

    static Character Seed(CharacterRepository repository, AccountOwners.Lease lease, TcpTestResources resources,
        string name, int ticket, DateTimeOffset now)
    {
        var point = new Location(38, 0, 629, 196);
        var candidate = CharacterCreation.Prepare(name, resources.Characters.Get(0), point, point, resources.Learning.Get);
        var learning = SkillPointLedger.Synchronize(0, 0, 95, candidate.InitialLearning with
            { Experience = resources.Growth.GetLevelStartExperience(95) }, resources.Learning);
        var role = repository.Create(lease, candidate with { Level = 95, AcceptedStoryLevel = 95, CompletedQuests = [8672], InitialLearning = learning });
        var items = resources.ItemResources;
        return InventoryService.Commit(repository, lease, role, InventoryRewardGrantService.TryGrantBatch(role,
            [new(ticket, 2)], items.Items, items.GoldLimits, items.EpicPieces, now));
    }
}
