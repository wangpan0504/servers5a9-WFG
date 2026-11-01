using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Raids;
using DfoServer.Game.Titles;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository : IFiendwarCounterStore
{
    public bool RecordFiendwarCounter(AccountOwners.Lease owner, Character expected,
        AntonRaidBattle.FiendwarCounterFact fact, string stream, uint sequence) => owner.Execute(() =>
    {
        ArgumentNullException.ThrowIfNull(fact);
        if (fact.Character != expected.Id || !fact.IsCurrent || string.IsNullOrEmpty(stream) || stream.Length > 256 || sequence == 0)
            throw new InvalidOperationException("漩涡破招事实的原角色或房间已失效");
        using var db = database.Open();
        using var write = db.BeginWrite("CharacterRepository.RecordFiendwarCounter");
        using var tx = write.Transaction;
        if (!fact.IsCurrent) throw new InvalidOperationException("漩涡破招等待存储期间原房间已失效");
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = """
            INSERT INTO character_fiendwar_counter_stream(character_id,account,stream,sequence)
            SELECT $id,$account,$stream,$sequence WHERE EXISTS(
              SELECT 1 FROM characters WHERE id=$id AND account=$account AND revision=$revision)
            ON CONFLICT(character_id) DO UPDATE SET stream=excluded.stream,sequence=excluded.sequence
            WHERE account=$account AND (stream<>excluded.stream OR sequence<excluded.sequence);
            """;
        command.Parameters.AddWithValue("$id", expected.Id); command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$revision", expected.Revision); command.Parameters.AddWithValue("$stream", stream);
        command.Parameters.AddWithValue("$sequence", (long)sequence);
        if (command.ExecuteNonQuery() == 0) return false;
        AddFiendwarTitleFact(db, tx, expected.Id, owner.Account, 7006);
        tx.Commit();
        return true;
    });
}
