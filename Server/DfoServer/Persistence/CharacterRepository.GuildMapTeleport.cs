using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Town;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    public GuildMapTeleportCommit CommitGuildMapTeleport(AccountOwners.Lease owner, TeleportPlan plan, string requestId) =>
        owner.Execute(() =>
        {
            var expected = plan.Expected;
            if (expected.Guild is not { } guild || !Game.Guild.GuildEconomyService.ValidRequestId(requestId) ||
                plan.Inventory.Reason != "guild_map_teleport") return new GuildMapTeleportCommit("guild-teleport-unavailable");
            using var db = database.Open();
            using var write = db.BeginWrite("CharacterRepository.CommitGuildMapTeleport");
            using var tx = write.Transaction;
            if (!GuildRequestAvailable(db, tx, requestId, 3, owner.Account, expected.Id))
                return new("guild-request-conflict");
            using var command = ManagementCommand(db, tx, """
                SELECT joined_at_unix FROM guild_members WHERE guild_id=$guild AND character_id=$id
                """, ("$guild", guild.Id), ("$id", expected.Id), ("$key", requestId), ("$account", owner.Account));
            if (command.ExecuteScalar() is not long joined) return new("guild-membership-changed");
            // 单人地图入口与434共用请求归属表，但回执内容明确区分，不能相互重放兑现。
            string destination = JsonSerializer.Serialize(new { Kind = "guild-map", plan.Position });
            command.CommandText = """
                SELECT account,character_id,guild_id,joined_at_unix,destination
                FROM guild_teleport_receipts WHERE request_id=$key
                """;
            using (var reader = command.ExecuteReader())
            {
                if (reader.Read())
                    return reader.GetString(0) == owner.Account && reader.GetInt32(1) == expected.Id &&
                        reader.GetInt64(2) == guild.Id && reader.GetInt64(3) == joined && reader.GetString(4) == destination
                        ? new("replayed", Replayed: true) : new("guild-request-conflict");
            }
            if (!plan.Inventory.Changed) return new("guild-teleport-coins-insufficient");
            try
            {
                var committed = CommitTeleportInTransaction(db, tx, owner, plan);
                command.CommandText = """
                    INSERT INTO guild_teleport_receipts(request_id,account,character_id,guild_id,joined_at_unix,participants,destination)
                    VALUES($key,$account,$id,$guild,$joined,$participants,$destination)
                    """;
                command.Parameters.AddWithValue("$joined", joined);
                command.Parameters.AddWithValue("$participants", JsonSerializer.Serialize(new[] { expected.Id }));
                command.Parameters.AddWithValue("$destination", destination);
                command.ExecuteNonQuery();
                ClaimGuildRequest(db, tx, requestId, 3, owner.Account, expected.Id);
                tx.Commit();
                return new("committed", committed);
            }
            catch (SqliteException error) when (error.SqliteErrorCode == 19)
            {
                return new("guild-teleport-commit-rejected");
            }
        });
}
