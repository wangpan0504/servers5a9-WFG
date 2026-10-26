using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.TagEvildom;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using DfoServer.Persistence;
using DfoServer.Network.Builders;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class SessionPlanningTests
{
    static void TagEvildomClearTransaction()
    {
        using var fixture = new Fixture();
        var first = fixture.Repository.Create(fixture.Owner, new("ClearPrimary", fixture.Towns.Room, fixture.Towns.InitialReturn)
            { Level = 95, InitialLearning = fixture.Read().Learning, DungeonState = new(256, 0, 0, 0, 0) });
        var second = fixture.Repository.Create(fixture.Owner, new("ClearStandby", fixture.Towns.Room, fixture.Towns.InitialReturn)
            { Level = 95, InitialLearning = first.Learning });
        var config = fixture.Repository.CommitTagEvildomConfiguration(fixture.Owner, TagEvildomConfiguration.Empty,
            TagEvildomConfiguration.Empty with { Teams = [new(first.Id, second.Id), default, default] });
        var clearAt = DateTimeOffset.Parse("2026-11-01T21:59:59Z");
        long admittedAt = clearAt.AddMilliseconds(-3_612_345).ToUnixTimeSeconds();
        var map = new DungeonMap("boss", new(0, 0), 1, "map/tag-clear-fixture.map");
        var maze = new DungeonMaze(0, 1, 1, "tag-clear-fixture", [map.Position], [map.Position], [map], []);

        (SinglePlayerDungeonRun Run, NativeDungeonCombatAuthority Authority, DungeonMonsterPlan Monsters) Enter(bool weekly = false)
        {
            var current = fixture.Repository.LoadCharacter(fixture.Owner, first.Slot);
            var admission = new TagEvildomAdmission(config, current, second, 9608, new string('a', 64), admittedAt);
            var entry = new DungeonEntryPlan(current.Id, current.Revision + 1, 9608, 0, byte.MaxValue, 0, maze, map, map)
                { DisableMonsterAndClearExperience = true, DisableFatigueConsumption = true, DisableGiveupPenalty = true };
            var monster = new DungeonRoomMonster(1, [1, 0, 95, 100, 100, 0, 1, 1], "[fixed]", "[boss]", "monster/tag-fixture.mob");
            var monsters = DungeonMonsterPlan.FromRoom(new(map, [monster], [], []), current.Id, checked((ushort)current.DungeonState!.NextObjectKey));
            var route = new DungeonFixedRoute([monsters], []);
            var authority = new NativeDungeonCombatAuthority();
            That(SinglePlayerDungeonRun.TryCreate(entry, current.DungeonState.LastAttempt + 1, 1, route, authority, out var created, out _) &&
                authority.BindTo(created!), "真实尝试号与Native权威组成合成TAG终点夹具");
            var run = created!;
            var charge = new DungeonEntryCharge(null, 0, run.Snapshot.Identity, map.Id)
                { DungeonId = 9608, TagEvildom = admission, Weekly = weekly ? new(20261026, 0) : null };
            var committed = fixture.Repository.CommitDungeonEntry(fixture.Owner, current, current.DungeonState.NextObjectKey + 100, charge);
            That(run.InstallTagEvildom(new(committed.Character, second, config.Revision), admission) &&
                run.CompleteLoading(run.Snapshot.Identity, Guid.NewGuid()).Outcome == DungeonRunOutcome.Accepted,
                "固定准入和key/attempt同事务提交后才安装Run");
            return (run, authority, monsters);
        }
        TagEvildomClearPlan Terminal((SinglePlayerDungeonRun Run, NativeDungeonCombatAuthority Authority, DungeonMonsterPlan Monsters) live)
        {
            var monster = live.Monsters.Monsters.Single();
            var report = new NativeDungeonDeathObservation(monster.ObjectKey, first.Id, 0x0111, first.Id, 600, 3, 0,
                checked((uint)monster.CandidateLevel), 504, 620, 3, true, new string('A', 64));
            var issued = live.Authority.ValidateAndIssue(live.Run, live.Run.Snapshot.Identity, report).Death!;
            var pending = live.Run.PrepareConfirmedDeath(issued).Pending!;
            That(pending.TagEvildomClear is not null && pending.Result.ClearFact is null && live.Run.HasPendingDeath &&
                live.Run.ConfirmCommittedDeath(pending).Outcome == DungeonRunOutcome.Rejected,
                "仅可信终点产生TAG候选，普通死亡确认不得提前宣布通关");
            var selection = new TagEvildomRewardSelection(9608, 490012775, [new(10309367, 81), new(10309368, 81)], new string('a', 64));
            ItemTemplate Template(int id) => new(id, ItemKind.Material, 0, 1, 0, 1000, 0, 3, 1, "[account]", ushort.MaxValue, 0, "fixture", "[material] 0");
            var attachments = TagEvildomRewardPolicy.Materialize(selection, Template, clearAt);
            return new(live.Run, pending, selection, attachments, clearAt.ToUnixTimeSeconds(), 3_612_345);
        }

        string initial = fixture.Snapshot();
        Reject<InvalidDataException>(() => Enter(weekly: true), "TAG准入不能混入普通每周地下城计数");
        That(fixture.Snapshot() == initial, "混用普通周次数的失败连原attempt预留一起回滚");
        fixture.Sql("CREATE TRIGGER reject_tag_entry BEFORE INSERT ON account_tag_evildom_runs BEGIN SELECT RAISE(ABORT,'fixture'); END;");
        Reject<SqliteException>(() => Enter(), "准入末段失败回滚原attempt、key和revision");
        fixture.Sql("DROP TRIGGER reject_tag_entry");
        That(fixture.Snapshot() == initial, "准入失败不留下半次正式预留");
        var abandoned = Enter();
        var live = Enter();
        That(fixture.Scalar("SELECT count(*) FROM account_tag_evildom_runs") is 1L &&
            fixture.Scalar("SELECT count(*) FROM account_tag_evildom_clearances") is 0L,
            "连续失败/断线重入只留当前admitted，不消费通关额度");
        var old = Terminal(abandoned);
        Reject<InvalidOperationException>(() => fixture.Repository.CommitTagEvildomClear(fixture.Owner, old),
            "被新attempt取代的旧终点不能补领奖");
        var stale = Terminal(live);
        var advanced = fixture.Repository.LoadCharacter(fixture.Owner, first.Slot);
        fixture.Repository.ReserveDungeonAttempt(fixture.Owner, advanced, advanced.DungeonState!.NextObjectKey + 100);
        string newerAttempt = fixture.Snapshot();
        Reject<InvalidOperationException>(() => fixture.Repository.CommitTagEvildomClear(fixture.Owner, stale),
            "旧admitted仍在时也必须复核真正LastAttempt，不能仅依赖新TAG入场剪裁");
        That(fixture.Snapshot() == newerAttempt, "过期attempt不创建邮件或改变已有准入行");
        live = Enter();
        live.Run.ReportTagEvildomDeath(live.Run.Snapshot.Identity, 0, admittedAt * 1000);
        var plan = Terminal(live);
        Reject<InvalidDataException>(() => fixture.Repository.CommitTagEvildomClear(fixture.Owner,
            plan with { Reward = plan.Reward with { ResourceKey = new string('b', 64) } }), "其它资源表的奖励不能套用旧固定准入");
        That(live.Run.TagEvildomLife!.ActiveOrdinal == 1 && live.Run.CurrentTagEvildomRevival() is null &&
            live.Run.SwitchTagEvildom(live.Run.Snapshot.Identity, 0) is null &&
            live.Run.Return(live.Run.Snapshot.Identity, Guid.NewGuid()).Outcome == DungeonRunOutcome.Rejected,
            "终点准备期间停止切角/复活/退出，奖励仍固定给登录队首而非active1");
        using (var foreign = fixture.Owners.Acquire("foreign-tag", () => { }))
            Reject<InvalidOperationException>(() => fixture.Repository.CommitTagEvildomClear(foreign, plan), "别的账号不能重用真实终点");
        string before = fixture.Snapshot();
        fixture.Sql("CREATE TRIGGER reject_tag_reward BEFORE UPDATE ON account_tag_evildom_runs WHEN NEW.clearance_receipt IS NOT NULL BEGIN SELECT RAISE(ABORT,'fixture'); END;");
        Reject<SqliteException>(() => fixture.Repository.CommitTagEvildomClear(fixture.Owner, plan), "邮件已规划后末段失败整笔回滚");
        fixture.Sql("DROP TRIGGER reject_tag_reward");
        That(fixture.Snapshot() == before && live.Run.IsPendingDeath(plan.Death), "失败保留同一终点和冻结附件，不占次数、不留信或物品");
        fixture.Sql("UPDATE account_tag_evildom_configuration SET revision=revision+1");
        var committed = fixture.Repository.CommitTagEvildomClear(fixture.Owner, plan);
        That(!committed.Duplicate && committed.MailId > 0 && fixture.Scalar("SELECT count(*) FROM account_tag_evildom_clearances") is 1L &&
            fixture.Scalar("SELECT recipient_id FROM mailbox_messages WHERE delivery_key LIKE 'tag-evildom:%'") is long recipient && recipient == first.Id &&
            fixture.Repository.ReadTagEvildomState(fixture.Owner, plan.Period).ProtectedMembers.SetEquals([first.Id,second.Id]),
            "无关配置revision不改固定队伍；单份邮件给队首，两个成员同事务保护");
        That(fixture.Scalar("SELECT elapsed_ms FROM account_tag_evildom_runs") is 3_612_345L,
            "一小时以上真实通关仍发一份奖励，持久化完整实际耗时");
        var display = TagEvildomRunPackets.ClearPresentation(new([], plan.ElapsedMilliseconds));
        That(display.Length == 92 && display[84] == 0x80 && display[85] == 0xEE && display[86] == 0x36 && display[87] == 0,
            "仅1679按消费者一小时上限显示，不能把显示限制变成丢奖励条件");
        string after = fixture.Snapshot();
        var replay = new CharacterRepository(fixture.Database).CommitTagEvildomClear(fixture.Owner, plan);
        That(replay.Duplicate && replay.MailId == committed.MailId && fixture.Snapshot() == after &&
            live.Run.ConfirmCommittedTagEvildomClear(replay).Outcome == DungeonRunOutcome.Accepted && live.Run.ClearFact is null,
            "模拟commit后回执丢失及库重开，先回读原mail再确认Run，不产生普通clear");
        using (var entered = new ManualResetEventSlim())
        using (var release = new ManualResetEventSlim())
        {
            var holder = Task.Run(() =>
            {
                using var db = new SqliteConnection("Data Source=" + fixture.Database + ";Pooling=False");
                db.Open();
                using var write = db.BeginWrite("TagEvildomClearTests.OtherWriter");
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(5));
            });
            That(entered.Wait(TimeSpan.FromSeconds(5)), "另一写事务实际占有writer门闩");
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                That(fixture.Repository.CommitTagEvildomClear(fixture.Owner, plan).Duplicate && watch.Elapsed < TimeSpan.FromSeconds(1),
                    "已完成回执只读恢复，不排队等待其它账号writer");
            }
            finally { release.Set(); holder.GetAwaiter().GetResult(); }
        }
        Reject<InvalidDataException>(() => fixture.Repository.CommitTagEvildomClear(fixture.Owner, plan with { Created = plan.Created + 1 }),
            "跨日周重试不能移动冻结归属");
        That(fixture.Repository.ReadTagEvildomState(fixture.Owner, TagEvildomPeriod.At(clearAt.AddSeconds(1))).UsedThisWeek == 0,
            "首个可信终点位于周日界前，次周读取不被回执重试占用");
        Reject<InvalidDataException>(() => fixture.Repository.CommitTagEvildomClear(fixture.Owner,
            plan with { Reward = plan.Reward with { DefinitionId = 490012776 } }), "不能用同run换抽奖定义");
        fixture.Repository.Delete(fixture.Owner, fixture.Repository.LoadDeletionTarget(fixture.Owner, first.Slot));
        string deleted = fixture.Snapshot();
        That(fixture.Repository.CommitTagEvildomClear(fixture.Owner, plan).Duplicate && fixture.Snapshot() == deleted &&
            fixture.Scalar("SELECT count(*) FROM account_tag_evildom_clearances") is 1L &&
            fixture.Scalar("SELECT count(*) FROM mailbox_messages WHERE delivery_key LIKE 'tag-evildom:%'") is 0L &&
            fixture.Repository.ReadTagEvildomState(fixture.Owner, plan.Period).UsedThisWeek == 1,
            "删队首真实清理邮箱，但永久历史回执和额度保留，后续回读绝不补发");
    }
}
