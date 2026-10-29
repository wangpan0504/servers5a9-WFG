using DfoServer.Game.Accounts;
using DfoServer.Game.Dungeons;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    // 唯一阶段凭据与成长、库存、溢出邮件共用事务；不同死亡不得借重放再次发罐。
    static void SaveOrVerifyBeastStageReward(SqliteConnection db, SqliteTransaction tx,
        AccountOwners.Lease owner, BeastStageRewardPlan? reward, bool verifyOnly)
    {
        if (reward is null) return;
        var prepared = reward.Prepared;
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.Parameters.AddWithValue("$character", reward.Expected.Id);
        command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$instance", prepared.Death.Identity.InstanceId.ToString("N"));
        command.Parameters.AddWithValue("$stage", reward.Stage);
        command.Parameters.AddWithValue("$event", prepared.Death.EventId.ToString("N"));
        command.Parameters.AddWithValue("$map", prepared.Result.Snapshot.CurrentMap.Id);
        command.Parameters.AddWithValue("$key", prepared.Death.MonsterKey);
        command.Parameters.AddWithValue("$item", BeastDungeonRotation.StageRewardItem(reward.Stage)!.Value);
        command.CommandText = """
            SELECT account,event_id,map_id,monster_key,item_id FROM character_beast_stage_rewards
            WHERE character_id=$character AND instance_id=$instance AND stage=$stage
            """;
        using (var reader = command.ExecuteReader())
        {
            if (reader.Read())
            {
                if (!verifyOnly || reader.GetString(0) != owner.Account ||
                    reader.GetString(1) != prepared.Death.EventId.ToString("N") ||
                    reader.GetInt64(2) != prepared.Result.Snapshot.CurrentMap.Id ||
                    reader.GetInt64(3) != prepared.Death.MonsterKey ||
                    reader.GetInt64(4) != BeastDungeonRotation.StageRewardItem(reward.Stage))
                    throw new InvalidDataException("同一魔兽阶段奖励已属于另一死亡或来源已变化");
                return;
            }
        }
        if (verifyOnly) throw new InvalidDataException("魔兽成长回执缺少原阶段奖励凭据");
        command.CommandText = """
            INSERT INTO character_beast_stage_rewards(character_id,account,instance_id,stage,event_id,map_id,monster_key,item_id)
            VALUES($character,$account,$instance,$stage,$event,$map,$key,$item)
            """;
        command.ExecuteNonQuery();
    }
}
