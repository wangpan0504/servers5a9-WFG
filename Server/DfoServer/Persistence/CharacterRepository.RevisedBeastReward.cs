using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository : IRevisedBeastRewardStore
{
    public DungeonIncountUsage LoadRevisedBeastRewardUsage(AccountOwners.Lease owner, ushort characterId, DungeonIncountPeriod period) => owner.Execute(() =>
    {
        using var db=database.Open();
        using var command=db.CreateCommand();
        command.CommandText="SELECT coalesce(sum(CASE WHEN day_key=$day THEN awarded ELSE 0 END),0),coalesce(sum(CASE WHEN week_key=$week THEN awarded ELSE 0 END),0) FROM (SELECT * FROM character_revised_beast_rewards UNION ALL SELECT * FROM character_revised_beast_stage_rewards) WHERE character_id=$id AND account=$account AND (day_key=$day OR week_key=$week)";
        command.Parameters.AddWithValue("$id",characterId); command.Parameters.AddWithValue("$account",owner.Account);
        command.Parameters.AddWithValue("$day",period.Day); command.Parameters.AddWithValue("$week",period.Week);
        using var reader=command.ExecuteReader(); reader.Read();
        return new DungeonIncountUsage(checked((int)reader.GetInt64(0)),checked((int)reader.GetInt64(1)));
    });

    public RevisedBeastRewardCommit CommitRevisedBeastReward(AccountOwners.Lease owner, SinglePlayerDungeonRun run,
        RevisedBeastRewardPlan plan) => owner.Execute(() =>
    {
        if (!ReferenceEquals(run.RevisedBeastTerminal,plan.Terminal) || !run.Entry.IsRevisedBeast ||
            run.Snapshot.Identity != plan.Terminal.Identity || run.Phase != SinglePlayerDungeonPhase.Settlement ||
            run.Entry.DungeonId != plan.DungeonId || run.Entry.Difficulty != plan.Difficulty ||
            run.HasPendingDeath || run.HasPendingSettlement || run.Entry.RevisedBeastEncounter is not { } rules ||
            plan.Expected.Id != plan.Terminal.Identity.CharacterId || plan.DailyLimit != rules.DailyRewardLimit ||
            plan.WeeklyLimit != rules.WeeklyRewardLimit || run.RevisedBeastAnneStage is null ||
            (plan.Terminal.Success ? !plan.Rewards.SequenceEqual(new[]{new Game.Inventory.Services.InventoryReward(rules.HierroRewardItem,1)}) : !plan.Rewards.IsEmpty))
            throw new InvalidDataException("改版魔兽奖励必须属于本实例冻结终局");
        var expected=plan.Expected;
        ValidateRevisedBeastRewardInventory(expected,plan.Inventory,plan.Rewards);
        string snapshot=JsonSerializer.Serialize(new { plan.Terminal.Identity,plan.Terminal.Success,plan.DungeonId,plan.Difficulty,
            plan.Rewards,plan.Period,plan.DailyLimit,plan.WeeklyLimit });
        using var db=database.Open();
        using var write=db.BeginWrite("CharacterRepository.CommitRevisedBeastReward",deferred:true);
        using var tx=write.Transaction;
        var current=Require(db,tx,owner.Account,expected.Name);
        using var command=db.CreateCommand(); command.Transaction=tx;
        command.Parameters.AddWithValue("$id",expected.Id); command.Parameters.AddWithValue("$account",owner.Account);
        command.Parameters.AddWithValue("$instance",plan.Terminal.Identity.InstanceId.ToString("N"));
        command.CommandText="SELECT awarded FROM character_revised_beast_stage_rewards WHERE character_id=$id AND account=$account AND instance_id=$instance";
        var stageAward=command.ExecuteScalar();
        bool stageExists=stageAward is not null, stageGranted=stageExists && Convert.ToInt64(stageAward)!=0;
        if (!stageExists) throw new InvalidDataException("改版魔兽终局必须先完成安妮阶段回执");
        command.CommandText="SELECT plan_snapshot,before_revision,revision,awarded FROM character_revised_beast_rewards WHERE character_id=$id AND account=$account AND instance_id=$instance";
        using (var reader=command.ExecuteReader())
        {
            if (reader.Read())
            {
                if (reader.GetString(0) != snapshot || reader.GetInt64(1) != expected.Revision)
                    throw new InvalidDataException("改版魔兽重试不能更换冻结奖励");
                bool awarded=stageGranted && !plan.Rewards.IsEmpty;
                return Result(current,true,current.Revision == reader.GetInt64(2),awarded);
            }
        }
        if (current.Id != expected.Id || current.Revision != expected.Revision ||
            current.DungeonState?.LastAttempt != plan.Terminal.Identity.TaskAttempt)
            throw new InvalidOperationException("改版魔兽结算角色或实例尝试已变化");
        command.Parameters.AddWithValue("$day",plan.Period.Day); command.Parameters.AddWithValue("$week",plan.Period.Week);
        // 资格已在阶段冻结并扣次；终局仅可补发歇罗，死亡终局只保存无资产变化回执。
        bool grant=stageGranted && !plan.Rewards.IsEmpty;
        if (grant) current=CommitInventoryInTransaction(db,tx,owner,current,expected,plan.Inventory);
        foreach(var (name,value) in new (string,object)[]{
            ("$attempt",plan.Terminal.Identity.TaskAttempt),("$epoch",plan.Terminal.Identity.Epoch),("$dungeon",plan.DungeonId),
            ("$difficulty",plan.Difficulty),("$success",plan.Terminal.Success?1:0),("$award",0),
            ("$before",expected.Revision),("$revision",current.Revision),("$snapshot",snapshot)}) command.Parameters.AddWithValue(name,value);
        command.CommandText="""
            INSERT INTO character_revised_beast_rewards(character_id,account,instance_id,task_attempt,epoch,dungeon_id,difficulty,
            success,day_key,week_key,awarded,before_revision,revision,plan_snapshot)
            VALUES($id,$account,$instance,$attempt,$epoch,$dungeon,$difficulty,$success,$day,$week,$award,$before,$revision,$snapshot)
            """;
        command.ExecuteNonQuery(); tx.Commit();
        return Result(current,false,true,grant);
        RevisedBeastRewardCommit Result(Character character,bool duplicate,bool projection,bool awarded) =>
            new(character,duplicate,projection,awarded,awarded?plan.Rewards:[],
                awarded?plan.Inventory.ChangedSlots:[],awarded && !plan.Inventory.OverflowItems.IsEmpty);
    });
    static void ValidateRevisedBeastRewardInventory(DfoServer.Game.Characters.Character expected, InventoryPlan inventory,
        ImmutableArray<DfoServer.Game.Inventory.Services.InventoryReward> rewards)
    {
        ValidateInventoryPlan(expected,inventory);
        if (JsonSerializer.Serialize(inventory.After with { Items=expected.Inventory.Items }) != JsonSerializer.Serialize(expected.Inventory))
            throw new InvalidDataException("改版魔兽奖励不能改变无关资产");
        foreach (var item in expected.Inventory.Items)
        {
            var after=inventory.After.Find(item.Position);
            if (after is null || after with { Core=after.Core with { Value=item.Core.Value } } != item ||
                after.Core.Count < item.Core.Count || after.Core.Count != item.Core.Count && !rewards.Any(row=>row.ItemId == item.Core.ItemId))
                throw new InvalidDataException("改版魔兽奖励不能扣除或替换已有物品");
        }
        foreach (var reward in rewards)
        {
            long delta=inventory.After.Items.Where(row=>row.Core.ItemId == reward.ItemId).Sum(row=>(long)row.Core.Count) -
                expected.Inventory.Items.Where(row=>row.Core.ItemId == reward.ItemId).Sum(row=>(long)row.Core.Count) +
                inventory.OverflowItems.Where(row=>row.Core.ItemId == reward.ItemId).Sum(row=>(long)row.Core.Count);
            if (delta != reward.Count) throw new InvalidDataException("改版魔兽实际奖励与冻结项目不符");
        }
        if (inventory.After.Items.Where(row=>expected.Inventory.Find(row.Position) is null).Concat(inventory.OverflowItems)
            .Any(row=>row.Uid != 0 || !rewards.Any(reward=>reward.ItemId == row.Core.ItemId)))
            throw new InvalidDataException("改版魔兽不能附加未声明物品");
    }
}
