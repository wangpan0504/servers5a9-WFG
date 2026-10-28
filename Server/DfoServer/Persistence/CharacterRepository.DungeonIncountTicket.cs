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
        if (usage.DailyExtra >= 1000000 || usage.WeeklyExtra >= 1000000)
            throw new InvalidDataException("追加入场额度超过存储上限");
        var committed = CommitInventoryInTransaction(db, tx, owner, expected, plan.Inventory);
        usage = usage with { DailyExtra = usage.DailyExtra + 1, WeeklyExtra = usage.WeeklyExtra + 1 };
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
        tx.Commit();
        return new DungeonIncountCommit(committed, usage);
    });
}
