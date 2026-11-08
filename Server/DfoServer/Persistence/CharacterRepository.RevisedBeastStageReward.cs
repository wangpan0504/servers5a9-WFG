using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    public RevisedBeastRewardCommit CommitRevisedBeastStageReward(AccountOwners.Lease owner, SinglePlayerDungeonRun run,
        RevisedBeastStageRewardPlan plan) => owner.Execute(() =>
    {
        if (!run.Entry.IsRevisedBeast || !ReferenceEquals(run.RevisedBeastAnneStage,plan.Stage) ||
            run.RevisedBeastTerminal is not null || run.Phase != SinglePlayerDungeonPhase.Dungeon ||
            run.Snapshot.Identity != plan.Stage.Identity || run.HasPendingDeath || run.HasPendingSettlement ||
            run.Entry.RevisedBeastEncounter is not { } rules || plan.Stage.MonsterId != rules.AnneId ||
            plan.Expected.Id != plan.Stage.Identity.CharacterId || plan.DailyLimit != rules.DailyRewardLimit ||
            plan.WeeklyLimit != rules.WeeklyRewardLimit || plan.Rewards.Length != 1 ||
            plan.Rewards[0] != new DfoServer.Game.Inventory.Services.InventoryReward(rules.AnneRewardItem,1))
            throw new InvalidDataException("改版魔兽阶段奖励必须属于本实例已提交安妮死亡");
        var expected=plan.Expected;
        ValidateRevisedBeastRewardInventory(expected,plan.Inventory,plan.Rewards);
        string snapshot=JsonSerializer.Serialize(new { plan.Stage.Identity,Success=true,run.Entry.DungeonId,run.Entry.Difficulty,
            plan.Rewards,plan.Period,plan.DailyLimit,plan.WeeklyLimit });
        using var db=database.Open();
        using var write=db.BeginWrite("CharacterRepository.CommitRevisedBeastStageReward",deferred:true);
        using var tx=write.Transaction;
        var current=Require(db,tx,owner.Account,expected.Name);
        using var command=db.CreateCommand(); command.Transaction=tx;
        command.Parameters.AddWithValue("$id",expected.Id); command.Parameters.AddWithValue("$account",owner.Account);
        command.Parameters.AddWithValue("$instance",plan.Stage.Identity.InstanceId.ToString("N"));
        command.CommandText="SELECT plan_snapshot,before_revision,revision,awarded FROM character_revised_beast_stage_rewards WHERE character_id=$id AND account=$account AND instance_id=$instance";
        using (var reader=command.ExecuteReader())
        {
            if (reader.Read())
            {
                if (reader.GetString(0) != snapshot || reader.GetInt64(1) != expected.Revision)
                    throw new InvalidDataException("改版魔兽重试不能更换冻结奖励");
                bool awarded=reader.GetInt64(3) != 0;
                return Result(current,true,current.Revision == reader.GetInt64(2),awarded);
            }
        }
        if (current.Id != expected.Id || current.Revision != expected.Revision ||
            current.DungeonState?.LastAttempt != plan.Stage.Identity.TaskAttempt)
            throw new InvalidOperationException("改版魔兽结算角色或实例尝试已变化");
        command.Parameters.AddWithValue("$day",plan.Period.Day); command.Parameters.AddWithValue("$week",plan.Period.Week);
        command.CommandText="SELECT coalesce(sum(CASE WHEN day_key=$day THEN awarded ELSE 0 END),0),coalesce(sum(CASE WHEN week_key=$week THEN awarded ELSE 0 END),0) FROM (SELECT * FROM character_revised_beast_rewards UNION ALL SELECT * FROM character_revised_beast_stage_rewards) WHERE character_id=$id AND account=$account AND (day_key=$day OR week_key=$week)";
        bool grant;
        using (var reader=command.ExecuteReader())
        { reader.Read(); grant=reader.GetInt64(0)<plan.DailyLimit && reader.GetInt64(1)<plan.WeeklyLimit; }
        if (grant) current=CommitInventoryInTransaction(db,tx,owner,current,expected,plan.Inventory);
        foreach(var (name,value) in new (string,object)[]{
            ("$attempt",plan.Stage.Identity.TaskAttempt),("$epoch",plan.Stage.Identity.Epoch),("$dungeon",run.Entry.DungeonId),
            ("$difficulty",run.Entry.Difficulty),("$success",1),("$award",grant?1:0),
            ("$before",expected.Revision),("$revision",current.Revision),("$snapshot",snapshot)}) command.Parameters.AddWithValue(name,value);
        command.CommandText="""
            INSERT INTO character_revised_beast_stage_rewards(character_id,account,instance_id,task_attempt,epoch,dungeon_id,difficulty,
            success,day_key,week_key,awarded,before_revision,revision,plan_snapshot)
            VALUES($id,$account,$instance,$attempt,$epoch,$dungeon,$difficulty,$success,$day,$week,$award,$before,$revision,$snapshot)
            """;
        command.ExecuteNonQuery(); tx.Commit();
        return Result(current,false,true,grant);
        RevisedBeastRewardCommit Result(Character character,bool duplicate,bool projection,bool awarded) =>
            new(character,duplicate,projection,awarded,awarded?plan.Rewards:[],
                awarded?plan.Inventory.ChangedSlots:[],awarded && !plan.Inventory.OverflowItems.IsEmpty);
    });
}
