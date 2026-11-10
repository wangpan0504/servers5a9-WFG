using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Accounts;
using DfoServer.Game.Dungeons;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    public DungeonQuotaResetResult ResetDungeonLimits(AccountOwners.Lease owner) => owner.Execute(() =>
    {
        var now = DateTimeOffset.UtcNow;
        using var db = database.Open();
        using var write = db.BeginWrite("CharacterRepository.ResetDungeonLimits");
        using var tx = write.Transaction;
        // 先归档奖励资格，再清入场计数，避免再战魔界的当前周期先被入场刷新迁走。
        var rewards = ResetRewardLimitsInTransaction(db, tx, owner, now);
        var entries = ResetDungeonEntriesInTransaction(db, tx, owner, now);
        int crack = Execute(db, tx, """
            UPDATE character_crack_daily SET clear_count=0,daily_reward=0
            WHERE account=$account AND day=$day AND (clear_count<>0 OR daily_reward<>0)
            """, ("$account", owner.Account), ("$day", AssaultModeEntryPolicy.DayKey(now.ToLocalTime())));
        int earliest = DungeonIncountPolicy.CurrentAccountPeriods(now).Min();
        int today = DungeonIncountPolicy.Period(now).Day;
        // 两种绑定表只保存当前名额，不是奖励回执；已购买的追加次数保持原样。
        int single = Execute(db, tx, """
            DELETE FROM account_single_dungeon_bindings
            WHERE account=$account AND period BETWEEN $first AND $last
            """, ("$account", owner.Account), ("$first", earliest), ("$last", today));
        int evildom = Execute(db, tx, """
            DELETE FROM account_evildom_war_guides
            WHERE account=$account AND week BETWEEN $first AND $last
            """, ("$account", owner.Account), ("$first", earliest), ("$last", today));
        int dazzlement = Execute(db, tx, "DELETE FROM account_dazzlement_tower_bindings WHERE account=$account",
            ("$account", owner.Account));
        int endless = Execute(db, tx, "UPDATE account_endless_adventure_days SET earned=0 WHERE account=$account AND day=$day AND earned<>0",
            ("$account", owner.Account), ("$day", ShopPurchaseCount.Today(now)));
        tx.Commit();
        return new DungeonQuotaResetResult(entries, rewards, crack, single, evildom, dazzlement, endless);
    });
}
