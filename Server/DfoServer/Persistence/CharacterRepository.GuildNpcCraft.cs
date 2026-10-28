using DfoServer.Game.Guild;
using DfoServer.GameWorld.Guilds;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal static class GuildNpcCraftSchema
{
    public const string Sql = """
        CREATE TABLE IF NOT EXISTS guild_npc_crafts(
          guild_id INTEGER PRIMARY KEY REFERENCES guilds(guild_id) ON DELETE CASCADE,
          npc_id INTEGER NOT NULL CHECK(npc_id=400001060),
          benefit_id INTEGER NOT NULL CHECK(benefit_id>0),
          public_funds INTEGER NOT NULL CHECK(public_funds>0),
          reward_count INTEGER NOT NULL CHECK(reward_count>0),
          deadline INTEGER NOT NULL CHECK(deadline BETWEEN 1 AND 2147483647),
          craft_id TEXT NOT NULL UNIQUE CHECK(length(craft_id)=64)
        );
        CREATE INDEX IF NOT EXISTS guild_npc_crafts_due ON guild_npc_crafts(deadline,guild_id);
        CREATE TABLE IF NOT EXISTS guild_npc_craft_receipts(
          request_id TEXT PRIMARY KEY CHECK(length(request_id)=64),
          account TEXT NOT NULL,character_id INTEGER NOT NULL,guild_id INTEGER NOT NULL,
          joined_at INTEGER NOT NULL,action INTEGER NOT NULL CHECK(action IN (4,5)),
          benefit_id INTEGER NOT NULL,craft_id TEXT NOT NULL CHECK(length(craft_id)=64),
          deadline INTEGER NOT NULL CHECK(deadline BETWEEN 1 AND 2147483647)
        );
        CREATE TABLE IF NOT EXISTS guild_npc_craft_completions(
          craft_id TEXT PRIMARY KEY CHECK(length(craft_id)=64),guild_id INTEGER NOT NULL,
          benefit_id INTEGER NOT NULL,reward_count INTEGER NOT NULL CHECK(reward_count>0),
          before_crystal INTEGER NOT NULL CHECK(before_crystal>=0),
          after_crystal INTEGER NOT NULL CHECK(after_crystal=before_crystal+reward_count),
          completed_at INTEGER NOT NULL CHECK(completed_at BETWEEN 1 AND 2147483647)
        );
        """;
}

internal sealed partial class CharacterRepository
{
    static GuildNpcCraftProgress[] ReadNpcCrafts(SqliteConnection db, SqliteTransaction? tx, uint guildId)
    {
        using var query = ManagementCommand(db, tx,
            "SELECT benefit_id,deadline FROM guild_npc_crafts WHERE guild_id=$guild", ("$guild", guildId));
        using var reader = query.ExecuteReader();
        return reader.Read() ? [new(checked((uint)reader.GetInt64(0)), reader.GetInt64(1))] : [];
    }

    public GuildNpcCommit StartGuildNpcCraft(GuildManagementIdentity actor, GuildNpcCatalog catalog,
        GuildNpcCraftCatalog crafts, uint benefitId, string requestId, long now) =>
        ChangeGuildNpcCraft(actor, catalog, crafts, 4, benefitId, requestId, now);

    public GuildNpcCommit CancelGuildNpcCraft(GuildManagementIdentity actor, GuildNpcCatalog catalog,
        GuildNpcCraftCatalog crafts, uint benefitId, string requestId, long now) =>
        ChangeGuildNpcCraft(actor, catalog, crafts, 5, benefitId, requestId, now);

    GuildNpcCommit ChangeGuildNpcCraft(GuildManagementIdentity actor, GuildNpcCatalog catalog,
        GuildNpcCraftCatalog crafts, uint action, uint benefitId, string requestId, long now) => actor.Owner.Execute<GuildNpcCommit>(() =>
    {
        if (!actor.IsCurrent() || !GuildEconomyService.ValidRequestId(requestId) || now is <= 0 or > int.MaxValue ||
            crafts.Find(benefitId) is not { } definition) return new(14, action);
        using var db = database.Open(); using var write = db.BeginWrite("CharacterRepository.StartGuildNpcCraft");
        using var tx = write.Transaction;
        var state = ReadNpcs(db, tx, actor.Owner.Account, actor.CharacterId, catalog);
        if (state is null || !GuildRequestAvailable(db, tx, requestId, 6, actor.Owner.Account, actor.CharacterId)) return new(14, action);
        uint guildId = state.Management.Guild.Id;
        using var command = ManagementCommand(db, tx,
            "SELECT joined_at_unix FROM guild_members WHERE guild_id=$guild AND character_id=$id",
            ("$guild", guildId), ("$id", actor.CharacterId));
        long joined = Convert.ToInt64(command.ExecuteScalar());
        command.CommandText = """
            SELECT account,character_id,guild_id,joined_at,action,benefit_id
            FROM guild_npc_craft_receipts WHERE request_id=$request
            """;
        command.Parameters.AddWithValue("$request", requestId);
        using (var reader = command.ExecuteReader())
            if (reader.Read())
            {
                bool matches = reader.GetString(0) == actor.Owner.Account && reader.GetInt32(1) == actor.CharacterId &&
                    reader.GetInt64(2) == guildId && reader.GetInt64(3) == joined && reader.GetInt64(4) == action &&
                    reader.GetInt64(5) == benefitId;
                return matches ? new(0, action, state, true) : new(14, action);
            }
        if ((state.Management.Permissions & GuildRoleRules.NpcManagementPermission) == 0)
            return new(14, action);
        long deadline;
        string craftId = requestId;
        command.Parameters.AddWithValue("$benefit", benefitId);
        if (action == 5)
        {
            var progress = state.Crafts.SingleOrDefault(row => row.BenefitId == benefitId);
            if (progress is null) return new(14, action);
            deadline = progress.Deadline;
            command.CommandText = "SELECT craft_id FROM guild_npc_crafts WHERE guild_id=$guild AND benefit_id=$benefit";
            craftId = (string)command.ExecuteScalar()!;
            // 本版取消确认明确不返还费用；仅清除当前生产，不触碰公款和已完成结晶。
            command.CommandText = "DELETE FROM guild_npc_crafts WHERE guild_id=$guild AND benefit_id=$benefit";
            if (command.ExecuteNonQuery() != 1) return new(14, action);
        }
        else
        {
            var guardian = state.Npcs.Single(row => row.NpcId == GuildNpcCatalog.Guardian);
            if (state.Crafts.Length != 0 || guardian.Level != definition.Level || guardian.UpgradeUntil != 0)
                return new(14, action);
            deadline = now + definition.DurationSeconds;
            if (deadline > int.MaxValue) return new(14, action);
            command.CommandText = """
            UPDATE guilds SET public_funds=public_funds-$funds
            WHERE guild_id=$guild AND public_funds>=$funds AND guild_crystal<=$room
            """;
            command.Parameters.AddWithValue("$funds", definition.PublicFunds);
            command.Parameters.AddWithValue("$room", crafts.CrystalMaximum - definition.Amount);
            if (command.ExecuteNonQuery() != 1) return new(17, action);
            command.CommandText = """
            INSERT INTO guild_npc_crafts(guild_id,npc_id,benefit_id,public_funds,reward_count,deadline,craft_id)
            VALUES($guild,$npc,$benefit,$funds,$amount,$deadline,$request)
            """;
            command.Parameters.AddWithValue("$npc", GuildNpcCatalog.Guardian);
            command.Parameters.AddWithValue("$amount", definition.Amount); command.Parameters.AddWithValue("$deadline", deadline);
            command.ExecuteNonQuery();
        }
        if (action == 5) command.Parameters.AddWithValue("$deadline", deadline);
        command.Parameters.AddWithValue("$craft", craftId); command.Parameters.AddWithValue("$action", action);
        command.CommandText = """
            INSERT INTO guild_npc_craft_receipts(request_id,account,character_id,guild_id,joined_at,action,benefit_id,craft_id,deadline)
            VALUES($request,$account,$id,$guild,$joined,$action,$benefit,$craft,$deadline)
            """;
        command.Parameters.AddWithValue("$account", actor.Owner.Account); command.Parameters.AddWithValue("$joined", joined);
        command.ExecuteNonQuery(); ClaimGuildRequest(db, tx, requestId, 6, actor.Owner.Account, actor.CharacterId);
        TouchManagement(db, tx, guildId);
        var result = ReadNpcs(db, tx, actor.Owner.Account, actor.CharacterId, catalog);
        if (!actor.IsCurrent()) return new(14, action);
        tx.Commit(); return new(0, action, result);
    });

    public GuildNpcState[] CompleteGuildNpcCrafts(GuildNpcCatalog catalog, GuildNpcCraftCatalog crafts, long now)
    {
        if (now is <= 0 or > int.MaxValue) return [];
        using var db = database.Open();
        var due = new List<(uint Guild, uint Benefit, int Amount, long Deadline, string Id)>();
        using (var query = ManagementCommand(db, null, """
            SELECT c.guild_id,c.benefit_id,c.reward_count,c.deadline,c.craft_id
            FROM guild_npc_crafts c JOIN guilds g ON g.guild_id=c.guild_id
            WHERE c.deadline<=$now AND g.guild_crystal<=$cap-c.reward_count
            ORDER BY c.deadline,c.guild_id LIMIT 64
            """, ("$now", now), ("$cap", crafts.CrystalMaximum)))
        using (var reader = query.ExecuteReader())
            while (reader.Read()) due.Add((checked((uint)reader.GetInt64(0)), checked((uint)reader.GetInt64(1)),
                reader.GetInt32(2), reader.GetInt64(3), reader.GetString(4)));
        if (due.Count == 0) return [];
        using var write = db.BeginWrite("CharacterRepository.CompleteGuildNpcCrafts"); using var tx = write.Transaction;
        var states = new List<GuildNpcState>();
        foreach (var row in due)
        {
            if (crafts.Find(row.Benefit) is null || row.Amount <= 0 || row.Amount > crafts.CrystalMaximum)
                throw new InvalidDataException("持久守护石生产目标与资源不符");
            using var command = ManagementCommand(db, tx, """
                SELECT reward_count FROM guild_npc_crafts
                WHERE guild_id=$guild AND craft_id=$craft AND benefit_id=$benefit AND deadline=$deadline AND deadline<=$now
                """, ("$guild", row.Guild), ("$craft", row.Id), ("$benefit", row.Benefit),
                ("$deadline", row.Deadline), ("$now", now));
            if (command.ExecuteScalar() is not long amount || amount != row.Amount) continue;
            command.CommandText = "SELECT guild_crystal FROM guilds WHERE guild_id=$guild";
            int before = Convert.ToInt32(command.ExecuteScalar());
            if (before > crafts.CrystalMaximum - row.Amount) continue;
            command.CommandText = """
                UPDATE guilds SET guild_crystal=guild_crystal+$amount WHERE guild_id=$guild AND guild_crystal=$before
                """;
            command.Parameters.AddWithValue("$amount", row.Amount); command.Parameters.AddWithValue("$before", before);
            if (command.ExecuteNonQuery() != 1) throw new InvalidDataException("守护石生产结晶入账失败");
            command.CommandText = """
                INSERT INTO guild_npc_craft_completions(craft_id,guild_id,benefit_id,reward_count,before_crystal,after_crystal,completed_at)
                VALUES($craft,$guild,$benefit,$amount,$before,$before+$amount,$now)
                """;
            command.ExecuteNonQuery();
            command.CommandText = "DELETE FROM guild_npc_crafts WHERE guild_id=$guild AND craft_id=$craft";
            if (command.ExecuteNonQuery() != 1) throw new InvalidDataException("守护石生产状态清除失败");
            TouchManagement(db, tx, row.Guild);
            command.CommandText = """
                SELECT c.account,c.id FROM guilds g JOIN characters c ON c.id=g.master_character_id WHERE g.guild_id=$guild
                """;
            string account; ushort id;
            using (var reader = command.ExecuteReader())
            {
                if (!reader.Read()) throw new InvalidDataException("守护石生产缺少现任会长");
                account = reader.GetString(0); id = checked((ushort)reader.GetInt32(1));
            }
            states.Add(ReadNpcs(db, tx, account, id, catalog) ?? throw new InvalidDataException("守护石生产缺少公会会籍"));
        }
        tx.Commit(); return states.ToArray();
    }
}
