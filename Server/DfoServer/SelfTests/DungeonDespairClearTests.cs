using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Titles;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Quests;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class DungeonDespairClearTests
{
    public static void Run(TcpTestResources resources)
    {
        int before = Count;
        var dungeons = SelfTestCatalogs.Dungeons(resources.Archive);
        var rooms = SelfTestCatalogs.Rooms(resources.Archive);
        var tower = TowerOfDespairCatalog.Load(resources.Archive);
        var growth = SelfTestCatalogs.Growth(resources.Archive);
        var titles = new TitleCatalog(resources.Archive, resources.Quests);
        var items = resources.ItemResources;
        var achievements = new TitleAchievementService(titles, resources.Quests, items.Items, items.GoldLimits, items.EpicPieces);
        using var fixture = new QuestSceneTests.Fixture(resources.Towns, resources.Characters, resources.Learning, growth,
            resources.Quests, false, 70);
        var repo = fixture.Repository;
        var character = fixture.Read();
        const int day = 739911;
        var floor = tower.Floors[0];
        var definition = dungeons.Get(floor.DungeonId);
        var maze = dungeons.ResolveOrdinaryMaze(definition, definition.Mazes.Single());
        ushort key = 256;
        var plans = new List<DungeonMonsterPlan>();
        foreach (var map in maze.MapSpecification!)
        {
            var plan = DungeonMonsterPlan.FromRoom(rooms.Get(map, requirePassiveDefinitions: false), character.Id, key, definition.BasisLevel);
            plans.Add(plan);
            key = checked((ushort)plan.NextObjectKey);
        }
        var entry = new DungeonEntryPlan(character.Id, character.Revision + 1, (uint)floor.DungeonId, 0, byte.MaxValue, 0,
            maze, maze.MapSpecification.Single(map => map.Position == maze.Starts.Single()),
            maze.MapSpecification.Single(map => map.Position == maze.Bosses.Single()));
        var authority = new Authority();
        That(SinglePlayerDungeonRun.TryCreate(entry, character.DungeonState!.LastAttempt + 1, 1,
            new(plans, maze.StaticTopology.Links!.Select(link => new DungeonRouteConnection(link.First, link.Second)).ToArray()),
            authority, out var run, out _), "绝望实际首层演员与路线建立原实例");
        var admission = DungeonDespairEntryPolicy.Prepare(11008, character, day, new(), new(0, 0), tower, definition, 0)!.Entry;
        character = repo.CommitDungeonEntry(fixture.Owner, character, key,
            new(null, 0, run!.Snapshot.Identity, entry.StartMap.Id) { DungeonId = floor.DungeonId, Special = admission }).Character;
        var empty = new InventoryPlan(character.Inventory, character.Inventory, "despair_clear_reward");
        That(run.PrepareDespairClear(character, admission, day, 1000, empty) is null,
            "没有实际清图不能预先推进绝望楼层");
        run.CompleteLoading(run.Snapshot.Identity, Guid.NewGuid());
        foreach (var actor in plans.Single().Monsters.Where(actor => actor.CanReportDeath))
            run.AcceptConfirmedDeath(authority.Death(run.Snapshot.Identity, actor.ObjectKey));
        That(run.ClearFact is { ConfirmedDeathEvents.Count: > 0 }, "实际首层敌对APC死亡后建立通关事实");
        int associated = 0;
        foreach (var row in tower.Floors.Where(row => row.TitleQuestId > 0))
        {
            var checkedTitle = achievements.DespairClear(character, run.ClearFact! with { DungeonId = (uint)row.DungeonId }, row, DateTimeOffset.UtcNow);
            That(checkedTitle is { Accepted: true, Plan: { } title } && title.Completed.SequenceEqual(new[] { row.TitleQuestId }),
                "绝望称号字段逐层对应真实子类型6任务、奖励与称号簿：" + row.Layer);
            associated++;
        }
        That(associated == 49 && !achievements.Trigger(character, new(floor.TitleQuestId, new(1, 0, 0)), DateTimeOffset.UtcNow).Accepted &&
            !achievements.DespairClear(character, run.ClearFact!, tower.Floors[2], DateTimeOffset.UtcNow).Accepted,
            "全部49项有真实称号关联，客户端计数和错误楼层不能代替原实例通关");
        var titlePlan = achievements.DespairClear(character, run.ClearFact!, floor, DateTimeOffset.UtcNow).Plan!;
        // 仅验证事务：123金币是合成测试资产，不表示任何绝望楼层的正式奖励数量。
        var inventory = new InventoryPlan(character.Inventory, titlePlan.Inventory.After with { Gold = character.Inventory.Gold + 123 },
            "despair_clear_reward", new(character));
        var pending = run.PrepareDespairClear(character, admission, day, 1000, inventory, titlePlan)!;
        That(pending is not null && run.HasPendingSettlement &&
            ReferenceEquals(run.PrepareDespairClear(character, admission, day, 1000, inventory, titlePlan), pending),
            "绝望通关冻结原入场、资产和称号，重复准备沿用同一事件");
        fixture.Sql("UPDATE dungeon_despair_progress SET next_layer=2 WHERE character_id=$p0", character.Id);
        Reject<InvalidOperationException>(() => repo.CommitDungeonDespairClear(fixture.Owner, run, pending!),
            "通关准备后持久楼层变化必须拒绝旧结果");
        That(fixture.Read().Revision == character.Revision && fixture.Read().Inventory.Gold == character.Inventory.Gold &&
            !fixture.Read().TitleProgress.ContainsKey(floor.TitleQuestId), "楼层版本冲突不提交任何奖励或称号");
        fixture.Sql("UPDATE dungeon_despair_progress SET next_layer=1 WHERE character_id=$p0", character.Id);
        fixture.Sql("CREATE TRIGGER fail_despair_clear BEFORE INSERT ON dungeon_despair_clear_receipts BEGIN SELECT RAISE(ABORT,'test rollback'); END");
        try
        {
            Reject<SqliteException>(() => repo.CommitDungeonDespairClear(fixture.Owner, run, pending!),
                "绝望通关回执失败触发整笔事务回滚");
            var rolledBack = fixture.Read();
            That(rolledBack.Revision == character.Revision && rolledBack.Inventory.Gold == character.Inventory.Gold &&
                !rolledBack.TitleProgress.ContainsKey(floor.TitleQuestId) &&
                repo.LoadDespairProgress(fixture.Owner, rolledBack) == new DungeonDespairProgress(),
                "回执失败不留下金币、称号实体、称号进度或半个楼层推进");
        }
        finally { fixture.Sql("DROP TRIGGER fail_despair_clear"); }
        var committed = repo.CommitDungeonDespairClear(fixture.Owner, run, pending!);
        That(!committed.Duplicate && committed.Progress == new DungeonDespairProgress(2, day, 1) &&
            committed.Character.Inventory.Gold == character.Inventory.Gold + 123 &&
            committed.Character.TitleProgress[floor.TitleQuestId].Remaining.Complete &&
            committed.Character.Inventory.Items.Any(item => item.Core.ItemId == 26541 && item.Uid > 0),
            "资产、真实称号UID及下一层进度在同一次事务提交");
        var duplicate = repo.CommitDungeonDespairClear(fixture.Owner, run, pending!);
        That(duplicate.Duplicate && duplicate.Character.Revision == committed.Character.Revision &&
            duplicate.Character.Inventory.Gold == committed.Character.Inventory.Gold && duplicate.Progress == committed.Progress,
            "重复通关回执不能重复发奖或再次推进楼层");
        That(achievements.DespairClear(committed.Character, run.ClearFact!, floor, DateTimeOffset.UtcNow) is { Accepted: true, Plan: null },
            "再次爬塔时既有称号不重复创建");
        That(run.ConfirmCommittedDespairClear(pending!) && !run.HasPendingSettlement,
            "只在持久通关结果成功后解除原实例结算占用");
        Reject<InvalidOperationException>(() => repo.CommitDungeonDespairClear(fixture.Owner, run, pending!),
            "已结束占用的实例不能再次调用提交入口");
        Console.WriteLine($"PASS {Count - before} 绝望实际资源称号关联与通关事务隔离断言（TCP及正式规则另验）");
    }

    sealed class Authority : DungeonCombatAuthority
    {
        long sequence;
        public ConfirmedDungeonMonsterDeath Death(DungeonRunIdentity identity, ushort key) =>
            ConfirmDeath(identity, Guid.NewGuid(), key, ++sequence);
    }
}
