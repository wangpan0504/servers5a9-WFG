using DfoServer.Game.Accounts;
using DfoServer.Game.Dungeons;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    public Guid? LoadOrdinaryLukeDropEvent(AccountOwners.Lease owner, ushort characterId, int day, int sourceMonsterId) => owner.Execute(() =>
    {
        using var db = database.Open();
        return ReadOrdinaryLukeDrop(db, null, owner.Account, characterId, day, sourceMonsterId)?.Event;
    });

    static (Guid Event, Guid Instance)? ReadOrdinaryLukeDrop(SqliteConnection db, SqliteTransaction? tx,
        string account, ushort characterId, int day, int sourceMonsterId)
    {
        using var query = db.CreateCommand();
        query.Transaction = tx;
        query.CommandText = """
            SELECT first_event_id,first_instance_id FROM character_luke_normal_drop_claims
            WHERE character_id=$id AND account=$account AND day=$day AND source_monster_id=$monster
            """;
        query.Parameters.AddWithValue("$id", characterId);
        query.Parameters.AddWithValue("$account", account);
        query.Parameters.AddWithValue("$day", day);
        query.Parameters.AddWithValue("$monster", sourceMonsterId);
        using var reader = query.ExecuteReader();
        return reader.Read() ? (Guid.ParseExact(reader.GetString(0), "N"), Guid.ParseExact(reader.GetString(1), "N")) : null;
    }

    static void CommitOrdinaryLukeDrop(SqliteConnection db, SqliteTransaction tx, AccountOwners.Lease owner,
        SinglePlayerDungeonRun run, PreparedDungeonDeath prepared, FrozenDungeonGroundDeath? loot, bool verifyOnly = false)
    {
        if (loot?.OrdinaryLukeDaily is not { } claim) return;
        var identity = prepared.Death.Identity;
        if (!OrdinaryLukeProgress.Applies(run.Entry) || run.Entry.OrdinaryLukeDay != claim.Day ||
            claim.SourceMonsterId != prepared.Monster.MonsterId || !loot.UsesCollectionLedger ||
            !ReferenceEquals(loot.Death, prepared.Death) || !claim.Eligible && !loot.Grounds.IsEmpty)
            throw new InvalidDataException("普通卢克每日掉落不属于原角色Run与冻结死亡");
        var previous = ReadOrdinaryLukeDrop(db, tx, owner.Account, identity.CharacterId, claim.Day, claim.SourceMonsterId);
        if (previous is { } found)
        {
            bool same = found.Event == prepared.Death.EventId && found.Instance == identity.InstanceId;
            if (claim.Eligible != same || claim.Eligible && !verifyOnly)
                throw new InvalidDataException("普通卢克首掉资格与原死亡提交不一致");
            return;
        }
        if (verifyOnly || !claim.Eligible)
            throw new InvalidDataException("普通卢克已消费掉落缺少原首击资格");
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            INSERT INTO character_luke_normal_drop_claims(character_id,account,day,source_monster_id,first_event_id,first_instance_id)
            VALUES($id,$account,$day,$monster,$event,$instance)
            """;
        command.Parameters.AddWithValue("$id", identity.CharacterId);
        command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$day", claim.Day);
        command.Parameters.AddWithValue("$monster", claim.SourceMonsterId);
        command.Parameters.AddWithValue("$event", prepared.Death.EventId.ToString("N"));
        command.Parameters.AddWithValue("$instance", identity.InstanceId.ToString("N"));
        command.ExecuteNonQuery();
    }
}
