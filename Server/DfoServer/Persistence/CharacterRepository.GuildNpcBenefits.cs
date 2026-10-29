using System.Text.Json;
using DfoServer.Game.Characters;
using DfoServer.Game.Guild;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Guilds;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository : IGuildNpcBenefitStore
{
    static GuildNpcBenefitState? ReadNpcBenefits(SqliteConnection db, SqliteTransaction? tx,
        string account, ushort characterId, long now)
    {
        var management = ReadManagement(db, tx, account, characterId);
        if (management is null) return null;
        using var command = ManagementCommand(db, tx, """
            SELECT benefit_id,remaining,deadline,0 FROM guild_npc_common_benefits
            WHERE guild_id=$guild AND deadline>$now ORDER BY benefit_id
            """, ("$guild", management.Guild.Id), ("$now", now), ("$id", characterId));
        GuildNpcBenefitProgress[] Read()
        {
            var rows = new List<GuildNpcBenefitProgress>();
            using var reader = command.ExecuteReader();
            while (reader.Read()) rows.Add(new(checked((uint)reader.GetInt64(0)), reader.GetInt32(1),
                reader.GetInt64(2), reader.GetInt32(3) != 0));
            return rows.ToArray();
        }
        var common = Read();
        command.CommandText = """
            SELECT b.benefit_id,b.remaining,b.deadline,b.selected
            FROM character_guild_npc_benefits b JOIN guild_members m
              ON m.character_id=b.character_id AND m.guild_id=b.guild_id AND m.joined_at_unix=b.joined_at
            WHERE b.character_id=$id AND b.guild_id=$guild AND (b.deadline=0 OR b.deadline>$now)
            ORDER BY b.benefit_id
            """;
        var individual = Read();
        // 个人委托独立保存；退会和解散不销毁已付款的产物，只有当前会籍投影倒计时。
        command.CommandText = """
            SELECT c.benefit_id,0,c.deadline,0 FROM character_guild_npc_item_crafts c
            JOIN guild_members m ON m.character_id=c.character_id AND m.guild_id=c.guild_id AND m.joined_at_unix=c.joined_at
            WHERE c.character_id=$id AND c.guild_id=$guild AND c.state=0 ORDER BY c.benefit_id
            """;
        individual = [.. individual, .. Read()];
        command.CommandText = "SELECT npc_id,level,upgrade_until,contract_until FROM guild_npc_progress WHERE guild_id=$guild";
        var npcs = new List<GuildNpcProgress>();
        using (var reader = command.ExecuteReader())
            while (reader.Read()) npcs.Add(new(checked((uint)reader.GetInt64(0)), reader.GetInt32(1), reader.GetInt64(2), reader.GetInt64(3)));
        return new(management, common, individual) { Npcs = npcs.ToArray() };
    }

    public GuildNpcBenefitState? ReadGuildNpcBenefits(GuildManagementIdentity actor, long now) => actor.Owner.Execute(() =>
    {
        if (!actor.IsCurrent()) return null;
        using var db = database.Open(); using var tx = db.BeginRead();
        return ReadNpcBenefits(db, tx, actor.Owner.Account, actor.CharacterId, now);
    });

    public GuildNpcBenefitCommit PurchaseGuildNpcBenefit(GuildManagementIdentity actor, Character expected,
        GuildNpcCatalog npcs, GuildNpcBenefitDefinition definition, string requestId, long now,
        Func<Character, InventoryPlan?> prepare) => actor.Owner.Execute<GuildNpcBenefitCommit>(() =>
    {
        if (!actor.IsCurrent() || expected.Id != actor.CharacterId || !GuildEconomyService.ValidRequestId(requestId) ||
            now is <= 0 or > int.MaxValue || npcs.Find(definition.NpcId) is not { } npc) return new(14);
        using var db = database.Open(); using var write = db.BeginWrite("GuildNpcBenefit.Purchase");
        using var tx = write.Transaction;
        var state = ReadNpcs(db, tx, actor.Owner.Account, actor.CharacterId, npcs);
        if (state is null || !GuildRequestAvailable(db, tx, requestId, 4, actor.Owner.Account, actor.CharacterId)) return new(14);
        uint guildId = state.Management.Guild.Id;
        using var command = ManagementCommand(db, tx,
            "SELECT joined_at_unix FROM guild_members WHERE character_id=$id AND guild_id=$guild",
            ("$id", actor.CharacterId), ("$guild", guildId));
        long joined = Convert.ToInt64(command.ExecuteScalar());
        var replay = ReplayNpcBenefit(db, tx, actor, guildId, joined, definition.NpcId, 3, definition.Id, requestId, now);
        if (replay is not null) return replay;
        var progress = state.Npcs.Single(row => row.NpcId == definition.NpcId);
        if (progress.Level < definition.Level || npc.Contract && progress.ContractUntil <= now ||
            definition.Common && (state.Management.Permissions & GuildRoleRules.NpcManagementPermission) == 0) return new(14);
        bool training = GuildNpcBenefitService.Training(definition);
        if (!(training || GuildNpcBenefitService.Timed(definition)) ||
            training && (definition.Common || definition.Parameters is not [> 0, > 0, > 0]) ||
            !training && definition.DurationSeconds <= 0) return new(14);
        var benefits = ReadNpcBenefits(db, tx, actor.Owner.Account, actor.CharacterId, now)!;
        if (training && (benefits.Individual.Any(row => row.BenefitId == definition.Id) ||
            definition.Parameters[1] > 1 && !benefits.Individual.Any(row => row.BenefitId == definition.Id - 1))) return new(14);
        long deadline = training ? 0 : now + definition.DurationSeconds;
        if (deadline > int.MaxValue) return new(14);
        int remaining = definition.Effect == "emblem compound buff" && definition.Parameters is [> 0, > 0]
            ? definition.Parameters[1] : 0;
        Character? committed = null; InventoryPlan? inventory = null;
        if (definition.Common)
        {
            if (definition.FirstCurrency != "public funds" || definition.SecondCurrency != "none" || definition.SecondAmount != 0)
                return new(14);
            command.CommandText = "UPDATE guilds SET public_funds=public_funds-$cost WHERE guild_id=$guild AND public_funds>=$cost";
            command.Parameters.AddWithValue("$cost", definition.FirstAmount);
            if (command.ExecuteNonQuery() != 1) return new(17);
        }
        else
        {
            var current = Require(db, tx, actor.Owner.Account, actor.Name);
            if (current.Revision != expected.Revision || current.Id != expected.Id || current.Position != expected.Position)
                return new(14);
            inventory = prepare(current);
            if (inventory is null) return new(17);
            committed = CommitInventoryInTransaction(db, tx, actor.Owner, current, inventory);
        }
        command.Parameters.AddWithValue("$benefit", definition.Id);
        command.Parameters.AddWithValue("$effect", definition.Effect);
        command.Parameters.AddWithValue("$deadline", deadline);
        command.Parameters.AddWithValue("$remaining", remaining);
        command.Parameters.AddWithValue("$joined", joined);
        if (definition.Common)
            command.CommandText = """
                INSERT INTO guild_npc_common_benefits(guild_id,effect,benefit_id,remaining,deadline)
                VALUES($guild,$effect,$benefit,$remaining,$deadline)
                ON CONFLICT(guild_id,effect) DO UPDATE SET benefit_id=excluded.benefit_id,
                  remaining=excluded.remaining,deadline=excluded.deadline
                """;
        else
        {
            if (!training)
            {
                command.CommandText = """
                    DELETE FROM character_guild_npc_benefits
                    WHERE character_id=$id AND guild_id=$guild AND joined_at=$joined AND effect=$effect
                    """;
                command.ExecuteNonQuery();
            }
            command.CommandText = """
                INSERT INTO character_guild_npc_benefits(character_id,guild_id,joined_at,effect,benefit_id,remaining,deadline)
                VALUES($id,$guild,$joined,$effect,$benefit,$remaining,$deadline)
                """;
        }
        command.ExecuteNonQuery();
        SaveNpcBenefitReceipt(db, tx, actor, guildId, joined, definition.NpcId, 3, definition.Id, requestId, deadline);
        TouchManagement(db, tx, guildId);
        var result = ReadNpcBenefits(db, tx, actor.Owner.Account, actor.CharacterId, now);
        if (!actor.IsCurrent()) return new(14);
        tx.Commit(); return new(0, result, Character: committed, Inventory: inventory);
    });

    static GuildNpcBenefitCommit? ReplayNpcBenefit(SqliteConnection db, SqliteTransaction tx,
        GuildManagementIdentity actor, uint guild, long joined, uint npc, uint action, uint argument,
        string requestId, long now)
    {
        using var query = ManagementCommand(db, tx, """
            SELECT account,character_id,guild_id,joined_at,npc_id,action,argument
            FROM guild_npc_receipts WHERE request_id=$request
            """, ("$request", requestId));
        bool matches;
        using (var reader = query.ExecuteReader())
        {
            if (!reader.Read()) return null;
            matches = reader.GetString(0) == actor.Owner.Account && reader.GetInt32(1) == actor.CharacterId &&
                reader.GetInt64(2) == guild && reader.GetInt64(3) == joined && reader.GetInt64(4) == npc &&
                reader.GetInt64(5) == action && reader.GetInt64(6) == argument;
        }
        return matches ? new(0, ReadNpcBenefits(db, tx, actor.Owner.Account, actor.CharacterId, now), true) : new(14);
    }

    static void SaveNpcBenefitReceipt(SqliteConnection db, SqliteTransaction tx, GuildManagementIdentity actor,
        uint guild, long joined, uint npc, uint action, uint argument, string requestId, long deadline)
    {
        using var command = ManagementCommand(db, tx, """
            INSERT INTO guild_npc_receipts(request_id,account,character_id,guild_id,joined_at,npc_id,action,argument,deadline)
            VALUES($request,$account,$id,$guild,$joined,$npc,$action,$argument,$deadline)
            """, ("$request", requestId), ("$account", actor.Owner.Account), ("$id", actor.CharacterId),
            ("$guild", guild), ("$joined", joined), ("$npc", npc), ("$action", action),
            ("$argument", argument), ("$deadline", deadline));
        command.ExecuteNonQuery();
        ClaimGuildRequest(db, tx, requestId, 4, actor.Owner.Account, actor.CharacterId);
    }

    public GuildNpcBenefitCommit SelectGuildNpcTraining(GuildManagementIdentity actor, GuildNpcBenefitCatalog catalog,
        uint[] benefits, string requestId, long now) => actor.Owner.Execute<GuildNpcBenefitCommit>(() =>
    {
        if (!actor.IsCurrent() || !GuildEconomyService.ValidRequestId(requestId) || benefits.Length > 4 ||
            benefits.Distinct().Count() != benefits.Length || now is <= 0 or > int.MaxValue) return new(14);
        var definitions = benefits.Select(catalog.Find).ToArray();
        if (definitions.Any(row => row is null || !GuildNpcBenefitService.Training(row)) ||
            definitions.Select(row => row!.Effect).Distinct().Count() != benefits.Length) return new(14);
        using var db = database.Open(); using var write = db.BeginWrite("GuildNpcBenefit.SelectTraining");
        using var tx = write.Transaction;
        if (!GuildRequestAvailable(db, tx, requestId, 4, actor.Owner.Account, actor.CharacterId)) return new(14);
        var state = ReadNpcBenefits(db, tx, actor.Owner.Account, actor.CharacterId, now);
        if (state is null || benefits.Any(id => !state.Individual.Any(row => row.BenefitId == id))) return new(14);
        using var command = ManagementCommand(db, tx,
            "SELECT joined_at_unix FROM guild_members WHERE character_id=$id AND guild_id=$guild",
            ("$id", actor.CharacterId), ("$guild", state.Management.Guild.Id));
        long joined = Convert.ToInt64(command.ExecuteScalar());
        string selection = JsonSerializer.Serialize(benefits.Order().ToArray());
        var replay = ReplayNpcBenefit(db, tx, actor, state.Management.Guild.Id, joined, 0, 6, 0, requestId, now);
        if (replay is not null)
        {
            command.CommandText = "SELECT selection FROM guild_npc_training_receipts WHERE request_id=$request";
            command.Parameters.AddWithValue("$request", requestId);
            return replay.Success && command.ExecuteScalar() as string == selection ? replay : new(14);
        }
        if (!state.Npcs.Any(row => row.NpcId == 400001064 && row.ContractUntil > now)) return new(14);
        command.CommandText = """
            UPDATE character_guild_npc_benefits SET selected=0
            WHERE character_id=$id AND guild_id=$guild AND joined_at=$joined
              AND effect LIKE 'guild trainer % raid buff'
            """;
        command.Parameters.AddWithValue("$joined", joined); command.ExecuteNonQuery();
        command.Parameters.AddWithValue("$benefit", 0);
        foreach (uint id in benefits)
        {
            command.CommandText = """
                UPDATE character_guild_npc_benefits SET selected=1
                WHERE character_id=$id AND guild_id=$guild AND joined_at=$joined AND benefit_id=$benefit
                """;
            command.Parameters["$benefit"].Value = id;
            if (command.ExecuteNonQuery() != 1) throw new InvalidDataException("公会训练选择缺少已解锁能力");
        }
        SaveNpcBenefitReceipt(db, tx, actor, state.Management.Guild.Id, joined, 0, 6, 0, requestId, 0);
        command.CommandText = "INSERT INTO guild_npc_training_receipts(request_id,selection) VALUES($request,$selection)";
        command.Parameters.AddWithValue("$request", requestId); command.Parameters.AddWithValue("$selection", selection);
        command.ExecuteNonQuery(); TouchManagement(db, tx, state.Management.Guild.Id);
        var result = ReadNpcBenefits(db, tx, actor.Owner.Account, actor.CharacterId, now);
        if (!actor.IsCurrent()) return new(14);
        tx.Commit(); return new(0, result);
    });
}
