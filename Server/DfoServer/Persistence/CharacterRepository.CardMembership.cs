using DfoServer.Game.Accounts;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
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
        using var tx = db.BeginTransaction(deferred:true);
        var current = Require(db, tx, owner.Account, name);
        int type = kind == CardMembershipKind.BlackDiamond ? CardMembership.BlackDiamondType : CardMembership.DevilContractType;
        var before = current.Inventory;
        var after = before with { AccountWallet = before.AccountWallet.SetContractExpiry(type, expires) };
        var plan = new InventoryPlan(before, after, "gm_premium_contract");
        var committed = CommitInventoryInTransaction(db, tx, owner, current, plan);
        if (kind == CardMembershipKind.BlackDiamond && expires > DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            GrantAccountBlackDiamondFatigue(db, tx, owner.Account, committed.Revision != current.Revision ? current.Id : null);
        var result = ReadCardMembership(db,tx,owner.Account,current.Id,DateTimeOffset.UtcNow);
        tx.Commit();
        return result;
    });

    static CardMembership ReadCardMembership(SqliteConnection db,SqliteTransaction tx,string account,ushort id,DateTimeOffset now)
    {
        using var query = db.CreateCommand();
        query.Transaction = tx;
        query.Parameters.AddWithValue("$account",account);
        var wallet = ReadAccountWallet(db, account, tx);
        int day = CardMembership.ServiceDay(now);
        query.Parameters.AddWithValue("$id",id);
        query.Parameters.AddWithValue("$day",day);
        query.CommandText = "SELECT used FROM character_gold_card_uses WHERE character_id=$id AND account=$account AND service_day=$day";
        int used = query.ExecuteScalar() is long value ? checked((int)value) : 0;
        return new(wallet.ContractExpiry(CardMembership.BlackDiamondType), wallet.ContractExpiry(CardMembership.DevilContractType),
            wallet.Revision,day,used);
    }

    static void GrantAccountBlackDiamondFatigue(SqliteConnection db, SqliteTransaction tx, string account, ushort? committedCharacter = null)
    {
        using var query = db.CreateCommand();
        query.Transaction = tx;
        query.Parameters.AddWithValue("$account", account);
        query.Parameters.AddWithValue("$base", CharacterFatigueState.OrdinaryMaximum);
        query.Parameters.AddWithValue("$bonus", CharacterFatigueState.BlackDiamondBonus);
        query.Parameters.AddWithValue("$committed", (object?)committedCharacter ?? DBNull.Value);
        query.CommandText = """
            UPDATE characters SET revision=revision+1 WHERE account=$account AND ($committed IS NULL OR id<>$committed) AND id IN
              (SELECT character_id FROM character_fatigue WHERE account=$account AND maximum=$base);
            UPDATE character_fatigue SET maximum=maximum+$bonus,remaining=remaining+$bonus
              WHERE account=$account AND maximum=$base;
            """;
        query.ExecuteNonQuery();
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
