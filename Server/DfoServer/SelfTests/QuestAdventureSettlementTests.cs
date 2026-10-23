using DfoServer.Game.Dungeons;
using DfoServer.Game.Quests;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class QuestAdventureSettlementTests
{
    // 真实资源路线配合明确合成的死亡报告，不宣称客户端演出或结算界面已验收。
    internal static void Run(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters, ItemResources items,
        QuestCatalog quests, SkillLearningCatalog skills)
    {
        int checks = Count;
        var growth = Testing.SelfTestCatalogs.Growth(pvf,skills);
        var kills = Testing.SelfTestCatalogs.KillExperience(pvf);
        var clears = Testing.SelfTestCatalogs.ClearExperience(pvf);
        var parameters = Testing.SelfTestCatalogs.QuestRewardParameters(pvf);
        var dungeons = Testing.SelfTestCatalogs.Dungeons(pvf);
        var rooms = Testing.SelfTestCatalogs.Rooms(pvf);
        // 扫描真实单故事块得到的同类资源样本；仅断言难度归属，不绕过各任务的其它准入资格。
        foreach (var sample in new (int Dungeon,int Story,int[] Quests)[]
        {
            (Dungeon:35,Story:3243,Quests:new[] {7977}), (36,3247,new[] {7924}),
            (141,3320,new[] {7976}), (43,3299,new[] {7914}), (291100420,6363,new[] {6396}),
            (164,3496,new[] {3780,4897}), (152,3257,new[] {7927,7929,7930}),
            (153,3259,new[] {7954}), (161,3481,new[] {3069}), (162,3486,new[] {3070}), (156,3452,new[] {2994})
        })
        {
            var definition = dungeons.Get(sample.Dungeon);
            foreach (byte difficulty in new byte[] {0,1,2,3,4})
            {
                That(definition.AllowsDifficulty(difficulty,true,sample.Story) == (difficulty == 0),
                    "真实主线保留所属故事块难度："+sample.Story);
                foreach (int id in sample.Quests)
                    That(definition.AllowsDifficulty(difficulty,true,id) == definition.AllowsDifficulty(difficulty,false) &&
                        definition.Mazes.Any(maze => maze.HasActiveQuestConnection(id)),
                        "明确不属于故事块的真实任务连接沿普通难度表："+id);
            }
        }
        var quest = quests.Get(7930);
        var entrance = MiddleQuestEntryTests.Entrance(towns,quest,152);
        foreach (uint request in new uint[] {7930,0})
        foreach (byte difficulty in new byte[] {0,1,2})
        {
            using var f = new QuestSceneTests.Fixture(towns,characters,skills,growth,quests,false,40,
                entrance.Completed,position:entrance.Position);
            That(f.Session.ChangeQuest(7930,QuestLifecycleAction.Accept,quests,items.Items,items.GoldLimits,items.EpicPieces)?.Commit is not null,
                "7930沿真实前置接取，不预置任务完成或奖励");
            var opening = f.Session.EnterFormalDungeon(152,request,difficulty,dungeons,rooms);
            That(opening is not null && opening.Entry.RouteQuestId == 7930 && opening.Entry.Difficulty == difficulty,
                "冒险任务显式及普通0入口保留所选合法难度");
            var loaded = f.Session.CompleteFormalDungeonLoading()!;
            foreach (var next in opening!.Entry.Maze.MapSpecification!.Skip(1))
            {
                foreach (var actor in loaded.Monsters.Monsters.Where(row => row.CanReportDeath))
                    f.Session.ObserveFormalDungeonScriptedDeath(new(actor.ObjectKey,0x22,true,new string('A',64)),kills,growth);
                That(f.Session.MoveFormalDungeon((byte)next.Position.X,(byte)next.Position.Y) is not null,
                    "沿7930实际连接逐房前进");
                loaded = f.Session.CompleteFormalDungeonLoading()!;
            }
            var friend = loaded.Monsters.Monsters.Single(row => row.MonsterId == 107012140);
            var enemy = loaded.Monsters.Monsters.Single(row => row.AiCharacter?.Id == 1301);
            That(loaded.Snapshot.CurrentMap.Id == 655528 && friend is { IsFriendly:true,CanReportDeath:false } &&
                enemy is { IsBoss:false,IsBlocking:false,CanReportDeath:true } && f.Session.Phase == SessionPhase.Dungeon,
                "终点保留瓦格纳友方演员及真实敌对普通APC，不伪造Boss资格");
            var before = f.Read();
            f.Session.ObserveFormalDungeonScriptedDeath(new(friend.ObjectKey,0x22,true,new string('B',64)),kills,growth);
            That(QuestCompletionRepositoryTests.Same(before,f.Read()) && f.Session.EnableFormalDungeonSettlement() is null,
                "友方演出演员退场不签发死亡、清图或奖励");
            var death = f.Session.ObserveFormalDungeonScriptedDeath(new(enemy.ObjectKey,0x22,true,new string('C',64)),kills,growth);
            That(death.ClearFact?.ClearMap?.Id == 655528 && f.Session.Phase == SessionPhase.Settlement,
                "真实敌对APC死亡提交原ClearFact");
            var enabled = f.Session.EnableFormalDungeonSettlement(enemy.ObjectKey);
            That(enabled is not null && enabled.Value.BossKey == enemy.ObjectKey,
                "115与31引用实际死亡的普通敌对APC，不能卡在已清图状态");
            f.Session.ConfirmFormalDungeonSettlementEnabled(enabled!.Value.Receipt);
            That(f.Session.EnableFormalDungeonSettlement() is null,"结算许可发布后不重复");
            byte[] report = DungeonSettlementProtocolTests.Report(f.Read().Id,99);
            That(f.Session.SettleFormalDungeon(f.Read().Id,99,99,report,dungeons,clears,growth).Outcome == DungeonRunOutcome.Accepted,
                "普通APC终点沿原46事务结算");
            var committed = f.Read();
            That(f.Session.SettleFormalDungeon(f.Read().Id,99,99,report,dungeons,clears,growth).Outcome == DungeonRunOutcome.Duplicate &&
                QuestCompletionRepositoryTests.Same(committed,f.Read()),"重复46不重新发奖");
            That(f.Session.FinishQuest(7930,ushort.MaxValue,quests,parameters,items.Items,items.GoldLimits,items.EpicPieces,growth).Receipt is not null &&
                f.Read().CompletedQuests.Contains(7930),"7930凭实际清图沿34正常交付");
            That(f.Session.ReturnFromFormalDungeon() is not null && !f.Session.CanPublishDungeonSettlement(enabled.Value.Receipt),
                "回城后旧实例结算回执失效");
        }
        Console.WriteLine($"PASS {Count-checks} 冒险任务难度、普通敌对APC结算及交付回归（非实机）");
    }
}
