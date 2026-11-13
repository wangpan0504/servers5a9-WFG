using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Pvp;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    static PvpPrivateCharacterState? ReadPvpPrivate(SqliteConnection db,ushort id,SqliteTransaction? transaction)
    {
        using var command=db.CreateCommand();command.Transaction=transaction;
        command.CommandText="SELECT recreate_days FROM account_pvp_private_characters WHERE character_id=$id";
        command.Parameters.AddWithValue("$id",id);
        return command.ExecuteScalar() is long days ? new(checked((int)days)) : null;
    }

    public bool CanCreatePvpPrivate(AccountOwners.Lease owner,byte job,byte grow,PvpPrivateCharacterCreation creation)=>owner.Execute(()=>
    {
        creation.Validate();
        if(job>15 || grow>5) return false;
        using var db=database.Open();
        try {CheckPvpPrivateCreation(db,null,owner.Account,job,grow,creation);return true;}
        catch(InvalidOperationException) {return false;}
    });

    static void CheckPvpPrivateCreation(SqliteConnection db,SqliteTransaction transaction,string account,NewCharacter character)
    {
        if(character.PvpPrivateCreation is {} creation) CheckPvpPrivateCreation(db,transaction,account,character.Job,(byte)(character.GrowType&15),creation);
    }
    static void CheckPvpPrivateCreation(SqliteConnection db,SqliteTransaction? transaction,string account,byte job,byte grow,PvpPrivateCharacterCreation creation)
    {
        using var command=db.CreateCommand();command.Transaction=transaction;
        command.Parameters.AddWithValue("$account",account);
        command.Parameters.AddWithValue("$level",creation.AccountLevelRequirement);
        command.CommandText="""
            SELECT EXISTS(SELECT 1 FROM characters c WHERE c.account=$account AND c.level>=$level
              AND NOT EXISTS(SELECT 1 FROM account_pvp_private_characters p WHERE p.character_id=c.id))
            """;
        if(command.ExecuteScalar() is not 1L) throw new InvalidOperationException("账号没有满足等级的普通角色");
        command.Parameters.AddWithValue("$job",job);
        command.Parameters.AddWithValue("$grow",grow);
        command.CommandText="SELECT character_id,blocked_until FROM account_pvp_private_characters WHERE account=$account AND job=$job AND grow_type=$grow";
        using var reader=command.ExecuteReader();
        if(reader.Read() && (!reader.IsDBNull(0) || reader.GetInt64(1)>creation.CreatedAtUnix))
            throw new InvalidOperationException("同转职专用角色已存在或尚在删除冷却");
    }

    static void WritePvpPrivateCreation(SqliteConnection db,SqliteTransaction transaction,string account,ushort id,NewCharacter character)
    {
        if(character.PvpPrivateCreation is not {} creation) return;
        using var command=db.CreateCommand();command.Transaction=transaction;
        command.CommandText="""
            INSERT INTO account_pvp_private_characters(account,job,grow_type,character_id,blocked_until,recreate_days)
            VALUES($account,$job,$grow,$id,0,$days)
            ON CONFLICT(account,job,grow_type) DO UPDATE SET character_id=excluded.character_id,
                blocked_until=0,recreate_days=excluded.recreate_days
            WHERE account_pvp_private_characters.character_id IS NULL AND account_pvp_private_characters.blocked_until<=$now
            """;
        command.Parameters.AddWithValue("$account",account);command.Parameters.AddWithValue("$job",character.Job);
        command.Parameters.AddWithValue("$grow",character.GrowType&15);command.Parameters.AddWithValue("$id",id);
        command.Parameters.AddWithValue("$days",creation.RecreateDays);command.Parameters.AddWithValue("$now",creation.CreatedAtUnix);
        if(command.ExecuteNonQuery()!=1) throw new InvalidOperationException("专用角色创建资格已经变化");
    }

    public Character ConvertPvpPrivateToNormal(AccountOwners.Lease owner, Character expected) => owner.Execute(() =>
    {
        if (!expected.IsPvpPrivate)
            throw new InvalidOperationException("非决斗专用角色不可转换");
        using var db = database.Open();
        using var tx = db.BeginTransaction();
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.Parameters.AddWithValue("$account", owner.Account);
        cmd.Parameters.AddWithValue("$id", expected.Id);
        cmd.Parameters.AddWithValue("$rev", expected.Revision);
        cmd.CommandText = "UPDATE account_pvp_private_characters SET character_id=NULL WHERE account=$account AND character_id=$id";
        if (cmd.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("决斗专用角色身份不存在或已转换");
        cmd.CommandText = "UPDATE characters SET revision=revision+1 WHERE id=$id AND account=$account AND revision=$rev";
        if (cmd.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("角色版本不匹配");
        tx.Commit();
        return expected with { PvpPrivate = null, Revision = checked(expected.Revision + 1) };
    });
}
