using DfoServer.Game.Accounts;
using DfoServer.Game.PcRoom;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository : IFatigueAccelerationStore
{
    public bool ReadFatigueAcceleration(AccountOwners.Lease owner, ushort characterId) => owner.Execute(() =>
    {
        using var db = database.Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT COALESCE(f.enabled,0) FROM characters c
            LEFT JOIN character_fatigue_acceleration f ON f.character_id=c.id AND f.account=c.account
            WHERE c.id=$id AND c.account=$account
            """;
        command.Parameters.AddWithValue("$id", characterId);
        command.Parameters.AddWithValue("$account", owner.Account);
        return command.ExecuteScalar() is long value ? value != 0 :
            throw new InvalidOperationException("疲劳燃烧角色不属于当前账号");
    });

    public void SaveFatigueAcceleration(AccountOwners.Lease owner, ushort characterId, bool enabled) => owner.Execute(() =>
    {
        if (ReadFatigueAcceleration(owner, characterId) == enabled) return true;
        using var db = database.Open();
        using var write = db.BeginWrite("CharacterRepository.SaveFatigueAcceleration");
        using var tx = write.Transaction;
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            INSERT INTO character_fatigue_acceleration(character_id,account,enabled)
            SELECT id,account,$enabled FROM characters WHERE id=$id AND account=$account
            ON CONFLICT(character_id) DO UPDATE SET enabled=excluded.enabled
            WHERE character_fatigue_acceleration.account=excluded.account
            """;
        command.Parameters.AddWithValue("$id", characterId);
        command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("疲劳燃烧保存时角色归属已经变化");
        tx.Commit();
        return true;
    });
}
