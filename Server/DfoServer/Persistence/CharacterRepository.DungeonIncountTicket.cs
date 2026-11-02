using DfoServer.Game.Accounts;
using DfoServer.Game.Dungeons;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository : IDungeonIncountTicketStore
{
    public DungeonIncountCommit CommitDungeonIncountTicket(AccountOwners.Lease owner, DungeonIncountTicketPlan plan, DateTimeOffset now) => owner.Execute(() =>
    {
        var expected = plan.Expected;
        if (plan.Period != DungeonIncountPolicy.Period(now, plan.WeeklyResetDay) ||
            expected.Inventory.Find(plan.Source.Position) != plan.Source || plan.Source.IsExpired(now.ToUnixTimeSeconds()) ||
            !ReferenceEquals(plan.Inventory.Before, expected.Inventory) || plan.Inventory.Reason != "action_consumable_source")
            throw new InvalidOperationException("追加券的周期、源或角色快照已变化");
        using var db = database.Open();
        using var write = db.BeginWrite("CharacterRepository.CommitDungeonIncountTicket");
        using var tx = write.Transaction;
        var usage = ReadDungeonIncount(db, tx, expected.Id, owner.Account, plan.Row.CountKey, plan.Period);
        if (plan.Reset && DungeonIncountPolicy.DailyRemaining(plan.Row, usage) > 0)
            throw new InvalidOperationException("reset-requires-exhausted-count");
        var committed = CommitInventoryInTransaction(db, tx, owner, expected, plan.Inventory);
        if (plan.Reset)
        {
            usage = usage with { DailyUsed = 0, WeeklyUsed = 0, DailyExtra = 0, WeeklyExtra = 0 };
            using var reset = db.CreateCommand();
            reset.Transaction = tx;
            reset.CommandText = """
                UPDATE character_dungeon_incount
                SET day=$day,daily_used=0,week=$week,weekly_used=0
                WHERE character_id=$id AND account=$account AND dungeon=$dungeon;
                """;
            reset.Parameters.AddWithValue("$id", expected.Id);
            reset.Parameters.AddWithValue("$account", owner.Account);
            reset.Parameters.AddWithValue("$dungeon", plan.Row.CountKey);
            reset.Parameters.AddWithValue("$day", plan.Period.Day);
            reset.Parameters.AddWithValue("$week", plan.Period.Week);
            reset.ExecuteNonQuery();
            using var resetExtra = db.CreateCommand();
            resetExtra.Transaction = tx;
            resetExtra.CommandText = """
                UPDATE character_dungeon_incount_extra
                SET day=$day,daily_extra=0,week=$week,weekly_extra=0
                WHERE character_id=$id AND account=$account AND dungeon=$dungeon;
                """;
            resetExtra.Parameters.AddWithValue("$id", expected.Id);
            resetExtra.Parameters.AddWithValue("$account", owner.Account);
            resetExtra.Parameters.AddWithValue("$dungeon", plan.Row.CountKey);
            resetExtra.Parameters.AddWithValue("$day", plan.Period.Day);
            resetExtra.Parameters.AddWithValue("$week", plan.Period.Week);
            resetExtra.ExecuteNonQuery();
        }
        else
        {
            if (usage.DailyExtra > 1000000 - plan.Amount || usage.WeeklyExtra > 1000000 - plan.Amount)
                throw new InvalidDataException("追加入场额度超过存储上限");
            usage = usage with
            {
                DailyExtra = usage.DailyExtra + plan.Amount,
                WeeklyExtra = plan.Row.WeeklyLimit > 0 ? usage.WeeklyExtra + plan.Amount : usage.WeeklyExtra
            };
            using var command = db.CreateCommand();
            command.Transaction = tx;
            command.CommandText = """
                INSERT INTO character_dungeon_incount_extra(character_id,account,dungeon,day,daily_extra,week,weekly_extra)
                VALUES($id,$account,$dungeon,$day,$daily,$week,$weekly)
                ON CONFLICT(character_id,dungeon) DO UPDATE SET account=excluded.account,day=excluded.day,
                  daily_extra=excluded.daily_extra,week=excluded.week,weekly_extra=excluded.weekly_extra;
                """;
            command.Parameters.AddWithValue("$id", expected.Id);
            command.Parameters.AddWithValue("$account", owner.Account);
            command.Parameters.AddWithValue("$dungeon", plan.Row.CountKey);
            command.Parameters.AddWithValue("$day", plan.Period.Day);
            command.Parameters.AddWithValue("$daily", usage.DailyExtra);
            command.Parameters.AddWithValue("$week", plan.Period.Week);
            command.Parameters.AddWithValue("$weekly", usage.WeeklyExtra);
            command.ExecuteNonQuery();
        }
        tx.Commit();
        return new DungeonIncountCommit(committed, usage);
    });
}
