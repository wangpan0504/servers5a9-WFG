using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Guild;
using DfoServer.Game.Inventory.Models;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository : IGuildCargoStore
{
    public GuildCargoRead ReadGuildCargo(GuildCargoActor actor, bool initialize) => actor.Owner.Execute(() =>
    {
        if (!actor.IsCurrent()) return new GuildCargoRead(8);
        using var db = database.Open();
        // 只在明确打开仓库时补建缺失状态；后续广播不凭空创建仓库。
        using var write = initialize ? db.BeginWrite("CharacterRepository.ReadGuildCargo") : null;
        using var read = initialize ? null : db.BeginRead();
        var transaction = write?.Transaction ?? read!;
        var membership = ReadManagement(db, transaction, actor.Owner.Account, actor.Character.Id);
        if (membership is null) return new(100);
        if ((membership.Permissions & GuildCargoRules.Permission) == 0) return new(36);
        if (initialize)
        {
            using var command = ManagementCommand(db, transaction,
                "INSERT INTO guild_cargo_state(guild_id) VALUES($guild) ON CONFLICT(guild_id) DO NOTHING", ("$guild", membership.Guild.Id));
            command.ExecuteNonQuery();
        }
        var view = ReadCargo(db, transaction, membership.Guild.Id);
        if (view is null) return new(196);
        if (!actor.IsCurrent()) return new(8);
        if (initialize) transaction.Commit();
        return new(0, view);
    });

    public GuildCargoCommit CommitGuildCargo(GuildCargoActor actor, GuildCargoPlan plan, string requestId, long now) =>
        actor.Owner.Execute(() =>
        {
            if (!actor.IsCurrent() || requestId.Length != 64 || !requestId.All(Uri.IsHexDigit)) return new GuildCargoCommit(8);
            using var db = database.Open();
            using var write = db.BeginWrite("CharacterRepository.CommitGuildCargo");
            using var transaction = write.Transaction;
            if (!GuildRequestAvailable(db, transaction, requestId, 1, actor.Owner.Account, actor.Character.Id)) return new(198);
            var membership = ReadManagement(db, transaction, actor.Owner.Account, actor.Character.Id);
            if (membership is null) return new(100);
            if ((membership.Permissions & GuildCargoRules.Permission) == 0) return new(36);
            var view = ReadCargo(db, transaction, membership.Guild.Id);
            if (view is null) return new(196);
            // 在转移incoming所有权前读完整角色；中间态不允许被通用库存读取器当作无位置实例读取。
            var current = Require(db, transaction, actor.Owner.Account, actor.Character.Name);
            using (var receipt = ManagementCommand(db, transaction, """
                SELECT fingerprint,account,character_id,guild_id,destination_slot FROM guild_cargo_receipts WHERE request_id=$request
                """, ("$request", requestId)))
            using (var row = receipt.ExecuteReader())
            {
                if (row.Read())
                {
                    bool same = row.GetString(0) == plan.Request.Fingerprint && row.GetString(1) == actor.Owner.Account &&
                        row.GetInt32(2) == actor.Character.Id && row.GetInt64(3) == membership.Guild.Id;
                    return same ? new(0, view, current, Replayed: true, Destination: checked((short)row.GetInt32(4))) : new(198, view, current);
                }
            }
            if (plan.Error != 0 || plan.Before is null || plan.Inventory is null || plan.Core is null || plan.After.IsDefault)
                return new(plan.Error == 0 ? (ushort)198 : plan.Error, view, current);
            if (plan.Before.GuildId != view.GuildId || plan.Before.Revision != view.Revision || plan.Before.Capacity != view.Capacity ||
                !plan.Before.Items.SequenceEqual(view.Items)) return new(198, view, current);
            if (current.Revision != actor.Character.Revision || !SameInventory(current.Inventory, plan.Inventory) ||
                plan.Inventory.CharacterPrecondition?.Matches(current, now) == false) return new(198, view, current);
            if (plan.Request.Move)
            {
                var authoritative = GuildCargoRules.Move(current, view, plan.Request, 56);
                if (authoritative.Error != 0 || !authoritative.After.SequenceEqual(plan.After)) return new(198, view, current);
            }

            var changed = view.Items.Where(old => !plan.After.Contains(old)).ToArray();
            foreach (var old in changed)
            {
                using var command = ManagementCommand(db, transaction,
                    "DELETE FROM guild_cargo_slots WHERE guild_id=$guild AND slot_index=$slot AND item_uid=$uid",
                    ("$guild", view.GuildId), ("$slot", old.Position.Index), ("$uid", old.Uid));
                if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("公会仓库位置已变化");
            }
            if (plan.Request.Move)
            {
                // 249只交换完整实例的槽位；中央UID、核心、角色库存及角色revision均保持原值。
                foreach (var item in plan.After.Where(item => !view.Items.Contains(item)))
                {
                    using var slot = ManagementCommand(db, transaction,
                        "INSERT INTO guild_cargo_slots(guild_id,slot_index,item_uid) VALUES($guild,$slot,$uid)",
                        ("$guild", view.GuildId), ("$slot", item.Position.Index), ("$uid", item.Uid));
                    slot.ExecuteNonQuery();
                }
                using (var version = ManagementCommand(db, transaction,
                    "UPDATE guild_cargo_state SET revision=revision+1 WHERE guild_id=$guild AND revision=$revision",
                    ("$guild", view.GuildId), ("$revision", view.Revision)))
                    if (version.ExecuteNonQuery() != 1) throw new InvalidOperationException("公会仓库版本已变化");
                using (var receipt = ManagementCommand(db, transaction, """
                    INSERT INTO guild_cargo_receipts(request_id,fingerprint,account,character_id,guild_id,revision,destination_slot,created_at_unix)
                    VALUES($request,$fingerprint,$account,$character,$guild,$revision,$destination,$time)
                    """, ("$request", requestId), ("$fingerprint", plan.Request.Fingerprint), ("$account", actor.Owner.Account),
                    ("$character", actor.Character.Id), ("$guild", view.GuildId), ("$revision", view.Revision + 1),
                    ("$destination", plan.Destination), ("$time", now))) receipt.ExecuteNonQuery();
                ClaimGuildRequest(db, transaction, requestId, 1, actor.Owner.Account, actor.Character.Id);
                var moved = ReadCargo(db, transaction, view.GuildId)!;
                if (!actor.IsCurrent()) return new(198);
                transaction.Commit();
                return new(0, moved, current, Destination: plan.Destination, Members: membership.Members);
            }
            var incoming = plan.Inventory.After.Items.Where(item => item.Uid != 0 && plan.Inventory.BeforeIndex.FindUid(item.Uid) is null)
                .ToDictionary(item => item.Uid);
            foreach (var item in incoming.Values)
            {
                using var command = ManagementCommand(db, transaction, """
                    UPDATE character_inventory_items SET account=$account,character_id=$character,updated_at=CURRENT_TIMESTAMP
                    WHERE item_uid=$uid AND account=$oldAccount AND character_id IS NULL
                    """, ("$account", actor.Owner.Account), ("$character", actor.Character.Id), ("$uid", item.Uid), ("$oldAccount", CargoAccount(view.GuildId)));
                if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("公会仓库取出失去所有权");
            }
            var retained = plan.After.Where(item => item.Uid != 0).Select(item => item.Uid).ToHashSet();
            var committed = CommitInventoryInTransaction(db, transaction, actor.Owner, current, actor.Character,
                plan.Inventory, retained, incoming);
            foreach (var item in plan.After.Where(item => !view.Items.Contains(item)))
            {
                long uid = item.Uid;
                using var command = ManagementCommand(db, transaction, "SELECT 1", ("$guild", view.GuildId),
                    ("$slot", item.Position.Index), ("$account", CargoAccount(view.GuildId)), ("$core", JsonSerializer.Serialize(item.Core)));
                if (uid == 0)
                {
                    command.CommandText = "INSERT INTO character_inventory_items(account,character_id,item_core) VALUES($account,NULL,$core) RETURNING item_uid";
                    uid = Convert.ToInt64(command.ExecuteScalar());
                }
                else
                {
                    command.Parameters.AddWithValue("$uid", uid);
                    command.Parameters.AddWithValue("$actorAccount", actor.Owner.Account);
                    command.Parameters.AddWithValue("$character", actor.Character.Id);
                    command.CommandText = """
                        UPDATE character_inventory_items SET account=$account,character_id=NULL,item_core=$core,updated_at=CURRENT_TIMESTAMP
                        WHERE item_uid=$uid AND ((account=$account AND character_id IS NULL) OR (account=$actorAccount AND character_id=$character))
                        """;
                    if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("公会仓库存入失去所有权");
                }
                using var slot = ManagementCommand(db, transaction,
                    "INSERT INTO guild_cargo_slots(guild_id,slot_index,item_uid) VALUES($guild,$slot,$uid)",
                    ("$guild", view.GuildId), ("$slot", item.Position.Index), ("$uid", uid));
                slot.ExecuteNonQuery();
            }
            var kept = retained.Concat(committed.Inventory.Items.Select(item => item.Uid)).ToHashSet();
            foreach (var old in changed.Where(item => !kept.Contains(item.Uid)))
            {
                using var command = ManagementCommand(db, transaction,
                    "DELETE FROM character_inventory_items WHERE account=$account AND character_id IS NULL AND item_uid=$uid",
                    ("$account", CargoAccount(view.GuildId)), ("$uid", old.Uid));
                if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("公会仓库合并失去所有权");
            }
            using (var version = ManagementCommand(db, transaction,
                "UPDATE guild_cargo_state SET revision=revision+1 WHERE guild_id=$guild AND revision=$revision",
                ("$guild", view.GuildId), ("$revision", view.Revision)))
                if (version.ExecuteNonQuery() != 1) throw new InvalidOperationException("公会仓库版本已变化");
            using (var command = ManagementCommand(db, transaction, """
                INSERT INTO guild_cargo_history(guild_id,created_at_unix,kind,name,item_core,moved_count)
                VALUES($guild,$time,$kind,$name,$core,$count);
                DELETE FROM guild_cargo_history WHERE guild_id=$guild AND id NOT IN
                    (SELECT id FROM guild_cargo_history WHERE guild_id=$guild ORDER BY id DESC LIMIT 50);
                INSERT INTO guild_cargo_receipts(request_id,fingerprint,account,character_id,guild_id,revision,destination_slot,created_at_unix)
                VALUES($request,$fingerprint,$account,$character,$guild,$revision+1,$destination,$time);
                """, ("$guild", view.GuildId), ("$revision", view.Revision), ("$time", now), ("$kind", plan.Request.Deposit ? 1 : 2),
                ("$name", current.Name), ("$core", JsonSerializer.Serialize(plan.Core)), ("$count", plan.Request.Count),
                ("$request", requestId), ("$fingerprint", plan.Request.Fingerprint), ("$account", actor.Owner.Account),
                ("$character", actor.Character.Id), ("$destination", plan.Destination))) command.ExecuteNonQuery();
            var after = ReadCargo(db, transaction, view.GuildId)!;
            ClaimGuildRequest(db, transaction, requestId, 1, actor.Owner.Account, actor.Character.Id);
            if (!actor.IsCurrent()) return new(198);
            transaction.Commit();
            return new(0, after, committed, plan.Inventory, Destination: plan.Destination, Members: membership.Members);
        });

    static string CargoAccount(uint guildId) => "guild:" + guildId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    static GuildCargoView? ReadCargo(SqliteConnection db, SqliteTransaction transaction, uint guildId)
    {
        using var command = ManagementCommand(db, transaction,
            "SELECT revision,capacity FROM guild_cargo_state WHERE guild_id=$guild", ("$guild", guildId));
        long revision;
        int capacity;
        using (var row = command.ExecuteReader())
        {
            if (!row.Read()) return null;
            revision = row.GetInt64(0);
            capacity = row.GetInt32(1);
        }
        command.CommandText = """
            SELECT i.item_uid,0,s.slot_index,i.item_core,a.detail,p.detail FROM guild_cargo_slots s
            JOIN character_inventory_items i ON i.item_uid=s.item_uid AND i.account=s.account AND i.character_id IS NULL
            LEFT JOIN inventory_avatar_details a ON a.item_uid=i.item_uid
            LEFT JOIN inventory_creature_details p ON p.item_uid=i.item_uid
            WHERE s.guild_id=$guild ORDER BY s.slot_index
            """;
        var items = ImmutableArray.CreateBuilder<InventoryItem>();
        using (var row = command.ExecuteReader()) while (row.Read()) items.Add(ReadInventoryItem(row));
        command.CommandText = "SELECT id,created_at_unix,kind,name,item_core,moved_count FROM guild_cargo_history WHERE guild_id=$guild ORDER BY id DESC LIMIT 50";
        var history = ImmutableArray.CreateBuilder<GuildCargoHistory>();
        using (var row = command.ExecuteReader()) while (row.Read()) history.Add(new(row.GetInt64(0), row.GetInt64(1),
            checked((byte)row.GetInt32(2)), row.GetString(3), JsonSerializer.Deserialize<ItemCore>(row.GetString(4))!, row.GetInt32(5)));
        return new(guildId, revision, capacity, items.ToImmutable(), history.ToImmutable());
    }
}
