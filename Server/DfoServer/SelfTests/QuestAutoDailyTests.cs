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
            foreach(var unsupported in new[]{quest with { IntData=[-4,0,-1,-1,150,-4,0,-1,-3,3] },quest with { IntData=[-4,38,-1,-1,150,-4,39,-1,-3,3] },quest with { IntData=[-4,39,-1,-1,512,-4,39,-1,-3,3] }})
                That(!QuestAutoDailyPlan.Supports(unsupported),"拒绝未知区域、互相矛盾的区域或越界数量");
            var regional = quest with { Id = 13031, IntData = [-4,6,-1,-1,100,-4,6,-1,-3,3],
                Definition = quest.Definition! with { Dungeons = [new(40,-1)] } };
            That(QuestAutoDailyPlan.Hunt(regional) && QuestHuntProgress.Initial(regional) == 1636 &&
                QuestAutoDailyPlan.MatchesDungeon(regional,40,0) && !QuestAutoDailyPlan.MatchesDungeon(regional,310,0),
                "同形地区每日按自身数量初始化，仅认自身地下城，不借地轨范围");
            That(!QuestAutoDailyPlan.Hunt(regional with { Definition = regional.Definition! with { Dungeons = [] } }) &&
                !QuestAutoDailyPlan.Hunt(regional with { CompleteNpc = 1 }) &&
                !QuestAutoDailyPlan.Hunt(regional with { RewardType = "[unknown]" }),
                "无明确地下城或专用领奖合同不同的每日保持拒绝");
            // 独立列举PVF数量及wire常量，不用生产编码结果构造预期。
            foreach (var (ordinary, encoded) in new[] {(100,1636),(110,1646),(120,1656),(130,1666),(140,1676),(150,1686)})
            {
                var counted = regional with { IntData = [-4,6,-1,-1,ordinary,-4,6,-1,-3,3] };
                var countedCatalog = new QuestCatalog(new() { [counted.Id] = counted });
                var countedActive = new ActiveMainQuest(0,checked((ushort)counted.Id),checked((uint)encoded)) { AcceptanceId = Guid.NewGuid() };
                var countedEntry = entry with { DungeonId = 40, QuestAcceptances = [countedActive] };
                That(QuestHuntProgress.Initial(counted) == encoded && (encoded & 511) == ordinary && (encoded >> 9) == 3,
                    "各地区原数量独立常量与双计数逆解一致："+ordinary);
                var remaining = countedActive;
                // 首领先减一次，再逐个普通，最后剩余首领，验证双通道互不借位。
                remaining = QuestHuntProgress.MonsterDeath([remaining],countedEntry,countedCatalog,monster with {SpawnType=3}).Single();
                That(remaining.ConditionState == ordinary+1024 && (remaining.ConditionState & 511) == ordinary &&
                    (remaining.ConditionState >> 9) == 2,"首领单步只递减高位计数");
                for (int n=1;n<=ordinary;n++)
                {
                    remaining = QuestHuntProgress.MonsterDeath([remaining],countedEntry,countedCatalog,monster).Single();
                    That(remaining.ConditionState == ordinary-n+1024 && (remaining.ConditionState >> 9) == 2,
                        "普通计数逐个下降且保留两次首领");
                }
                for (int n=1;n<=2;n++)
                {
                    remaining = QuestHuntProgress.MonsterDeath([remaining],countedEntry,countedCatalog,monster with {SpawnType=3}).Single();
                    That(remaining.ConditionState == (2-n)*512 && (remaining.ConditionState & 511) == 0,
                        "普通归零后首领计数独立归零");
                }
            }
            var regionalCatalog = new QuestCatalog(new() { [regional.Id] = regional });
            var regionalActive = new ActiveMainQuest(2,13031,1600) { AcceptanceId = Guid.NewGuid() };
            var regionalCharacter = selected with { ActiveMainQuests = [regionalActive] };
            That(QuestAutoDailyPlan.Prepare(regionalCharacter,regionalCatalog,day,true) is null,
                "地区每日同日重连保留已有数量和接取身份");
            var regionalReset = QuestAutoDailyPlan.Prepare(regionalCharacter,regionalCatalog,tomorrow,true)!;
            That(regionalReset.ActiveAfter is [{ QuestId:13031,ConditionState:1636 }] &&
                regionalReset.ActiveAfter[0].AcceptanceId != regionalActive.AcceptanceId &&
                QuestHuntProgress.MonsterDeath(regionalReset.ActiveAfter,entry with { DungeonId = 40,
                    QuestAcceptances = [regionalActive] },regionalCatalog,monster).Single() == regionalReset.ActiveAfter[0],
                "地区每日跨日重建自身计数且拒绝旧Run身份推进");
            var recommended = QuestEligibilityTests.Quest(8691) with { Grade = "[auto daily mission]",
                Condition = "[condition under clear]", SubType = 17, IntData = [-1,-1,3], CompleteNpc = -1,
                MinimumLevel = 1, MaximumLevel = 99 };
            var mixedCatalog = new QuestCatalog(new() { [quest.Id] = quest, [recommended.Id] = recommended });
            var addition = QuestAutoDailyPlan.Prepare(renewed,mixedCatalog,tomorrow,true)!;
            var combined = repository.AdvanceQuestCycle(owner,renewed,tomorrow,addition);
            That(combined.ActiveMainQuests.Length == 2 && combined.ActiveMainQuests.Single(row => row.QuestId == 13056) ==
                renewed.ActiveMainQuests.Single() && combined.ActiveMainQuests.Select(row => row.Slot).Distinct().Count() == 2,
                "新增推荐每日与已有地轨每日独立占槽，不重置旧任务身份或进度");
            int nextDay = QuestCycle.Day(DateTimeOffset.UtcNow.AddDays(2));
            var combinedReset = QuestAutoDailyPlan.Prepare(combined,mixedCatalog,nextDay,true)!;
            Sql("CREATE TRIGGER daily_second_abort AFTER INSERT ON character_active_main_quests WHEN NEW.quest_id=13056 BEGIN SELECT RAISE(ABORT,'second daily rollback'); END");
            Reject<SqliteException>(() => repository.AdvanceQuestCycle(owner,combined,nextDay,combinedReset), "多个每日第二槽插入失败");
            That(QuestCompletionRepositoryTests.Same(combined,repository.LoadCharacter(owner,0)), "跨日多个每日删除和重建一起回滚");
            Sql("DROP TRIGGER daily_second_abort");
            var bothRenewed = repository.AdvanceQuestCycle(owner,combined,nextDay,combinedReset);
            That(bothRenewed.ActiveMainQuests.Length == 2 && bothRenewed.ActiveMainQuests.All(row =>
                row.AcceptanceId != combined.ActiveMainQuests.Single(old => old.QuestId == row.QuestId).AcceptanceId),
                "多个每日同事务跨日换接取身份");
            Console.WriteLine($"PASS {Count-checks} 地轨自动每日兼容规则、真实周期事务和旧副本隔离断言");
            void Sql(string sql) { using var db=new SqliteConnection("Data Source="+Path.Combine(root,"game.db")+";Pooling=False"); db.Open(); using var c=db.CreateCommand();c.CommandText=sql;c.ExecuteNonQuery(); }
        }
        finally { if(Directory.Exists(root)) TestFiles.DeleteDirectory(root,true); }
    }

    // 资源层只验证接取后升级的领奖边界；真实死亡、提交与重放由所属TCP组验证。
    internal static void RunCompletionResources(PvfLib.PvfArchive archive, DfoServer.Infrastructure.ItemResources items)
    {
        int checks = Count;
        var quests = DfoServer.Testing.SelfTestCatalogs.Quests(archive);
        var parameters = DfoServer.Testing.SelfTestCatalogs.QuestRewardParameters(archive);
        var growth = DfoServer.Testing.SelfTestCatalogs.Growth(archive);
        var now = DateTimeOffset.UtcNow;
        foreach (var quest in quests.All.Where(quest => QuestAutoDailyPlan.Hunt(quest) && quest.MaximumLevel < 95))
        {
            var eligible = new Character(774,0,"DailyLevel",1,new Location(1,0,100,100),null)
            {
                Level = checked((byte)quest.MaximumLevel), QuestCycleDay = QuestCycle.Day(now),
                DungeonState = CharacterDungeonState.NewOrdinary,
                CompletedQuests = quest.Prerequisites.ToArray()
            };
            var assigned = QuestAutoDailyPlan.Prepare(eligible,quests,eligible.QuestCycleDay,true)!;
            var active = assigned.ActiveAfter.Single(row => row.QuestId == quest.Id);
            byte level = checked((byte)(quest.MaximumLevel+1));
            var completed = eligible with
            {
                Level = level, ActiveMainQuests = [active with { ConditionState = 0 }],
                Learning = new(growth.GetLevelStartExperience(level),[new(0,0,0,[]),new(1,0,0,[])])
            };
            QuestCompletionPreparation Prepare(Character character) => QuestCompletionPlan.Prepare(character,quest,
                ushort.MaxValue,quests,parameters,items.Items,items.GoldLimits,items.EpicPieces,growth,now);
            var result = Prepare(completed);
            That(result.Outcome == QuestCompletionOutcome.Accepted,
                $"{quest.Id}当日原接取完成后升过分配上限仍可领奖：{result.Reason}");
            That(!QuestEligibility.Evaluate(completed with { ActiveMainQuests = [] },quest,quests).Eligible &&
                !(QuestAutoDailyPlan.Prepare(completed with { ActiveMainQuests = [] },quests,eligible.QuestCycleDay,true)?
                    .ActiveAfter.Any(row => row.QuestId == quest.Id) ?? false),
                $"{quest.Id}超等级不能新分配，交付例外不放宽接取");
            foreach (var invalid in new[]
            {
                completed with { ActiveMainQuests = [] },
                completed with { ActiveMainQuests = [active with { ConditionState = 1 }] },
                completed with { ActiveMainQuests = [active with { ConditionState = 0, AcceptanceId = Guid.Empty }] },
                completed with { QuestCycleDay = QuestCycle.Day(now.AddDays(-1)) },
                completed with { CompletedQuests = [..completed.CompletedQuests,quest.Id] },
                completed with { Level = checked((byte)(quest.MinimumLevel-1)) }
            }) That(Prepare(invalid).Outcome != QuestCompletionOutcome.Accepted,
                $"{quest.Id}缺接取、未完成、空身份、过日、重复或低等级仍拒绝");
        }
        // 仅地区猎杀自动每日使用例外，普通任务和推荐地下城每日保留自己的接取上限检查。
        foreach (int id in new[] {3145,8691})
        {
            var original = quests.Get(id);
            var quest = original with { MinimumLevel = 1, MaximumLevel = 1 };
            byte level = checked((byte)(quest.MinimumLevel+1));
            var character = new Character(774,0,"OtherDaily",1,new Location(1,0,100,100),null)
            {
                Level = level, QuestCycleDay = QuestCycle.Day(now), CompletedQuests = quest.Prerequisites.ToArray(),
                ActiveMainQuests = [new(0,checked((ushort)id),0) { AcceptanceId = Guid.NewGuid() }]
            };
            var result = QuestCompletionPlan.Prepare(character,quest,ushort.MaxValue,quests,parameters,
                items.Items,items.GoldLimits,items.EpicPieces,growth,now);
            That(result.Outcome == QuestCompletionOutcome.Rejected && result.Reason == "任务接取身份、完成位或当前资格不符",
                $"{id}非地区每日不能借用超上限领奖例外");
        }
        Console.WriteLine($"PASS {Count-checks} 真实地区每日接取后升级领奖与资格拒绝断言");
    }

}
