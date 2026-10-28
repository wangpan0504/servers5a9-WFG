using DfoServer.Game.Characters;
using DfoServer.Game.Guild;
using DfoServer.Game.Inventory.Models;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    static uint ReadGuildHongbaoSpent(SqliteConnection db, SqliteTransaction tx, ushort characterId, long day)
    {
        using var query = ManagementCommand(db, tx, "SELECT cera FROM character_guild_hongbao_purchases WHERE character_id=$id AND day_start_unix=$day",
            ("$id", characterId), ("$day", day));
        return query.ExecuteScalar() is long cera ? checked((uint)cera) : 0;
    }

    static void CommitGuildHongbaoPurchase(SqliteConnection db, SqliteTransaction tx, Character current, InventoryPlan plan)
    {
        if (plan.GuildHongbaoPurchase is not { } purchase) return;
        if (plan.Reason != "cash_shop_buy" || purchase.GuildId == 0 || purchase.Cera <= 0 || purchase.MinimumMembers <= 0 ||
            purchase.DailyLimit <= 0 || purchase.Cera > purchase.DailyLimit ||
            purchase.DayStartUnix != GuildAttendanceDay.Start(DateTimeOffset.UtcNow.ToUnixTimeSeconds()))
            throw new GuildHongbaoPurchaseRejectedException("公会红包购买额度或日期已失效");
        using var query = ManagementCommand(db, tx, """
            SELECT guild_id FROM guild_members WHERE character_id=$id
            """, ("$id", current.Id), ("$guild", purchase.GuildId));
        if (query.ExecuteScalar() is not long guild || guild != purchase.GuildId)
            throw new GuildHongbaoPurchaseRejectedException("公会红包购买会籍已变化");
        query.CommandText = "SELECT count(*) FROM guild_members WHERE guild_id=$guild";
        if (Convert.ToInt64(query.ExecuteScalar()) < purchase.MinimumMembers)
            throw new GuildHongbaoPurchaseRejectedException("公会成员不足，不能购买红包");
        // 额度属于角色，退会、换会或解散不清除；与真实点券及商品库存共用原写事务。
        query.CommandText = """
            INSERT INTO character_guild_hongbao_purchases(character_id,day_start_unix,cera) VALUES($id,$day,$amount)
            ON CONFLICT(character_id,day_start_unix) DO UPDATE SET cera=cera+$amount WHERE cera<=$limit-$amount
            """;
        query.Parameters.AddWithValue("$day", purchase.DayStartUnix);
        query.Parameters.AddWithValue("$amount", purchase.Cera);
        query.Parameters.AddWithValue("$limit", purchase.DailyLimit);
        if (query.ExecuteNonQuery() != 1)
            throw new GuildHongbaoPurchaseRejectedException("角色今日公会红包购买已达上限");
    }
}
