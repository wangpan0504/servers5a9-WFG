using System.Collections.Immutable;
using System.Reflection;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Session;
using DfoServer.Game.TagEvildom;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Quests;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;
using DfoServer.Testing.Inventory;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class SessionPlanningTests
{
    static async Task TagEvildomInstalledRunAsync()
    {
        using var fixture = new Fixture();
        var first = fixture.Repository.Create(fixture.Owner, new("RunPrimary", fixture.Towns.Room, fixture.Towns.InitialReturn)
            { Level = 95, InitialLearning = fixture.Read().Learning, DungeonState = new(256, 0, 0, 0, 0) });
        var second = fixture.Repository.Create(fixture.Owner, new("RunStandby", fixture.Towns.Room, fixture.Towns.InitialReturn)
            { Level = 95, InitialLearning = first.Learning });
        var clock = new DailyBoosterTestClock(DateTimeOffset.UnixEpoch.AddSeconds(1_000));
        var session = new GameSession(fixture.Repository, fixture.Towns, fixture.Owner,
            entry: new(new(2, 46), 53, "[evildom_gathering]"), timeProvider: clock, questCatalog: new QuestCatalog([]));
        session.Select(first.Slot); session.MarkReady();
        var expected = session.Selected!;
        var map = new DungeonMap("boss", new(0, 0), 1, "map/tag-owner-fixture.map");
        var maze = new DungeonMaze(0, 1, 1, "tag-owner-fixture", [map.Position], [map.Position], [map], []);
        var entry = new DungeonEntryPlan(expected.Id, expected.Revision + 1, 9608, 0, byte.MaxValue, 0, maze, map, map)
            { DisableMonsterAndClearExperience = true, DisableFatigueConsumption = true, DisableGiveupPenalty = true };
        var monster = new DungeonRoomMonster(1, [1, 0, 95, 100, 100, 0, 1, 1], "[fixed]", "[boss]", "monster/tag-fixture.mob");
        var plan = DungeonMonsterPlan.FromRoom(new(map, [monster], [], []), expected.Id, 256);
        var route = new DungeonFixedRoute([plan], []);
        var authority = new NativeDungeonCombatAuthority();
        That(SinglePlayerDungeonRun.TryCreate(entry, 1, session.SelectionEpoch, route, authority, out var created, out _) &&
            authority.BindTo(created!), "合成TAG领域夹具仍使用原生死亡核验权威");
        var run = created!;
        var ground = new DungeonGroundState(run, route.Rooms, [], "");
        // 与既有Session领域测试一致：仅合成私有规划，实际安装仍走原attempt/key事务。
        // 不调用或放宽生产TAG准入，不能将这个夹具称为真实资源入图验收。
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        object Nested(string name, params object?[] arguments) => Activator.CreateInstance(
            typeof(GameSession).GetNestedType(name, BindingFlags.NonPublic)!, instance, null, arguments, null)!;
        var dungeon = Nested("FormalDungeon", run.Entry, route, run, authority, 1u, ground,
            new Dictionary<int, DungeonExplicitDropPool>(), (Func<int, int>)(_ => throw new InvalidOperationException("TAG不能抽取普通掉落")),
            null, ImmutableDictionary<ushort, DungeonDropGenerationPlan>.Empty);
        var prepared = Nested("PreparedFormalDungeon", expected, session.SelectionEpoch, dungeon, 300u);
        That(fixture.Owner.Execute(() => typeof(GameSession).GetMethod("InstallFormalDungeon", instance)!.Invoke(session, [prepared])) is not null &&
            run.InstallTagEvildom(new(session.Selected!, second, 1)) && session.CompleteFormalDungeonLoading() is not null,
            "合成实例实际提交预留后安装双槽，再沿Session确认加载");
        var primary = session.Selected!;
        string before = fixture.Snapshot();
        That(session.ReportFormalCharacterDeath() is null && session.CurrentDungeonDeathReturn() is null &&
            session.ReviveFormalCharacter(primary.Id) is null, "已装TAG拒绝普通玩家死亡、10秒回城和复活币，不建平行CharacterDead");
        var syntheticItem = new InventoryItem(777, new(InventoryListType.Main, 0), new(1, ItemKind.Material, 1));
        var available = new DungeonGroundObject(Guid.NewGuid(), run.Snapshot.Identity,
            new(primary.Id, 301, map, 0, 0, 0), syntheticItem.Core)
            { ResourceIdentity = "fixture", InventoryOrigin = new(syntheticItem, 1, primary.Revision) };
        That(ground.PrepareInventoryDrop(available) && ground.ConfirmInventoryDrop(available), "合成可用地面对象，拾取反例不能仅依赖找不到key");
        // null目录是不可访问哨兵：这些已安装TAG用例必须在规划/目录/库存服务调用之前拒绝。
        That(session.PickFormalDungeonItem(301, null!, null!).Outcome == DungeonRunOutcome.Rejected &&
            ground.Snapshot.Items.Single().Status == DungeonGroundStatus.Available &&
            session.DiscardFormalDungeonItem(new(0, 0, 0, 0, 1, 0), null!).Error == 19 &&
            session.ObserveFormalPassiveDeath(1, true, new string('A', 64), null!) is null,
            "已装TAG不能普通拾取、丢弃或按物件事件规划任务资产");
        var scene = session.CurrentFormalDungeonScene()!;
        var observation = FormalDungeonSessionTests.Observation(scene, plan.Monsters.Single());
        var issued = authority.ValidateAndIssue(run, run.Snapshot.Identity, observation).Death!;
        var pending = run.PrepareConfirmedDeath(issued).Pending!;
        var reward = new DungeonExperienceReward("fixture", DungeonKillExperienceCatalog.ResourcePath, 1, 95, 0);
        Reject<InvalidOperationException>(() => session.CommitDungeonGrowth(session.SelectionEpoch, primary, run, pending, reward, null!),
            "公开成长入口在调用存储/成长目录前拒绝已装TAG");
        var death = session.ObserveFormalDungeonDeath(observation, null!, null!);
        That(death.Outcome == DungeonRunOutcome.Accepted && death.Reward is null && death.ClearFact is null && death.Drops.IsEmpty &&
            run.ClearFact is null && run.Phase == SinglePlayerDungeonPhase.Dungeon &&
            session.ObserveFormalDungeonDeath(observation, null!, null!).Outcome == DungeonRunOutcome.Duplicate &&
            ReferenceEquals(primary, session.Selected) && fixture.Snapshot() == before,
            "TAG实际Session死怪保留核验/去重事实，但不查询经验、抽普通loot、写任务/成长/角色revision");

        using var log = new ServerLog(Path.GetDirectoryName(fixture.Database)!);
        using var stop = new CancellationTokenSource();
        using var stream = new MemoryStream();
        var published = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        int sent = 0;
        await using var sender = new GameConnectionSender(fixture.Owner, stream, stop, 0, (_, opcode, _, body, _) =>
        {
            That(opcode == 1676, "实际TAG到期投影只发1676，不发普通复活/奖励");
            Interlocked.Increment(ref sent);
            published.TrySetResult(body.ToArray());
        });
        var context = new GameConnectionContext(new(1, System.Net.IPAddress.Loopback, stream, stop), fixture.Owner, session, sender);
        await using var timer = new TagEvildomRevivalProjection(context, log, clock);
        var dying = session.ReportFormalTagEvildomDeath(0)!;
        var scheduled = session.CurrentTagEvildomRevival()!;
        That(dying.Result.SwitchTo == 1 && !dying.Result.DefeatAcknowledgement && session.CanPublishTagEvildomLife(dying),
            "实际Session只改变Run当前槽，生成带owner/epoch的非终局回执");
        await timer.RefreshAsync();
        await timer.RefreshAsync();
        await clock.WaitForOneShotTimersAsync(1);
        clock.Advance(TimeSpan.FromSeconds(12));
        Bytes(await published.Task.WaitAsync(TimeSpan.FromSeconds(5)), [0, 0, 0, 0], "实际调度和发送泵在12秒发准确ordinal0");
        await timer.RefreshAsync();
        That(sent == 1 && session.CurrentTagEvildomRevival() is null && !session.CanPublishTagEvildomLife(dying) &&
            session.ReviveFormalTagEvildom(scheduled, clock.GetUtcNow().ToUnixTimeMilliseconds()) is null,
            "重复Refresh无第二个任务，旧死亡回执和重复到期提交均失效");
        session.ReportFormalTagEvildomDeath(1);
        var oldEpoch = session.CurrentTagEvildomRevival()!;
        await timer.RefreshAsync();
        await clock.WaitForOneShotTimersAsync(1);
        session.ReturnToRoster();
        await timer.RefreshAsync();
        await clock.WaitForOneShotTimersAsync(0);
        clock.Advance(TimeSpan.FromSeconds(12));
        That(sent == 1 && run.TagEvildomLife is { Cancelled: true } &&
            session.ReviveFormalTagEvildom(oldEpoch, clock.GetUtcNow().ToUnixTimeMilliseconds()) is null && fixture.Snapshot() == before,
            "真实小退取消已注册定时器，旧epoch不向新角色发复活，也不改任何数据库行");
        session.End();
    }
}
