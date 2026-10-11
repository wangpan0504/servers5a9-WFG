using DfoServer.Game.Accounts;
using DfoServer.Game.Dungeons;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    public CardMembership LoadCardMembership(AccountOwners.Lease owner,ushort characterId,DateTimeOffset now) => owner.Execute(() =>
    {
        using var db = database.Open();
        using var tx = db.BeginTransaction(deferred:true);
        VerifyCardCharacter(db,tx,owner.Account,characterId);
        var result = ReadCardMembership(db,tx,owner.Account,characterId,now);
        tx.Commit();
        return result;
    });

    public CardMembership SetCardMembership(AccountOwners.Lease owner,string name,CardMembershipKind kind,long expires) => owner.Execute(() =>
    {
        if (expires < 0 || !Enum.IsDefined(kind)) throw new InvalidDataException("会员类型或到期时间无效");
        using var db = database.Open();
        using var tx = db.BeginTransaction(deferred:false);
        using var query = db.CreateCommand();
        query.Transaction = tx;
        query.Parameters.AddWithValue("$account",owner.Account);
        query.Parameters.AddWithValue("$name",name);
        query.CommandText = "SELECT id FROM characters WHERE account=$account AND name=$name";
        if (query.ExecuteScalar() is not long id) throw new InvalidDataException("会员设置目标角色不属于该账号");
        string field = kind == CardMembershipKind.BlackDiamond ? "black_diamond_expires" : "gold_card_expires";
        query.Parameters.AddWithValue("$expires",expires);
        query.CommandText = $"INSERT INTO account_card_memberships(account,{field},revision) VALUES($account,$expires,1) ON CONFLICT(account) DO UPDATE SET {field}=$expires,revision=revision+1";
        query.ExecuteNonQuery();
        var result = ReadCardMembership(db,tx,owner.Account,checked((ushort)id),DateTimeOffset.UtcNow);
        tx.Commit();
        return result;
    });

    static CardMembership ReadCardMembership(SqliteConnection db,SqliteTransaction tx,string account,ushort id,DateTimeOffset now)
    {
        using var query = db.CreateCommand();
        query.Transaction = tx;
        query.Parameters.AddWithValue("$account",account);
        query.CommandText = "SELECT black_diamond_expires,gold_card_expires,revision FROM account_card_memberships WHERE account=$account";
        long black = 0,gold = 0,revision = 0;
        using (var reader = query.ExecuteReader())
            if (reader.Read()) { black = reader.GetInt64(0);gold = reader.GetInt64(1);revision = reader.GetInt64(2); }
        int day = CardMembership.ServiceDay(now);
        query.Parameters.AddWithValue("$id",id);
        query.Parameters.AddWithValue("$day",day);
        query.CommandText = "SELECT used FROM character_gold_card_uses WHERE character_id=$id AND account=$account AND service_day=$day";
        int used = query.ExecuteScalar() is long value ? checked((int)value) : 0;
        return new(black,gold,revision,day,used);
    }

    static void VerifyCardCharacter(SqliteConnection db,SqliteTransaction tx,string account,ushort id)
    {
        using var query = db.CreateCommand();
        query.Transaction = tx;
        query.Parameters.AddWithValue("$account",account);
        query.Parameters.AddWithValue("$id",id);
        query.CommandText = "SELECT count(*) FROM characters WHERE account=$account AND id=$id";
        if (query.ExecuteScalar() is not 1L) throw new InvalidDataException("翻牌会员角色与账号不匹配");
    }
}
