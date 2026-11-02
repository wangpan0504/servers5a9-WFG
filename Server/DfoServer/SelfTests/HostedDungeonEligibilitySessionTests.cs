using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class HostedDungeonEligibilitySessionTests
{
    public static void Run(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters)
    {
        int checks = Count;
        var learning = Testing.SelfTestCatalogs.SkillLearning(pvf);
        var catalog = Testing.SelfTestCatalogs.Dungeons(pvf);
        var rooms = Testing.SelfTestCatalogs.Rooms(pvf);
        var quests = Testing.SelfTestCatalogs.Quests(pvf);
        DefaultOffUnchanged();
        OrdinaryMissingProfile();
        QuestRouteRejected();
        Console.WriteLine($"PASS {Count - checks} hosted dungeon eligibility session assertions");

        void DefaultOffUnchanged()
        {
            using var f = new Fixture("HostedOff", towns, characters, learning, quests, ordinaryMaze: true);
            f.SelectEntry();
            var entry = f.Session.EnterFormalDungeon(3, 0, 0, catalog, rooms) ?? throw new InvalidOperationException("普通入场失败");
            That(entry.Entry.IsOrdinary, "默认关闭样本使用普通零任务图");
            long gold = f.Read().Inventory.Gold;
            var loaded = f.Session.CompleteFormalDungeonLoading() ?? throw new InvalidOperationException("普通加载失败");
            That(f.Session.LastHostedDungeonEligibility is null, "默认关闭不应用探测结果");
            That(f.Session.HostedDungeonEligibilityTask.Result is null, "默认关闭不调度探测任务");
            That(loaded.Snapshot.Identity.InstanceId == entry.Snapshot.Identity.InstanceId, "默认关闭不替换 Native 实例");
            That(f.Read().Inventory.Gold == gold, "默认关闭加载不改金币");
        }

        void OrdinaryMissingProfile()
        {
            using var f = new Fixture("HostedOn", towns, characters, learning, quests, ordinaryMaze: true);
            f.Session.AttachHostedDungeonEligibility(new HostedDungeonEligibilityCoordinator(new HostedDungeonEligibilityOptions { Enabled = true }));
            f.SelectEntry();
            That(f.Session.EnterFormalDungeon(3, 0, 0, catalog, rooms) is not null, "开启后普通入场仍走原 Native 费用合同");
            long gold = f.Read().Inventory.Gold;
            var loaded = f.Session.CompleteFormalDungeonLoading() ?? throw new InvalidOperationException("开启后普通加载失败");
            var result = f.Session.HostedDungeonEligibilityTask.GetAwaiter().GetResult();
            That(result is { CanFight: false, Reason: HostedDungeonEligibilityRules.ReasonMissingCombatProfile },
                "普通房间缺少 CombatProfile 时明确拒绝");
            That(ReferenceEquals(result, f.Session.LastHostedDungeonEligibility), "探测结果回写到当前会话");
            That(loaded.Entry.IsOrdinary && f.Read().Inventory.Gold == gold, "探测不改已完成入场后的金币");
            That(f.Session.DungeonSnapshot is { } snapshot && snapshot.Identity.InstanceId == loaded.Snapshot.Identity.InstanceId,
                "探测不替换 Native 实例身份");
        }

        void QuestRouteRejected()
        {
            using var f = new Fixture("HostedQuest", towns, characters, learning, quests);
            f.Session.AttachHostedDungeonEligibility(new HostedDungeonEligibilityCoordinator(new HostedDungeonEligibilityOptions { Enabled = true }));
            f.SelectEntry();
            var entry = f.Session.EnterFormalDungeon(3, 3145, 0, catalog, rooms) ?? throw new InvalidOperationException("任务路线入场失败");
            That(!entry.Entry.IsOrdinary, "3145 为任务路线");
            That(f.Session.CompleteFormalDungeonLoading() is not null, "任务路线加载仍由原 Native 路径完成");
            var result = f.Session.HostedDungeonEligibilityTask.GetAwaiter().GetResult();
            That(result is { CanFight: false, Reason: HostedDungeonEligibilityRules.ReasonNotOrdinary },
                "非普通请求拒绝探测且 CanFight 为 false");
        }
    }

    sealed class Fixture : IDisposable
    {
        readonly string root = Path.Combine(Path.GetTempPath(), "S5A9-hosted-eligibility-" + Guid.NewGuid().ToString("N"));
        public CharacterRepository Repository { get; }
        public AccountOwners Owners { get; } = new();
        public AccountOwners.Lease Owner { get; }
        public GameSession Session { get; }
        public Character Created { get; }

        public Fixture(string name, TownCatalog towns, CharacterCatalog characters, SkillLearningCatalog learning,
            DfoServer.GameWorld.Quests.QuestCatalog quests, bool ordinaryMaze = false)
        {
            Directory.CreateDirectory(root);
            string path = Path.Combine(root, "game.db");
            Repository = new(path, quests);
            Owner = Owners.Acquire("hosted-eligibility", () => { });
            Session = new(Repository, towns, Owner, questCatalog: quests);
            Created = Repository.Create(Owner, CharacterCreation.Prepare(name, characters.Get(0), towns.Room, towns.InitialReturn, learning.Get)
                with
                {
                    ActiveMainQuests = ordinaryMaze ? [] : [new(0, 3145, 1)],
                    CompletedQuests = ordinaryMaze ? [3145] : []
                });
        }

        public void SelectEntry()
        {
            Session.Select(Created.Slot);
            Session.MarkReady();
            That(Session.LeaveRoom() && Session.Travel(new(38, 2, 1085, 249, 5, 38, 0, 0)), "资格探测夹具可到达普通入口");
        }

        public Character Read() => Repository.LoadCharacter(Owner, Created.Slot);

        public void Dispose()
        {
            Session.End();
            Owner.Dispose();
            if (Directory.Exists(root)) TestFiles.DeleteDirectory(root, recursive: true);
        }
    }
}
