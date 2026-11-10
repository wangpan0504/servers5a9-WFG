using DfoServer.Game.Accounts;
using DfoServer.Game.Dungeons;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository : IDungeonIncountTicketStore
{
    public DungeonIncountCommit CommitDungeonIncountTicket(AccountOwners.Lease owner, DungeonIncountTicketPlan plan, DateTimeOffset now) => owner.Execute(() =>
    {
        var expected = plan.Expected;
        if (plan.Period != DungeonIncountPolicy.Period(now, plan.WeeklyResetDay) ||
            plan.AccountQuotas.Any(quota => quota.Period != (quota.ResetDay < 0
                ? DungeonIncountPolicy.Period(now).Day : DungeonIncountPolicy.Period(now, quota.ResetDay).Week)) ||
            expected.Inventory.Find(plan.Source.Position) != plan.Source || plan.Source.IsExpired(now.ToUnixTimeSeconds()) ||
            !ReferenceEquals(plan.Inventory.Before, expected.Inventory) || plan.Inventory.Reason != "action_consumable_source")
            throw new InvalidOperationException("追加券的周期、源或角色快照已变化");
        using var db = database.Open();
        using var write = db.BeginWrite("CharacterRepository.CommitDungeonIncountTicket");
        using var tx = write.Transaction;
        var row = plan.Row;
        var usage = row is null ? default : ReadDungeonIncount(db, tx, expected.Id, owner.Account, row.CountKey, plan.Period);
        if (plan.Reset && (row is null || DungeonIncountPolicy.DailyRemaining(row, usage) > 0))
            throw new DungeonTicketRejectedException("reset-requires-exhausted-count");
        if (!plan.ResetUsedTargets.IsEmpty && !plan.ResetUsedTargets.Any(target =>
            ReadDungeonIncount(db, tx, expected.Id, owner.Account, target.CountKey, plan.Period).DailyUsed > 0))
            throw new DungeonTicketRejectedException("reset-requires-used-count");
        var anton = plan.ResetAntonRewards ? ReadAntonNormalProgress(db, tx, owner.Account, expected.Id) : default;
        if (plan.ResetAntonRewards && (anton.Day != plan.Period.Day || anton.Completed <= 0))
            throw new DungeonTicketRejectedException("reset-requires-anton-reward-progress");
        if (plan.ResetSingleGroup is not null && ResetSingleDungeonBindings(db, tx, owner.Account, plan, now, false) == 0)
            throw new DungeonTicketRejectedException("reset-requires-current-bindings");
        var committed = CommitInventoryInTransaction(db, tx, owner, expected, plan.Inventory);
        if (plan.Reset && row is not null)
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
            reset.Parameters.AddWithValue("$dungeon", row.CountKey);
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
            resetExtra.Parameters.AddWithValue("$dungeon", row.CountKey);
            resetExtra.Parameters.AddWithValue("$day", plan.Period.Day);
            resetExtra.Parameters.AddWithValue("$week", plan.Period.Week);
            resetExtra.ExecuteNonQuery();
        }
        else if (row is not null)
        {
            if (usage.DailyExtra > 1000000 - plan.Amount || usage.WeeklyExtra > 1000000 - plan.Amount)
                throw new InvalidDataException("追加入场额度超过存储上限");
            usage = usage with
            {
                DailyExtra = usage.DailyExtra + plan.Amount,
                WeeklyExtra = row.WeeklyLimit > 0 ? usage.WeeklyExtra + plan.Amount : usage.WeeklyExtra
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
            command.Parameters.AddWithValue("$dungeon", row.CountKey);
            command.Parameters.AddWithValue("$day", plan.Period.Day);
            command.Parameters.AddWithValue("$daily", usage.DailyExtra);
            command.Parameters.AddWithValue("$week", plan.Period.Week);
            command.Parameters.AddWithValue("$weekly", usage.WeeklyExtra);
            command.ExecuteNonQuery();
        }
        foreach (var quota in plan.AccountQuotas)
        {
            int extra = checked(ReadAccountIncountExtra(db, tx, owner.Account, quota.Dungeon, quota.Period) + quota.Amount);
            if (extra > 1000000) throw new InvalidDataException("账号追加额度超过存储上限");
            Execute(db, tx, """
                INSERT INTO account_dungeon_incount_extra(account,dungeon,period,extra) VALUES($account,$dungeon,$period,$extra)
                ON CONFLICT(account,dungeon) DO UPDATE SET period=excluded.period,extra=excluded.extra
                """, ("$account", owner.Account), ("$dungeon", quota.Dungeon), ("$period", quota.Period), ("$extra", extra));
        }
        foreach (var quota in plan.RewardQuotas)
        {
            var before = ReadRewardTicketExtra(db, tx, owner.Account, expected.Id, quota.Kind, quota.Target, plan.Period);
            int daily = checked(before.DailyExtra + quota.Amount), weekly = checked(before.WeeklyExtra + quota.Amount);
            if (daily > 1000000 || weekly > 1000000) throw new InvalidDataException("奖励追加额度超过存储上限");
            Execute(db, tx, """
                INSERT INTO character_dungeon_reward_extra(character_id,account,kind,target,day,daily_extra,week,weekly_extra)
                VALUES($id,$account,$kind,$target,$day,$daily,$week,$weekly)
                ON CONFLICT(character_id,kind,target) DO UPDATE SET day=excluded.day,daily_extra=excluded.daily_extra,
                  week=excluded.week,weekly_extra=excluded.weekly_extra
                """, ("$id", expected.Id), ("$account", owner.Account), ("$kind", quota.Kind), ("$target", quota.Target),
                ("$day", plan.Period.Day), ("$daily", daily), ("$week", plan.Period.Week), ("$weekly", weekly));
        }
        foreach (var target in plan.ResetUsedTargets)
            Execute(db, tx, """
                UPDATE character_dungeon_incount SET daily_used=0
                WHERE character_id=$id AND account=$account AND dungeon=$dungeon AND day=$day
                """, ("$id", expected.Id), ("$account", owner.Account), ("$dungeon", target.CountKey), ("$day", plan.Period.Day));
        if (plan.ResetAntonRewards)
            WriteAntonNormalProgress(db, tx, owner.Account, expected.Id, anton with { Completed = 0 });
        if (plan.ResetSingleGroup is not null)
            ResetSingleDungeonBindings(db, tx, owner.Account, plan, now, true);
        tx.Commit();
        return new DungeonIncountCommit(committed, usage);
    });
}
