using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Tutorials;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.GameWorld.Dungeons;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class TutorialTests
{
    public static void RunGrowthBoundaries(DfoServer.GameWorld.Characters.CharacterGrowthCatalog growth)
    {
        string root = Path.Combine(Path.GetTempPath(), "S5A9-tutorial-cap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var repository = new CharacterRepository(Path.Combine(root, "game.db"));
            using var owner = new AccountOwners().Acquire("tutorial-cap", () => { });
            ulong cap = growth.GetCumulativeExperienceBoundary(95) - 1;
            foreach (uint remaining in new uint[] { 5, 0 })
            {
                var created = repository.Create(owner, new NewCharacter("Cap" + remaining, new(38, 0, 1677, 222), null)
                {
                    Level = 95, Tutorial = new(7115),
                    InitialLearning = new(cap - remaining, [new(0, 11, 3, []), new(1, 19, 4, [])])
                });
                var expected = repository.LoadCharacter(owner, created.Slot);
                var candidate = CharacterGrowthPlan.AddExperience(expected, 100, growth).Plan!;
                var plan = new TutorialCheckpointPlan(expected, Guid.NewGuid(), null, 0, "settlement", 0, 100,
                    new(expected.Inventory, expected.Inventory, "tutorial_settlement"),
                    expected.Tutorial! with { Completed = true }, expected.Position, candidate);
                var saved = repository.CommitTutorialCheckpoint(owner, plan);
                That(saved.Character.Learning.Experience == cap && saved.Character.Level == 95 &&
                    saved.Character.Revision == expected.Revision + 1 && saved.Character.Tutorial!.Completed &&
                    saved.Character.Learning.Trees.Select(tree => (tree.Context, tree.Sp, tree.Tp)).SequenceEqual(
                        expected.Learning.Trees.Select(tree => (tree.Context, tree.Sp, tree.Tp))),
                    "训练奖励保留原额度，封顶只影响实际到账5或0，完成及双树余额正常提交");
                That(repository.CommitTutorialCheckpoint(owner, plan).Duplicate &&
                    repository.LoadCharacter(owner, created.Slot).Revision == saved.Character.Revision,
                    "训练封顶结算回放不再次增加经验或revision");
                Reject<InvalidDataException>(() => repository.CommitTutorialCheckpoint(owner, plan with { Experience = 99 }),
                    "实际到账截断不允许修改原奖励额度");
            }
        }
        finally { Directory.Delete(root, true); }
    }

    public static void RunCore()
    {
        That(TutorialRequests.Flag([0, 38, 0, 0, 0, 1]) == new TutorialFlagRequest(0, 38, 1), "教程请求按S5六字节保留完整检查点");
        foreach (int length in new[] { 0, 1, 4, 5, 7, 10 })
            Reject<InvalidDataException>(() => TutorialRequests.Flag(new byte[length]), "错误宽度不接受旧参考服额外四字节");
        var location = new Location(38, 0, 1677, 222, 0);
        var role = new Character(1, 0, "Tutorial", 0, location, location);
        Bytes(TutorialPackets.Flag(new(0, role, 1, 1, [])), [1, 0], "无奖励ACK143也必须包含零条数");
        Bytes(TutorialPackets.Flag(new(0, role, 1, 1, [new(3, 8474, 1)])),
            [1, 1, 3, 0, 26, 33, 0, 0, 1, 0, 0, 0], "教程奖励ACK143每条是槽u16、物品u32、数量u32");
        Bytes(TutorialPackets.Flag(new(19, role, 1, 1, [])), [0, 19, 0], "业务拒绝走本版u16失败前缀");
        var pending = TownPackets.Select(role with { Tutorial = new(7115) });
        var legacy = TownPackets.Select(role);
        That(pending.Length == 234 && legacy.Length == 235 && pending[223] == 0 && pending[224] == 0 &&
            legacy.AsSpan(226).SequenceEqual(pending.AsSpan(225)), "ACK4空教程列表后尾字段整体前移，不残留固定padding");
        VerifyActorPolicy();
        VerifyCinematicActors();

        string root = Path.Combine(Path.GetTempPath(), "S5A9-tutorial-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "game.db");
            var repository = new CharacterRepository(path);
            var owners = new AccountOwners();
            using var owner = owners.Acquire("tutorial", () => { });
            var created = repository.Create(owner, new NewCharacter("Tutorial", location, location)
                { Tutorial = new(7115), DungeonState = CharacterDungeonState.NewOrdinary });
            var expected = repository.LoadCharacter(owner, created.Slot);
            That(expected.Tutorial == new CharacterTutorialState(7115), "训练待完成状态与角色创建同事务且重读保留");
            var item = new InventoryItem(0, new(InventoryListType.Main, 3), new(8474, ItemKind.Consumable, 1));
            var inventory = new InventoryPlan(expected.Inventory, expected.Inventory with { Items = [item] }, "tutorial_reward", new(expected));
            var split = new InventoryPlan(expected.Inventory, expected.Inventory with { Items = [item, item with { Position = new(InventoryListType.Main, 4) }] }, "tutorial_reward");
            That(TutorialRewards.Rows(split, split.AfterIndex) is [{ Slot: 3, Count: 1 }, { Slot: 4, Count: 1 }],
                "相同物品分成多堆时逐实际槽位发奖励行，不按物品ID要求唯一堆");
            var beforeStack = expected.Inventory with { Items = [item with { Uid = 7, Core = item.Core with { Value = 3 } }] };
            var stacked = new InventoryPlan(beforeStack, beforeStack with { Items = [beforeStack.Items[0] with { Core = item.Core with { Value = 4 } }] }, "tutorial_reward");
            That(TutorialRewards.Rows(stacked, stacked.AfterIndex) is [{ Slot: 3, Count: 1 }], "已有堆奖励只提示本次数量，不重复总堆量");
            var plan = new TutorialCheckpointPlan(expected, Guid.NewGuid(), null, 0, "reward", 38, 0,
                inventory, expected.Tutorial! with { RewardReceived = true }, location);
            Sql("CREATE TRIGGER fail_tutorial BEFORE INSERT ON character_tutorial_receipts BEGIN SELECT RAISE(ABORT,'fixture'); END");
            Reject<SqliteException>(() => repository.CommitTutorialCheckpoint(owner, plan), "训练晚期账本失败触发整笔SQL回滚");
            var failed = repository.LoadCharacter(owner, created.Slot);
            That(failed.Revision == expected.Revision && failed.Tutorial == expected.Tutorial && failed.Inventory.Items.Length == 0 &&
                Sql("SELECT count(*) FROM inventory_audit_log") is 0L, "药剂、标记、实际UID分配、审计和revision一起回滚");
            Sql("DROP TRIGGER fail_tutorial");
            var committed = repository.CommitTutorialCheckpoint(owner, plan);
            That(committed.Character.Revision == expected.Revision + 1 && committed.Character.Tutorial!.RewardReceived &&
                committed.Character.Inventory.Find(item.Position) is { Uid: > 0, Core.Value: 1 }, "奖励提交结果携带实际UID而非规划中的零UID");
            That(repository.CommitTutorialCheckpoint(owner, plan).Duplicate && Sql("SELECT count(*) FROM character_tutorial_receipts") is 1L,
                "原事件可恢复已提交结果，不再次奖励或递增revision");
            Reject<InvalidDataException>(() => repository.CommitTutorialCheckpoint(owner, plan with { Position = location with { X = 999 } }),
                "同事件更改位置不可借回放绕过验证");
            var changed = new InventoryPlan(expected.Inventory, inventory.After with { Items = [item with { Core = item.Core with { Value = 2 } }] }, inventory.Reason);
            Reject<InvalidDataException>(() => repository.CommitTutorialCheckpoint(owner, plan with { Inventory = changed }), "同事件更改奖励不能恢复旧结果");
            expected = committed.Character;
            var skip = new TutorialCheckpointPlan(expected, Guid.NewGuid(), null, 0, "skip", 0, 0,
                new(expected.Inventory, expected.Inventory, "tutorial_skip"), expected.Tutorial! with { Completed = true }, location);
            var finished = repository.CommitTutorialCheckpoint(owner, skip).Character;
            That(finished.Tutorial is { Completed: true, RewardReceived: true } && finished.Learning.Experience == expected.Learning.Experience,
                "明确退出只完成训练状态，不伪造自动任务或经验");
            Reject<InvalidOperationException>(() => repository.CommitTutorialCheckpoint(owner, skip with { EventId = Guid.NewGuid() }), "旧revision新事件不能覆盖完成后的状态");
            using var replacement = owners.Acquire("tutorial", () => { });
            Reject<InvalidOperationException>(() => repository.CommitTutorialCheckpoint(owner, skip), "旧generation连重放也不能写库或恢复新owner结果");
            That(new CharacterRepository(path).LoadCharacter(replacement, created.Slot).Tutorial == finished.Tutorial,
                "新owner重开保留完成与奖励状态");
            object? Sql(string statement)
            {
                using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
                db.Open(); using var command = db.CreateCommand(); command.CommandText = statement;
                return command.ExecuteScalar();
            }
        }
        finally { Directory.Delete(root, true); }
    }

    static void VerifyCinematicActors()
    {
        var map = new DungeonMap("boss", new(0, 0), 1, "map/tutorial-cinematic.map");
        var maze = new DungeonMaze(0, 1, 1, "EE", [map.Position], [map.Position], [map], []);
        var entry = new DungeonEntryPlan(1, 0, 7125, 0, byte.MaxValue, 0, maze, map, map) { BasisLevel = 1, IsTutorial = true };
        var monster = new DungeonRoomMonster(2, [2, 0, 1, 100, 200, 0, 0, 0], "[fixed]", "[boss]", "monster/fixture.mon");
        foreach (string faction in new[] { "[monster]", "[character]", "[neutral]" })
        {
            var room = new DungeonRoomDefinition(map, [monster], [], [])
            {
                AiCharacters = [new(3, [3, 0, 100, 200, 0, 1], faction, "[normal]", "aicharacter/fixture.aic", 1)],
                TutorialCinematics = [new(1, "cinematic/fixture.cmt", [0], [0])]
            };
            var plan = DungeonMonsterPlan.FromRoom(room, 1, 2, 1, includeSpecial: false);
            var authority = new NativeDungeonCombatAuthority();
            That(SinglePlayerDungeonRun.TryCreate(entry, 1, 1, new([plan], []), authority, out var run, out _) &&
                authority.BindTo(run!), "训练剧情仍保留真实APC及其阵营");
            run!.CompleteLoading(run.Snapshot.Identity, Guid.NewGuid());
            That((run.TutorialCinematicTargets(2) is not null) == (faction != "[monster]"),
                "CMT收尾不能绕过活着的敌方APC；友军和中立不阻挡剧情完成");
        }
    }

    static void VerifyActorPolicy()
    {
        var map = new DungeonMap("boss", new(0, 0), 1, "map/tutorial-fixture.map");
        var maze = new DungeonMaze(0, 1, 1, "EE", [map.Position], [map.Position], [map], []);
        var entry = new DungeonEntryPlan(1, 0, 7125, 0, byte.MaxValue, 0, maze, map, map) { BasisLevel = 1 };
        var monster = new DungeonRoomMonster(2, [2, 0, 1, 100, 200, 0, 0, 0], "[fixed]", "[boss]", "monster/fixture.mon");
        var child = new DungeonRoomSpecialChild(0, "[monster]", [3, 1, 100, 200, -1], "monster/child.mon");
        var parent = new DungeonRoomSpecialObject(0, 1000, [1000, 100, 200, 0, 1], "passiveobject/fixture.obj", [child]);
        var room = new DungeonRoomDefinition(map, [monster], [], []) { SpecialObjects = [parent] };
        var ordinary = DungeonMonsterPlan.FromRoom(room, 1, 2, 1);
        var training = DungeonMonsterPlan.FromRoom(room, 1, 2, 1, includeSpecial: false);
        foreach (var (tutorial, planned, accepted) in new (bool, DungeonMonsterPlan, bool)[] {
            (false, ordinary, true), (false, training, false), (true, training, true), (true, ordinary, false) })
        {
            var authority = new NativeDungeonCombatAuthority();
            That(SinglePlayerDungeonRun.TryCreate(entry with { IsTutorial = tutorial }, 1, 1,
                new([planned], []), authority, out var run, out _) == accepted &&
                (run is null || authority.BindTo(run)),
                "实例复验保持普通完整父子与训练生成规则，不能用另一种对象计划通过校验");
        }
        That(!SinglePlayerDungeonRun.TryCreate(entry with { IsTutorial = true }, 1, 1,
            new([training with { Monsters = [] }], []), new NativeDungeonCombatAuthority(), out _, out _),
            "训练排除特殊父子不允许遗漏真实固定怪物、关闭对象身份或清房校验");
    }
}
