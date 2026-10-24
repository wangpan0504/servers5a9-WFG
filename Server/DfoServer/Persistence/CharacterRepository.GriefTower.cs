using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    public GriefTowerProgress ReadGriefTower(AccountOwners.Lease owner, ushort characterId) => owner.Execute(() =>
    {
        using var db = database.Open();
        using var tx = db.BeginRead();
        return ReadGriefTower(db, tx, owner.Account, characterId);
    });

    static GriefTowerProgress ReadGriefTower(SqliteConnection db, SqliteTransaction tx, string account, ushort characterId)
    {
        using var query = db.CreateCommand();
        query.Transaction = tx;
        query.CommandText = """
            SELECT COALESCE(a.game_day,0),COALESCE(a.used,0),COALESCE(g.game_day,0),COALESCE(g.cleared,0),
              COALESCE(g.layer,1),COALESCE(v.level,1),g.instance_id,COALESCE(g.task_attempt,0)
            FROM characters c LEFT JOIN account_grief_tower a ON a.account=c.account
            LEFT JOIN character_grief_tower g ON g.character_id=c.id AND g.account=c.account
            LEFT JOIN account_adventures v ON v.account=c.account WHERE c.id=$id AND c.account=$account
            """;
        query.Parameters.AddWithValue("$id", characterId);
        query.Parameters.AddWithValue("$account", account);
        using var reader = query.ExecuteReader();
        if (!reader.Read()) throw new InvalidOperationException("悲叹状态不属于当前账号角色");
        return new(new(reader.GetInt32(0),reader.GetInt32(1),reader.GetInt32(2),reader.GetInt32(3)!=0,reader.GetInt32(4)),
            reader.GetInt32(5),reader.IsDBNull(6) ? Guid.Empty : Guid.ParseExact(reader.GetString(6),"N"),reader.GetInt64(7));
    }

    static void CommitGriefEntry(SqliteConnection db, SqliteTransaction tx, AccountOwners.Lease owner,
        Character expected, DungeonEntryCharge charge, long revision)
    {
        if (charge.Grief is not { } admission) return;
        if (charge.Fatigue != 0 || admission.Before != ReadGriefTower(db, tx, owner.Account, expected.Id) ||
            admission.DungeonId <= 0 || admission.Before.State.Layer is < 1 or > 100)
            throw new InvalidOperationException("悲叹准入的次数、楼层或冒险团状态已变化");
        var after = admission.After;
        if (admission.Decision.UsesInvitation != (charge.Items is not null))
            throw new InvalidDataException("悲叹重试门票规划缺失或基础入场重复收费");
        if (charge.Items is { } items)
        {
            long Count(Game.Inventory.Models.InventorySnapshot inventory) => inventory.Items
                .Where(item => item.Core.ItemId == GriefTowerEntryDecision.InvitationItem)
                .Sum(item => (long)item.Core.Count);
            if (Count(items.Before) - Count(items.After) != 1)
                throw new InvalidDataException("悲叹重试必须恰好消费一张邀请函");
        }
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$id", expected.Id);
        command.Parameters.AddWithValue("$day", after.CharacterDay);
        command.Parameters.AddWithValue("$used", after.AccountUsed);
        command.Parameters.AddWithValue("$layer", after.Layer);
        command.Parameters.AddWithValue("$instance", charge.Identity.InstanceId.ToString("N"));
        command.Parameters.AddWithValue("$attempt", charge.Identity.TaskAttempt);
        command.Parameters.AddWithValue("$dungeon", admission.DungeonId);
        command.Parameters.AddWithValue("$ticket", admission.Decision.UsesInvitation ? 1 : 0);
        command.Parameters.AddWithValue("$limit", admission.Decision.AccountLimit);
        command.Parameters.AddWithValue("$revision", revision);
        command.CommandText = """
            INSERT INTO account_grief_tower(account,game_day,used) VALUES($account,$day,$used)
            ON CONFLICT(account) DO UPDATE SET game_day=excluded.game_day,used=excluded.used;
            INSERT INTO character_grief_tower(character_id,account,game_day,cleared,layer,instance_id,task_attempt)
            VALUES($id,$account,$day,0,$layer,$instance,$attempt)
            ON CONFLICT(character_id) DO UPDATE SET game_day=excluded.game_day,cleared=0,layer=excluded.layer,
              instance_id=excluded.instance_id,task_attempt=excluded.task_attempt;
            INSERT INTO character_grief_tower_entries(character_id,account,instance_id,task_attempt,game_day,layer,
              dungeon_id,invitation,account_used,account_limit,revision)
            VALUES($id,$account,$instance,$attempt,$day,$layer,$dungeon,$ticket,$used,$limit,$revision);
            """;
        command.ExecuteNonQuery();
    }
}
