using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal static class GuildRequestSchema
{
    public const string Sql = """
        CREATE TABLE IF NOT EXISTS guild_request_claims(
            request_id TEXT PRIMARY KEY CHECK(length(request_id)=64),
            kind INTEGER NOT NULL CHECK(kind BETWEEN 1 AND 6),
            account TEXT NOT NULL,
            character_id INTEGER NOT NULL
        );
        INSERT INTO guild_request_claims SELECT request_id,1,account,character_id FROM guild_cargo_receipts;
        INSERT INTO guild_request_claims SELECT request_id,2,account,character_id FROM guild_economy_receipts;
        INSERT INTO guild_request_claims SELECT request_id,3,account,character_id FROM guild_teleport_receipts;
        """;
}

internal sealed partial class CharacterRepository
{
    // 同一连接序号只能兑现一种公会资产操作；精确内容仍由各业务回执复核。
    static bool GuildRequestAvailable(SqliteConnection db, SqliteTransaction tx, string requestId,
        int kind, string account, ushort characterId)
    {
        using var command = ManagementCommand(db, tx,
            "SELECT kind,account,character_id FROM guild_request_claims WHERE request_id=$key", ("$key", requestId));
        using var reader = command.ExecuteReader();
        return !reader.Read() || reader.GetInt32(0) == kind && reader.GetString(1) == account && reader.GetInt32(2) == characterId;
    }

    static void ClaimGuildRequest(SqliteConnection db, SqliteTransaction tx, string requestId,
        int kind, string account, ushort characterId)
    {
        using var command = ManagementCommand(db, tx, """
            INSERT INTO guild_request_claims(request_id,kind,account,character_id) VALUES($key,$kind,$account,$id)
            """, ("$key", requestId), ("$kind", kind), ("$account", account), ("$id", characterId));
        command.ExecuteNonQuery();
    }
}
