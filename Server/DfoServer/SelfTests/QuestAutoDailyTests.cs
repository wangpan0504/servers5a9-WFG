using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Towns;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Quests;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class QuestAutoDailyTests
{
    internal static StoryQuest Quest() => QuestEligibilityTests.Quest(13056) with
    {
        Grade = "[auto daily mission]", Condition = "[hunt monster]", MinimumLevel = 87, MaximumLevel = 90,
        IntData = [-4,39,-1,-1,150,-4,39,-1,-3,3], CompleteNpc = -1,
        Definition = new([]) { Job = "[all]", Dungeons = [new(310,-1),new(311,-1)] }
    };

    public static void RunCore()
    {
        int checks = Count;
        string root = Path.Combine(Path.GetTempPath(),"S5A9-auto-daily-"+Guid.NewGuid().ToString("N"));
        var quest = Quest(); var catalog = new QuestCatalog(new() { [quest.Id] = quest });
        using var owner = new AccountOwners().Acquire("auto-daily",() => { });
        try
        {
            var repository = new CharacterRepository(Path.Combine(root,"game.db"),catalog);
            var point = new Location(1,0,100,100);
            var created = repository.Create(owner,new("Daily",point,point) { Level = 87 });
            int day = QuestCycle.Day(DateTimeOffset.UtcNow);
            var plan = QuestAutoDailyPlan.Prepare(created,catalog,day,true)!;
            That(plan.ActiveAfter is [{ QuestId:13056,ConditionState:1686 }],"每日初值来自两个PVF数量150/3的兼容通道");
            var selected = repository.AdvanceQuestCycle(owner,created,day,plan);
            var active = selected.ActiveMainQuests.Single();
            That(active.AcceptanceId != Guid.Empty && QuestEligibility.Visible(selected,catalog).Contains(13056) &&
                QuestAutoDailyPlan.Prepare(selected,catalog,day,true) is null,"选角分配真实接取，同日重连不换身份");
            foreach (byte level in new byte[] {86,87,90,91})
                That((QuestAutoDailyPlan.Prepare(created with { Level=level },catalog,day,true) is not null) == (level is >=87 and <=90),
                    "自动分配按QST等级边界："+level);
            That(QuestAutoDailyPlan.Prepare(created with { ActiveMainQuests=Enumerable.Range(0,30).Select(i=>new ActiveMainQuest((byte)i,(ushort)(100+i),1)).ToArray() },catalog,day,true) is null,
                "30个任务槽已满时延期，不覆盖其它任务");
            var map = new DungeonMap("boss",new(0,0),1,"map/daily-fixture.map");
            var maze = new DungeonMaze(0,1,1,"fixture",[map.Position],[map.Position],[map],[]);
            var entry = new DungeonEntryPlan(selected.Id,selected.Revision,310,0,0,0,maze,map,map) { QuestAcceptances=[active] };
            var monster = new DungeonPlannedMonster(0,10,100,87,null);
            var normal = QuestHuntProgress.MonsterDeath([active],entry,catalog,monster).Single();
            var boss = QuestHuntProgress.MonsterDeath([active],entry,catalog,monster with { SpawnType=3 }).Single();
            That(normal.ConditionState == 1685 && boss.ConditionState == 1174,"普通与Boss分开减量，Boss不同时推进普通目标");
            foreach (var excluded in new[] {monster with { IsFriendly=true },monster with { IsBlocking=false },monster with { IsDummyMapBoss=true },monster with { MonsterId=50001 }})
                That(QuestHuntProgress.MonsterDeath([active],entry,catalog,excluded).Single()==active,"友军、演出和排除模板不推进每日");
            That(QuestHuntProgress.MonsterDeath([active],entry with { DungeonId=3 },catalog,monster).Single()==active &&
                QuestHuntProgress.AiCharacterDeath([active],entry,catalog,100).Single()==active,"其它副本/APC不混入普通怪目标");
            var progress = active;
            for(int i=0;i<153;i++) progress=QuestHuntProgress.MonsterDeath([progress],entry,catalog,monster with { SpawnType=(byte)(i<150?0:3) }).Single();
            That(progress.ConditionState==0 && QuestHuntProgress.MonsterDeath([progress],entry,catalog,monster).Single()==progress,"完整150+3事件归零且不下溢，不自动发奖励");
            int tomorrow=QuestCycle.Day(DateTimeOffset.UtcNow.AddDays(1));
            var reset=QuestAutoDailyPlan.Prepare(selected,catalog,tomorrow,true)!;
            Sql("CREATE TRIGGER daily_abort AFTER INSERT ON character_active_main_quests BEGIN SELECT RAISE(ABORT,'daily rollback'); END");
            Reject<SqliteException>(()=>repository.AdvanceQuestCycle(owner,selected,tomorrow,reset),"每日重建失败");
            That(QuestCompletionRepositoryTests.Same(selected,repository.LoadCharacter(owner,0)),"旧槽删除、新身份和周期revision一起回滚");
            Sql("DROP TRIGGER daily_abort");
            var renewed=repository.AdvanceQuestCycle(owner,selected,tomorrow,reset);
            That(renewed.ActiveMainQuests.Single().AcceptanceId!=active.AcceptanceId &&
                QuestHuntProgress.MonsterDeath(renewed.ActiveMainQuests,entry,catalog,monster).Single()==renewed.ActiveMainQuests.Single(),
                "跨日换身份，旧Run冻结接取不能推进新一天");
            Reject<InvalidOperationException>(()=>repository.AdvanceQuestCycle(owner,selected,tomorrow,reset),"旧revision重放不能再次分配");
            That(QuestAutoDailyPlan.Prepare(selected,catalog,tomorrow,false)!.ActiveAfter.Length==0,"副本中跨日只撤旧任务，回城才分配");
            var claimed=selected with { ActiveMainQuests=[],CompletedQuests=[13056],QuestDailyClaims=selected.QuestDailyClaims.SetItem(13056,day) };
            That(QuestAutoDailyPlan.Prepare(claimed,catalog,day,true) is null && QuestAutoDailyPlan.Prepare(claimed,catalog,tomorrow,true) is not null,
                "已领奖当天不再分配，下一周期恢复资格");
            foreach(var unsupported in new[]{quest with { Id=13057 },quest with { IntData=[-4,38,-1,-1,150,-4,39,-1,-3,3] },quest with { IntData=[-4,39,-1,-1,512,-4,39,-1,-3,3] }})
                That(!QuestAutoDailyPlan.Supports(unsupported),"不扩大到其它任务、区域或越界数量");
            Console.WriteLine($"PASS {Count-checks} 地轨自动每日兼容规则、真实周期事务和旧副本隔离断言");
            void Sql(string sql) { using var db=new SqliteConnection("Data Source="+Path.Combine(root,"game.db")+";Pooling=False"); db.Open(); using var c=db.CreateCommand();c.CommandText=sql;c.ExecuteNonQuery(); }
        }
        finally { if(Directory.Exists(root)) Directory.Delete(root,true); }
    }
}
