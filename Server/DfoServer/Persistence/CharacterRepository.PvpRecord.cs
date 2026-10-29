using DfoServer.Game.Accounts;
using DfoServer.Game.Pvp;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository : IPvpRecordStore
{
    public PvpRecordStatistics ReadPvpRecord(AccountOwners.Lease owner, ushort characterId) => owner.Execute(() =>
    {
        using var db = database.Open();
        RequirePvpRatingCharacter(db, null, owner.Account, characterId);
        using var query = db.CreateCommand();
        // 仅消费已到账个人匹配来源。普通手动局、取消、总力角色经验及未结算局均不冒充个人排位。
        // 数据库聚合标量，不装载历史整局JSON或其它角色资产；近期按最后十局计算。
        query.CommandText = """
            WITH settled AS (
                SELECT outcome, json_extract(receipt_json,'$.Change.After.WinStreak') AS streak,
                    row_number() OVER (ORDER BY sequence DESC) AS recent
                FROM character_pvp_rating_claims
                WHERE account=$account AND character_id=$id AND rating_mode=1
                    AND receipt_json IS NOT NULL AND outcome IN ($win,$loss,$draw)
            )
            SELECT coalesce(sum(outcome=$win),0), coalesce(sum(outcome=$loss),0), coalesce(sum(outcome=$draw),0),
                coalesce(sum(recent<=10 AND outcome=$win),0), coalesce(sum(recent<=10 AND outcome=$loss),0),
                coalesce(sum(recent<=10 AND outcome=$draw),0),
                coalesce(max(CASE WHEN recent=1 THEN streak END),0), coalesce(max(streak),0)
            FROM settled
            """;
        query.Parameters.AddWithValue("$account", owner.Account);
        query.Parameters.AddWithValue("$id", characterId);
        query.Parameters.AddWithValue("$win", (int)PvpArenaOutcome.Win);
        query.Parameters.AddWithValue("$loss", (int)PvpArenaOutcome.Lose);
        query.Parameters.AddWithValue("$draw", (int)PvpArenaOutcome.Draw);
        using var reader = query.ExecuteReader();
        if (!reader.Read()) throw new InvalidDataException("决斗战绩聚合结果缺失");
        return new PvpRecordStatistics(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2),
            checked((byte)reader.GetInt32(3)), checked((byte)reader.GetInt32(4)), checked((byte)reader.GetInt32(5)),
            reader.GetInt32(6), reader.GetInt32(7));
    });
}
