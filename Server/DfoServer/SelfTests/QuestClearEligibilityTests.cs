using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Towns;

namespace DfoServer;

internal static class QuestClearEligibilityTests
{
    public static void Run()
    {
        int checks = 0;
        var acceptance = Guid.NewGuid();
        var map = new DungeonMap("boss", new(0, 0), 76126, "map/cataclysm/granflores/01_mirkwood/3145_76126.map");
        var maze = new DungeonMaze(0, 1, 1, "fixture-only", [map.Position], [map.Position], [map], []);
        var source = new DungeonRoomMonster(107000903, [107000903, 0, 3, 831, 239, 0, 1, 1],
            "[fixed]", "[boss]", "monster/fixture.mob");
        var room = new DungeonRoomDefinition(map, [source], [], []);
        var monster = new DungeonPlannedMonster(0, 10, 107000903, 3, source) { SpawnType = 3 };
        var entry = new DungeonEntryPlan(99, 17, 3, 3145, 0, 0, maze, map, map) { QuestAcceptanceId = acceptance };
        var authority = new FixtureCombatAuthority();
        var route = new DungeonFixedRoute([new(room, [monster])], []);
        Check(SinglePlayerDungeonRun.TryCreate(entry, 3, 500, route, authority, out var run, out _), "合成完整正式身份创建");
        run!.CompleteLoading(run.Snapshot.Identity, Guid.NewGuid());
        var prepared = run.PrepareConfirmedDeath(authority.Death(run.Snapshot.Identity, 10)).Pending!;
        Check(QuestClearEligibility.TryValidate(entry, prepared), "实际BossMap末次可信死亡满足纯资格");
        Check(!QuestClearEligibility.TryValidate(entry with { QuestAcceptanceId = Guid.Empty }, prepared), "空接取身份拒绝");
        Check(!QuestClearEligibility.TryValidate(entry with { BossMap = map with { Id = 76136 } }, prepared), "其他任务地图不能借3145事实");
        Check(!QuestClearEligibility.TryValidate(entry with { CharacterId = 98 }, prepared), "其他角色拒绝");
        Check(!QuestClearEligibility.TryValidate(entry with { QuestId = 3146 }, prepared), "其他任务拒绝");
        Check(!QuestClearEligibility.TryValidate(entry with { DungeonId = 6 }, prepared), "其他副本拒绝");
        var badClear = prepared.Result.ClearFact! with { ConfirmedDeathEvents = [prepared.Death.EventId, prepared.Death.EventId] };
        var forged = new PreparedDungeonDeath(prepared.Death, prepared.Monster, prepared.Result with { ClearFact = badClear });
        Check(!QuestClearEligibility.TryValidate(entry, forged), "重复死亡集合拒绝");
        var character = new Character(99, 0, "fixture", 17, new Location(38, 2, 100, 100, 0), null)
        {
            ActiveMainQuests = [new(0, 3145, 1) { AcceptanceId = acceptance }]
        };
        Check(QuestClearEligibility.MatchesActiveQuest(entry, character), "接取身份slot及未达成状态精确匹配");
        Check(!QuestClearEligibility.MatchesActiveQuest(entry, character with { Revision = 16 }), "旧角色revision拒绝");
        Check(!QuestClearEligibility.MatchesActiveQuest(entry, character with { CompletedQuests = [3145] }), "已完成任务不得再次清图");
        Check(!QuestClearEligibility.MatchesActiveQuest(entry, character with
            { ActiveMainQuests = [new(0, 3145, 1) { AcceptanceId = Guid.NewGuid() }] }), "重新接取身份拒绝旧事实");
        Check(!QuestClearEligibility.MatchesActiveQuest(entry, character with
            { ActiveMainQuests = [new(0, 3145, 0) { AcceptanceId = acceptance }] }), "已达成状态须走持久receipt精确重放");
        Check(!QuestClearEligibility.MatchesActiveQuest(entry, character with
            { ActiveMainQuests = [character.ActiveMainQuests[0], new(1, 3145, 1) { AcceptanceId = acceptance }] }), "重复任务身份拒绝");
        RoomClear();
        Console.WriteLine($"PASS {checks} 3145清图资格纯规则断言");

        void Check(bool value, string name)
        {
            checks++;
            if (!value)
                throw new InvalidOperationException("FAIL 清图资格: " + name);
        }

        void RoomClear()
        {
            var start = map with { Kind = "map", Position = new(0,0), Id = 100 };
            var middle = map with { Kind = "map", Position = new(1,0), Id = 101 };
            var boss = map with { Position = new(2,0), Id = 102 };
            var roomMaze = new DungeonMaze(0,3,1,"fixture-only",[start.Position],[boss.Position],[start,middle,boss],[]);
            var active = character.ActiveMainQuests[0];
            var roomEntry = entry with { Maze = roomMaze, StartMap = start, BossMap = boss, BaseBossMap = boss,
                HasExplicitClearMap = true, QuestClearMap = middle, QuestAcceptances = [active] };
            var ordinary = source with { RankKind = "[normal]" };
            var first = new DungeonPlannedMonster(0,20,ordinary.MonsterId,3,ordinary);
            var last = new DungeonPlannedMonster(1,21,ordinary.MonsterId,3,ordinary);
            var friendSource = new DungeonRoomAiCharacter(10448,[10448,100,100,0,0,0],"[character]","[normal]","aicharacter/fixture.chr",3);
            var friend = new DungeonPlannedMonster(0,22,10448,3,null)
                { AiCharacter = friendSource,SpawnType = 5,IsBlocking = false,IsFriendly = true };
            var end = new DungeonPlannedMonster(0,23,source.MonsterId,3,source) { SpawnType = 3 };
            var roomRoute = new DungeonFixedRoute([new(new(start,[],[],[]),[]),
                new(new(middle,[ordinary,ordinary],[],[]) { AiCharacters = [friendSource] },[first,last,friend]),new(new(boss,[source],[],[]),[end])],
                [new(start.Position,middle.Position),new(middle.Position,boss.Position)]);
            var roomAuthority = new FixtureCombatAuthority();
            Check(SinglePlayerDungeonRun.TryCreate(roomEntry,4,500,roomRoute,roomAuthority,out var roomRun,out _),"中途目标与原Boss独立建Run");
            roomRun!.CompleteLoading(roomRun.Snapshot.Identity,Guid.NewGuid());
            roomRun.Move(roomRun.Snapshot.Identity,Guid.NewGuid(),middle.Position);
            roomRun.CompleteLoading(roomRun.Snapshot.Identity,Guid.NewGuid());
            var initial = roomRun.Snapshot.Identity;
            var partial = roomRun.PrepareConfirmedDeath(roomAuthority.Death(initial,20)).Pending!;
            Check(partial.Result.RoomClearFact is null && partial.Result.ClearFact is null,"少一敌人不产生任何清房或通关事实");
            roomRun.ConfirmCommittedDeath(partial);
            var final = roomRun.PrepareConfirmedDeath(roomAuthority.Death(initial,21)).Pending!;
            Check(final.Result.RoomClearFact is { Map.Id: 101 } && final.Result.ClearFact is null &&
                final.Result.Snapshot.Phase == SinglePlayerDungeonPhase.Dungeon && final.Result.Snapshot.Identity.Epoch == initial.Epoch+1 &&
                QuestClearEligibility.TryValidateRoom(roomRun,final) && !QuestClearEligibility.TryValidate(roomEntry,final),
                "友方存活时末次敌人只准备RoomClear和下一epoch，不借用终点资格");
            var clear = new QuestClearReceipt(acceptance,3145,0,final.Death.EventId,initial.InstanceId,initial.TaskAttempt,initial.Epoch+1,18)
                { DungeonId = 3,MapId = middle.Id };
            Check(roomRun.Snapshot.Identity == initial && !roomRun.HasCommittedRoomClear(clear) &&
                !QuestClearEligibility.TryValidateRoom(roomRun,new(final.Death,final.Monster,final.Result)),"未提交或重造候选不能读取事实");
            roomRun.ConfirmCommittedDeath(final);
            Check(roomRun.HasCommittedRoomClear(clear) && roomRun.Snapshot.LivingMonsters.Contains(friend.ObjectKey) && roomRun.ClearFact is null &&
                !roomRun.HasCommittedRoomClear(clear with { EventId = Guid.NewGuid() }) &&
                !roomRun.HasCommittedRoomClear(clear with { MapId = boss.Id }) &&
                !roomRun.HasCommittedRoomClear(clear with { Epoch = initial.Epoch }) &&
                !roomRun.HasCommittedRoomClear(clear with { AcceptanceId = Guid.NewGuid() }),"提交后具体事件、地图、epoch和接取绑定，不删友方");
            Check(roomRun.Move(roomRun.Snapshot.Identity,Guid.NewGuid(),boss.Position).Outcome == DungeonRunOutcome.Accepted,
                "任务房清除后可沿原拓扑继续Boss");
            roomRun.CompleteLoading(roomRun.Snapshot.Identity,Guid.NewGuid());
            var terminal = roomRun.PrepareConfirmedDeath(roomAuthority.Death(roomRun.Snapshot.Identity,23)).Pending!;
            Check(terminal.Result.RoomClearFact is null && terminal.Result.ClearFact is { ClearMap.Id: 102 } &&
                QuestClearEligibility.TryValidate(roomEntry,terminal),"原Boss仍按原通关资格准备独立终点事实");
            roomRun.ConfirmCommittedDeath(terminal);
            Check(roomRun.HasCommittedRoomClear(clear),"进入后续结算仍保留同Run已提交中途事实");
        }
    }

    sealed class FixtureCombatAuthority : DungeonCombatAuthority
    {
        long sequence;
        public ConfirmedDungeonMonsterDeath Death(DungeonRunIdentity identity, ushort key) => ConfirmDeath(identity, Guid.NewGuid(), key, ++sequence);
    }
}
