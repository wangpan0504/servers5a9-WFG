using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    static DungeonDespairProgress ReadDespairProgress(SqliteConnection db,SqliteTransaction? tx,string account,ushort id)
    {
        using var command=db.CreateCommand();
        command.Transaction=tx;
        command.CommandText="SELECT next_layer,last_clear_day,clear_count FROM dungeon_despair_progress WHERE character_id=$id AND account=$account";
        command.Parameters.AddWithValue("$id",id);
        command.Parameters.AddWithValue("$account",account);
        using var reader=command.ExecuteReader();
        var result=reader.Read()?new DungeonDespairProgress(reader.GetInt32(0),reader.GetInt32(1),reader.GetInt32(2)):new();
        result.Validate();
        return result;
    }

    public DungeonDespairProgress LoadDespairProgress(AccountOwners.Lease owner,Character expected) => owner.Execute(() =>
    {
        using var db=database.Open();
        using var identity=db.CreateCommand();
        identity.CommandText="SELECT count(*) FROM characters WHERE id=$id AND account=$account AND name=$name";
        identity.Parameters.AddWithValue("$id",expected.Id);
        identity.Parameters.AddWithValue("$account",owner.Account);
        identity.Parameters.AddWithValue("$name",expected.Name);
        if(Convert.ToInt64(identity.ExecuteScalar())!=1) throw new InvalidOperationException("绝望进度角色归属变化");
        return ReadDespairProgress(db,null,owner.Account,expected.Id);
    });

    static void ValidateDespairEntry(SqliteConnection db,SqliteTransaction tx,AccountOwners.Lease owner,
        Character expected,DungeonSpecialEntry entry)
    {
        if(entry.Kind!=DungeonSpecialEntryKind.Despair) return;
        var progress=entry.DespairProgress ?? throw new InvalidDataException("绝望入场缺少原楼层版本");
        progress.Validate();
        if(progress.NextLayer!=entry.Layer || progress.LastClearDay==entry.Day ||
            ReadDespairProgress(db,tx,owner.Account,expected.Id)!=progress)
            throw new InvalidOperationException("绝望楼层或当日通关状态已变化");
        using var command=db.CreateCommand();
        command.Transaction=tx;
        command.CommandText="""
            INSERT INTO dungeon_despair_progress(character_id,account,next_layer,last_clear_day,clear_count)
            VALUES($id,$account,$layer,$day,$count) ON CONFLICT(character_id) DO NOTHING
            """;
        command.Parameters.AddWithValue("$id",expected.Id);
        command.Parameters.AddWithValue("$account",owner.Account);
        command.Parameters.AddWithValue("$layer",progress.NextLayer);
        command.Parameters.AddWithValue("$day",progress.LastClearDay);
        command.Parameters.AddWithValue("$count",progress.ClearCount);
        command.ExecuteNonQuery();
    }
}
