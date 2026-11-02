using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Guild;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Guilds;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository : IGuildHongbaoStore
{
    public GuildHongbaoList? ReadGuildHongbao(AccountOwners.Lease owner, ushort character, long now) => owner.Execute(() =>
    {
        using var db = database.Open();
        using var tx = db.BeginRead();
        using var command = ManagementCommand(db, tx, """
            SELECT m.guild_id FROM guild_members m JOIN characters c ON c.id=m.character_id
            WHERE c.id=$id AND c.account=$account
            """, ("$id", character), ("$account", owner.Account), ("$now", now));
        if (command.ExecuteScalar() is not long guild) return null;
        command.Parameters.AddWithValue("$guild", guild);
        command.CommandText = "SELECT count(*) FROM guild_members WHERE guild_id=$guild";
        ushort members = checked((ushort)Convert.ToInt64(command.ExecuteScalar()));
        uint spent = ReadGuildHongbaoSpent(db, tx, character, GuildAttendanceDay.Start(now));
        command.Parameters.AddWithValue("$day", GuildAttendanceDay.Start(now));
        command.CommandText = "SELECT count(*) FROM guild_hongbao_claims WHERE account=$account AND day_start_unix=$day";
        uint taken = checked((uint)Convert.ToInt64(command.ExecuteScalar()));
        command.CommandText = """
            SELECT record_id,item_id,sender_name,portions-taken,expires_at FROM guild_hongbao_records
            WHERE guild_id=$guild AND expires_at>$now ORDER BY record_id DESC LIMIT 500
            """;
        var rows = new List<GuildHongbaoRow>();
        using (var row = command.ExecuteReader())
            while (row.Read()) rows.Add(new(checked((uint)row.GetInt64(0)), row.GetInt32(1), row.GetString(2),
                checked((byte)row.GetInt32(3)), row.GetInt64(4)));
        return new GuildHongbaoList(members, spent, taken, rows.ToArray());
    });

    public GuildHongbaoClaim[]? ReadGuildHongbaoHistory(AccountOwners.Lease owner, ushort character, uint id) => owner.Execute(() =>
    {
        using var db = database.Open();
        using var tx = db.BeginRead();
        using var command = ManagementCommand(db, tx, """
            SELECT 1 FROM guild_hongbao_records r JOIN guild_members m ON m.guild_id=r.guild_id
            JOIN characters c ON c.id=m.character_id WHERE r.record_id=$record AND c.id=$id AND c.account=$account
            """, ("$record", id), ("$id", character), ("$account", owner.Account));
        if (command.ExecuteScalar() is null) return null;
        command.CommandText = "SELECT level,name,amount FROM guild_hongbao_claims WHERE record_id=$record ORDER BY ordinal";
        var rows = new List<GuildHongbaoClaim>();
        using var row = command.ExecuteReader();
        while (row.Read()) rows.Add(new(checked((byte)row.GetInt32(0)), row.GetString(1), checked((uint)row.GetInt64(2))));
        return rows.ToArray();
    });

    public GuildHongbaoCommit IssueGuildHongbao(GuildManagementIdentity actor, Character expected,
        GuildHongbaoDefinition definition, byte count, long now, Func<Character, InventoryPlan?> prepare) => actor.Owner.Execute(() =>
    {
        if (!actor.IsCurrent() || actor.CharacterId != expected.Id || count is < 5 or > 30 ||
            definition.Cera < count || now is <= 0 or > int.MaxValue - GuildHongbaoService.LifetimeSeconds)
            return new GuildHongbaoCommit(7);
        using var db = database.Open();
        using var write = db.BeginWrite("CharacterRepository.IssueGuildHongbao");
        using var tx = write.Transaction;
        if (!ServerEventRepository.Read(db, tx, DfoServer.Game.Events.ServerEventService.GuildHongbaoId).IsActive(DateTimeOffset.FromUnixTimeSeconds(now)))
            return new GuildHongbaoCommit(7);
        var state = ReadManagement(db, tx, actor.Owner.Account, actor.CharacterId);
        if (state is null) return new(34);
        using var command = ManagementCommand(db, tx, """
            SELECT count(*) FROM guild_hongbao_records WHERE guild_id=$guild AND expires_at>$now
            """, ("$guild", state.Guild.Id), ("$now", now));
        if (Convert.ToInt64(command.ExecuteScalar()) >= GuildHongbaoService.MaximumRows) return new(7);
        var plan = prepare(expected);
        if (plan is null) return new(7);
        RequireHongbaoPlan(expected, plan, "guild_hongbao_issue");
        var committed = CommitInventoryInTransaction(db, tx, actor.Owner, expected, plan);
        command.CommandText = """
            INSERT INTO guild_hongbao_records(guild_id,sender_account,sender_name,item_id,total,remaining,portions,taken,created_at,expires_at)
            VALUES($guild,$account,$name,$item,$total,$total,$count,0,$now,$expires) RETURNING record_id
            """;
        command.Parameters.AddWithValue("$account", actor.Owner.Account);
        command.Parameters.AddWithValue("$name", expected.Name);
        command.Parameters.AddWithValue("$item", definition.ItemId);
        command.Parameters.AddWithValue("$total", definition.Cera);
        command.Parameters.AddWithValue("$count", count);
        command.Parameters.AddWithValue("$expires", now + GuildHongbaoService.LifetimeSeconds);
        uint id = checked((uint)(long)command.ExecuteScalar()!);
        if (!actor.IsCurrent()) return new(3);
        tx.Commit();
        return new(0, id, Character: committed, Inventory: plan);
    });

    public GuildHongbaoCommit TakeGuildHongbao(GuildManagementIdentity actor, Character expected, uint id,
        int dailyLimit, long now, Func<Character, int, InventoryPlan?> prepare) => actor.Owner.Execute(() =>
    {
        if (!actor.IsCurrent() || actor.CharacterId != expected.Id || dailyLimit <= 0 || now is <= 0 or > int.MaxValue)
            return new GuildHongbaoCommit(7);
        using var db = database.Open();
        using var write = db.BeginWrite("CharacterRepository.TakeGuildHongbao");
        using var tx = write.Transaction;
        using var command = ManagementCommand(db, tx, """
            SELECT r.total,r.portions,r.taken FROM guild_hongbao_records r
            JOIN guild_members m ON m.guild_id=r.guild_id JOIN characters c ON c.id=m.character_id
            WHERE r.record_id=$record AND c.id=$id AND c.account=$account AND r.expires_at>$now AND r.refunded=0
            """, ("$record", id), ("$id", expected.Id), ("$account", actor.Owner.Account), ("$now", now));
        int total, portions, ordinal;
        using (var row = command.ExecuteReader())
        {
            if (!row.Read()) return new(7);
            total = row.GetInt32(0); portions = row.GetInt32(1); ordinal = row.GetInt32(2);
        }
        if (ordinal >= portions) return new(7);
        command.CommandText = "SELECT 1 FROM guild_hongbao_claims WHERE record_id=$record AND account=$account";
        if (command.ExecuteScalar() is not null) return new(7);
        command.CommandText = "SELECT count(*) FROM guild_hongbao_claims WHERE account=$account AND day_start_unix=$day";
        command.Parameters.AddWithValue("$day", GuildAttendanceDay.Start(now));
        int taken = checked((int)(long)command.ExecuteScalar()!);
        if (taken >= dailyLimit) return new(7);
        int amount = GuildHongbaoService.Share(total, portions, ordinal);
        var plan = prepare(expected, amount);
        if (plan is null) return new(7);
        RequireHongbaoPlan(expected, plan, "guild_hongbao_take");
        command.CommandText = """
            UPDATE guild_hongbao_records SET remaining=remaining-$amount,taken=taken+1
            WHERE record_id=$record AND taken=$ordinal AND remaining>=$amount AND expires_at>$now AND refunded=0
            """;
        command.Parameters.AddWithValue("$amount", amount);
        command.Parameters.AddWithValue("$ordinal", ordinal);
        if (command.ExecuteNonQuery() != 1) return new(7);
        var committed = CommitInventoryInTransaction(db, tx, actor.Owner, expected, plan);
        command.CommandText = """
            INSERT INTO guild_hongbao_claims(record_id,account,character_id,name,level,amount,ordinal,day_start_unix,created_at)
            VALUES($record,$account,$id,$name,$level,$amount,$ordinal,$day,$now)
            """;
        command.Parameters.AddWithValue("$name", expected.Name);
        command.Parameters.AddWithValue("$level", expected.Level);
        command.ExecuteNonQuery();
        if (!actor.IsCurrent()) return new(3);
        tx.Commit();
        return new(0, id, amount, taken + 1, committed, plan);
    });

    public GuildHongbaoCommit RefundGuildHongbao(GuildManagementIdentity actor, Character expected, long now,
        Func<Character, int, InventoryPlan?> prepare) => actor.Owner.Execute(() =>
    {
        if (!actor.IsCurrent() || actor.CharacterId != expected.Id) return new GuildHongbaoCommit(3);
        using var db = database.Open();
        // 剩余属于发行账号；退会、解散、删除发行角色不清账，任一本账号角色登录均可恢复。
        using var command = ManagementCommand(db, null, """
            SELECT COALESCE(SUM(remaining),0) FROM guild_hongbao_records
            WHERE sender_account=$account AND expires_at<=$now AND refunded=0
            """, ("$account", actor.Owner.Account), ("$now", now));
        // 没有待退余额时保持纯查询；有余额才申请writer，并复验领取竞争后的真实余额。
        if ((long)command.ExecuteScalar()! == 0) return new(0);
        using var write = db.BeginWrite("CharacterRepository.RefundGuildHongbao");
        using var tx = write.Transaction;
        command.Transaction = tx;
        long sum = (long)command.ExecuteScalar()!;
        if (sum == 0) return new(0);
        if (sum > int.MaxValue) return new(7);
        var plan = prepare(expected, (int)sum);
        if (plan is null) return new(7);
        RequireHongbaoPlan(expected, plan, "guild_hongbao_refund");
        var committed = CommitInventoryInTransaction(db, tx, actor.Owner, expected, plan);
        command.CommandText = """
            UPDATE guild_hongbao_records SET refunded=1,remaining=0
            WHERE sender_account=$account AND expires_at<=$now AND refunded=0
            """;
        command.ExecuteNonQuery();
        if (!actor.IsCurrent()) return new(3);
        tx.Commit();
        return new(0, Amount: (int)sum, Character: committed, Inventory: plan);
    });

    static void RequireHongbaoPlan(Character expected, InventoryPlan plan, string reason)
    {
        if (!ReferenceEquals(plan.Before, expected.Inventory) || plan.Reason != reason || plan.OverflowItems.Length != 0)
            throw new InvalidDataException("公会红包库存规划身份或用途无效");
    }
}
