using static DfoServer.Testing.TestCheck;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Towns;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using PvfLib;

namespace DfoServer;

internal static class LostLandSessionTests
{
    public static void Run(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters)
    {
        int checks = Count;
        var time = new LostLandTestClock();
        var dungeons = SelfTestCatalogs.Dungeons(pvf);
        var rooms = SelfTestCatalogs.Rooms(pvf);
        var incounts = DungeonIncountCatalog.Load(pvf);
        using var f = new QuestSceneTests.Fixture(towns, characters, SelfTestCatalogs.SkillLearning(pvf),
            SelfTestCatalogs.Growth(pvf), SelfTestCatalogs.Quests(pvf), false, level: 95, job: 4,
            completed: [3156], position: new(40, 0, 845, 155), timeProvider: time, incounts: incounts);
        var session = f.Session;
        That(Enter() is null && session.LastFormalDungeonRejection == "lost-land-selection-mismatch", "直接16不能绕过乔安选择身份");
        That(session.BeginDungeonSelection() is not null, "乔安建立本角色本次选图身份");
        string before = State();
        f.Sql("CREATE TRIGGER lost_land_entry_failure BEFORE UPDATE ON character_dungeon_state BEGIN SELECT RAISE(ABORT,'lost land entry rollback'); END");
        bool failed = false;
        try { Enter(); } catch (SqliteException) { failed = true; }
        That(failed && State() == before && session.DungeonSnapshot is null && session.Phase == SessionPhase.MapSelect,
            $"入场SQL失败回滚角色、次数、尝试号和对象key，不安装半个正式实例：failed={failed},before={before},after={State()},phase={session.Phase},reason={session.LastFormalDungeonRejection}");
        f.Sql("DROP TRIGGER lost_land_entry_failure");
        var entered = Enter();
        That(entered is { Entry.LostLand: not null, Entry.Maze.StaticTopology.Rooms.Count: 87, Entry.Maze.StaticTopology.Links.Count: 81 },
            "匹配PVF整条87房正式资源准备成功，保留81条普通连接");
        string installed = State();
        That(Enter() is null && State() == installed && Convert.ToInt64(f.Query("SELECT daily_used FROM character_dungeon_incount WHERE dungeon=4000")) == 1,
            "重复16不再次扣次或递增尝试，沿既有角色次数事务扣一次");
        time.Advance(TimeSpan.FromHours(1));
        var loaded = session.CompleteFormalDungeonLoading()!;
        var revision = session.PlanAndCommitInventory(character => new(true, 0,
            new InventoryPlan(character.Inventory, character.Inventory with { Gold = character.Inventory.Gold + 1 }, "test_lost_land_revision")),
            requireTown: false, requiredPhase: SessionPhase.Dungeon);
        That(revision.Accepted && !session.CanPublishFormalDungeon(loaded), "正常资产提交使旧加载快照失效");
        var timer = session.StartedLostLandTimer(loaded) ?? throw new InvalidOperationException("资产revision变化后计时任务身份丢失");
        That(loaded.LostLandStarted && timer.Remaining.TotalSeconds == 600 && timer.OpaqueValue == 1200 &&
            session.CompleteFormalDungeonLoading() is null, "首次37才启动600秒，重复37不成功或重置");
        That(session.TickLostLandTimer(timer) is { Expired: false } && session.Phase == SessionPhase.Dungeon,
            "提前计时唤醒不结束实例");
        time.Advance(TimeSpan.FromSeconds(600));
        var expired = session.TickLostLandTimer(timer)!;
        That(expired.Expired && session.DungeonSnapshot!.Phase == SinglePlayerDungeonPhase.TimedOut &&
            !session.CanPublishLostLandTimer(timer) && session.CanPublishLostLandTimer(expired), "到期冻结原Run并使开始通知失效");
        That(session.ReturnFromExpiredLostLand(expired) is { Character.Position.Town: 40, Character.Position.Area: 0 } &&
            session.Phase == SessionPhase.Town && session.DungeonSnapshot is null &&
            session.ReturnFromExpiredLostLand(expired) is null && session.ReturnFromFormalDungeon() is null,
            "自然到期沿原回城一次退出，重复到期和42不重复保存");
        That(Convert.ToInt64(f.Query("SELECT count(*) FROM character_dungeon_settlement_receipts")) == 0,
            "限时退出不借普通结算产生奖励回执");
        session.BeginDungeonSelection();
        That(Enter() is null && session.LastFormalDungeonRejection == "dungeon-incount-exhausted",
            "资源每日一次用尽后仍拒绝再入场，计时退出不恢复次数");
        time.Now = time.Now.AddDays(1);
        That(session.BeginDungeonSelection() is not null && Enter() is not null, "次日新选择可创建独立新实例");
        var second = session.CompleteFormalDungeonLoading()!;
        var secondTimer = session.StartedLostLandTimer(second)!;
        That(session.TickLostLandTimer(timer) is null && session.ReturnFromExpiredLostLand(expired) is null &&
            session.CanPublishLostLandTimer(secondTimer), "旧实例回调不得结束新实例");
        That(session.ReturnFromFormalDungeon() is not null && session.TickLostLandTimer(secondTimer) is null,
            "主动退出使原计时回调失效");
        time.Now = time.Now.AddDays(1);
        session.BeginDungeonSelection(); Enter();
        var thirdTimer = session.StartedLostLandTimer(session.CompleteFormalDungeonLoading()!)!;
        session.ReturnToRoster();
        That(session.TickLostLandTimer(thirdTimer) is null && !session.CanPublishLostLandTimer(thirdTimer), "换角使旧计时失效");
        Console.WriteLine($"PASS {Count - checks} 遗忘之地正式Session次数事务、600秒和退出生命周期断言（非客户端验收）");

        FormalDungeonReceipt? Enter() => session.EnterFormalDungeon(4000, 0, 0, dungeons, rooms);
        string State() => $"{f.Query("SELECT printf('%d|%d|%d',c.revision,s.last_attempt,s.next_object_key) FROM characters c JOIN character_dungeon_state s ON s.character_id=c.id")};" +
            $"{f.Query("SELECT coalesce(sum(daily_used),0) FROM character_dungeon_incount WHERE dungeon=4000")};" +
            $"{f.Query("SELECT coalesce(sum(used),0) FROM account_dungeon_incount WHERE dungeon=4000")}";
    }
}
