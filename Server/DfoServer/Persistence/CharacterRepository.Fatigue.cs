using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    static CharacterFatigueState? ReadFatigue(SqliteConnection db, ushort id, SqliteTransaction? tx)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            SELECT f.used,f.maximum,f.used_maximum,f.remaining,f.context_auxiliary,f.selection_auxiliary,f.account,c.account
            FROM character_fatigue f JOIN characters c ON c.id=f.character_id WHERE f.character_id=$id
            """;
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;
        if (reader.GetString(6) != reader.GetString(7))
            throw new InvalidDataException("疲劳owner账号归属损坏");
        var state = new CharacterFatigueState(checked((ushort)reader.GetInt32(0)), checked((ushort)reader.GetInt32(1)),
            checked((ushort)reader.GetInt32(2)), checked((ushort)reader.GetInt32(3)), checked((ushort)reader.GetInt32(4)),
            checked((ushort)reader.GetInt32(5)));
        state.Validate();
        return state;
    }

    static void CreateFatigue(SqliteConnection db, SqliteTransaction tx, string account, ushort id)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "INSERT INTO character_fatigue VALUES($id,$account,0,156,0,156,0,0)";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$account", account);
        command.ExecuteNonQuery();
    }

    public Character ConsumeDungeonRoomFatigue(AccountOwners.Lease owner, Character expected, SinglePlayerDungeonRun run) => owner.Execute(() =>
    {
        var snapshot = run.Snapshot;
        var identity = snapshot.Identity;
        var state = expected.Fatigue ?? throw new InvalidOperationException("普通副本疲劳owner未初始化");
        state.Validate();
        if (run.DungeonId != 3 || run.Entry.QuestId != 3145 ||
            snapshot.Phase != SinglePlayerDungeonPhase.Loading || identity.CharacterId != expected.Id ||
            identity.InstanceId == Guid.Empty || identity.TaskAttempt != expected.DungeonState?.LastAttempt ||
            snapshot.CurrentMap.Id is < 76121 or > 76126)
            throw new InvalidDataException("疲劳扣点仅承接本服普通3145真实实例的首次房间加载");
        using var db = database.Open();
        using var tx = db.BeginTransaction(deferred: true);
        var current = Require(db, tx, owner.Account, expected.Name);
        if (current.Id != expected.Id || current.DungeonState != expected.DungeonState)
            throw new InvalidOperationException("扣点实例或角色已变化");
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.Parameters.AddWithValue("$id", expected.Id);
        command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$instance", identity.InstanceId.ToString("N"));
        command.Parameters.AddWithValue("$map", snapshot.CurrentMap.Id);
        command.CommandText = """
            SELECT task_attempt,rule_id,before_revision,revision,before_state,after_state FROM character_dungeon_fatigue_receipts
            WHERE character_id=$id AND account=$account AND instance_id=$instance AND map_id=$map
            """;
        using (var reader = command.ExecuteReader())
        {
            if (reader.Read())
            {
                var before = JsonSerializer.Deserialize<CharacterFatigueState>(reader.GetString(4));
                var after = JsonSerializer.Deserialize<CharacterFatigueState>(reader.GetString(5));
                if (reader.GetInt64(0) != identity.TaskAttempt || reader.GetString(1) != CharacterFatigueState.OrdinaryRoomRuleId ||
                    before is null || after != before.ConsumeOrdinaryRoom() || reader.GetInt64(3) != reader.GetInt64(2) + 1)
                    throw new InvalidDataException("房间扣点账本的规则、身份或原序owner损坏");
                if (current.Revision == expected.Revision && current.Fatigue == state)
                    return expected; // 同一实例再次经过已付费房间，不推进revision。
                if (expected.Revision != reader.GetInt64(2) || current.Revision != reader.GetInt64(3) ||
                    before != state || current.Fatigue != after)
                    throw new InvalidOperationException("已提交扣点的原快照精确恢复失效");
                return current;
            }
        }
        if (current.Revision != expected.Revision || current.Fatigue != state)
            throw new InvalidOperationException("扣点的原revision或疲劳owner已变化");
        var target = state.ConsumeOrdinaryRoom();
        command.Parameters.AddWithValue("$revision", expected.Revision);
        command.CommandText = "UPDATE characters SET revision=revision+1 WHERE id=$id AND account=$account AND revision=$revision";
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("首次房间扣点revision冲突");
        command.Parameters.AddWithValue("$used", target.Used);
        command.Parameters.AddWithValue("$remaining", target.Remaining);
        command.CommandText = "UPDATE character_fatigue SET used=$used,remaining=$remaining WHERE character_id=$id AND account=$account";
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("首次房间扣点缺少原疲劳owner");
        command.Parameters.AddWithValue("$attempt", identity.TaskAttempt);
        command.Parameters.AddWithValue("$epoch", identity.Epoch);
        command.Parameters.AddWithValue("$generation", owner.Generation);
        command.Parameters.AddWithValue("$rule", CharacterFatigueState.OrdinaryRoomRuleId);
        command.Parameters.AddWithValue("$before", JsonSerializer.Serialize(state));
        command.Parameters.AddWithValue("$after", JsonSerializer.Serialize(target));
        command.CommandText = """
            INSERT INTO character_dungeon_fatigue_receipts(character_id,account,instance_id,task_attempt,map_id,epoch,generation,
              rule_id,before_revision,revision,before_state,after_state)
            VALUES($id,$account,$instance,$attempt,$map,$epoch,$generation,$rule,$revision,$revision+1,$before,$after)
            """;
        command.ExecuteNonQuery();
        var committed = Require(db, tx, owner.Account, expected.Name);
        tx.Commit();
        return committed;
    });
}
