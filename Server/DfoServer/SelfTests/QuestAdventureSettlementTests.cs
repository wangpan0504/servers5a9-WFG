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
        var summit = quests.Get(7975);
        var summitEntrance = MiddleQuestEntryTests.Entrance(towns,summit,9618);
        That(summit.MinimumLevel == 85 && summit.MaximumLevel == 99 && summit.Prerequisites.SequenceEqual([3779]) &&
            summit.IntData.SequenceEqual([655964]) && summitEntrance.Position is { Town:18,Area:0 } &&
            towns.Areas[(18,0)].DungeonWorldMap == 30 && towns.DungeonMaps[30].Options
                .Where(row => row.DungeonId == 9618).Select(row => (row.QuestId,row.InProgress)).SequenceEqual([(7975,true),(7979,true)]),
            "山巅修炼场保持真实任务等级、前置、末图及月轮山进行中门禁");
        foreach (uint request in new uint[] {7975,0})
        {
            using var f = new QuestSceneTests.Fixture(towns,characters,skills,growth,quests,false,85,
                summitEntrance.Completed,position:summitEntrance.Position);
            var before = f.Read();
            That(f.Session.EnterFormalDungeon(9618,request,0,dungeons,rooms) is null &&
                f.Session.LastFormalDungeonRejection == "selection-quest-in-progress-required:7975" &&
                QuestCompletionRepositoryTests.Same(before,f.Read()),"未接取任务仍不能进入山巅修炼场，拒绝不保存");
            That(f.Session.ChangeQuest(7975,QuestLifecycleAction.Accept,quests,items.Items,items.GoldLimits,items.EpicPieces)?.Commit is not null,
                "7975沿真实前置正式接取");
            foreach (byte difficulty in new byte[] {0,1,2,3,4})
            {
                var opening = f.Session.EnterFormalDungeon(9618,request,difficulty,dungeons,rooms);
                That(opening is not null && opening.Entry.RouteQuestId == 7975 && opening.Entry.Difficulty == difficulty &&
                    opening.Entry.Maze.MapSpecification!.Select(map => map.Id).SequenceEqual([655961,655962,655963,655964]),
                    "7975显式及普通0入口的五档难度均准备完整四房："+f.Session.LastFormalDungeonRejection);
                That(f.Session.CompleteFormalDungeonLoading() is not null && f.Session.ReturnFromFormalDungeon() is not null,
                    "山巅任务真实加载与回城沿既有实例入口，不伪造清图");
            }
        }
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
        DungeonRoomCatalogTests.RunMonsterFollowers();
        QuestProtectedMapTests.Run(towns,pvf,characters,items,quests,skills,growth);
        foreach (uint request in new uint[] {7909,0}) HerbaQuest(request);
        Console.WriteLine($"PASS {Count-checks} 冒险任务难度、普通敌对APC结算及交付回归（非实机）");

        void HerbaQuest(uint request)
        {
            var quest = quests.Get(7909);
            That(quest is { Condition: "[clear map]", IntData: [655628], DungeonIndex: 92 },
                "悬崖上的花绑定真实7909、雾都赫伊斯及末层655628");
            var entrance = MiddleQuestEntryTests.Entrance(towns,quest,92);
            using var f = new QuestSceneTests.Fixture(towns,characters,skills,growth,quests,false,93,
                entrance.Completed,position:entrance.Position);
            That(f.Session.ChangeQuest(7909,QuestLifecycleAction.Accept,quests,items.Items,items.GoldLimits,items.EpicPieces)?.Commit is not null,
                "悬崖上的花沿真实前置7908接取，不预置完成");
            var opening = f.Session.EnterFormalDungeon(92,request,0,dungeons,rooms);
            That(opening is not null && opening.Entry.BossMap.Id == 655628 && opening.Entry.BaseBossMap.Id == 655627,
                "任务保留基础Boss格及显式通关末层："+f.Session.LastFormalDungeonRejection);
            var loaded = f.Session.CompleteFormalDungeonLoading()!;
            var herba = loaded.Monsters.Monsters.Single(row => row.MonsterId == 109008616);
            That(loaded.Snapshot.CurrentMap.Id == 655624 && herba is { AiCharacter: null, IsFriendly: true, CanReportDeath: false, Team: 0, Source.FollowType: "follow" },
                "首房真实赫尔巴是monster类型友方，不伪装为APC或敌人");
            foreach (var point in new[] { (X:1,Y:1), (X:1,Y:0), (X:2,Y:0) })
            {
                foreach (var enemy in loaded.Monsters.Monsters.Where(row => row.IsBlocking))
                    That(f.Session.ObserveFormalDungeonScriptedDeath(new(enemy.ObjectKey,0x22,true,new string('A',64)),kills,growth).Outcome == DungeonRunOutcome.Accepted,
                        "沿任务真实路线清除当前房间阻挡敌人");
                That(f.Session.MoveFormalDungeon((byte)point.X,(byte)point.Y) is not null,"按真实拓扑前进至剧情Boss格");
                loaded = f.Session.CompleteFormalDungeonLoading()!;
                That(loaded.Monsters.Monsters.All(row => row.MonsterId != herba.MonsterId),
                    "赫尔巴沿首房原key随行，后续地图不重复生成演员");
            }
            That(loaded.Snapshot.CurrentMap.Id == 655627 && loaded.Monsters.Room.CinematicLayerReturn is { CinematicId: 4246, IsOnStartMap: true } &&
                f.Session.Phase == SessionPhase.Dungeon && f.Session.EnableFormalDungeonSettlement() is null,
                "基础Boss格保留4246进图演出导航，不提前清图结算");
            That(f.Session.MoveFormalDungeon(2,0,1) is not null,"4246按原kind1导航至655628");
            loaded = f.Session.CompleteFormalDungeonLoading()!;
            That(loaded.Snapshot.CurrentMap.Id == 655628 && loaded.Monsters.Monsters.Single(row => row.IsBoss).MonsterId == 63821 &&
                f.Session.EnableFormalDungeonSettlement() is null,"末层保留真实dummy Boss，不从演出存在推定完成");
            var before = f.Read();
            var retirement = f.Session.ObserveFormalDungeonScriptedDeath(new(herba.ObjectKey,0x22,true,new string('B',64)),kills,growth);
            That(retirement is { Outcome: DungeonRunOutcome.Accepted, Receipt: not null, Reward: null } && retirement.ReportedTarget == herba &&
                QuestCompletionRepositoryTests.Same(before,f.Read()),"4247销毁已发布随行赫尔巴只退场，无击杀、经验或任务奖励："+
                $"outcome={retirement.Outcome},receipt={retirement.Receipt is not null},target={retirement.ReportedTarget?.MonsterId}");
            var replay = f.Session.ObserveFormalDungeonScriptedDeath(new(herba.ObjectKey,0x22,true,new string('B',64)),kills,growth);
            That(replay.Reward is null && replay.ClearFact is null && QuestCompletionRepositoryTests.Same(before,f.Read()),
                "随行赫尔巴重复退场不产生击杀、清图或持久奖励");
            var boss = loaded.Monsters.Monsters.Single(row => row.IsBoss);
            var death = f.Session.ObserveFormalDungeonScriptedDeath(new(boss.ObjectKey,0x22,true,new string('C',64)),kills,growth);
            That(death.ClearFact?.ClearMap?.Id == 655628 && f.Session.Phase == SessionPhase.Settlement,
                "4247实际报告终末63821死亡后，原事务才签发655628清图");
            That(f.Session.FinishQuest(7909,ushort.MaxValue,quests,parameters,items.Items,items.GoldLimits,items.EpicPieces,growth).Receipt is not null &&
                f.Read().CompletedQuests.Contains(7909) && QuestEligibility.Candidates(f.Read(),quests).Contains(7910),
                "悬崖上的花凭真实清图交付并解锁后继7910");
            That(f.Session.ReturnFromFormalDungeon() is not null,"任务完成沿原返回流程退出，不部署或启动客户端");
        }
    }
}
