using DfoServer.Game.Accounts;
using DfoServer.Game.Guild;
using DfoServer.GameWorld.Guilds;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository : IGuildEconomyStore
{
    static GuildEconomyState? ReadEconomy(SqliteConnection db, SqliteTransaction? tx, string account, ushort id, long now)
    {
        var management = ReadManagement(db, tx, account, id);
        if (management is null) return null;
        using var command = ManagementCommand(db, tx, """
            SELECT g.public_funds,COALESCE(e.member_limit,300) FROM guilds g
            LEFT JOIN guild_economy e ON e.guild_id=g.guild_id WHERE g.guild_id=$guild
            """, ("$guild", management.Guild.Id));
        int funds, limit;
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) return null;
            funds = reader.GetInt32(0); limit = reader.GetInt32(1);
        }
        command.CommandText = "SELECT type,value,expires_unix FROM guild_benefits WHERE guild_id=$guild ORDER BY type";
        var benefits = new List<GuildBenefit>();
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                var benefit = new GuildBenefit(checked((byte)reader.GetInt32(0)), checked((uint)reader.GetInt64(1)), reader.GetInt64(2));
                if (benefit.Active(now)) benefits.Add(benefit);
            }
        return new(management.Guild.Id, funds, limit, management.Revision, benefits.ToArray(), management.Members);
    }
    public GuildEconomyState? ReadGuildEconomy(AccountOwners.Lease owner, ushort characterId, long now) => owner.Execute(() =>
    {
        using var db = database.Open(); using var tx = db.BeginRead();
        return ReadEconomy(db, tx, owner.Account, characterId, now);
    });
    public int ReadGuildMemberLimit(AccountOwners.Lease owner, ushort characterId) => owner.Execute(() =>
    {
        using var db = database.Open();
        using var command = ManagementCommand(db, null, """
            SELECT COALESCE(e.member_limit,300) FROM guild_members m JOIN characters c ON c.id=m.character_id
            LEFT JOIN guild_economy e ON e.guild_id=m.guild_id WHERE c.id=$id AND c.account=$account
            """, ("$id", characterId), ("$account", owner.Account));
        return command.ExecuteScalar() is { } value ? Convert.ToInt32(value) : 300;
    });
    public int ReadGuildKillBonus(ushort characterId, long now)
    {
        using var db = database.Open();
        using var command = ManagementCommand(db, null, """
            SELECT b.value FROM guild_members m JOIN guild_benefits b ON b.guild_id=m.guild_id
            WHERE m.character_id=$id AND b.type=1 AND b.expires_unix>$now
            """, ("$id", characterId), ("$now", now));
        return command.ExecuteScalar() is { } value ? checked(Convert.ToInt32(value)) : 0;
    }
    public GuildMileageEntry[] ReadGuildMileageHistory(AccountOwners.Lease owner, ushort characterId) => owner.Execute(() =>
    {
        using var db = database.Open(); using var tx = db.BeginRead();
        using var command = ManagementCommand(db, tx, """
            SELECT h.created_at_unix,h.character_name,h.kind,h.content_id,h.amount FROM guild_mileage_history h
            JOIN guild_members m ON m.guild_id=h.guild_id JOIN characters c ON c.id=m.character_id
            WHERE c.id=$id AND c.account=$account ORDER BY h.id DESC LIMIT 50
            """, ("$id", characterId), ("$account", owner.Account));
        var rows = new List<GuildMileageEntry>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) rows.Add(new(reader.GetInt64(0), reader.GetString(1), checked((byte)reader.GetInt32(2)),
            checked((uint)reader.GetInt64(3)), reader.GetInt32(4)));
        return rows.ToArray();
    });
    static void AppendGuildMileageHistory(SqliteConnection db, SqliteTransaction tx, uint guildId, ushort id,
        string name, byte kind, uint contentId, int amount, long now)
    {
        using var command = ManagementCommand(db, tx, """
            INSERT INTO guild_mileage_history(guild_id,character_id,character_name,kind,content_id,amount,created_at_unix)
            VALUES($guild,$id,$name,$kind,$content,$amount,$now);
            DELETE FROM guild_mileage_history WHERE guild_id=$guild AND id NOT IN
                (SELECT id FROM guild_mileage_history WHERE guild_id=$guild ORDER BY id DESC LIMIT 50)
            """, ("$guild", guildId), ("$id", id), ("$name", name), ("$kind", kind),
            ("$content", contentId), ("$amount", amount), ("$now", now));
        command.ExecuteNonQuery();
    }
    static bool HasEconomyReceipt(SqliteConnection db, SqliteTransaction tx, string requestId,
        GuildManagementIdentity actor, uint guildId, int operation, uint content, short? slot, out bool matched)
    {
        using var command = ManagementCommand(db, tx, """
            SELECT account,character_id,guild_id,operation,content_id,source_slot FROM guild_economy_receipts WHERE request_id=$key
            """, ("$key", requestId));
        using var reader = command.ExecuteReader();
        matched = false;
        if (!reader.Read()) return false;
        matched = reader.GetString(0) == actor.Owner.Account && reader.GetInt32(1) == actor.CharacterId &&
            reader.GetInt64(2) == guildId && reader.GetInt32(3) == operation &&
            (operation == 2 || reader.GetInt64(4) == content) && (slot is null || reader.GetInt32(5) == slot);
        return true;
    }
    static void SaveEconomyReceipt(SqliteConnection db, SqliteTransaction tx, string requestId,
        GuildManagementIdentity actor, uint guildId, int operation, uint content, long uid, short slot, int amount, long now)
    {
        using var command = ManagementCommand(db, tx, """
            INSERT INTO guild_economy_receipts(request_id,account,character_id,guild_id,operation,content_id,source_uid,source_slot,amount,created_at_unix)
            VALUES($key,$account,$id,$guild,$operation,$content,$uid,$slot,$amount,$now)
            """, ("$key", requestId), ("$account", actor.Owner.Account), ("$id", actor.CharacterId),
            ("$guild", guildId), ("$operation", operation), ("$content", content), ("$uid", uid),
            ("$slot", slot), ("$amount", amount), ("$now", now));
        command.ExecuteNonQuery();
    }
    public GuildEconomyCommit PurchaseGuildContent(GuildManagementIdentity actor, GuildContentDefinition content,
        string requestId, long now, int cargoLimit) => actor.Owner.Execute<GuildEconomyCommit>(() =>
    {
        if (!actor.IsCurrent() || !GuildEconomyService.ValidRequestId(requestId) || now is <= 0 or > int.MaxValue) return new(14);
        using var db = database.Open(); using var write = db.BeginWrite("CharacterRepository.PurchaseGuildContent");
        using var tx = write.Transaction;
        var management = ReadManagement(db, tx, actor.Owner.Account, actor.CharacterId);
        if (!GuildRequestAvailable(db, tx, requestId, 2, actor.Owner.Account, actor.CharacterId)) return new(14);
        if (management is null) return new(34);
        uint guildId = management.Guild.Id;
        if (HasEconomyReceipt(db, tx, requestId, actor, guildId, 1, content.Id, null, out bool matched))
            return matched ? new(0, ReadEconomy(db, tx, actor.Owner.Account, actor.CharacterId, now), true) : new(14);
        if ((management.Permissions & (1u << 8)) == 0) return new(36);
        if (content.Price <= 0 || content.Value <= 0 || content.Id > int.MaxValue) return new(14);
        var state = ReadEconomy(db, tx, actor.Owner.Account, actor.CharacterId, now)!;
        if (state.PublicFunds < content.Price) return new(22);
        using var command = ManagementCommand(db, tx, "SELECT 1", ("$guild", guildId));
        if (content.Type == 4)
        {
            if (content.Days != -1 || content.Mode != 0 || content.Value is not (8 or 16)) return new(14);
            command.CommandText = "INSERT INTO guild_cargo_state(guild_id) VALUES($guild) ON CONFLICT(guild_id) DO NOTHING";
            command.ExecuteNonQuery();
            command.CommandText = "UPDATE guild_cargo_state SET capacity=capacity+$amount,revision=revision+1 WHERE guild_id=$guild AND capacity+$amount<=$limit";
            command.Parameters.AddWithValue("$amount", content.Value); command.Parameters.AddWithValue("$limit", cargoLimit);
            if (command.ExecuteNonQuery() != 1) return new(13);
        }
        else if (content.Type == 6)
        {
            if (content.Days != -1 || content.Mode != 0 || content.Value is not (500 or 700 or 1000)) return new(14);
            if (state.MemberLimit >= content.Value) return new(116);
            int prerequisite = content.Value switch { 500 => 300, 700 => 500, _ => 700 };
            if (state.MemberLimit != prerequisite) return new(105);
            command.CommandText = "INSERT INTO guild_economy(guild_id,member_limit) VALUES($guild,$value) ON CONFLICT(guild_id) DO UPDATE SET member_limit=excluded.member_limit";
            command.Parameters.AddWithValue("$value", content.Value); command.ExecuteNonQuery();
        }
        else if (content.Type is 1 or 3 or 5 or 7)
        {
            uint value = checked((uint)content.Value); long expiry = 0;
            command.CommandText = "SELECT value,expires_unix FROM guild_benefits WHERE guild_id=$guild AND type=$type";
            command.Parameters.AddWithValue("$type", content.Type);
            uint oldValue = 0; long oldExpiry = 0;
            using (var reader = command.ExecuteReader())
                if (reader.Read()) { oldValue = checked((uint)reader.GetInt64(0)); oldExpiry = reader.GetInt64(1); }
            if (content.Type == 5)
            {
                if (content.Value != 169 || content.Days != -1 || content.Mode != 0) return new(14);
                if (oldValue != 0) return new(9);
            }
            else if (content.Type == 7)
            {
                if (content.Days != -1 || content.Mode != 1 || (ulong)oldValue + value > uint.MaxValue) return new(14);
                value = checked(oldValue + value);
            }
            else
            {
                if (content.Value != (content.Type == 1 ? 10 : 167) || content.Days is not (1 or 7 or 30) || content.Mode != 0 ||
                    oldValue != 0 && oldValue != value) return new(14);
                expiry = checked(Math.Max(now, oldExpiry) + content.Days * 86400L);
                if (expiry > int.MaxValue) return new(14);
            }
            command.CommandText = """
                INSERT INTO guild_benefits(guild_id,type,value,expires_unix) VALUES($guild,$type,$value,$expiry)
                ON CONFLICT(guild_id,type) DO UPDATE SET value=excluded.value,expires_unix=excluded.expires_unix
                """;
            command.Parameters.AddWithValue("$value", value); command.Parameters.AddWithValue("$expiry", expiry);
            command.ExecuteNonQuery();
        }
        else return new(14);
        command.CommandText = "UPDATE guilds SET public_funds=public_funds-$price WHERE guild_id=$guild AND public_funds>=$price";
        command.Parameters.AddWithValue("$price", content.Price);
        if (command.ExecuteNonQuery() != 1) return new(22);
        AppendGuildMileageHistory(db, tx, guildId, actor.CharacterId, actor.Name, 5, content.Id, content.Price, now);
        SaveEconomyReceipt(db, tx, requestId, actor, guildId, 1, content.Id, 0, 0, content.Price, now);
        ClaimGuildRequest(db, tx, requestId, 2, actor.Owner.Account, actor.CharacterId);
        TouchManagement(db, tx, guildId);
        var result = ReadEconomy(db, tx, actor.Owner.Account, actor.CharacterId, now);
        if (!actor.IsCurrent()) return new(3);
        tx.Commit(); return new(0, result);
    });
    public GuildEconomyCommit? ReplayGuildMileageGift(GuildManagementIdentity actor, string requestId, short slot, long now) => actor.Owner.Execute<GuildEconomyCommit?>(() =>
    {
        if (!actor.IsCurrent() || !GuildEconomyService.ValidRequestId(requestId)) return new GuildEconomyCommit(14);
        using var db = database.Open(); using var tx = db.BeginRead();
        if (!GuildRequestAvailable(db, tx, requestId, 2, actor.Owner.Account, actor.CharacterId)) return new(14);
        var state = ReadEconomy(db, tx, actor.Owner.Account, actor.CharacterId, now);
        if (state is null) return new(34);
        if (!HasEconomyReceipt(db, tx, requestId, actor, state.GuildId, 2, 0, slot, out bool matched)) return null;
        return matched ? new(0, state, true, Require(db, tx, actor.Owner.Account, actor.Name)) : new(14);
    });
    public GuildEconomyCommit CommitGuildMileageGift(GuildManagementIdentity actor, GuildMileageGiftPlan plan,
        string requestId, long now) => actor.Owner.Execute<GuildEconomyCommit>(() =>
    {
        if (!actor.IsCurrent() || !GuildEconomyService.ValidRequestId(requestId) || now is <= 0 or > int.MaxValue ||
            plan.Expected.Id != actor.CharacterId || plan.Source.Uid <= 0 || plan.Source.IsExpired(now) ||
            plan.Expected.Inventory.Find(plan.Source.Position) != plan.Source || plan.Source.Core.ItemId != plan.Definition.Item.Id ||
            plan.Definition.Amount <= 0 || !ReferenceEquals(plan.Expected.Inventory, plan.Inventory.Before) ||
            plan.Inventory.Reason != "guild_mileage_gift") return new(19);
        using var db = database.Open(); using var write = db.BeginWrite("CharacterRepository.CommitGuildMileageGift");
        using var tx = write.Transaction;
        if (!GuildRequestAvailable(db, tx, requestId, 2, actor.Owner.Account, actor.CharacterId)) return new(14);
        var state = ReadEconomy(db, tx, actor.Owner.Account, actor.CharacterId, now);
        if (state is null) return new(34);
        if (HasEconomyReceipt(db, tx, requestId, actor, state.GuildId, 2, 0, plan.Source.Position.Index, out bool matched))
            return matched ? new(0, state, true, Require(db, tx, actor.Owner.Account, actor.Name)) : new(14);
        if ((long)state.PublicFunds + plan.Definition.Amount > int.MaxValue) return new(22);
        var current = Require(db, tx, actor.Owner.Account, actor.Name);
        if (current.Position != plan.Expected.Position || !plan.Definition.Item.AllowsUse(current.Level, current.Job)) return new(19);
        var committed = CommitInventoryInTransaction(db, tx, actor.Owner, plan.Expected, plan.Inventory);
        using var command = ManagementCommand(db, tx,
            "UPDATE guilds SET public_funds=public_funds+$amount WHERE guild_id=$guild AND public_funds<=$ceiling",
            ("$amount", plan.Definition.Amount), ("$guild", state.GuildId), ("$ceiling", int.MaxValue - plan.Definition.Amount));
        if (command.ExecuteNonQuery() != 1) return new(22);
        AppendGuildMileageHistory(db, tx, state.GuildId, actor.CharacterId, actor.Name, 8, 0, plan.Definition.Amount, now);
        SaveEconomyReceipt(db, tx, requestId, actor, state.GuildId, 2, checked((uint)plan.Source.Core.ItemId), plan.Source.Uid,
            plan.Source.Position.Index, plan.Definition.Amount, now);
        ClaimGuildRequest(db, tx, requestId, 2, actor.Owner.Account, actor.CharacterId);
        TouchManagement(db, tx, state.GuildId);
        state = ReadEconomy(db, tx, actor.Owner.Account, actor.CharacterId, now)!;
        if (!actor.IsCurrent()) return new(3);
        tx.Commit(); return new(0, state, Character: committed, Inventory: plan.Inventory);
    });

    // 调用方复用通关结算事务；非会员也写零回执，事后入会不能追领旧副本。
    internal uint CreditGuildDungeonMileageInTransaction(SqliteConnection db, SqliteTransaction tx, AccountOwners.Lease owner,
        ushort characterId, string eventId, int amount, long now, GuildDungeonMembership expectedMembership)
    {
        if (eventId.Length is < 1 or > 256 || amount <= 0 || now is <= 0 or > int.MaxValue || expectedMembership.CharacterId != characterId)
            throw new InvalidDataException("公会通关积分事件无效");
        using var command = ManagementCommand(db, tx, "SELECT character_id,guild_id FROM guild_economy_events WHERE event_id=$event",
            ("$event", eventId));
        using (var reader = command.ExecuteReader())
            if (reader.Read())
            {
                if (reader.GetInt32(0) != characterId) throw new InvalidDataException("公会积分事件归属不符");
                return checked((uint)reader.GetInt64(1));
            }
        uint guildId = 0; string characterName = ""; int publicFunds = 0;
        using (var membership = ManagementCommand(db, tx, """
            SELECT m.guild_id,c.name,g.public_funds FROM characters c
            JOIN guild_members m ON m.character_id=c.id JOIN guilds g ON g.guild_id=m.guild_id
            WHERE c.id=$id AND c.account=$account AND m.guild_id=$guild AND m.joined_at_unix=$joined
            """, ("$id", characterId), ("$account", owner.Account), ("$guild", expectedMembership.GuildId),
            ("$joined", expectedMembership.JoinedAtUnix)))
        using (var reader = membership.ExecuteReader())
            if (reader.Read())
            {
                guildId = checked((uint)reader.GetInt64(0)); characterName = reader.GetString(1); publicFunds = reader.GetInt32(2);
            }
        if (guildId != 0)
        {
            if ((long)publicFunds + amount > int.MaxValue) throw new InvalidDataException("公会公共积分超限");
            command.CommandText = "UPDATE guilds SET public_funds=public_funds+$amount WHERE guild_id=$guild";
            command.Parameters.AddWithValue("$amount", amount); command.Parameters.AddWithValue("$guild", guildId);
            if (command.ExecuteNonQuery() != 1) throw new InvalidDataException("公会积分归属已变化");
            AppendGuildMileageHistory(db, tx, guildId, characterId, characterName, 3, 0, amount, now);
            TouchManagement(db, tx, guildId);
        }
        command.Parameters.Clear(); command.CommandText = "INSERT INTO guild_economy_events(event_id,character_id,guild_id,amount) VALUES($event,$id,$guild,$amount)";
        command.Parameters.AddWithValue("$event", eventId); command.Parameters.AddWithValue("$id", characterId);
        command.Parameters.AddWithValue("$guild", guildId); command.Parameters.AddWithValue("$amount", guildId == 0 ? 0 : amount);
        command.ExecuteNonQuery(); return guildId;
    }

    // 传送业务拥有同意窗口、全员位置与耐久回执，本方法只参与同一事务扣公共次数。
    internal bool ConsumeGuildTeleportInTransaction(SqliteConnection db, SqliteTransaction tx, AccountOwners.Lease owner,
        ushort characterId, uint guildId, long joinedAtUnix)
    {
        using var command = ManagementCommand(db, tx, """
            UPDATE guild_benefits SET value=value-1 WHERE guild_id=$guild AND type=7 AND value>0
              AND EXISTS(SELECT 1 FROM guild_members m JOIN characters c ON c.id=m.character_id
                WHERE m.guild_id=$guild AND m.character_id=$id AND m.joined_at_unix=$joined AND c.account=$account)
            """, ("$guild", guildId), ("$id", characterId), ("$joined", joinedAtUnix), ("$account", owner.Account));
        if (command.ExecuteNonQuery() != 1) return false;
        TouchManagement(db, tx, guildId);
        return true;
    }
}
