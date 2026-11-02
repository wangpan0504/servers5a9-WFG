using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Quests;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network.Parsers;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class Issue262OtherverseQuestTests
{
    internal static void Run(TownCatalog towns, CharacterCatalog characters, ItemResources items, QuestCatalog quests,
        SkillLearningCatalog skills, CharacterGrowthCatalog growth, DungeonCatalog dungeons, DungeonRoomCatalog rooms,
        DungeonKillExperienceCatalog kills, DungeonClearExperienceCatalog clears, QuestRewardParameterCatalog parameters)
    {
        int checks = Count;
        var quest = quests.Get(12871);
        That(quest.Condition == "[clear a dungeon]" && quest.SubType == 6 && quest.MinimumLevel == 85 &&
            quest.Prerequisites.SequenceEqual([6928]) && quest.IntData.SequenceEqual([7167,-1,7170,-1,7173,-1,7176,-1,7179,-1]),
            "262复用实际12871等级、前置及五组通关OR目标，不套用单地图清图");
        var firstEntrance = MiddleQuestEntryTests.Entrance(towns, quest, 7167);
        foreach (bool missingPrerequisite in new[] {true,false})
        {
            using var f = new QuestSceneTests.Fixture(towns,characters,skills,growth,quests,false,
                missingPrerequisite ? (byte)85 : (byte)84,
                missingPrerequisite ? firstEntrance.Completed.Where(id => id != 6928).ToArray() : firstEntrance.Completed,
                position:firstEntrance.Position);
            var before = f.Read();
            That(f.Session.ChangeQuest(12871,QuestLifecycleAction.Accept,quests,items.Items,items.GoldLimits,items.EpicPieces)?.Commit is null &&
                QuestCompletionRepositoryTests.Same(before,f.Read()),
                "262未完成6928或等级不足85不能接取，不放宽公共资格");
        }
        foreach (uint dungeon in new uint[] {7167,7170,7173,7176,7179})
        foreach (uint request in new uint[] {12871,0})
        {
            var entrance = MiddleQuestEntryTests.Entrance(towns,quest,dungeon);
            using var f = new QuestSceneTests.Fixture(towns,characters,skills,growth,quests,false,85,
                entrance.Completed,position:entrance.Position,items:[(3332,5)]);
            That(f.Session.ChangeQuest(12871,QuestLifecycleAction.Accept,quests,items.Items,items.GoldLimits,items.EpicPieces)?.Commit is not null,
                "262满足真实资格时接取12871");
            var accepted = f.Read().ActiveMainQuests.Single(row => row.QuestId == 12871);
            That(accepted.ConditionState == 1 && accepted.AcceptanceId != Guid.Empty &&
                f.Session.FinishQuest(12871,ushort.MaxValue,quests,parameters,items.Items,items.GoldLimits,items.EpicPieces,growth).Outcome == QuestFinishOutcome.Rejected,
                "262接取保留pending1和独立身份，未通关不能领奖");
            if (dungeon == 7167 && request == 12871)
            {
                var before = f.Read();
                foreach (var playable in new[]
                {
                    quest with {IntData=[7170,-1]},
                    quest with {IntData=[7167,3]},
                    quest with {Definition=quest.Definition! with {Dungeons=[]}},
                    quest with {Grade="[epic]"},
                    quest with {Condition="[condition under clear]",IntData=[7167,-1]}
                })
                    That(DungeonEntryPlan.TryCreate(dungeons,before,7167,12871,2,out _,out _,quest:playable,quests:quests),
                        "262目标及界面指引不是建图约束，已确认通关形状仍可借用普通路线");
                foreach (int[] invalid in new int[][] { [7167,-1,7170], [7167,5], [0,-1] })
                    That(!DungeonEntryPlan.TryCreate(dungeons,before,7167,12871,2,out _,out _,quest:quest with { IntData=invalid },quests:quests),
                        "262未知或不完整目标不能伪装成已实现的通关合同");
                That(!QuestDungeonClearProgress.MatchesAlternative(quest with {IntData=[7170,-1]},7167,2) &&
                    !QuestDungeonClearProgress.MatchesAlternative(quest with {IntData=[7167,3]},7167,2),
                    "进入不代表任务达成，错误目标及不足最低难度仍不能推进列表通关");
                That(!DungeonEntryPlan.TryCreate(dungeons,before with
                    {ActiveMainQuests=[accepted with {AcceptanceId=Guid.Empty}]},7167,12871,2,out _,out _,quest:quest,quests:quests) &&
                    QuestCompletionRepositoryTests.Same(before,f.Read()),
                    "262缺失接取身份仍拒绝；所有准入反例不改变存档");
            }
            var opening = f.Session.EnterFormalDungeon(dungeon,request,2,dungeons,rooms);
            That(opening is not null && opening.Entry.IsOrdinary && opening.Entry.RouteQuestId == 0 &&
                opening.Entry.QuestAcceptances.Contains(accepted) &&
                (request == 0 ? opening.Entry.QuestAcceptanceId == Guid.Empty : opening.Entry.QuestAcceptanceId == accepted.AcceptanceId),
                "262显式12871与普通0均沿实际普通迷宫进入目标副本："+dungeon+"/"+request+"/"+f.Session.LastFormalDungeonRejection);
            var path = QuestStoryRouteTests.Path(opening!.Entry.Maze.StaticTopology,opening.Entry.StartMap.Position,opening.Entry.BossMap.Position);
            for (int at = 0; at < path.Length; at++)
            {
                var loaded = f.Session.CompleteFormalDungeonLoading()!;
                foreach (var monster in loaded.Monsters.Monsters.Where(row => row.IsBlocking))
                    That(f.Session.ObserveFormalDungeonDeath(FormalDungeonSessionTests.Observation(loaded,monster),kills,growth).Outcome == DungeonRunOutcome.Accepted,
                        "262合成死亡只报告当前实际演员key，不伪造地图或直接设置通关");
                if (at+1 < path.Length)
                    That(f.Session.MoveFormalDungeon(checked((byte)path[at+1].X),checked((byte)path[at+1].Y)) is not null,
                        "262沿真实地图连接清房前进");
            }
            var clear = f.Session.ReadClearedQuest(12871)?.Clear;
            That(f.Session.Phase == SessionPhase.Settlement && clear is not null && clear.DungeonId == dungeon &&
                clear.AcceptanceId == accepted.AcceptanceId &&
                f.Read().ActiveMainQuests.Single(row => row.QuestId == 12871).ConditionState == 0,
                "262列表任一目标真实清图候选归零并保存原接取凭据");
            var enabled = f.Session.EnableFormalDungeonSettlement();
            That(enabled is not null,"262沿真实清图许可31");
            f.Session.ConfirmFormalDungeonSettlementEnabled(enabled!.Value.Receipt);
            byte[] report = DungeonSettlementProtocolTests.Report(f.Read().Id,99);
            That(DungeonSettlementRequests.TryParsePlayResult(report,out var parsed),"262沿正式46结构解析");
            That(f.Session.SettleFormalDungeon(parsed!.ActorId,parsed.RankPoint,parsed.MaximumRankPoint,report,dungeons,clears,growth) is
                {Outcome:DungeonRunOutcome.Accepted,Receipt:not null},"262正常结算，不借33伪造完成");
            That(f.Session.ReturnFromFormalDungeon() is not null,"262通关后返回城镇");
            f.Session.ReturnToRoster();
            f.Session.Select(0,resumeTown:true);
            f.Session.MarkReady();
            That(f.Session.ReadClearedQuest(12871)?.Clear?.AcceptanceId == accepted.AcceptanceId,
                "262重选角色保留原接取的持久清图凭据");
            var finished = f.Session.FinishQuest(12871,ushort.MaxValue,quests,parameters,items.Items,items.GoldLimits,items.EpicPieces,growth);
            That(finished.Receipt?.Commit is {ConditionType:45} && f.Read().CompletedQuests.Contains(12871) &&
                f.Read().ActiveMainQuests.All(row => row.QuestId != 12871),
                "262以原生45正式交付并移除活动槽，不能冒充type1");
            That(f.Session.FinishQuest(12871,ushort.MaxValue,quests,parameters,items.Items,items.GoldLimits,items.EpicPieces,growth).Outcome ==
                QuestFinishOutcome.AlreadyCompleted,"262重复交付不再次发奖");
        }
        Console.WriteLine($"PASS {Count-checks} #262实际12871接取、五副本显式/普通入口及通关重选交付（合成战斗，非实机）");
    }
}
