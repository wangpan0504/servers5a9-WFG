using DfoServer.Game.Characters;
using DfoServer.Game.Guild;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Guilds;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    static GuildNpcEmblemSnapshot? ReadNpcEmblem(SqliteConnection db, SqliteTransaction tx,
        GuildManagementIdentity actor, long now)
    {
        var benefits = ReadNpcBenefits(db, tx, actor.Owner.Account, actor.CharacterId, now);
        if (benefits is null) return null;
        using var command = ManagementCommand(db, tx,
            "SELECT joined_at_unix FROM guild_members WHERE guild_id=$guild AND character_id=$id",
            ("$guild", benefits.Management.Guild.Id), ("$id", actor.CharacterId));
        return command.ExecuteScalar() is long joined ? new(benefits, joined) : null;
    }

    public GuildNpcEmblemSnapshot? ReadGuildNpcEmblem(GuildManagementIdentity actor, long now) => actor.Owner.Execute(() =>
    {
        if (!actor.IsCurrent()) return null;
        using var db = database.Open(); using var tx = db.BeginRead();
        return ReadNpcEmblem(db, tx, actor, now);
    });

    public GuildNpcBenefitCommit CommitGuildNpcEmblem(GuildManagementIdentity actor, Character expected,
        InventoryPlan inventory, GuildNpcEmblemSelection selected, GuildNpcCatalog npcs,
        GuildNpcBenefitCatalog catalog, long now) => actor.Owner.Execute<GuildNpcBenefitCommit>(() =>
    {
        if (!actor.IsCurrent() || expected.Id != actor.CharacterId || now is <= 0 or > int.MaxValue ||
            inventory.Reason != "emblem_compound" || !ReferenceEquals(inventory.Before, expected.Inventory) ||
            inventory.CharacterPrecondition is null) return new(22);
        using var db = database.Open(); using var write = db.BeginWrite("GuildNpcEmblem.Compound");
        using var tx = write.Transaction;
        if (ReadNpcEmblem(db, tx, actor, now) is not { } snapshot ||
            GuildNpcBenefitService.SelectEmblem(snapshot, npcs, catalog, now) != selected) return new(22);
        var current = Require(db, tx, actor.Owner.Account, actor.Name);
        if (current.Id != expected.Id || current.Revision != expected.Revision || current.Position != expected.Position)
            return new(22);
        ValidateInventoryPlan(expected, inventory);
        Character committed;
        try { committed = CommitInventoryInTransaction(db, tx, actor.Owner, current, expected, inventory); }
        catch (InventoryQualificationExpiredException) { return new(22); }
        using var command = ManagementCommand(db, tx, selected.Definition.Common ? """
            UPDATE guild_npc_common_benefits SET remaining=remaining-1
            WHERE guild_id=$guild AND benefit_id=$benefit AND effect='emblem compound buff'
              AND remaining=$remaining AND remaining>0 AND deadline=$deadline AND deadline>$now
            """ : """
            UPDATE character_guild_npc_benefits SET remaining=remaining-1
            WHERE guild_id=$guild AND character_id=$id AND joined_at=$joined AND benefit_id=$benefit
              AND effect='emblem compound buff' AND remaining=$remaining AND remaining>0
              AND deadline=$deadline AND deadline>$now
            """, ("$guild", selected.GuildId), ("$id", actor.CharacterId), ("$joined", selected.JoinedAt),
            ("$benefit", selected.Progress.BenefitId), ("$remaining", selected.Progress.Remaining),
            ("$deadline", selected.Progress.Deadline), ("$now", now));
        if (command.ExecuteNonQuery() != 1 || !actor.IsCurrent()) return new(22);
        TouchManagement(db, tx, selected.GuildId);
        var state = ReadNpcBenefits(db, tx, actor.Owner.Account, actor.CharacterId, now);
        tx.Commit();
        return new(0, state, Character: committed, Inventory: inventory);
    });
}
