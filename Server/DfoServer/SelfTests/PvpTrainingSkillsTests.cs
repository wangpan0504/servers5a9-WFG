using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Pvp;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class PvpTrainingSkillsTests
{
    public static void Run()
    {
        int before=Count;
        string root=Path.Combine(Path.GetTempPath(),"S5A9-pvp-skills-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            string path=Path.Combine(root,"game.db");
            var repo=new CharacterRepository(path);using var owner=new AccountOwners().Acquire("trainer",()=>{});
            var learning=new CharacterLearningState(0,[new(0,100,0,[]),new(1,100,0,[])]) {BonusSp=0,BonusTp=0};
            var quest=QuestEligibilityTests.Quest(2672) with {Condition="[custom multi condition]",Npc=323,CompleteNpc=-1,IntData=[1,1,1,0]};
            var quests=new QuestCatalog(new(){[2672]=quest});
            var original=repo.Create(owner,new("SkillTrainer",new Location(10,4,10,10),new Location(10,1,10,10))
            {Level=60,InitialLearning=learning,ActiveMainQuests=[new(0,2672,262657){AcceptanceId=Guid.NewGuid()}]});
            string normal=JsonSerializer.Serialize(original.Learning);
            var definition=new SkillLearningDefinition(0,5,"skill/fixture.skl",SkillType.Active,[7],
                [3,3,3,3,3,3],[3,3,3,3,3,3],1,1,[],new int[6],false,false,[],3,6);
            SkillLearningResult Learn(CharacterLearningState source,SkillLearningCommand command)=>
                SkillLearning.Plan(0,0,60,source,command,(_,_)=>definition);
            var request=new SkillLearningCommand(0,3,[new(5,false,1)]);
            That(PvpTrainingSkillPlan.Prepare(original with {Position=new(38,1,10,10)},request,quests,Learn) is null &&
                PvpTrainingSkillPlan.Prepare(original,request with {Context=1},quests,Learn) is null &&
                PvpTrainingSkillPlan.Prepare(original,request with {Mode=2},quests,Learn) is null,
                "训练加点限定原训练地区、主树和已证模式");
            That(PvpTrainingSkillPlan.Prepare(original with {ActiveMainQuests=[]},request,quests,Learn) is null,
                "无真实训练接取不能创建训练技能事实");
            var plan=PvpTrainingSkillPlan.Prepare(original,request,quests,Learn)!;
            That(plan.After.Learning.Trees[0].Sp==93 && plan.After.Learning.Trees[0].Skills.Single().Level==1 &&
                plan.ActiveAfter.Single().ConditionState==262656 && JsonSerializer.Serialize(original.Learning)==normal,
                "真实学习扣7SP只在独立快照，自动模式只清第一计数");
            Sql("CREATE TRIGGER training_abort AFTER INSERT ON character_pvp_training_skills BEGIN SELECT RAISE(ABORT,'training rollback'); END");
            Reject<SqliteException>(()=>repo.CommitPvpTrainingSkills(owner,plan),"训练快照写失败整个学习与任务事务回滚");
            var rolled=repo.LoadCharacter(owner,original.Slot);
            That(rolled.PvpTrainingSkills is null && rolled.Revision==original.Revision && rolled.ActiveMainQuests.Single().ConditionState==262657,
                "故障不改普通树、训练计数或角色revision");
            Sql("DROP TRIGGER training_abort");
            var committed=repo.CommitPvpTrainingSkills(owner,plan);
            var duplicate=repo.CommitPvpTrainingSkills(owner,plan);
            That(committed.Revision==original.Revision+1 && duplicate.Revision==committed.Revision &&
                JsonSerializer.Serialize(committed.Learning)==normal && JsonSerializer.Serialize(duplicate.Learning)==normal &&
                duplicate.PvpTrainingSkills!.AcceptanceId==original.ActiveMainQuests.Single().AcceptanceId,
                "同一冻结operation只提交一次，普通双树完整不变且事实绑定原接取");
            var reloaded=new CharacterRepository(path).LoadCharacter(owner,original.Slot);
            That(reloaded.PvpTrainingSkills!.Learning.Trees[0].Sp==93 && reloaded.ActiveMainQuests.Single().ConditionState==262656,
                "重开数据库恢复独立训练状态与同事务任务进度");
            var manual=PvpTrainingSkillPlan.Prepare(reloaded,request with {Mode=0},quests,Learn)!;
            repo.CommitPvpTrainingSkills(owner,manual);
            Reject<InvalidOperationException>(()=>repo.CommitPvpTrainingSkills(owner,plan),"后续训练改变后不能用旧计划恢复旧投影");
            var copyCharacter=repo.Create(owner,new("CopyTrainer",original.Position,original.Return)
                {Level=60,InitialLearning=learning with {SelectedTree=1},CompletedQuests=[2674]});
            var training=learning with {Trees=Array.AsReadOnly(new CharacterSkillTree[]{
                new(0,79,0,[new(11,5,3)]),new(1,100,0,[])})};
            var profile=new PvpTrainingSkills(training,2674,Guid.NewGuid()){Arena=learning with {SelectedTree=1}};
            using(var db=new SqliteConnection("Data Source="+path+";Pooling=False"))
            {
                db.Open();using var command=db.CreateCommand();
                command.CommandText="INSERT INTO character_pvp_training_skills VALUES($id,$state,$operation,$before)";
                command.Parameters.AddWithValue("$id",copyCharacter.Id);command.Parameters.AddWithValue("$state",JsonSerializer.Serialize(profile));
                command.Parameters.AddWithValue("$operation",Guid.NewGuid().ToString("N"));command.Parameters.AddWithValue("$before",copyCharacter.Revision);
                command.ExecuteNonQuery();
            }
            copyCharacter=repo.LoadCharacter(owner,copyCharacter.Slot);
            profile=copyCharacter.PvpTrainingSkills!;
            That(PvpTrainingSkillPlan.Copy(copyCharacter with {CompletedQuests=[]},1) is null &&
                PvpTrainingSkillPlan.Copy(copyCharacter,0) is null,"没有完成身份或非法选择不能复制");
            var cancelled=PvpTrainingSkillPlan.Copy(copyCharacter,2)!;
            That(cancelled.After.Arena==profile.Arena && cancelled.After.CopyChoice==2,"取消只保存决定，正式PK双页不改");
            var copy=PvpTrainingSkillPlan.Copy(copyCharacter,1)!;
            That(copy.After.Arena!.SelectedTree==1 && copy.After.Arena.Trees[0]==profile.Arena!.Trees[0] &&
                copy.After.Arena.Trees[1].Skills.Single()==new LearnedSkill(11,5,3) && copy.After.Arena.Trees[1].Sp==79 &&
                copy.After.Learning.SelectedTree==-1 && JsonSerializer.Serialize(copy.Expected.Learning)==JsonSerializer.Serialize(copyCharacter.Learning),
                "源页0复制到目标活动页1，保留普通双页、源页选择及正式PK备用页");
            Sql("CREATE TRIGGER copy_abort AFTER UPDATE ON character_pvp_training_skills BEGIN SELECT RAISE(ABORT,'copy rollback'); END");
            Reject<SqliteException>(()=>repo.CommitPvpTrainingSkills(owner,copy),"复制末段失败回滚技能和决定");
            That(repo.LoadCharacter(owner,copyCharacter.Slot).Revision==copyCharacter.Revision &&
                repo.LoadCharacter(owner,copyCharacter.Slot).PvpTrainingSkills!.CopyChoice==0,"复制失败没有半份决定或角色版本");
            Sql("DROP TRIGGER copy_abort");
            var copied=repo.CommitPvpTrainingSkills(owner,copy);
            That(repo.CommitPvpTrainingSkills(owner,copy).Revision==copied.Revision &&
                PvpTrainingSkillPlan.Copy(copied,1) is null && PvpTrainingSkillPlan.Copy(copied,2) is null,
                "原冻结复制可精确重放，新决定不能覆盖已完成选择");
            var restored=new CharacterRepository(path).LoadCharacter(owner,copyCharacter.Slot);
            That(restored.PvpTrainingSkills!.CopyAcceptanceId==profile.AcceptanceId && restored.PvpTrainingSkills.Arena!.Trees[1].Skills.Single().Level==3,
                "重开保留原任务接取身份、复制决定、等级及布局");
            var locked=restored with {Learning=restored.Learning with {SelectedTree=-1},PvpTrainingSkills=restored.PvpTrainingSkills with
                {Arena=restored.PvpTrainingSkills.Arena! with {SelectedTree=-1}}};
            That(PvpTrainingSkills.ArenaSource(locked).SelectedTree==-1 &&
                PvpTrainingSkills.ArenaSource(locked with {Learning=locked.Learning with {SelectedTree=1}}).SelectedTree==0 &&
                PvpTrainingSkills.ArenaSource(restored).SelectedTree==1,
                "方案权益继承普通域开通状态，正式PK各自保留活动页选择");
            That(PvpTrainingSkills.ArenaSource(restored with {GrowType=1})==restored.Learning &&
                PvpTrainingSkills.ArenaSource(restored with {Job=1})==restored.Learning &&
                PvpTrainingSkills.ArenaSource(restored with {GrowType=16}).Trees[1].Skills.Single().Level==3,
                "换职业分支或职业立即舍弃旧PK技能，觉醒保留同分支方案");
            var rewarded=restored with {Learning=restored.Learning with {BonusSp=3,BonusTp=2}};
            That(PvpTrainingSkills.ArenaSource(rewarded).Trees.Zip(restored.PvpTrainingSkills.Arena.Trees)
                .All(pair=>pair.First.Sp==pair.Second.Sp+3 && pair.First.Tp==pair.Second.Tp+2) &&
                PvpTrainingSkills.Source(rewarded).Trees.Zip(restored.PvpTrainingSkills.Learning.Trees)
                .All(pair=>pair.First.Sp==pair.Second.Sp+3 && pair.First.Tp==pair.Second.Tp+2),
                "额外SP和末项2TP立刻反映到训练及PK各页余额，不等下一次加点或重登");
            DatabaseSchemaTests.RewindRaidLifecycle(path);
            Sql(DatabaseSchemaTests.GuildExtensionFixtureRewindSql + "DROP TABLE account_pvp_private_characters; DROP TABLE character_operation_hope_reward_receipts; ALTER TABLE characters DROP COLUMN selected_skill_tree; PRAGMA user_version=95");
            var existing=new CharacterRepository(path).LoadCharacter(owner,original.Slot);
            That(existing.PvpTrainingSkills is not null && existing.Revision==reloaded.Revision+1,
                "升级遇到已存在的同合同训练表保留既有快照，不重复发点或清数据");
            // 精确v94升级：旧库没有训练表，不伪造历史训练或改普通双树。
            DatabaseSchemaTests.RewindRaidLifecycle(path);
            Sql(DatabaseSchemaTests.GuildExtensionFixtureRewindSql + "DROP TABLE account_pvp_private_characters; DROP TABLE character_operation_hope_reward_receipts; DROP TABLE character_pvp_training_skills; ALTER TABLE characters DROP COLUMN selected_skill_tree; PRAGMA user_version=94");
            var upgraded=new CharacterRepository(path).LoadCharacter(owner,original.Slot);
            That(upgraded.PvpTrainingSkills is null && JsonSerializer.Serialize(upgraded.Learning)==normal,
                "v94迁移新增空训练域，保留普通技能与已有任务事实");
            DatabaseSchemaTests.RewindRaidLifecycle(path);
            Sql(DatabaseSchemaTests.GuildExtensionFixtureRewindSql + "DROP TABLE account_pvp_private_characters; DROP TABLE character_operation_hope_reward_receipts; DROP TABLE character_pvp_training_skills; CREATE TABLE character_pvp_training_skills(character_id INTEGER PRIMARY KEY,state_json TEXT,operation_id TEXT,before_revision INTEGER); ALTER TABLE characters DROP COLUMN selected_skill_tree; PRAGMA user_version=94");
            Reject<InvalidDataException>(()=>new CharacterRepository(path),"已存在但缺约束的训练表不能靠IF NOT EXISTS混过升级校验");
            void Sql(string sql)
            {
                using var db=new SqliteConnection("Data Source="+path+";Pooling=False");db.Open();using var command=db.CreateCommand();
                command.CommandText=sql;command.ExecuteNonQuery();
            }
        }
        finally {TestFiles.DeleteDirectory(root,true);}
        Console.WriteLine($"PASS {Count-before} 训练技能隔离、原子进度与升级断言");
    }
}
