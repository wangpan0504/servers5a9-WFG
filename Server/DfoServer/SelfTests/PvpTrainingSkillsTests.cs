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
            Sql("DROP TABLE account_pvp_private_characters; DROP TABLE character_operation_hope_reward_receipts; PRAGMA user_version=95");
            var existing=new CharacterRepository(path).LoadCharacter(owner,original.Slot);
            That(existing.PvpTrainingSkills is not null && existing.Revision==reloaded.Revision+1,
                "升级遇到已存在的同合同训练表保留既有快照，不重复发点或清数据");
            // 精确v94升级：旧库没有训练表，不伪造历史训练或改普通双树。
            Sql("DROP TABLE account_pvp_private_characters; DROP TABLE character_operation_hope_reward_receipts; DROP TABLE character_pvp_training_skills; PRAGMA user_version=94");
            var upgraded=new CharacterRepository(path).LoadCharacter(owner,original.Slot);
            That(upgraded.PvpTrainingSkills is null && JsonSerializer.Serialize(upgraded.Learning)==normal,
                "v94迁移新增空训练域，保留普通技能与已有任务事实");
            Sql("DROP TABLE account_pvp_private_characters; DROP TABLE character_operation_hope_reward_receipts; DROP TABLE character_pvp_training_skills; CREATE TABLE character_pvp_training_skills(character_id INTEGER PRIMARY KEY,state_json TEXT,operation_id TEXT,before_revision INTEGER); PRAGMA user_version=94");
            Reject<InvalidDataException>(()=>new CharacterRepository(path),"已存在但缺约束的训练表不能靠IF NOT EXISTS混过升级校验");
            void Sql(string sql)
            {
                using var db=new SqliteConnection("Data Source="+path+";Pooling=False");db.Open();using var command=db.CreateCommand();
                command.CommandText=sql;command.ExecuteNonQuery();
            }
        }
        finally {Directory.Delete(root,true);}
        Console.WriteLine($"PASS {Count-before} 训练技能隔离、原子进度与升级断言");
    }
}
