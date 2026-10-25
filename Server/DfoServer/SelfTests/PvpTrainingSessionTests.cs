using System.Collections.Frozen;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Pvp;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Quests;
using Microsoft.Data.Sqlite;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Pvp;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class PvpTrainingSessionTests
{
    public static void Run()
    {
        int before=Count;
        string root=Path.Combine(Path.GetTempPath(),"S5A9-pvp-session-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var position=new Location(10,4,10,10);
            var map=new TownMap("synthetic.map",[new(new(0,0,100,100),[],[])],[],false,[]);
            var area=new TownArea(10,4,1,[],[],[],position,map,[],[]);
            var towns=new TownCatalog(new(){[(10,4)]=area},position,position,new(0,[],[]),[],new Dictionary<uint,uint[]>());
            var repository=new CharacterRepository(Path.Combine(root,"game.db"));
            var owners=new AccountOwners(); using var owner=owners.Acquire("training",()=>{});
            var quest=QuestEligibilityTests.Quest(2672) with {Condition="[custom multi condition]",Npc=323,CompleteNpc=-1,IntData=[1,1,1,0]};
            var active=new ActiveMainQuest(0,2672,262657){AcceptanceId=Guid.NewGuid()};
            var created=repository.Create(owner,new("Trainer",position,position){Level=60,ActiveMainQuests=[active]});
            var session=new GameSession(repository,towns,owner);
            session.Select(created.Slot);
            var mode=new PvpArcadeMode(80,0,2,99,[1201]);
            var opponents=new PvpArcadeOpponents(new Dictionary<int,System.Collections.Immutable.ImmutableArray<int>>{{1,[1201]}}.ToFrozenDictionary(),[1202]);
            PvpTrainingEntry? Plan(Character character)=>PvpTrainingEntry.Prepare(character,quest,mode,opponents) is { } entry
                ? entry with {Rounds=PvpArcadeCatalog.Resolve(mode,opponents,_=>true)!.Allowed} : null;
            That(session.BeginPvpTraining(_=>throw new Exception("不应规划")) is null,"未就绪不解析训练资源");
            session.MarkReady(); var original=session.Selected!;
            That(session.BeginPvpTraining(Plan,true) is null && session.GetState()==UserState.Town,"活动门禁禁止重叠入场");
            That(Plan(original with {ActiveMainQuests=[]}) is null &&
                Plan(original with {ActiveMainQuests=[active with {AcceptanceId=Guid.Empty}]}) is null,
                "未接取或缺少接取UUID不能规划训练");
            That(PvpTrainingEntry.Prepare(original,quest with {Npc=324},mode,opponents) is null &&
                PvpTrainingEntry.Prepare(original,quest,mode with {Rounds=0},opponents) is null,
                "资源声明不匹配或零轮次不能规划训练");
            var entered=session.BeginPvpTraining(Plan)!;
            That(entered is not null && session.GetState()==UserState.PvpRoom && session.CanPublishPvpTraining(entered),"正式Session安装原epoch训练实例");
            That(session.BeginPvpTraining(Plan) is null && session.EndPvpTraining(Guid.NewGuid()) is null,"重复进入及错误实例不能替换或结束训练");
            That(!session.LeaveRoom() && ReferenceEquals(original,session.Selected),"训练状态不能走普通离屋迁移或改资产");
            var left=session.EndPvpTraining(entered!.Run.InstanceId)!;
            That(left.Removed && session.GetState()==UserState.Town && session.CanPublishPvpTraining(left) &&
                !session.CanPublishPvpTraining(entered),"精确实例退出回原城镇，旧入场回执失效");
            var next=session.BeginPvpTraining(Plan)!;
            That(next.Run.InstanceId!=entered.Run.InstanceId && !session.CanPublishPvpTraining(left) &&
                session.EndPvpTraining(entered.Run.InstanceId) is null,"新实例不被旧退出或迟到回执影响");
            var report=new PvpArcadeRoundReport(0,1201,2,1,0,0,false,0,0);
            That(session.ReportPvpTraining(entered.Run,report) is null &&
                session.ReportPvpTraining(next.Run with {InstanceId=Guid.NewGuid()},report) is null,
                "上一实例和伪造等值快照都不能提交新训练报告");
            That(session.ReportPvpTraining(next.Run,report with {Event=1,Won=true}) is null,
                "会话必须先记录当前回合开始，不能直接提交胜利");
            var started=session.ReportPvpTraining(next.Run,report)!;
            That(!started.WonAllRounds && session.ReportPvpTraining(next.Run,report) is null,
                "开始提交替换快照，旧请求重放不能覆盖当前回合");
            var won=session.ReportPvpTraining(started.Run,report with {Event=1,Won=true})!;
            That(won.Run.Round.Round==2 && !won.WonAllRounds &&
                session.ReportPvpTraining(won.Run,report with {Round=2}) is null,"下一回合只允许自身资源池，不沿用第一轮固定APC");
            var second=session.ReportPvpTraining(won.Run,report with {Opponent=1202,Round=2})!;
            var finished=session.ReportPvpTraining(second.Run,report with {Opponent=1202,Round=2,Event=1,Won=true})!;
            That(finished.WonAllRounds && session.ReportPvpTraining(finished.Run,report) is null &&
                session.Selected!.ActiveMainQuests.Single().ConditionState==262657,
                "全轮胜利产生一次绑定实例事实，不在无任务事务时偷改条件或发奖");
            session.ReturnToRoster();
            That(!session.CanPublishPvpTraining(next) && session.GetState()==UserState.CharacterSelect,"小退清原训练实例");
            session.Select(created.Slot);session.MarkReady();
            That(session.GetState()==UserState.Town && !session.CanPublishPvpTraining(next) &&
                session.Selected!.ActiveMainQuests.Single().ConditionState==262657,"重登不恢复训练或伪造任务事实");
            var read=repository.LoadCharacter(owner,created.Slot);
            That(read.Revision==original.Revision && read.Position==original.Position &&
                read.ActiveMainQuests.Single().AcceptanceId==original.ActiveMainQuests.Single().AcceptanceId,
                "创建退出仅改会话生命周期，不改原角色位置/任务/持久化revision");
            That(session.BeginPvpTraining(character=>
            {
                var entry=Plan(character); session.ReturnToRoster(); return entry;
            }) is null,"锁外规划途中换角使原计划失效");
            session.Select(created.Slot);session.MarkReady();
            var last=session.BeginPvpTraining(Plan)!;
            using var replacement=owners.Acquire("training",()=>{});
            Reject<InvalidOperationException>(()=>session.EndPvpTraining(last.Run.InstanceId),"旧owner无法退出新代数");
            session.End();
            That(repository.LoadCharacter(replacement,created.Slot).ActiveMainQuests.Single().ConditionState==262657,
                "旧连接收尾不修改新owner任务资产");
            var catalog=new QuestCatalog(new(){[2672]=quest});
            var fresh=new GameSession(repository,towns,replacement);
            fresh.Select(created.Slot);fresh.MarkReady();
            var enteredAgain=fresh.BeginPvpTraining(Plan)!;
            var trigger=new QuestClientCondition(0x40,0,0);
            That(fresh.ObservePvpTrainingTrigger(2672,trigger,catalog) is null,"没有完整胜利事实时裸33不推进任务");
            var one=fresh.ReportPvpTraining(enteredAgain.Run,report)!;
            var two=fresh.ReportPvpTraining(one.Run,report with {Event=1,Won=true})!;
            var three=fresh.ReportPvpTraining(two.Run,report with {Opponent=1202,Round=2})!;
            var four=fresh.ReportPvpTraining(three.Run,report with {Opponent=1202,Round=2,Event=1,Won=true})!;
            That(fresh.ObservePvpTrainingTrigger(2672,trigger with {Channel=0x10},catalog) is null &&
                fresh.ObservePvpTrainingTrigger(2672,trigger with {Value=1},catalog) is null,
                "胜利不能冒充加点通道或客户端任意赋值");
            var unchanged=fresh.Selected!;
            Sql("CREATE TRIGGER pvp_rollback AFTER INSERT ON character_quest_lifecycle_receipts BEGIN SELECT RAISE(ABORT,'pvp rollback'); END");
            Reject<SqliteException>(()=>fresh.ObservePvpTrainingTrigger(2672,trigger,catalog),"真实事务故障保持冻结胜利计划以供原请求恢复");
            That(ReferenceEquals(fresh.Selected,unchanged) && repository.LoadCharacter(replacement,created.Slot).Revision==unchanged.Revision &&
                fresh.EndPvpTraining(four.Run.InstanceId) is null,"回滚不改任务或revision，待恢复操作不能被退出覆盖");
            Sql("DROP TRIGGER pvp_rollback");
            var committed=fresh.ObservePvpTrainingTrigger(2672,trigger,catalog)!;
            That(committed.Outcome==QuestLifecycleOutcome.Committed && fresh.CanPublishQuestLifecycle(committed) &&
                fresh.Selected!.ActiveMainQuests.Single().ConditionState==513 && fresh.Selected.Revision==unchanged.Revision+1,
                "原请求恢复只清第三个9位字段并同事务写一次revision");
            var duplicate=fresh.ObservePvpTrainingTrigger(2672,trigger,catalog)!;
            That(duplicate.Outcome==QuestLifecycleOutcome.Unchanged && ReferenceEquals(fresh.Selected,committed.Character),
                "已消费胜利重复确认无新事务，角色投影绑定新快照");
            fresh.EndPvpTraining(four.Run.InstanceId);
            That(fresh.ObservePvpTrainingTrigger(2672,trigger,catalog) is null,"退出后的迟到33不能复用胜利");
            fresh.ReturnToRoster();fresh.Select(created.Slot);fresh.MarkReady();
            That(fresh.Selected!.ActiveMainQuests.Single().ConditionState==513 && fresh.ReadPvpTraining() is null,
                "重登保留提交的通道进度而非旧训练实例");
            var combo=new PvpPracticeCombo(1,0,0,0,1,"连击1","Replay/Combo1.rep","",[5,0,-1]);
            PvpTrainingEntry? Combo(Character c)=>PvpTrainingEntry.PrepareCombo(c,quest,[combo]);
            That(PvpTrainingEntry.PrepareCombo(fresh.Selected!,quest,[]) is null &&
                PvpTrainingEntry.PrepareCombo(fresh.Selected!,quest,[combo with {Job=1}]) is null,
                "连招不借空表或其它职业声明建房");
            var comboRoom=fresh.BeginPvpTraining(Combo)!;
            var comboTrigger=new QuestClientCondition(0x20,0,0);
            That(comboRoom.Run.Entry.IsCombo && comboRoom.Run.Entry.Mode is null && comboRoom.Run.Entry.Rounds.IsEmpty &&
                fresh.ReportPvpTraining(comboRoom.Run,report) is null,"连招是独立实例，不伪造街机轮次或接受1287");
            That(fresh.ObservePvpTrainingTrigger(2672,trigger,catalog) is null &&
                fresh.ObservePvpTrainingTrigger(2672,comboTrigger with {Value=1},catalog) is null &&
                fresh.ObservePvpTrainingTrigger(2673,comboTrigger,catalog) is null,"连招仅接收原任务零值20通道，不能冒充街机胜利");
            var comboBefore=fresh.Selected!;
            Sql("CREATE TRIGGER combo_rollback AFTER INSERT ON character_quest_lifecycle_receipts BEGIN SELECT RAISE(ABORT,'combo rollback'); END");
            Reject<SqliteException>(()=>fresh.ObservePvpTrainingTrigger(2672,comboTrigger,catalog),"连招结果与任务事务失败完整回滚");
            That(ReferenceEquals(fresh.Selected,comboBefore) && repository.LoadCharacter(replacement,created.Slot).Revision==comboBefore.Revision &&
                fresh.EndPvpTraining(comboRoom.Run.InstanceId) is null,"失败连招冻结原计划，不允许退出清掉待恢复结果");
            Sql("DROP TRIGGER combo_rollback");
            var comboDone=fresh.ObservePvpTrainingTrigger(2672,comboTrigger,catalog)!;
            That(comboDone.Outcome==QuestLifecycleOutcome.Committed && comboDone.Character.ActiveMainQuests.Single().ConditionState==1 &&
                comboDone.Character.Revision==comboBefore.Revision+1,"原连招报告只清中间9位，不清技能或直接发奖");
            That(fresh.ObservePvpTrainingTrigger(2672,comboTrigger,catalog)!.Outcome==QuestLifecycleOutcome.Unchanged &&
                ReferenceEquals(fresh.Selected,comboDone.Character),"连招重复报告无新增revision或重复奖励");
            fresh.EndPvpTraining(comboRoom.Run.InstanceId);
            That(fresh.ObservePvpTrainingTrigger(2672,comboTrigger,catalog) is null,"离房后迟到连招报告不生效");
            fresh.ReturnToRoster();fresh.Select(created.Slot);fresh.MarkReady();
            That(fresh.Selected!.ActiveMainQuests.Single().ConditionState==1 && fresh.ReadPvpTraining() is null,
                "重登保留已提交连招进度但不恢复旧练习实例");
            fresh.End();
            void Sql(string sql)
            {
                using var db=new SqliteConnection("Data Source="+Path.Combine(root,"game.db")+";Pooling=False");db.Open();
                using var command=db.CreateCommand();command.CommandText=sql;command.ExecuteNonQuery();
            }

        }
        finally { Directory.Delete(root,true); }
        Console.WriteLine($"PASS {Count-before} 决斗训练Session生命周期、事务与身份断言");
    }
}
