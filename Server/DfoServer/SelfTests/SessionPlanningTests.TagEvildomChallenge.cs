using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.TagEvildom;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class SessionPlanningTests
{
    static void TagEvildomChallengeTransaction()
    {
        using var fixture = new Fixture();
        var first = fixture.Repository.Create(fixture.Owner, new("RouteFirst", fixture.Towns.Room, fixture.Towns.InitialReturn)
            { Level = 95, InitialLearning = fixture.Read().Learning, DungeonState = new(256, 0, 0, 0, 0) });
        var second = fixture.Repository.Create(fixture.Owner, new("RouteSecond", fixture.Towns.Room, fixture.Towns.InitialReturn)
            { Level = 95, InitialLearning = first.Learning });
        var config = fixture.Repository.CommitTagEvildomConfiguration(fixture.Owner, TagEvildomConfiguration.Empty,
            TagEvildomConfiguration.Empty with { Teams = [new(first.Id, second.Id), default, default] });
        var at = DateTimeOffset.Parse("2026-10-27T05:29:59+08:00");
        var day = TagEvildomPeriod.At(at);
        (Character Character, Guid Instance) Enter(DateTimeOffset when, int maze = 2, int dungeon = 9608)
        {
            var current = fixture.Repository.LoadCharacter(fixture.Owner, first.Slot);
            var identity = new DungeonRunIdentity(first.Id, 0, current.DungeonState!.LastAttempt + 1, Guid.NewGuid(), 1);
            var admission = new TagEvildomAdmission(config, current, second, dungeon, new string('a', 64), when.ToUnixTimeSeconds())
                { MazeOrdinal = maze };
            var committed = fixture.Repository.CommitDungeonEntry(fixture.Owner, current, current.DungeonState.NextObjectKey + 100,
                new(null, 0, identity, 1) { DungeonId = dungeon, TagEvildom = admission });
            return (committed.Character, identity.InstanceId);
        }
        void Return((Character Character, Guid Instance) entry, DateTimeOffset when, Location? position = null) =>
            fixture.Repository.CommitTagEvildomReturn(fixture.Owner, entry.Character, entry.Instance,
                position ?? entry.Character.Position, entry.Character.Return, null, TagEvildomPeriod.At(when).Day);

        string before = fixture.Snapshot();
        Reject<InvalidOperationException>(() => Enter(at.AddSeconds(1)), "关闭时段的仓储准入也拒绝，不能只依赖Handler");
        That(fixture.Snapshot() == before, "关闭拒绝回滚attempt/key/revision与路线");
        var abandoned = Enter(at);
        That(fixture.Repository.ReadTagEvildomRoute(fixture.Owner, day.Day, 0) == 2 &&
            fixture.Repository.ReadTagEvildomState(fixture.Owner, day).UsedThisWeek == 0, "实际入场才保存路线，未跨日不占周额度");
        before = fixture.Snapshot();
        Reject<InvalidOperationException>(() => Enter(at, maze: 3), "同日重进不能更换本队对手");
        That(fixture.Snapshot() == before, "路线冲突整笔回滚，不占attempt或替换旧实例");
        var retry = Enter(at, dungeon: 400000095);
        Reject<InvalidOperationException>(() => Return(abandoned, at), "旧退场角色版本必须拒绝");
        That(fixture.Scalar("SELECT state FROM account_tag_evildom_challenges") is 0L,
            "旧实例退出不能释放同日重试的新实例");
        fixture.Sql("CREATE TRIGGER reject_tag_return BEFORE UPDATE ON account_tag_evildom_challenges BEGIN SELECT RAISE(ABORT,'fixture'); END;");
        before = fixture.Snapshot();
        var changedPosition = retry.Character.Position with { X = checked((ushort)(retry.Character.Position.X + 1)) };
        Reject<SqliteException>(() => Return(retry, at, changedPosition), "退场末段SQL失败不能只保存位置或只释放挑战");
        That(fixture.Snapshot() == before, "退场失败位置、角色revision及路线状态全部回滚");
        fixture.Sql("DROP TRIGGER reject_tag_return");
        Return(retry, at);
        var tomorrow = TagEvildomPeriod.At(at.AddHours(1));
        That(fixture.Repository.ReadTagEvildomState(fixture.Owner, tomorrow).UsedThisWeek == 0 &&
            fixture.Scalar("SELECT state FROM account_tag_evildom_challenges") is 1L,
            "日界前明确退出不占次");
        config = fixture.Repository.CommitTagEvildomConfiguration(fixture.Owner, config, config.Reset(new HashSet<ushort>()), day);
        config = fixture.Repository.CommitTagEvildomConfiguration(fixture.Owner, config,
            config with { Teams = [new(first.Id, second.Id), default, default] }, day);
        That(new CharacterRepository(fixture.Database).ReadTagEvildomRoute(fixture.Owner, day.Day, 0) == 2 &&
            fixture.Repository.ReadTagEvildomState(fixture.Owner, day).Mode == 4, "重置编队与重开库仍恢复当天对手和最近实际入场难度");
        var disconnected = Enter(at);
        string afterEntry = fixture.Snapshot();
        var state = new CharacterRepository(fixture.Database).ReadTagEvildomState(fixture.Owner, tomorrow);
        That(state.UsedThisWeek == 1 && state.ProtectedMembers.Count == 0 && fixture.Snapshot() == afterEntry &&
            fixture.Repository.ReadTagEvildomState(fixture.Owner, tomorrow).UsedThisWeek == 1,
            "未完成跨日只读计算原周一次占用，不写假通关或重复累计");
        Return(disconnected, at.AddHours(1));
        That(fixture.Repository.ReadTagEvildomState(fixture.Owner, tomorrow).UsedThisWeek == 1,
            "日界后迟到退场不能撤销旧日未完成次数");
        That(fixture.Scalar("SELECT count(*) FROM account_tag_evildom_clearances") is 0L &&
            fixture.Scalar("SELECT count(*) FROM mailbox_messages") is 0L, "跨日占用不产生clearance、邮件或附件");
        var newDayEntry = Enter(at.AddHours(1), maze: 4);
        That(fixture.Repository.ReadTagEvildomRoute(fixture.Owner, tomorrow.Day, 0) == 4 &&
            fixture.Repository.ReadTagEvildomRoute(fixture.Owner, day.Day, 0) == 2, "新日可有新对手，不能改写旧日路线");
        Return(newDayEntry, at.AddHours(1));
        var sunday = DateTimeOffset.Parse("2026-11-02T05:29:59+08:00");
        Enter(sunday, maze: 1);
        That(fixture.Repository.ReadTagEvildomState(fixture.Owner, TagEvildomPeriod.At(sunday.AddHours(1))).UsedThisWeek == 0,
            "周一06点的旧周未完成尝试不侵占新周");
        before = fixture.Snapshot();
        Reject<InvalidOperationException>(() => Return(newDayEntry, at.AddHours(1)), "无位置变化的退场也拒绝旧角色版本");
        That(fixture.Snapshot() == before, "过期实例退场不影响当前挑战或存档");
    }
}
