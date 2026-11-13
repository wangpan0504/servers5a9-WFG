using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;
namespace DfoServer;
internal static class RevisedBeastRewardTests
{
    internal static void RunCore()
    {
        int before=Count;
        string root=Path.Combine(Path.GetTempPath(),"S5A9-revised-beast-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); string path=Path.Combine(root,"game.db");
        try
        {
            var repository=new CharacterRepository(path);
            var owners=new AccountOwners(); using var owner=owners.Acquire("beast-reward-test",()=>{});
            var role=repository.Create(owner,new NewCharacter("BeastAward",new(51,1,450,234),null)
                { Level=95,DungeonState=CharacterDungeonState.NewOrdinary });
            role=repository.ReserveDungeonAttempt(owner,role,100);
            var (run,firstAuthority)=RevisedBeastEncounterTests.Create(role.Id,role.DungeonState!.LastAttempt);
            RevisedBeastEncounterTests.LoadToBoss(run);
            That(!run.FinishRevisedBeastOnDeathTimeout() && run.RevisedBeastTerminal is null,"安妮前死亡没有终局奖励资格");
            run.AcceptConfirmedDeath(firstAuthority.Death(run.Snapshot.Identity,10,1));
            role=repository.CommitRevisedBeastStageReward(owner,run,StagePlan(role,run)).Character;
            run.AcceptConfirmedDeath(firstAuthority.Death(run.Snapshot.Identity,11,2));
            var plan=Plan(role,run);
            Execute("CREATE TRIGGER beast_reward_failure AFTER INSERT ON character_revised_beast_rewards BEGIN SELECT RAISE(ABORT,'beast rollback'); END");
            Reject<SqliteException>(()=>repository.CommitRevisedBeastReward(owner,run,plan),"改版末段回执失败整笔回滚");
            That(JsonSerializer.Serialize(repository.LoadCharacter(owner,role.Slot)) == JsonSerializer.Serialize(role) && Scalar("SELECT count(*) FROM character_revised_beast_rewards")==0,
                "回滚无资产、UID、revision及回执残留");
            Execute("DROP TRIGGER beast_reward_failure");
            var first=repository.CommitRevisedBeastReward(owner,run,plan);
            That(first.Awarded && !first.Duplicate && first.ProjectionCurrent && first.Character.Inventory.Items.Count(row=>row.Core.ItemId is 10305183 or 10305186)==2 &&
                first.Character.Inventory.Items.All(row=>row.Uid>0),"成功终局仅新增歇罗罐，保留阶段已到账安妮罐");
            var replay=new CharacterRepository(path).CommitRevisedBeastReward(owner,run,plan);
            That(replay.Duplicate && replay.Awarded && replay.ProjectionCurrent && JsonSerializer.Serialize(replay.Character)==JsonSerializer.Serialize(first.Character),
                "重开仓储与丢失响应恢复同一资产，不能重复发放");
            Reject<InvalidDataException>(()=>repository.CommitRevisedBeastReward(owner,run,plan with { Difficulty=1 }),"拒绝改写原终局难度");
            for(int round=0;round<2;round++)
            {
                role=repository.LoadCharacter(owner,role.Slot); role=repository.ReserveDungeonAttempt(owner,role,100);
                var (next,authority)=RevisedBeastEncounterTests.Create(role.Id,role.DungeonState!.LastAttempt);
                RevisedBeastEncounterTests.LoadToBoss(next);
                next.AcceptConfirmedDeath(authority.Death(next.Snapshot.Identity,10,1));
                role=repository.CommitRevisedBeastStageReward(owner,next,StagePlan(role,next)).Character;
                next.CompleteLoading(next.Snapshot.Identity,Guid.NewGuid());
                next.AcceptConfirmedDeath(authority.Death(next.Snapshot.Identity,11,2));
                var result=repository.CommitRevisedBeastReward(owner,next,Plan(role,next));
                That(next.RevisedBeastTerminal!.Success && result.Awarded==(round==0),"通关走同一奖励合同且每日两次上限跨实例生效");
            }
            That(Scalar("SELECT sum(awarded) FROM character_revised_beast_stage_rewards")==2 &&
                repository.LoadCharacter(owner,role.Slot).Inventory.Items.Where(row=>row.Core.ItemId is 10305183 or 10305186).Sum(row=>row.Core.Count)==4,
                "击杀与死亡共用领奖次数，不超发或重复计次");
            That(repository.LoadRevisedBeastRewardUsage(owner,role.Id,new(20261101,20261029))==new DungeonIncountUsage(2,2) &&
                repository.LoadRevisedBeastRewardUsage(owner,role.Id,new(20261102,20261029))==new DungeonIncountUsage(0,2) &&
                repository.LoadRevisedBeastRewardUsage(owner,role.Id,new(20261105,20261105))==default,
                "领奖显示读取真实回执，跨日和跨周分别重置");
            var row=new GameWorld.Dungeons.DungeonIncountRow(0,[5335,5336,5337,5338,5339],3,false,6);
            var period=new DungeonIncountPeriod(20261101,20261029);
            role=repository.LoadCharacter(owner,role.Slot);
            var rollbackBefore=JsonSerializer.Serialize(role);
            Execute("CREATE TRIGGER beast_entry_failure AFTER INSERT ON character_dungeon_incount BEGIN SELECT RAISE(ABORT,'entry rollback'); END");
            Reject<SqliteException>(()=>Enter(period),"入场记次失败必须回滚实例尝试号");
            That(repository.LoadDungeonIncount(owner,role.Id,5335,period)==default &&
                JsonSerializer.Serialize(repository.LoadCharacter(owner,role.Slot))==rollbackBefore,"失败入场无次数、revision和尝试号残留");
            Execute("DROP TRIGGER beast_entry_failure");
            for(int n=0;n<3;n++) Enter(period);
            That(repository.LoadDungeonIncount(owner,role.Id,5335,period)==new DungeonIncountUsage(3,3),"五图三档共用每日3次入场账本");
            Reject<InvalidOperationException>(()=>Enter(period),"第四次入场被拒绝");
            period=new(20261102,20261029);
            for(int n=0;n<3;n++) Enter(period);
            That(new CharacterRepository(path).LoadDungeonIncount(owner,role.Id,5335,period)==new DungeonIncountUsage(3,6),"重启保留每周6次使用量");
            Reject<InvalidOperationException>(()=>Enter(new(20261103,20261029)),"跨日不能绕过每周6次上限");
            That(repository.LoadRevisedBeastRewardUsage(owner,role.Id,period)==new DungeonIncountUsage(0,2),"入场计次不占用终局领奖次数");
            Enter(new(20261105,20261105));
            That(repository.LoadDungeonIncount(owner,role.Id,5335,new(20261105,20261105))==new DungeonIncountUsage(1,1),"跨周自然重置，无须清空历史账本");
            void Enter(DungeonIncountPeriod when)
            {
                var identity=new DungeonRunIdentity(role.Id,0,role.DungeonState!.LastAttempt+1,Guid.NewGuid(),1);
                var charge=new DungeonEntryCharge(null,0,identity,400362)
                    { DungeonId=5335,Incount=new(5335,when,row,[],true) };
                role=repository.CommitDungeonEntry(owner,role,100,charge).Character;
            }
            var stagedRole=repository.Create(owner,new NewCharacter("BeastStage",new(51,1,450,234),null)
                { Level=95,DungeonState=CharacterDungeonState.NewOrdinary });
            for(int round=0;round<3;round++)
            {
                stagedRole=repository.ReserveDungeonAttempt(owner,stagedRole,100);
                var (staged,authority)=RevisedBeastEncounterTests.Create(stagedRole.Id,stagedRole.DungeonState!.LastAttempt);
                RevisedBeastEncounterTests.LoadToBoss(staged);
                That(staged.RevisedBeastAnneStage is null,"安妮未死亡不产生阶段资格");
                staged.AcceptConfirmedDeath(authority.Death(staged.Snapshot.Identity,10,1));
                var rules=staged.Entry.RevisedBeastEncounter!;
                var one=ImmutableArray.Create(new InventoryReward(rules.AnneRewardItem,1));
                var stagePlan=new RevisedBeastStageRewardPlan(stagedRole,staged.RevisedBeastAnneStage!,
                    RewardInventory(stagedRole,one),one,new(20261101,20261029),2,4);
                if(round==0)
                {
                    Execute("CREATE TRIGGER beast_stage_failure AFTER INSERT ON character_revised_beast_stage_rewards BEGIN SELECT RAISE(ABORT,'stage rollback'); END");
                    Reject<SqliteException>(()=>repository.CommitRevisedBeastStageReward(owner,staged,stagePlan),"阶段回执失败回滚库存与额度");
                    That(repository.LoadRevisedBeastRewardUsage(owner,stagedRole.Id,stagePlan.Period)==default &&
                        JsonSerializer.Serialize(repository.LoadCharacter(owner,stagedRole.Slot))==JsonSerializer.Serialize(stagedRole),"阶段失败无资产与计次残留");
                    Execute("DROP TRIGGER beast_stage_failure");
                }
                var stageReceipt=repository.CommitRevisedBeastStageReward(owner,staged,stagePlan);
                That(stageReceipt.Awarded==(round<2) && stageReceipt.Rewards.Length==(round<2?1:0),"阶段只发安妮奖励且受共享日额度控制");
                var stageReplay=new CharacterRepository(path).CommitRevisedBeastStageReward(owner,staged,stagePlan);
                That(stageReplay.Duplicate && stageReplay.Character.Revision==stageReceipt.Character.Revision,"阶段重放不追加资产或次数");
                var stageScene=new FormalDungeonReceipt(stageReceipt.Character,1,1,staged.Entry,staged.Snapshot,
                    new(new(staged.Snapshot.CurrentMap,[],[],[]),[]),1);
                var wire=Network.Builders.RevisedBeastPackets.StageReward(new(stageScene,staged.RevisedBeastAnneStage!,stageReceipt));
                That(wire.Length==(round<2?13:5) && wire[4]==(round<2?1:0) &&
                    (round>=2 || BitConverter.ToUInt32(wire,5)==rules.AnneRewardItem && BitConverter.ToUInt32(wire,9)==1),"1410阶段抽屉正文符合独立原生字段");
                stagedRole=stageReceipt.Character;
                if(round==1) staged.FinishRevisedBeastOnDeathTimeout();
                else staged.AcceptConfirmedDeath(authority.Death(staged.Snapshot.Identity,11,2));
                var terminalPlan=Plan(stagedRole,staged,stageReceipt.Awarded);
                // 阶段已扣次后，即使终局跨业务日也不得再扣或重新判断资格。
                if(round==1) terminalPlan=terminalPlan with {Period=new(20261102,20261029)};
                var terminalReceipt=repository.CommitRevisedBeastReward(owner,staged,terminalPlan);
                That(terminalReceipt.Awarded==(round==0) && terminalReceipt.Rewards.All(x=>x.ItemId!=rules.AnneRewardItem),"终局扣除已发安妮奖励，击杀与死亡均延续本次资格");
                var terminalReplay=repository.CommitRevisedBeastReward(owner,staged,terminalPlan);
                That(terminalReplay.Duplicate && terminalReplay.Awarded==terminalReceipt.Awarded &&
                    terminalReplay.Character.Revision==terminalReceipt.Character.Revision,"分阶段终局重放返回原资产而不重发");
                That(repository.LoadRevisedBeastRewardUsage(owner,stagedRole.Id,new(20261101,20261029))==new DungeonIncountUsage(Math.Min(round+1,2),Math.Min(round+1,2)),
                    "阶段与终局共用一次领奖，不因终局跨日增加额度");
                var display=stageReceipt.Rewards.AddRange(terminalReceipt.Rewards);
                That(display.Length==(round==0?2:round==1?1:0),"终局展示累计已到账卡片，成功两卡、失败只安妮、超限零卡");
                stagedRole=terminalReceipt.Character;
            }
            That(stagedRole.Inventory.Items.Where(x=>x.Core.ItemId is 10305183 or 10305186).Sum(x=>x.Core.Count)==3,
                "胜利实例两件、安妮后死亡实例仅安妮一件，超限不发奖");
            // 模拟v199旧库，升级只增加专用空账本，不改原有玩家库存。
            var saved=JsonSerializer.Serialize(repository.LoadCharacter(owner,role.Slot));
            Execute("DROP TABLE character_revised_beast_stage_rewards; DROP TABLE character_revised_beast_rewards; PRAGMA user_version=199");
            repository=new CharacterRepository(path);
            That(Scalar("PRAGMA user_version")==SqliteMigrations.CurrentVersion && Scalar("SELECT count(*) FROM character_revised_beast_rewards")==0 &&
                saved==JsonSerializer.Serialize(repository.LoadCharacter(owner,role.Slot)),"旧库升级新增空回执，保留原角色资产");
        }
        finally { if(Directory.Exists(root)) TestFiles.DeleteDirectory(root,true); }
        Console.WriteLine($"PASS {Count-before} 改版魔兽终局奖励原子性断言");
        void Execute(string sql){using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText=sql;cmd.ExecuteNonQuery();}
        long Scalar(string sql){using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText=sql;return Convert.ToInt64(cmd.ExecuteScalar());}
        SqliteConnection Open(){var db=new SqliteConnection("Data Source="+path+";Pooling=False;Foreign Keys=True");db.Open();return db;}
    }
    static RevisedBeastRewardPlan Plan(Character role,SinglePlayerDungeonRun run,bool annePaid=false)
    {
        var rules=run.Entry.RevisedBeastEncounter!;
        var rewards=run.RevisedBeastTerminal!.Success ? ImmutableArray.Create(new InventoryReward(rules.HierroRewardItem,1)) :
            ImmutableArray<InventoryReward>.Empty;
        return new(role,run.RevisedBeastTerminal!,run.Entry.DungeonId,run.Entry.Difficulty,RewardInventory(role,rewards),rewards,new(20261101,20261029),2,4);
    }
    static RevisedBeastStageRewardPlan StagePlan(Character role,SinglePlayerDungeonRun run)
    {
        var rewards=ImmutableArray.Create(new InventoryReward(run.Entry.RevisedBeastEncounter!.AnneRewardItem,1));
        return new(role,run.RevisedBeastAnneStage!,RewardInventory(role,rewards),rewards,new(20261101,20261029),2,4);
    }
    static InventoryPlan RewardInventory(Character role,ImmutableArray<InventoryReward> rewards)
    {
        var items=role.Inventory.Items.ToList();
        foreach(var reward in rewards)
        {
            int at=items.FindIndex(row=>row.Core.ItemId==reward.ItemId);
            if(at>=0)items[at]=items[at] with { Core=items[at].Core with { Value=items[at].Core.Value+reward.Count } };
            else items.Add(new(0,new(InventoryListType.Main,checked((short)(65+items.Count))),new(reward.ItemId,GameWorld.Items.ItemKind.Consumable,reward.Count)));
        }
        return new InventoryPlan(role.Inventory,role.Inventory with { Items=items.ToImmutableArray() },"revised_beast_terminal",new(role));
    }
}
