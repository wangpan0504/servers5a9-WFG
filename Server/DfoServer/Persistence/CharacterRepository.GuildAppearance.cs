using DfoServer.Game.Accounts;
using DfoServer.Game.Guild;
using DfoServer.GameWorld.Guilds;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository : IGuildAppearanceStore
{
    internal static GuildAppearance ReadAppearanceInTransaction(SqliteConnection db, SqliteTransaction? tx, uint guildId)
    {
        using var command = ManagementCommand(db, tx,
            "SELECT mark,aura_mark,aura_frame FROM guild_appearance WHERE guild_id=$guild", ("$guild", guildId));
        using var row = command.ExecuteReader();
        return row.Read() ? new(checked((uint)row.GetInt64(0)), checked((uint)row.GetInt64(1)),
            checked((uint)row.GetInt64(2))) : new();
    }

    public GuildAppearance? ReadGuildAppearance(AccountOwners.Lease owner, ushort characterId) => owner.Execute(() =>
    {
        using var db = database.Open();
        using var tx = db.BeginRead();
        using var command = ManagementCommand(db, tx, """
            SELECT m.guild_id FROM guild_members m JOIN characters c ON c.id=m.character_id
            WHERE c.account=$account AND c.id=$id
            """, ("$account", owner.Account), ("$id", characterId));
        object? value = command.ExecuteScalar();
        return value is null ? null : ReadAppearanceInTransaction(db, tx, checked((uint)Convert.ToInt64(value)));
    });

    public GuildAppearanceCommit ChangeGuildAppearance(GuildManagementIdentity actor, GuildAppearanceChange change,
        GuildAppearanceCatalog catalog) => actor.Owner.Execute(() =>
    {
        if (!actor.IsCurrent()) return new GuildAppearanceCommit(34);
        if (change.Aura ? !catalog.ValidAura(change.AuraMark, change.AuraFrame) :
            change.LegacyMark ? !catalog.ValidLegacyMark(change.Mark) : !catalog.ValidPackedMark(change.Mark))
            return new(34);
        using var db = database.Open();
        using var write = db.BeginWrite("CharacterRepository.ChangeGuildAppearance");
        using var tx = write.Transaction;
        var state = ReadManagement(db, tx, actor.Owner.Account, actor.CharacterId);
        if (state is null) return new(34);
        if (state.MasterId != actor.CharacterId || state.Grade != 1) return new(36);
        if (!change.Aura && !change.LegacyMark && !catalog.ValidModernMark(change.Mark, state.Guild.Tier, state.Guild.Level))
            return new(34);
        uint id = state.Guild.Id;
        var before = ReadAppearanceInTransaction(db, tx, id);
        var after = change.Aura ? before with { AuraMark = change.AuraMark, AuraFrame = change.AuraFrame } :
            before with { Mark = change.Mark };
        // 同值重试不再次收费，资格仍在同一事务中复核。
        if (before == after) return new(0, before, new(0, id, state.Revision, Members: state.Members));
        using var command = ManagementCommand(db, tx, "SELECT 1");
        // 旧单字节选择器有独立费用声明；现代组合会标按成长门槛解锁，无费用声明。
        int price = change.Aura || !change.LegacyMark ? 0 : catalog.Price(change.Mark);
        if (price > 0)
        {
            command.CommandText = "UPDATE guilds SET public_funds=public_funds-$price WHERE guild_id=$guild AND public_funds>=$price";
            command.Parameters.AddWithValue("$price", price);
            command.Parameters.AddWithValue("$guild", id);
            if (command.ExecuteNonQuery() != 1) return new(22);
        }
        command.CommandText = """
            INSERT INTO guild_appearance(guild_id,mark,aura_mark,aura_frame) VALUES($guild,$mark,$auraMark,$auraFrame)
            ON CONFLICT(guild_id) DO UPDATE SET mark=excluded.mark,aura_mark=excluded.aura_mark,aura_frame=excluded.aura_frame
            """;
        command.Parameters.Clear();
        command.Parameters.AddWithValue("$guild", id);
        command.Parameters.AddWithValue("$mark", after.Mark);
        command.Parameters.AddWithValue("$auraMark", after.AuraMark);
        command.Parameters.AddWithValue("$auraFrame", after.AuraFrame);
        command.ExecuteNonQuery();
        long revision = TouchManagement(db, tx, id);
        if (!actor.IsCurrent()) return new(34);
        tx.Commit();
        return new(0, after, new(0, id, revision, Members: state.Members));
    });
}
