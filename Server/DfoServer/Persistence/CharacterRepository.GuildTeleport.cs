using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Guild;

namespace DfoServer.Persistence;

internal static class GuildTeleportSchema
{
    public const string Sql = """
        CREATE TABLE IF NOT EXISTS guild_teleport_receipts(
            request_id TEXT PRIMARY KEY CHECK(length(request_id)=64),
            account TEXT NOT NULL,
            character_id INTEGER NOT NULL,
            guild_id INTEGER NOT NULL,
            joined_at_unix INTEGER NOT NULL,
            participants TEXT NOT NULL,
            destination TEXT NOT NULL
        );
        """;
}

internal sealed partial class CharacterRepository : IGuildTeleportStore
{
    public GuildTeleportEntitlement? ReadGuildTeleportEntitlement(AccountOwners.Lease owner, ushort characterId) => owner.Execute(() =>
    {
        using var db = database.Open();
        using var command = ManagementCommand(db, null, """
            SELECT m.guild_id,m.joined_at_unix,COALESCE(b.value,0) FROM guild_members m JOIN characters c ON c.id=m.character_id
            LEFT JOIN guild_benefits b ON b.guild_id=m.guild_id AND b.type=7
            WHERE m.character_id=$id AND c.account=$account
            """, ("$id", characterId), ("$account", owner.Account));
        using var reader = command.ExecuteReader();
        return reader.Read() ? new GuildTeleportEntitlement(checked((uint)reader.GetInt64(0)), reader.GetInt64(1),
            checked((uint)reader.GetInt64(2))) : null;
    });
    public GuildTeleportCommit CommitGuildTeleport(GuildTeleportCommitPlan plan) => AccountOwners.Lease.ExecuteGroup(
        plan.Positions.Select(position => (position.Peer.Identity.Owner, position.Peer.Identity.CharacterId)).ToArray(),
        () =>
        {
            if (!GuildEconomyService.ValidRequestId(plan.RequestId) || plan.Positions.Length is < 1 or > 4 || !plan.IsCurrent() ||
                plan.Positions.Select(position => position.Expected.Id).Distinct().Count() != plan.Positions.Length ||
                plan.Positions.Any(position => position.Destination != plan.Positions[0].Destination)) return new GuildTeleportCommit(7);
            using var db = database.Open(); using var write = db.BeginWrite("CharacterRepository.CommitGuildTeleport");
            using var tx = write.Transaction;
            var leader = plan.Leader.Identity;
            if (!GuildRequestAvailable(db, tx, plan.RequestId, 3, leader.Owner.Account, leader.CharacterId)) return new(7);
            using var membership = ManagementCommand(db, tx, """
                SELECT 1 FROM guild_members m JOIN characters c ON c.id=m.character_id
                WHERE m.guild_id=$guild AND m.character_id=$id AND m.joined_at_unix=$joined AND c.account=$account
                """, ("$guild", plan.Entitlement.GuildId), ("$id", leader.CharacterId),
                ("$joined", plan.Entitlement.JoinedAtUnix), ("$account", leader.Owner.Account));
            if (membership.ExecuteScalar() is null) return new(7);
            string participants = JsonSerializer.Serialize(plan.Positions.Select(position => new
                { position.Peer.Identity.Owner.Account, position.Expected.Id, position.Expected.Revision, position.Expected.Position }));
            string destination = JsonSerializer.Serialize(plan.Positions[0].Destination);
            using var receipt = ManagementCommand(db, tx, """
                SELECT account,character_id,guild_id,joined_at_unix,participants,destination FROM guild_teleport_receipts WHERE request_id=$key
                """, ("$key", plan.RequestId));
            using (var reader = receipt.ExecuteReader())
                if (reader.Read())
                {
                    bool matches = reader.GetString(0) == leader.Owner.Account && reader.GetInt32(1) == leader.CharacterId &&
                        reader.GetInt64(2) == plan.Entitlement.GuildId && reader.GetInt64(3) == plan.Entitlement.JoinedAtUnix &&
                        reader.GetString(4) == participants && reader.GetString(5) == destination;
                    return new(matches ? (ushort)0 : (ushort)7, Replayed: matches);
                }
            var committed = new List<Character>();
            foreach (var position in plan.Positions)
            {
                var peer = position.Peer.Identity; var expected = position.Expected;
                var current = Require(db, tx, peer.Owner.Account, peer.Name);
                if (current.Id != peer.CharacterId || current.Revision != expected.Revision || current.Position != expected.Position ||
                    current.Return != expected.Return || current.Level != expected.Level ||
                    !current.CompletedQuests.SequenceEqual(expected.CompletedQuests) ||
                    !current.AccountCompletedQuests.SequenceEqual(expected.AccountCompletedQuests)) return new(7);
                using var command = ManagementCommand(db, tx, """
                    UPDATE characters SET revision=revision+1,town=$town,area=$area,x=$x,y=$y,direction=$direction,
                        return_town=$rtown,return_area=$rarea,return_x=$rx,return_y=$ry,return_direction=$rdirection
                    WHERE id=$id AND account=$account AND revision=$revision
                    """, ("$id", current.Id), ("$account", peer.Owner.Account), ("$revision", current.Revision));
                Bind(command, "", position.Destination); Bind(command, "r", position.Return);
                if (command.ExecuteNonQuery() != 1) return new(7);
                committed.Add(current with { Revision = current.Revision + 1, Position = position.Destination, Return = position.Return });
            }
            if (!ConsumeGuildTeleportInTransaction(db, tx, leader.Owner, leader.CharacterId,
                plan.Entitlement.GuildId, plan.Entitlement.JoinedAtUnix) || !plan.IsCurrent()) return new(7);
            receipt.Parameters.Clear(); receipt.CommandText = """
                INSERT INTO guild_teleport_receipts(request_id,account,character_id,guild_id,joined_at_unix,participants,destination)
                VALUES($key,$account,$id,$guild,$joined,$participants,$destination)
                """;
            receipt.Parameters.AddWithValue("$key", plan.RequestId); receipt.Parameters.AddWithValue("$account", leader.Owner.Account);
            receipt.Parameters.AddWithValue("$id", leader.CharacterId); receipt.Parameters.AddWithValue("$guild", plan.Entitlement.GuildId);
            receipt.Parameters.AddWithValue("$joined", plan.Entitlement.JoinedAtUnix); receipt.Parameters.AddWithValue("$participants", participants);
            receipt.Parameters.AddWithValue("$destination", destination); receipt.ExecuteNonQuery();
            ClaimGuildRequest(db, tx, plan.RequestId, 3, leader.Owner.Account, leader.CharacterId);
            if (!plan.IsCurrent()) return new(7);
            tx.Commit(); return new(0, committed.ToArray());
        });
}
