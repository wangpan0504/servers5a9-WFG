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
        using var tx = db.BeginRead();
        VerifyCardCharacter(db,tx,owner.Account,characterId);
        var result = ReadCardMembership(db,tx,owner.Account,characterId,now);
        tx.Commit();
        return result;
    });

    public CardMembership SetCardMembership(AccountOwners.Lease owner,string name,CardMembershipKind kind,long expires) => owner.Execute(() =>
    {
        if (expires < 0 || !Enum.IsDefined(kind)) throw new InvalidDataException("会员类型或到期时间无效");
        using var db = database.Open();
        using var write = db.BeginWrite("CharacterRepository.SetCardMembership", deferred: true);
        using var tx = write.Transaction;
        var current = Require(db, tx, owner.Account, name);
        int type = kind == CardMembershipKind.BlackDiamond ? CardMembership.BlackDiamondType : CardMembership.DevilContractType;
        var before = current.Inventory;
        var after = before with { AccountWallet = before.AccountWallet.SetContractExpiry(type, expires) };
        bool blackDiamond = kind == CardMembershipKind.BlackDiamond && expires > DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (blackDiamond)
            after = after with { StaminaUntil = 0 };
        var plan = new InventoryPlan(before, after, "gm_premium_contract");
        var committed = CommitInventoryInTransaction(db, tx, owner, current, plan);
        if (blackDiamond)
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
        query.CommandText = "SELECT devil_usage FROM characters WHERE id=$id AND account=$account";
        var devilUses = System.Text.Json.JsonSerializer.Deserialize<Game.Inventory.Models.DevilServiceUsage[]>((string)query.ExecuteScalar()!);
        return new(wallet.ContractExpiry(CardMembership.BlackDiamondType), Game.Inventory.Services.DevilContractPolicy.Expiry(wallet, 0),
            wallet.Revision,day,used)
        {
            DevilServiceExpiries = System.Collections.Immutable.ImmutableArray.CreateRange(
                Enumerable.Range(0, 8).Select(slot => Game.Inventory.Services.DevilContractPolicy.Expiry(wallet, slot))),
            DevilServiceUsed = System.Collections.Immutable.ImmutableArray.CreateRange(Enumerable.Range(0, 8)
                .Select(slot => slot == 0 ? checked((byte)used) : Game.Inventory.Services.DevilContractPolicy.ClientUsed(
                    devilUses!.SingleOrDefault(row => row.Slot == slot && row.Day == day))))
        };
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
            SELECT EXISTS(SELECT 1 FROM characters WHERE account=$account AND
              (stamina_until>0 OR id IN (SELECT character_id FROM character_fatigue WHERE account=$account AND maximum=$base)))
            """;
        if (query.ExecuteScalar() is 0L) return;
        query.CommandText = """
            UPDATE characters SET revision=revision+1,stamina_until=0
              WHERE account=$account AND ($committed IS NULL OR id<>$committed) AND
                (stamina_until>0 OR id IN (SELECT character_id FROM character_fatigue WHERE account=$account AND maximum=$base));
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
