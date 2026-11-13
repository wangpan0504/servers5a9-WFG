using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class ParticularHellEntryTests
{
    public static void Run(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters, ItemResources items,
        QuestCatalog quests, SkillLearningCatalog skills)
    {
        int checks = Count;
        var dungeons = Testing.SelfTestCatalogs.Dungeons(pvf);
        var rooms = Testing.SelfTestCatalogs.Rooms(pvf);
        var growth = Testing.SelfTestCatalogs.Growth(pvf, skills);
        var incounts = DungeonIncountCatalog.Load(pvf);
        var definition = dungeons.Get(316);
        That(definition.Mazes.Count > 0 && definition.Mazes.All(maze => maze.HellParty is null) &&
            !definition.IsCosmoDungeon && !definition.IsInfernoDungeon,
            "真实316是独立魔界裂缝，未声明可绕开普通入场计次的深渊派对路线");
        That(incounts.Find(316) is { CountKey: 316, Limit: 3, HasCountNotification: false } &&
            definition.RequiredItem is { Count: > 0, Flag: not 0 }, "真实魔界316三次额度与消耗型材料合同存在");
        var required = definition.RequiredItem!;
        var (done, position) = DungeonEntryCostTests.Selection(towns, 316);
        done.UnionWith(quests.All.Select(quest => quest.Id));
        byte difficulty = definition.TryGetDesignatedDifficulty(out byte designated) ? designated :
            (byte)Enumerable.Range(0, 5).First(value => definition.AllowsDifficulty((byte)value, false));
        var clock = new Clock(new DateTimeOffset(2026, 11, 10, 5, 59, 0, TimeSpan.FromHours(8)));
        using var f = new QuestSceneTests.Fixture(towns, characters, skills, growth, quests, false, 95, [.. done],
            position: position, items: [(required.ItemId, checked(required.Count * 5))], incounts: incounts, timeProvider: clock);
        var before = f.Read();
        That(f.Session.CaptureParticularHellCount() is { Total: 3, Used: 0 } && f.Session.BeginDungeonSelection() is not null,
            "新角色真实选图前显示3/3");
        That(f.Session.EnterFormalDungeon(316, 0, difficulty, dungeons, rooms, pieces: items.EpicPieces, hellParty: true) is null &&
            (f.Session.LastFormalDungeonRejection is "hell-party-area-unavailable" or "hell-party-entry-plan:hell-party-maze-unavailable") &&
            QuestCompletionRepositoryTests.Same(before, f.Read()) && f.Repository.LoadDungeonIncounts(f.Owner, before.Id).IsEmpty,
            "316伪造深渊模式被原资源准入拒绝，材料、疲劳、次数与尝试号均不变");
        f.Sql("CREATE TRIGGER particular_entry_fail AFTER UPDATE ON character_dungeon_state WHEN NEW.last_attempt>OLD.last_attempt BEGIN SELECT RAISE(ABORT,'particular entry rollback'); END");
        Reject<SqliteException>(() => Enter(f.Session), "魔界末段尝试号提交注入真实SQL故障");
        That(QuestCompletionRepositoryTests.Same(before, f.Read()) &&
            QuestCompletionRepositoryTests.Same(before, f.Session.Selected!) && f.Session.DungeonSnapshot is null &&
            f.Repository.LoadDungeonIncounts(f.Owner, before.Id).IsEmpty,
            "末段故障同时回滚材料、次数、疲劳、revision、预留键与尝试号，不安装实例");
        f.Sql("DROP TRIGGER particular_entry_fail");
        for (uint used = 1; used <= 3; used++)
        {
            if (used > 1) That(f.Session.BeginDungeonSelection() is not null, "退出后沿原选图入口重新准备");
            var entered = Enter(f.Session);
            That(entered is { ChannelConsumedSlots.IsEmpty: false } && f.Session.CaptureParticularHellCount() is { } view &&
                view.Total == 3 && view.Used == used && Materials(f.Read()) == required.Count * (5 - used),
                "每次正式入场原事务扣一次材料与角色次数，返回真实材料槽及已用次数");
            var after = f.Read();
            That(Enter(f.Session) is null && QuestCompletionRepositoryTests.Same(after, f.Read()) &&
                f.Session.CaptureParticularHellCount()!.Used == used, "重复入场不重扣材料/次数");
            That(f.Session.CompleteFormalDungeonLoading() is not null && f.Session.ReturnFromFormalDungeon() is not null,
                "原加载及退出流程不返还已扣次数");
        }
        That(f.Session.BeginDungeonSelection() is not null, "用尽次数仍可打开选图查看真实剩余");
        var exhausted = f.Read();
        That(Enter(f.Session) is null && f.Session.LastFormalDungeonRejection == "dungeon-incount-exhausted" &&
            QuestCompletionRepositoryTests.Same(exhausted, f.Read()) && Materials(exhausted) == required.Count * 2,
            "尚有两份材料时第4次仍拒绝，不扣材料、不推进疲劳及尝试号");
        var stale = f.Session.CaptureParticularHellCount()!;
        var second = f.Repository.Create(f.Owner, new NewCharacter("RiftSecond", position, position)
        { Level = 95, AcceptedStoryLevel = 95, CompletedQuests = [.. done], Skills = before.Skills, InitialLearning = before.Learning });
        f.Session.ReturnToRoster(); f.Session.Select(second.Slot, resumeTown: true); f.Session.MarkReady();
        That(f.Session.CaptureParticularHellCount() is { Total: 3, Used: 0 } && !f.Session.CanPublishParticularHellCount(stale),
            "同账号B角色仍为3/3，旧选角通知失效，不沿用A的0/3");
        f.Session.ReturnToRoster();
        var reopened = new CharacterRepository((string)f.Query("SELECT file FROM pragma_database_list WHERE name='main'")!, quests);
        var resumed = new GameSession(reopened, towns, f.Owner, questCatalog: quests, timeProvider: clock, incounts: incounts);
        try
        {
            resumed.Select(0, resumeTown: true); resumed.MarkReady();
            That(resumed.CaptureParticularHellCount() is { Total: 3, Used: 3 }, "重建仓储与会话后A仍从原日账本恢复0/3");
            var previousDay = resumed.CaptureParticularHellCount()!;
            clock.Now = clock.Now.AddMinutes(1);
            That(resumed.CaptureParticularHellCount() is { Total: 3, Used: 0 } && !resumed.CanPublishParticularHellCount(previousDay),
                "UTC+8 06:00同角色恢复3/3，昨日通知不能跨界发布");
            That(resumed.BeginDungeonSelection() is not null && Enter(resumed) is not null &&
                resumed.CaptureParticularHellCount() is { Used: 1 } && Materials(f.Read()) == required.Count,
                "新业务日可再次入场，仅扣新日一次次数与一份材料");
        }
        finally { resumed.End(); }
        Console.WriteLine($"PASS {Count - checks} 魔界316角色隔离、日切、持久恢复、耗尽拒绝与末段回滚断言");

        FormalDungeonReceipt? Enter(GameSession session) => session.EnterFormalDungeon(316, 0, difficulty, dungeons, rooms,
            pieces: items.EpicPieces);
        long Materials(Character character) => character.Inventory.Items.Where(item => item.Core.ItemId == required.ItemId).Sum(item => (long)item.Core.Count);
    }

    sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
    }
}
