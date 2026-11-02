using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Towns;
using DfoServer.Testing;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class SpecialNormalDungeonEntryTests
{
    internal static readonly int[] DungeonIds = [9595,9596,9597,9599,9600,9601,9602,9603,9604,9605,9606,9607,
        5101,5102,5103,5104,5105,5106,400000016];

    public static void Run(PvfArchive pvf)
    {
        int checks = Count;
        var catalog = SelfTestCatalogs.Dungeons(pvf);
        var rooms = SelfTestCatalogs.Rooms(pvf);
        var quests = SelfTestCatalogs.Quests(pvf);
        var character = new Character(17,0,"NormalEntry",1,new Location(38,2,100,200),null) { Level = 95 };
        var failures = new List<string>();
        foreach (int id in DungeonIds)
        {
            var definition = catalog.Get(id);
            if (id is >= 5101 and <= 5106)
            {
                That(definition is { IsOrdinaryLuke:true,CoinLimit:12,StartFatigue:8,AllowsPractice:false,OrdinaryEntryTicket:null },
                    "六白图原资源声明：十二币、八疲劳、禁止练习、无普通门票");
                That(!(definition with { Id=5107 }).IsOrdinaryLuke &&
                    !(definition with { Path="dungeon/luke/phase.dgn" }).IsOrdinaryLuke,
                    "不能只凭卢克标记把团队副本或其他编号纳入白图限币策略");
            }
            int choices = Math.Max(definition.Mazes.Count,definition.Mazes.SelectMany(maze => maze.MapSpecification ?? [])
                .Select(map => map.CandidateIds.Count).DefaultIfEmpty(1).Max());
            var sourceMaps = definition.Mazes.SelectMany(maze => maze.MapSpecification ?? []).ToArray();
            for (int choice = 0; choice < choices; choice++)
            {
                try
                {
                    That(DungeonEntryPlan.TryCreate(catalog,character,(uint)id,0,2,out var plan,out var reason,
                        draw: count => choice % count,quests: quests),$"普通副本{id}候选{choice}可规划：{reason}");
                    var entry = plan!;
                    That(entry is { IsOrdinary: true,QuestId: 0,RouteQuestId: 0,Difficulty: 2 } &&
                        entry.BaseBossMap.Position == entry.Maze.Bosses.Single() && entry.BaseBossMap.Kind == "boss" &&
                        sourceMaps.SequenceEqual(definition.Mazes.SelectMany(maze => maze.MapSpecification ?? [])),
                        "普通Boss坐标投影保留原资源、原地图和任务身份");
                    var planned = new List<DungeonMonsterPlan>();
                    uint next = 100;
                    foreach (var map in entry.Maze.MapSpecification!)
                    {
                        var room = DungeonMonsterPlan.Create(rooms,map,character.Id,checked((ushort)next),entry.MonsterBasisLevel,entry.PlayerTeam);
                        planned.Add(room);
                        if (room.Monsters.Count != 0 || room.SpecialObjects.Count != 0) next = room.NextObjectKey;
                    }
                    var route = new DungeonFixedRoute(planned,entry.Maze.StaticTopology.Links!
                        .Select(link => new DungeonRouteConnection(link.First,link.Second)).ToArray());
                    That(SinglePlayerDungeonRun.TryCreate(entry,1,100,route,new NativeDungeonCombatAuthority(),out _,out var runReason),
                        $"普通副本{id}完整原房间及怪物可创建正式Run：{runReason}");
                }
                catch (Exception error) { failures.Add($"{id}/{choice}: {error.Message}"); }
            }
            foreach (byte wrong in new byte[] { 0,1,3,4 })
                That(!definition.AllowsDifficulty(wrong,false),$"普通副本{id}不接受非指定难度{wrong}");
            That(!DungeonEntryPlan.TryCreate(catalog,character with { Level = (byte)(definition.MinimumLevel!.Value-1) },
                (uint)id,0,2,out _,out var levelReason,quests: quests) && levelReason == "dungeon-level-too-low",
                $"普通副本{id}保留最低等级限制");
        }
        foreach (ushort questId in new ushort[] { 2683,10019,2902,13200,8683 })
        {
            var quest = quests.Get(questId);
            bool daily = QuestCycle.NormalDungeonMission(quest);
            var accepted = new ActiveMainQuest(0,questId,daily ? 1u : 5u) { AcceptanceId = Guid.NewGuid() };
            var owner = character with { ActiveMainQuests = [accepted] };
            var targets = daily ? quest.Definition!.Dungeons.Select(row => row.DungeonId).ToArray() : [quest.IntData[0]];
            foreach (int id in targets.Where(DungeonIds.Contains))
            {
                var definition = catalog.Get(id);
                That(DungeonEntryPlan.TryCreate(catalog,owner,(uint)id,questId,2,out var explicitPlan,out var reason,
                    quest:quest,quests:quests,draw:_ => 0),$"任务{questId}显式进入普通副本{id}：{reason}");
                That(explicitPlan is { IsOrdinary:true,RouteQuestId:0,Difficulty:2 } &&
                    explicitPlan.QuestId == questId && explicitPlan.QuestAcceptanceId == accepted.AcceptanceId &&
                    explicitPlan.EntryItem == definition.OrdinaryEntryTicket && explicitPlan.StartFatigue == definition.StartFatigue,
                    "普通任务路线保留真实接取、原门票和一次疲劳，不转成免票剧情副本");
                That(DungeonEntryPlan.TryCreate(catalog,owner,(uint)id,0,2,out var zeroPlan,out _,quests:quests,draw:_ => 0) &&
                    zeroPlan!.BossMap == explicitPlan!.BossMap,"显式任务与零任务编号选中同一普通Boss路线");
                That(!DungeonEntryPlan.TryCreate(catalog,character,(uint)id,questId,2,out _,out var inactive,
                    quest:quest,quests:quests) && inactive == "quest-not-active","放弃后不能再使用旧任务身份进图");
                foreach (byte wrong in new byte[] { 0,1,3,4 })
                    That(!DungeonEntryPlan.TryCreate(catalog,owner,(uint)id,questId,wrong,out _,out _,quest:quest,quests:quests),
                        "显式任务不能绕过普通副本指定难度");
                That(!DungeonEntryPlan.TryCreate(catalog,owner with { ActiveMainQuests = [accepted with { AcceptanceId = Guid.Empty }] },
                    (uint)id,questId,2,out _,out _,quest:quest,quests:quests),"空接取身份不借普通路线");
            }
            int wrongDungeon = questId == 13200 ? 9595 : 9607;
            That(DungeonEntryPlan.TryCreate(catalog,owner,(uint)wrongDungeon,questId,2,out _,out _,quest:quest,quests:quests),
                "单纯寻物和通关目标不限制普通入场，进度仍要求真实目标路线");
            var foreign = quest with { Id=29999 };
            That(DungeonEntryPlan.TryCreate(catalog,character with { ActiveMainQuests = [accepted with { QuestId=29999 }] },
                (uint)targets.First(DungeonIds.Contains),29999,2,out _,out var foreignReason,quest:foreign,quests:quests),
                $"有效目标形状不按原任务编号限制建图：{questId}/{foreign.Condition}/{foreign.SubType} {foreignReason}");
        }
        var two = character with { ActiveMainQuests = [
            new(0,2683,1) { AcceptanceId=Guid.NewGuid() },new(1,10019,1) { AcceptanceId=Guid.NewGuid() }] };
        foreach (uint id in new uint[] { 2683,10019 })
            That(DungeonEntryPlan.TryCreate(catalog,two,9599,id,2,out var paired,out _,quest:quests.Get((int)id),quests:quests) &&
                paired!.QuestAcceptances.Length == 2,"同图两项材料任务均保留原接取，不相互覆盖");
        That(failures.Count == 0,"普通副本全路线准入：" + string.Join("; ",failures));
        Console.WriteLine($"PASS {Count-checks} 安徒恩、卢克与超时空普通副本资源及正式Run断言（非客户端实机）");
    }
}
