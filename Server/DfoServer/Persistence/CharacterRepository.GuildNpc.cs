using DfoServer.Game.Guild;
using DfoServer.GameWorld.Guilds;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal static class GuildNpcSchema
{
    public const string Sql = """
        CREATE TABLE IF NOT EXISTS guild_npc_progress(
            guild_id INTEGER NOT NULL REFERENCES guilds(guild_id) ON DELETE CASCADE,
            npc_id INTEGER NOT NULL,
            level INTEGER NOT NULL CHECK(level BETWEEN 1 AND 5),
            upgrade_target INTEGER NOT NULL DEFAULT 0 CHECK(upgrade_target BETWEEN 0 AND 5),
            upgrade_until INTEGER NOT NULL DEFAULT 0 CHECK(upgrade_until BETWEEN 0 AND 2147483647),
            contract_until INTEGER NOT NULL DEFAULT 0 CHECK(contract_until BETWEEN 0 AND 2147483647),
            PRIMARY KEY(guild_id,npc_id),
            CHECK((upgrade_target=0 AND upgrade_until=0) OR (upgrade_target=level+1 AND upgrade_until>0))
        );
        CREATE INDEX IF NOT EXISTS guild_npc_due ON guild_npc_progress(upgrade_until) WHERE upgrade_until>0;
        CREATE TABLE IF NOT EXISTS guild_npc_receipts(
            request_id TEXT PRIMARY KEY CHECK(length(request_id)=64),
            account TEXT NOT NULL,
            character_id INTEGER NOT NULL,
            guild_id INTEGER NOT NULL,
            joined_at INTEGER NOT NULL,
            npc_id INTEGER NOT NULL,
            action INTEGER NOT NULL,
            argument INTEGER NOT NULL,
            deadline INTEGER NOT NULL
        );
        """;
}

internal sealed partial class CharacterRepository : IGuildNpcStore
{
    static GuildNpcState? ReadNpcs(SqliteConnection db, SqliteTransaction? tx, string account, ushort id,
        GuildNpcCatalog catalog)
    {
        var management = ReadManagement(db, tx, account, id);
        if (management is null) return null;
        var states = catalog.Definitions.ToDictionary(row => row.Id, row => new GuildNpcProgress(row.Id, 1, 0, 0));
        using var command = ManagementCommand(db, tx,
            "SELECT npc_id,level,upgrade_until,contract_until FROM guild_npc_progress WHERE guild_id=$guild",
            ("$guild", management.Guild.Id));
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            uint npcId = checked((uint)reader.GetInt64(0));
            if (catalog.Find(npcId) is not { } definition || definition.At(reader.GetInt32(1)) is null)
                throw new InvalidDataException("持久公会NPC与资源名册不符");
            states[npcId] = new(npcId, reader.GetInt32(1), reader.GetInt64(2), reader.GetInt64(3));
        }
        reader.Close();
        return new(management, states.Values.OrderBy(row => row.NpcId).ToArray())
        { Crafts = ReadNpcCrafts(db, tx, management.Guild.Id) };
    }
    public GuildNpcState? ReadGuildNpcs(GuildManagementIdentity actor, GuildNpcCatalog catalog, long now) => actor.Owner.Execute(() =>
    {
        if (!actor.IsCurrent()) return null;
        using var db = database.Open(); using var tx = db.BeginRead();
        return ReadNpcs(db, tx, actor.Owner.Account, actor.CharacterId, catalog);
    });

    public GuildNpcCommit UpgradeGuildNpc(GuildManagementIdentity actor, GuildNpcCatalog catalog, uint npcId,
        uint argument, string requestId, long now) => ChangeGuildNpc(actor, catalog, 1, npcId, argument, requestId, now);
    public GuildNpcCommit ContractGuildNpc(GuildManagementIdentity actor, GuildNpcCatalog catalog, uint npcId,
        uint argument, string requestId, long now) => ChangeGuildNpc(actor, catalog, 2, npcId, argument, requestId, now);

    GuildNpcCommit ChangeGuildNpc(GuildManagementIdentity actor, GuildNpcCatalog catalog, uint action, uint npcId,
        uint argument, string requestId, long now) => actor.Owner.Execute<GuildNpcCommit>(() =>
    {
        if (!actor.IsCurrent() || !GuildEconomyService.ValidRequestId(requestId) || now is <= 0 or > int.MaxValue ||
            catalog.Find(npcId) is not { } definition || action == 1 && argument != 0) return new(1, action);
        using var db = database.Open(); using var write = db.BeginWrite("CharacterRepository.ChangeGuildNpc");
        using var tx = write.Transaction;
        var state = ReadNpcs(db, tx, actor.Owner.Account, actor.CharacterId, catalog);
        if (state is null || !GuildRequestAvailable(db, tx, requestId, 4, actor.Owner.Account, actor.CharacterId)) return new(1, action);
        uint guildId = state.Management.Guild.Id;
        using var command = ManagementCommand(db, tx,
            "SELECT joined_at_unix FROM guild_members WHERE guild_id=$guild AND character_id=$id",
            ("$guild", guildId), ("$id", actor.CharacterId));
        long joined = Convert.ToInt64(command.ExecuteScalar());
        command.CommandText = "SELECT account,character_id,guild_id,joined_at,npc_id,action,argument FROM guild_npc_receipts WHERE request_id=$request";
        command.Parameters.AddWithValue("$request", requestId);
        using (var reader = command.ExecuteReader())
            if (reader.Read())
            {
                bool matches = reader.GetString(0) == actor.Owner.Account && reader.GetInt32(1) == actor.CharacterId &&
                    reader.GetInt64(2) == guildId && reader.GetInt64(3) == joined && reader.GetInt64(4) == npcId &&
                    reader.GetInt64(5) == action && reader.GetInt64(6) == argument;
                return matches ? new(0, action, state, true) : new(1, action);
            }
        // 守护石晋阶为现任会长专属；现代NPC管理窗口使用独立权限位18。
        if (action == 1 && npcId == GuildNpcCatalog.Guardian
            ? state.Management.MasterId != actor.CharacterId
            : (state.Management.Permissions & GuildRoleRules.NpcManagementPermission) == 0) return new(1, action);
        var progress = state.Npcs.Single(row => row.NpcId == npcId);
        var level = definition.At(progress.Level)!;
        long deadline;
        int funds, crystals;
        if (action == 1)
        {
            if (progress.UpgradeUntil != 0) return new(2, action);
            if (definition.At(progress.Level + 1) is null || level.UpgradeDays <= 0 ||
                state.Management.Guild.Tier < level.RequiredTier || state.Management.Guild.Tier == level.RequiredTier &&
                state.Management.Guild.Level < level.RequiredLevel) return new(1, action);
            funds = (level.FirstCurrency == "public funds" ? level.FirstAmount : 0) +
                (level.SecondCurrency == "public funds" ? level.SecondAmount : 0);
            crystals = (level.FirstCurrency == "guild crystal" ? level.FirstAmount : 0) +
                (level.SecondCurrency == "guild crystal" ? level.SecondAmount : 0);
            deadline = checked(now + level.UpgradeDays * 86400L);
        }
        else
        {
            if (!definition.Contract || catalog.Contract(progress.Level, argument) is not { } contract ||
                state.Management.Guild.Tier < level.ClassTier) return new(3, action);
            funds = contract.PublicFunds; crystals = 0;
            deadline = checked(Math.Max(now, progress.ContractUntil) + contract.Days * 86400L);
            if (deadline - now > 90 * 86400L) return new(1, action);
        }
        if (deadline > int.MaxValue) return new(1, action);
        command.CommandText = "UPDATE guilds SET public_funds=public_funds-$funds,guild_crystal=guild_crystal-$crystals WHERE guild_id=$guild AND public_funds>=$funds AND guild_crystal>=$crystals";
        command.Parameters.AddWithValue("$funds", funds); command.Parameters.AddWithValue("$crystals", crystals);
        if (command.ExecuteNonQuery() != 1) return new(action == 1 ? 3 : 2, action);
        command.CommandText = """
            INSERT INTO guild_npc_progress(guild_id,npc_id,level,upgrade_target,upgrade_until,contract_until)
            VALUES($guild,$npc,$level,$target,$upgrade,$contract)
            ON CONFLICT(guild_id,npc_id) DO UPDATE SET upgrade_target=excluded.upgrade_target,
                upgrade_until=excluded.upgrade_until,contract_until=excluded.contract_until
            """;
        command.Parameters.AddWithValue("$npc", npcId); command.Parameters.AddWithValue("$level", progress.Level);
        command.Parameters.AddWithValue("$target", action == 1 ? progress.Level + 1 : 0);
        command.Parameters.AddWithValue("$upgrade", action == 1 ? deadline : progress.UpgradeUntil);
        command.Parameters.AddWithValue("$contract", action == 2 ? deadline : progress.ContractUntil);
        // 契约续期必须保留正在进行的升级目标。
        if (action == 2 && progress.UpgradeUntil != 0) command.Parameters["$target"].Value = progress.Level + 1;
        command.ExecuteNonQuery();
        command.CommandText = """
            INSERT INTO guild_npc_receipts(request_id,account,character_id,guild_id,joined_at,npc_id,action,argument,deadline)
            VALUES($request,$account,$id,$guild,$joined,$npc,$action,$argument,$deadline)
            """;
        command.Parameters.AddWithValue("$account", actor.Owner.Account); command.Parameters.AddWithValue("$joined", joined);
        command.Parameters.AddWithValue("$action", action); command.Parameters.AddWithValue("$argument", argument);
        command.Parameters.AddWithValue("$deadline", deadline); command.ExecuteNonQuery();
        ClaimGuildRequest(db, tx, requestId, 4, actor.Owner.Account, actor.CharacterId);
        TouchManagement(db, tx, guildId);
        var result = ReadNpcs(db, tx, actor.Owner.Account, actor.CharacterId, catalog);
        if (!actor.IsCurrent()) return new(1, action);
        tx.Commit(); return new(0, action, result);
    });
}
