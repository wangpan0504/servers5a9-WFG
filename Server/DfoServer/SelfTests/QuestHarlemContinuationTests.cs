using DfoServer.Game.Characters;
using DfoServer.Game.Quests;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class QuestHarlemContinuationTests
{
    public static void Run(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters, ItemResources items, QuestCatalog quests)
    {
        int checks = Count;
        var skills = Testing.SelfTestCatalogs.SkillLearning(pvf);
        var growth = Testing.SelfTestCatalogs.Growth(pvf,skills);
        var parameters = Testing.SelfTestCatalogs.QuestRewardParameters(pvf);
        var completed = new HashSet<int>();
        AddPrerequisites(6043);
        // 当前哈林角色的跨大陆交通前史；不能把仅末链前置闭包当作完整成长档案。
        completed.Add(towns.Dock.OpenQuest);
        foreach(var stop in towns.Dock.Stops.Where(stop=>stop.Phase==0 && stop.Level<=95))
            completed.Add(stop.Quest);
        using var f = new QuestSceneTests.Fixture(towns,characters,skills,growth,quests,false,95,
            completed.Order().ToArray(),position:new(35,2,500,300));
        foreach (ushort id in new ushort[] {6043,6044,6045})
        {
            var quest = quests.Get(id);
            var accepted = f.Session.ChangeQuest(id,QuestLifecycleAction.Accept,quests,items.Items,items.GoldLimits,items.EpicPieces);
            That(accepted?.Commit is not null,"哈林末链真实接取 "+id+": "+accepted?.Reason);
            if (quest.Condition == "[meet npc]") AtNpc(f,quest);
            var advanced = f.Session.ChangeQuest(id,QuestLifecycleAction.Condition,quests,items.Items,items.GoldLimits,items.EpicPieces,new(null,0,0));
            That(advanced?.Commit is not null,"哈林末链真实场景条件推进 "+id+": "+advanced?.Reason);
            var finished = f.Session.FinishQuest(id,ushort.MaxValue,quests,parameters,items.Items,items.GoldLimits,items.EpicPieces,growth);
            That(finished.Receipt?.Commit is not null && f.Read().CompletedQuests.Contains(id),"哈林末链领奖完成 "+id+": "+finished.Outcome);
        }
        var after = f.Read();
        That(QuestQuickClearPlan.TryCreateManual(after with {GrowType=0x25},Guid.NewGuid(),quests,
            System.Collections.Immutable.ImmutableDictionary<int,byte>.Empty,out var manual,out string manualReason,towns) &&
            manual!.CompletedAfter.Contains(3280),"95级已觉醒角色手册规划仍补齐39级交通前史："+manualReason);
        var successors = quests.All.Where(q => q.Definition?.PrerequisiteGroups.Any(g => g.Contains(6045)) == true).ToArray();
        var candidates = QuestEligibility.Candidates(after,quests);
        var failures=new List<string>();
        foreach (var quest in successors.Where(q => candidates.Contains(q.Id) && q.Condition is "[meet npc]" or "[clear map]" && q.Id != 6217))
        {
            using var continuation = new QuestSceneTests.Fixture(towns,characters,skills,growth,quests,false,95,
                after.CompletedQuests,position:after.Position);
            if (quest.Id is 6235 or 7913)
            {
                var targetArea=quest.Id==6235?towns.Areas[((byte)38,(byte)5)]:towns.Areas[((byte)43,(byte)0)];
                That(QuestEligibility.Evaluate(after,quest,quests).Eligible && targetArea.Quests.Contains(3280) &&
                    towns.DestinationAdmission(new(targetArea.Town,targetArea.Area,0,0),after.Level,QuestEligibility.Completed(after))=="target-quest-required" &&
                    !after.CompletedQuests.Contains(3280) && quests.Get(3280).MinimumLevel==39,
                    "仅哈林尾链不能伪造此前39级两国和解完成位");
                // 明确构造另一份已有3280前史的角色；此状态差异不是生产修复，也不证明玩家历史。
                continuation.Sql("INSERT INTO character_completed_quests(character_id,quest_id) VALUES($p0,3280)",continuation.Read().Id);
                continuation.Session.ReturnToRoster(); continuation.Session.Select(0,resumeTown:true); continuation.Session.MarkReady();
            }
            try {
                if (quest.Condition=="[meet npc]") CompleteNpc(continuation,quest);
                else CompleteDungeon(continuation,quest);
            } catch(Exception ex) { failures.Add(quest.Id+":"+ex.Message); Console.WriteLine("HARLEM-BLOCKED "+quest.Id+":"+ex.Message); }
        }
        foreach (byte answer in new byte[] {0,1})
        {
            using var branch = new QuestSceneTests.Fixture(towns,characters,skills,growth,quests,false,95,
                after.CompletedQuests,position:after.Position);
            var question=quests.Get(6217);
            That(branch.Session.ChangeQuest(6217,QuestLifecycleAction.Accept,quests,items.Items,items.GoldLimits,items.EpicPieces)?.Commit is not null,
                "哈林后旅程选择可真实接取");
            AtNpc(branch,question);
            That(branch.Session.ChangeQuest(6217,QuestLifecycleAction.Condition,quests,items.Items,items.GoldLimits,items.EpicPieces,new(null,answer,0))?.Commit is not null,
                "哈林后长短旅程真实回答 "+answer);
            That(branch.Session.FinishQuest(6217,ushort.MaxValue,quests,parameters,items.Items,items.GoldLimits,items.EpicPieces,growth).Receipt?.Commit is not null,
                "旅程回答真实落库 "+answer);
            var next=quests.All.Where(q=>q.Definition?.RequiredAnswers.Any(a=>a.QuestId==6217&&a.Answer==answer)==true).ToArray();
            foreach (var quest in next)
            {
                That(QuestEligibility.Evaluate(branch.Read(),quest,quests).Eligible,"旅程回答只开放实际后继 "+quest.Id);
                if (quest.Condition == "[meet npc]") CompleteNpc(branch,quest);
                else if (quest.Condition == "[clear map]") CompleteDungeon(branch,quest);
            }
        }
        using (var skip = new QuestSceneTests.Fixture(towns,characters,skills,growth,quests,false,95,
            after.CompletedQuests,position:after.Position))
        {
            That(skip.Session.ChangeQuest(6217,QuestLifecycleAction.Accept,quests,items.Items,items.GoldLimits,items.EpicPieces)?.Commit is not null,"实际跳过分支先接问题");
            var connections=Testing.SelfTestCatalogs.QuestConnections(pvf);
            That(connections.TryGetQuestion(6217,out var unit),"真实普雷章节有唯一关联任务单元");
            skip.Sql("INSERT INTO accounts(account_id,salt,password_hash,iterations,disabled) VALUES('scene-quest',X'01',X'01',1,0)");
            var cleared=skip.Session.QuickClearQuest(checked((uint)unit.ParentKey),quests,connections);
            That(cleared?.Commit is not null,"实际普雷章节跳过事务："+cleared?.Reason);
            That(skip.Read().QuestAnswers.TryGetValue(6217,out byte answer)&&answer==2,"跳过回答2真实保存");
        }
        That(failures.Count==0,"所有后继均实际完成："+string.Join(";",failures));
        Console.WriteLine($"PASS {Count-checks} 哈林尾链与后继真实事务断言（非客户端实机）");

        void CompleteDungeon(QuestSceneTests.Fixture fixture,StoryQuest quest)
        {
            ushort id=checked((ushort)quest.Id);
            uint dungeon=checked((uint)quest.Definition!.Dungeons[0].DungeonId);
            That(fixture.Session.ChangeQuest(id,QuestLifecycleAction.Accept,quests,items.Items,items.GoldLimits,items.EpicPieces)?.Commit is not null,"后继副本真实接取 "+id);
            fixture.TravelTo(towns,MiddleQuestEntryTests.Entrance(towns,quest,dungeon).Position,includeTransport:true);
            var dungeons=Testing.SelfTestCatalogs.Dungeons(pvf);
            var rooms=Testing.SelfTestCatalogs.Rooms(pvf);
            var kills=Testing.SelfTestCatalogs.KillExperience(pvf);
            var clears=Testing.SelfTestCatalogs.ClearExperience(pvf);
            var opening=fixture.Session.EnterFormalDungeon(dungeon,id,0,dungeons,rooms);
            That(opening is not null,"后继实际任务地下城入场 "+id);
            var path=QuestStoryRouteTests.Path(opening!.Entry.Maze.StaticTopology,opening.Entry.StartMap.Position,opening.Entry.BossMap.Position);
            var receipt=fixture.Session.CompleteFormalDungeonLoading()!;
            foreach(var point in path)
            {
                foreach(var monster in receipt.Monsters.Monsters.Where(row=>row.CanReportDeath).OrderBy(row=>row.IsBoss))
                    That(fixture.Session.ObserveFormalDungeonDeath(FormalDungeonSessionTests.Observation(receipt,monster),kills,growth).Outcome==DungeonRunOutcome.Accepted,"后继实际房间死亡 "+id);
                if(point==opening.Entry.BossMap.Position) break;
                var next=path[Array.IndexOf(path,point)+1];
                That(fixture.Session.MoveFormalDungeon(checked((byte)next.X),checked((byte)next.Y)) is not null,"后继实际门墙换房 "+id);
                receipt=fixture.Session.CompleteFormalDungeonLoading()!;
            }
            That(fixture.Session.Phase==SessionPhase.Settlement,"后继真实Boss死亡进入结算 "+id);
            That(fixture.Session.SettleFormalDungeon(fixture.Read().Id,99,99,[1,2,3,4],dungeons,clears,growth).Outcome==DungeonRunOutcome.Accepted,"后继实际结算 "+id);
            var finished=fixture.Session.FinishQuest(id,ushort.MaxValue,quests,parameters,items.Items,items.GoldLimits,items.EpicPieces,growth);
            That(finished.Receipt?.Commit is not null,"后继实际任务领奖 "+id+": "+finished.Reason);
        }
        void CompleteNpc(QuestSceneTests.Fixture fixture,StoryQuest quest)
        {
            ushort id=checked((ushort)quest.Id);
            var accepted=fixture.Session.ChangeQuest(id,QuestLifecycleAction.Accept,quests,items.Items,items.GoldLimits,items.EpicPieces);
            That(accepted?.Commit is not null,"哈林后实际接取 "+id+": "+accepted?.Reason);
            if (fixture.Read().ActiveMainQuests.Single(q=>q.QuestId==id).ConditionState != 0)
            {
                AtNpc(fixture,quest);
                var advanced=fixture.Session.ChangeQuest(id,QuestLifecycleAction.Condition,quests,items.Items,items.GoldLimits,items.EpicPieces,new(null,0,0));
                That(advanced?.Commit is not null,"哈林后真实NPC条件 "+id+": "+advanced?.Reason);
            }
            var result=fixture.Session.FinishQuest(id,ushort.MaxValue,quests,parameters,items.Items,items.GoldLimits,items.EpicPieces,growth);
            That(result.Receipt?.Commit is not null,"哈林后真实完成 "+id+": "+result.Outcome+"/"+result.Reason);
        }
        void AddPrerequisites(int id)
        {
            if (!quests.TryGet(id,out var quest)) return;
            foreach (int previous in quest.Definition?.PrerequisiteGroups.SelectMany(g=>g) ?? [])
                if (previous>0 && completed.Add(previous)) AddPrerequisites(previous);
        }
        void AtNpc(QuestSceneTests.Fixture fixture,StoryQuest quest)
        {
            int npc=QuestClientSceneProgress.ResolveNpc(fixture.Read(),quest.IntData[0],quest);
            var done=QuestEligibility.Completed(fixture.Read());
            var locations=towns.Areas.Values.Where(a=>a.Map(done).Npcs.Any(n=>n.Id==npc)).ToArray();
            That(locations.Length>0,"任务目标NPC在当前剧情地图存在 "+quest.Id+"/"+npc);
            var reachable=fixture.TravelPaths(towns,includeTransport:true);
            var area=locations.Where(a=>reachable.ContainsKey((a.Town,a.Area))).OrderBy(a=>a.Town==fixture.Read().Position.Town?0:1).FirstOrDefault();
            if(area is null) Console.WriteLine("HARLEM-TRAVEL-DIAG reachable="+string.Join(",",reachable.Keys)+" targets="+string.Join(";",locations.Select(a=>$"{a.Town}/{a.Area}:req={string.Join(',',a.Quests)} any={string.Join(',',a.AnyQuests)} except={string.Join(',',a.ExceptQuests)} admission={towns.DestinationAdmission(new(a.Town,a.Area,0,0),fixture.Read().Level,done)}")));
            That(area is not null,"当前实际城镇连接能到任务NPC "+quest.Id+"/"+npc);
            var bounds=area!.Map(done).Bounds.First(b=>b.Conditions.Length==0&&b.Quests.All(done.Contains)).Bounds;
            fixture.TravelTo(towns,new(area.Town,area.Area,checked((ushort)(bounds.X+bounds.Width/2)),checked((ushort)(bounds.Y+bounds.Height/2))),includeTransport:true);
        }
    }
}
