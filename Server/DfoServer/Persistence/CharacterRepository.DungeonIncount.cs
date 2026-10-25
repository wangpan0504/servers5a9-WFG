using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Dungeons;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    static DungeonIncountUsage ReadDungeonIncount(SqliteConnection db, SqliteTransaction? tx, ushort id, string account, int dungeon, DungeonIncountPeriod now)
    {
        using var query = db.CreateCommand();
        query.Transaction = tx;
        query.CommandText = "SELECT day,daily_used,week,weekly_used FROM character_dungeon_incount WHERE character_id=$id AND account=$account AND dungeon=$dungeon";
        query.Parameters.AddWithValue("$id", id);
        query.Parameters.AddWithValue("$account", account);
        query.Parameters.AddWithValue("$dungeon", dungeon);
        using var reader = query.ExecuteReader();
        // 跨日/跨周即视为重置；旧行在下一次入场时覆盖。
        return reader.Read()
            ? DungeonIncountPolicy.Current(new(reader.GetInt32(1), reader.GetInt32(3)), new(reader.GetInt32(0), reader.GetInt32(2)), now)
            : default;
    }

    // 账号次数：存储周期与当前周期不同即视为 0。
    static int ReadAccountIncount(SqliteConnection db, SqliteTransaction? tx, string account, int dungeon, int period)
    {
        using var query = db.CreateCommand();
        query.Transaction = tx;
        query.CommandText = "SELECT period,used FROM account_dungeon_incount WHERE account=$account AND dungeon=$dungeon";
        query.Parameters.AddWithValue("$account", account);
        query.Parameters.AddWithValue("$dungeon", dungeon);
        using var reader = query.ExecuteReader();
        return reader.Read() && reader.GetInt32(0) == period ? reader.GetInt32(1) : 0;
    }

    public DungeonIncountUsage LoadDungeonIncount(AccountOwners.Lease owner, ushort characterId, int dungeon, DungeonIncountPeriod now) => owner.Execute(() =>
    {
        using var db = database.Open();
        return ReadDungeonIncount(db, null, characterId, owner.Account, dungeon, now);
    });

    public int LoadAccountDungeonIncount(AccountOwners.Lease owner, int dungeon, int period) => owner.Execute(() =>
    {
        using var db = database.Open();
        return ReadAccountIncount(db, null, owner.Account, dungeon, period);
    });

    // 入场扣次与入场材料同一事务：check 时角色次数或任一 Check 账号次数已满返回 null，不扣材料；材料计划失效则整笔回滚。
    // increment=false 只核对不记次（通关扣次副本的入场）；通关时以 check=false、increment=true 补记。
    public DungeonIncountCommit? CommitDungeonIncountEntry(AccountOwners.Lease owner, Character expected, int dungeon, DungeonIncountPeriod now,
        DungeonIncountRow? row, InventoryPlan? consumption, ImmutableArray<AccountIncountCharge> account = default, bool increment = true, bool check = true) =>
        owner.Execute(() =>
    {
        using var db = database.Open();
        using var write = db.BeginWrite("CharacterRepository.CommitDungeonIncountEntry");
        using var tx = write.Transaction;
        var result = CommitDungeonIncountInTransaction(db, tx, owner, expected, dungeon, now, row, consumption, account, increment, check);
        if (result is null) return null;
        tx.Commit();
        return result;
    });

    DungeonIncountCommit? CommitDungeonIncountInTransaction(SqliteConnection db, SqliteTransaction tx,
        AccountOwners.Lease owner, Character expected, int dungeon, DungeonIncountPeriod now,
        DungeonIncountRow? row, InventoryPlan? consumption, ImmutableArray<AccountIncountCharge> account, bool increment, bool check)
    {
        var charges = account.IsDefault ? [] : account;
        var usage = ReadDungeonIncount(db, tx, expected.Id, owner.Account, dungeon, now);
        if (check && row is not null && !DungeonIncountPolicy.CanEnter(row, usage)) return null;
        var used = charges.Select(charge => ReadAccountIncount(db, tx, owner.Account, charge.Dungeon, charge.Period)).ToArray();
        if (check && charges.Where((charge, index) => charge.Check && used[index] >= charge.Limit).Any()) return null;
        var character = consumption is null ? expected : CommitInventoryInTransaction(db, tx, owner, expected, consumption);
        if (increment && row is not null)
        {
            usage = new(checked(usage.DailyUsed + 1), checked(usage.WeeklyUsed + 1));
            using var command = db.CreateCommand();
            command.Transaction = tx;
            command.CommandText = """
                INSERT INTO character_dungeon_incount(character_id,account,dungeon,day,daily_used,week,weekly_used)
                VALUES($id,$account,$dungeon,$day,$daily,$week,$weekly)
                ON CONFLICT(character_id,dungeon) DO UPDATE SET account=excluded.account,day=excluded.day,
                  daily_used=excluded.daily_used,week=excluded.week,weekly_used=excluded.weekly_used;
                """;
            command.Parameters.AddWithValue("$id", expected.Id);
            command.Parameters.AddWithValue("$account", owner.Account);
            command.Parameters.AddWithValue("$dungeon", dungeon);
            command.Parameters.AddWithValue("$day", now.Day);
            command.Parameters.AddWithValue("$daily", usage.DailyUsed);
            command.Parameters.AddWithValue("$week", now.Week);
            command.Parameters.AddWithValue("$weekly", usage.WeeklyUsed);
            command.ExecuteNonQuery();
        }
        if (increment)
            for (int index = 0; index < charges.Length; index++)
            {
                using var command = db.CreateCommand();
                command.Transaction = tx;
                command.CommandText = """
                    INSERT INTO account_dungeon_incount(account,dungeon,period,used) VALUES($account,$dungeon,$period,$used)
                    ON CONFLICT(account,dungeon) DO UPDATE SET period=excluded.period,used=excluded.used;
                    """;
                command.Parameters.AddWithValue("$account", owner.Account);
                command.Parameters.AddWithValue("$dungeon", charges[index].Dungeon);
                command.Parameters.AddWithValue("$period", charges[index].Period);
                command.Parameters.AddWithValue("$used", Math.Min(checked(used[index] + 1), 1000000));
                command.ExecuteNonQuery();
            }
        return new DungeonIncountCommit(character, usage);
    }
}
