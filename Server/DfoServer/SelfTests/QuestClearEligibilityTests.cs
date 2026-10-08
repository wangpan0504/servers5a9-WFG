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
            "[fixed]", "[boss]", "monster/fixture.mob") { Team = 100 };
        var room = new DungeonRoomDefinition(map, [source], [], []);
        var monster = new DungeonPlannedMonster(0, 10, 107000903, 3, source) { SpawnType = 3 };
        var entry = new DungeonEntryPlan(99, 17, 3, 3145, 0, 0, maze, map, map) { QuestAcceptanceId = acceptance };
        var authority = new FixtureCombatAuthority();
        var route = new DungeonFixedRoute([new(room, [monster])], []) { PlayerTeam = 0 };
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
        Console.WriteLine($"PASS {checks} 3145清图资格纯规则断言");

        void Check(bool value, string name)
        {
            checks++;
            if (!value)
                throw new InvalidOperationException("FAIL 清图资格: " + name);
        }
    }

    sealed class FixtureCombatAuthority : DungeonCombatAuthority
    {
        public ConfirmedDungeonMonsterDeath Death(DungeonRunIdentity identity, ushort key) => ConfirmDeath(identity, Guid.NewGuid(), key, 1);
    }
}
