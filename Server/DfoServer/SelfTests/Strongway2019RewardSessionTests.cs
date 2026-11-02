using System.Collections.Immutable;
using System.Reflection;
using DfoServer.Game.Accounts;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Events;
using DfoServer.Game.Session;
using DfoServer.Game.Strongway2019;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class Strongway2019RewardSessionTests
{
    public static void RunResources(TownCatalog towns, ItemResources items)
    {
        int before = Count;
        string directory = Path.Combine(Path.GetTempPath(), "s5a9-strongway-reward-session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var repository = new CharacterRepository(Path.Combine(directory, "game.db"));
            var owners = new AccountOwners();
            using var owner = owners.Acquire("sw-reward-session", () => { });
            var clock = new Clock(DateTimeOffset.Parse("2026-11-04T06:00:00+08:00"));
            var role = repository.Create(owner, new("SWReward", towns.Room, towns.InitialReturn)
                { Level = 95, DungeonState = new(256, 0, 0, 0, 0) });
            var session = new GameSession(repository, towns, owner, timeProvider: clock);
            session.Select(role.Slot); session.MarkReady();
            var expected = session.Selected!;
            var map = new DungeonMap("boss", new(0, 0), 1, "map/strongway-owner-fixture.map");
            var maze = new DungeonMaze(0, 1, 1, "strongway-owner-fixture", [map.Position], [map.Position], [map], []);
            var entry = new DungeonEntryPlan(expected.Id, expected.Revision + 1, 5359, 0, byte.MaxValue, 0, maze, map, map)
                { DisableMonsterAndClearExperience = true, DisableFatigueConsumption = true, DisableGiveupPenalty = true };
            var monster = new DungeonRoomMonster(1, [1, 0, 95, 100, 100, 0, 1, 1], "[fixed]", "[boss]", "monster/strongway-fixture.mob");
            var monsters = DungeonMonsterPlan.FromRoom(new(map, [monster], [], []), expected.Id, 256);
            var route = new DungeonFixedRoute([monsters], []);
            var authority = new NativeDungeonCombatAuthority();
            That(SinglePlayerDungeonRun.TryCreate(entry, expected.DungeonState!.LastAttempt + 1, session.SelectionEpoch, route, authority, out var created, out _) &&
                authority.BindTo(created!), "奖励Session测试使用实际Run及原生死亡权威");
            var run = created!;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            object Nested(string name, params object?[] arguments) => Activator.CreateInstance(
                typeof(GameSession).GetNestedType(name, BindingFlags.NonPublic)!, flags, null, arguments, null)!;
            var dungeon = Nested("FormalDungeon", entry, route, run, authority, 1u, new DungeonGroundState(run, route.Rooms, [], ""),
                new Dictionary<int, DungeonExplicitDropPool>(), (Func<int, int>)(_ => throw new InvalidOperationException("无普通掉落")),
                null, ImmutableDictionary<ushort, DungeonDropGenerationPlan>.Empty);
            var prepared = Nested("PreparedFormalDungeon", expected, session.SelectionEpoch, dungeon, 300u);
            That(owner.Execute(() => typeof(GameSession).GetMethod("InstallFormalDungeon", flags)!.Invoke(session, [prepared])) is not null &&
                session.CompleteFormalDungeonLoading() is not null, "合成私有规划仍沿实际入场事务预留尝试、安装和确认加载");
            var scene = session.CurrentFormalDungeonScene()!;
            var issued = authority.ValidateAndIssue(run, run.Scene.Identity, FormalDungeonSessionTests.Observation(scene, monsters.Monsters.Single())).Death!;
            var death = run.PrepareConfirmedDeath(issued).Pending!;
            That(run.ConfirmCommittedDeath(death).ClearFact is { } fact && ReferenceEquals(fact, run.ClearFact),
                "实际权威死亡提交产生原Run唯一终点事实");
            // 合成规划的Session阶段由测试同步到真实Run终局；不声称PVF正式准入或网络入图通过。
            typeof(GameSession).GetMethod("SetState", flags)!.Invoke(session, [SessionPhase.Settlement]);
            That(session.PrepareStrongway2019Reward() is null, "普通5359实例未附首期准入不能仅凭地下城ID领奖");
            var admission = new Strongway2019Admission(expected, Strongway2019Mode.Simple, 5359, 1, 4000,
                new ServerEventSchedule(312, 1, OverrideActive: true));
            // 安装会with复制并rebased正式状态；准入必须附在Session当前保存的实例上。
            var installed = typeof(GameSession).GetProperty("formalDungeon", flags)!.GetValue(session)!;
            That(!ReferenceEquals(installed, dungeon) &&
                ReferenceEquals(installed.GetType().GetProperty("Run")!.GetValue(installed), run),
                "安装后的正式状态是重基副本，仍持原Run引用");
            installed.GetType().GetProperty("Strongway2019Admission")!.SetValue(installed, admission);
            var pending = session.PrepareStrongway2019Reward();
            That(pending is not null && ReferenceEquals(pending.Run, run) && ReferenceEquals(pending.Fact, run.ClearFact) &&
                ReferenceEquals(pending.Character, session.Selected), "正式Session冻结已确认真终点、当前角色版本及原准入");
            var store = new FailOnce(repository);
            Reject<IOException>(() => session.CommitStrongway2019Reward(pending!, items.Items, store), "奖励事务失败保留原冻结规划供重试");
            var frozen = store.Last;
            clock.Now = clock.Now.AddDays(1);
            var receipt = session.CommitStrongway2019Reward(pending!, items.Items, store);
            That(receipt is { Commit.Granted: true, Commit.MailId: > 0 } && ReferenceEquals(store.Last, frozen) &&
                receipt.Preparation.Created == pending!.Created && frozen!.Item.Core.ItemId == 490012735 && frozen.Item.Core.Count == 1,
                "次日重试复用同一完整735附件、原归属日期和计划引用，不重新materialize");
            That(session.CanPublishStrongway2019Reward(receipt!) &&
                ReferenceEquals(session.CommitStrongway2019Reward(pending!, items.Items, store), receipt) && store.Calls == 2,
                "已提交Session重复调用复用原回执，不重复存储或投给后续角色");
            That(session.CommitStrongway2019Reward(pending! with { }, items.Items, store) is null,
                "复制准备对象不能冒充该实例被冻结的领奖准备");
            session.ReturnToRoster();
            That(!session.CanPublishStrongway2019Reward(receipt!) && session.CommitStrongway2019Reward(pending!, items.Items, store) is null && store.Calls == 2,
                "回到角色列表后旧选角epoch不能提交或发布首期奖励");
            Console.WriteLine($"PASS {Count - before} 首期奖励Session准入、真终点、失败冻结重试及旧epoch断言");
        }
        finally { TestFiles.DeleteDirectory(directory, true); }
    }

    sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    sealed class FailOnce(IStrongway2019RewardStore inner) : IStrongway2019RewardStore
    {
        public int Calls { get; private set; }
        public Strongway2019RewardPlan? Last { get; private set; }
        public Strongway2019RewardState ReadStrongway2019RewardState(AccountOwners.Lease owner, Strongway2019RewardPeriod period, Strongway2019RewardBucket bucket) =>
            inner.ReadStrongway2019RewardState(owner, period, bucket);
        public Strongway2019RewardCommit CommitStrongway2019Reward(AccountOwners.Lease owner, Strongway2019RewardPlan plan)
        {
            Last = plan;
            if (++Calls == 1) throw new IOException("fixture reward retry");
            return inner.CommitStrongway2019Reward(owner, plan);
        }
    }
}
