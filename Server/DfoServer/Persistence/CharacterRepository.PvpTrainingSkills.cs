using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Pvp;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    static PvpTrainingSkills? ReadPvpTrainingSkills(SqliteConnection db,ushort id,SqliteTransaction? transaction)
    {
        using var command=db.CreateCommand();command.Transaction=transaction;
        command.CommandText="SELECT state_json FROM character_pvp_training_skills WHERE character_id=$id";
        command.Parameters.AddWithValue("$id",id);
        if(command.ExecuteScalar() is not string json) return null;
        var state=JsonSerializer.Deserialize<PvpTrainingSkills>(json) ?? throw new InvalidDataException("空训练技能快照");
        state.Validate();
        return state with {Learning=state.Learning with {Trees=Array.AsReadOnly(state.Learning.Trees.Select(tree=>tree.Freeze()).ToArray())}};
    }

    public Character CommitPvpTrainingSkills(AccountOwners.Lease owner,PvpTrainingSkillPlan plan)=>owner.Execute(()=>
    {
        var expected=plan.Expected;plan.After.Validate();
        if(plan.OperationId==Guid.Empty || !plan.Result.Success || plan.Result.State!=plan.After.Learning ||
            expected.Position is not {Town:10,Area:4} || plan.After.Learning.Experience!=expected.Learning.Experience)
            throw new InvalidDataException("训练技能事务身份或规划无效");
        using var db=database.Open();using var write=db.BeginWrite("CharacterRepository.CommitPvpTrainingSkills");
        using var tx=write.Transaction;
        var current=Require(db,tx,owner.Account,expected.Name);
        string after=JsonSerializer.Serialize(plan.After);
        using var command=db.CreateCommand();command.Transaction=tx;
        command.Parameters.AddWithValue("$id",expected.Id);command.Parameters.AddWithValue("$account",owner.Account);
        command.Parameters.AddWithValue("$operation",plan.OperationId.ToString("N"));
        command.Parameters.AddWithValue("$before",expected.Revision);command.Parameters.AddWithValue("$state",after);
        command.CommandText="SELECT state_json,operation_id,before_revision FROM character_pvp_training_skills WHERE character_id=$id";
        using(var reader=command.ExecuteReader())
            if(reader.Read() && reader.GetString(1)==plan.OperationId.ToString("N"))
            {
                if(reader.GetString(0)!=after || reader.GetInt64(2)!=expected.Revision || current.Revision!=expected.Revision+1 ||
                    !current.ActiveMainQuests.SequenceEqual(plan.ActiveAfter) || !SameLearning(current.Learning,expected.Learning))
                    throw new InvalidOperationException("已提交的训练技能快照已过期，不恢复旧投影");
                return current;
            }
        if(current.Id!=expected.Id || current.Revision!=expected.Revision || current.Job!=expected.Job ||
            current.GrowType!=expected.GrowType || current.Level!=expected.Level ||
            !SameLearning(current.Learning,expected.Learning) || !current.ActiveMainQuests.SequenceEqual(expected.ActiveMainQuests) ||
            JsonSerializer.Serialize(current.PvpTrainingSkills)!=JsonSerializer.Serialize(expected.PvpTrainingSkills))
            throw new InvalidOperationException("训练技能原角色快照已变化");
        command.CommandText="UPDATE characters SET revision=revision+1 WHERE id=$id AND account=$account AND revision=$before";
        if(command.ExecuteNonQuery()!=1) throw new InvalidOperationException("训练技能角色版本失配");
        command.CommandText="""
            INSERT INTO character_pvp_training_skills VALUES($id,$state,$operation,$before)
            ON CONFLICT(character_id) DO UPDATE SET state_json=$state,operation_id=$operation,before_revision=$before
            """;
        command.ExecuteNonQuery();
        WriteQuestConditions(db,tx,current.Id,current.ActiveMainQuests,plan.ActiveAfter);
        tx.Commit();
        return current with {Revision=current.Revision+1,PvpTrainingSkills=plan.After,ActiveMainQuests=plan.ActiveAfter};
    });
}
