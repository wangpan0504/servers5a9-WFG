using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Quests;
using DfoServer.Game.Raids;
using DfoServer.Game.TagEvildom;
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
        // 跨日/跨周即视为重置；旧行在下一次入场时覆盖。
        DungeonIncountUsage usage;
        using (var reader = query.ExecuteReader())
            usage = reader.Read()
                ? DungeonIncountPolicy.Current(new(reader.GetInt32(1), reader.GetInt32(3)), new(reader.GetInt32(0), reader.GetInt32(2)), now)
                : default;
        query.CommandText = "SELECT day,daily_extra,week,weekly_extra FROM character_dungeon_incount_extra WHERE character_id=$id AND account=$account AND dungeon=$dungeon";
        using var extra = query.ExecuteReader();
        return extra.Read() ? usage with
        {
            DailyExtra = extra.GetInt32(0) == now.Day ? extra.GetInt32(1) : 0,
            WeeklyExtra = extra.GetInt32(2) == now.Week ? extra.GetInt32(3) : 0
        } : usage;
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

    public ImmutableArray<StoredDungeonIncount> LoadDungeonIncounts(AccountOwners.Lease owner, ushort characterId) => owner.Execute(() =>
    {
        using var db = database.Open();
        using var query = db.CreateCommand();
        query.CommandText = "SELECT dungeon,day,daily_used,week,weekly_used FROM character_dungeon_incount WHERE character_id=$id AND account=$account";
        query.Parameters.AddWithValue("$id", characterId);
        query.Parameters.AddWithValue("$account", owner.Account);
        using var reader = query.ExecuteReader();
        var rows = ImmutableArray.CreateBuilder<StoredDungeonIncount>();
        while (reader.Read()) rows.Add(new(reader.GetInt32(0), new(reader.GetInt32(1), reader.GetInt32(3)),
            new(reader.GetInt32(2), reader.GetInt32(4))));
        return rows.ToImmutable();
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
        DungeonIncountRow? row, InventoryPlan? consumption, ImmutableArray<AccountIncountCharge> account,
        bool increment, bool check = true)
    {
        var charges = account.IsDefault ? [] : account;
        var usage = ReadDungeonIncount(db, tx, expected.Id, owner.Account, dungeon, now);
        if (check && row is not null && !DungeonIncountPolicy.CanEnter(row, usage)) return null;
        var used = charges.Select(charge => ReadAccountIncount(db, tx, owner.Account, charge.Dungeon, charge.Period)).ToArray();
        if (check && charges.Where((charge, index) => charge.Check && used[index] >= checked(charge.Limit +
            ReadAccountIncountExtra(db, tx, owner.Account, charge.Dungeon, charge.Period))).Any()) return null;
        var character = consumption is null ? expected : CommitInventoryInTransaction(db, tx, owner, expected, consumption);
        if (increment && row is not null)
        {
            usage = usage with { DailyUsed = checked(usage.DailyUsed + 1), WeeklyUsed = checked(usage.WeeklyUsed + 1) };
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

    // 账号级刷新地下城入场次数：覆盖所有按次限进的副本与所有重置节奏。
    // 冒险团面板删当前周；角色日/周次数的 daily_used 与 weekly_used、账号次数（日键或任意周起点的周键）、
    // 强袭日次数只清当前周期。退役玩法的历史表不参与重置。
    // 魔界塔（ dazzle）只清本周已绑角色的当日入场；悲叹塔清账号日额度与角色当日通关锁，楼层进度保留；
    // 祭坛（血/究极）与绝望的当日入场回执把 service_day 挪到 1，绝望的当日通关日一并清零，楼层进度保留；
    // 四类团本推进入场额度世代并保留历史；再战魔界本周通关回执挪到1/1周并丢掉跨日未完成尝试。
    // 次元裂缝的日通关数与卢克奖励回执只决定奖励进度、不限制入场，不在刷新范围；
    // character_dungeon_incount_extra 是买来的额外次数（额度），清零等于没收玩家已买的次数，也不在范围。
    // 在线内存不缓存已用次数，下次入场重新读库。
    public DungeonEntryResetResult ResetDungeonEntries(AccountOwners.Lease owner) => owner.Execute(() =>
    {
        var now = DateTimeOffset.UtcNow;
        int panelWeek = WeeklyDungeonPolicy.WeekKey(now);
        var period = DungeonIncountPolicy.Period(now);
        int calendarDay = ShopPurchaseCount.Today(now);
        int assaultDay = AssaultModeEntryPolicy.DayKey(DateTimeOffset.Now);
        int dazzlementWeek = DazzlementTowerAdmission.At(now).Week;
        int griefDay = QuestCycle.Day(now);
        var tagPeriod = TagEvildomPeriod.At(now);
        using var db = database.Open();
        using var write = db.BeginWrite("CharacterRepository.ResetDungeonEntries");
        using var tx = write.Transaction;
        int panel = Execute(db, tx, "DELETE FROM character_weekly_dungeon WHERE account=$account AND week=$week",
            ("$account", owner.Account), ("$week", panelWeek));
        int daily = Execute(db, tx, """
            UPDATE character_dungeon_incount SET daily_used=0
            WHERE account=$account AND day=$day AND daily_used<>0
            """, ("$account", owner.Account), ("$day", period.Day));
        // 各副本可指定周重置星期；本周起点必在最近七个业务日内，旧周则在此范围之外。
        // 代号希望周一重置，不能只按默认周四的周键刷新。
        int earliestWeek = DungeonIncountPolicy.CurrentAccountPeriods(now).Min();
        int weekly = Execute(db, tx, """
            UPDATE character_dungeon_incount SET weekly_used=0
            WHERE account=$account AND week BETWEEN $earliest AND $today AND weekly_used<>0
            """, ("$account", owner.Account), ("$earliest", earliestWeek), ("$today", period.Day));
        int account = ClearAccountEntries(db, tx, owner.Account, DungeonIncountPolicy.CurrentAccountPeriods(now));
        int assault = Execute(db, tx, """
            UPDATE character_assault_daily SET clear_count=0,hidden_count=0
            WHERE account=$account AND day=$day AND (clear_count<>0 OR hidden_count<>0)
            """, ("$account", owner.Account), ("$day", assaultDay));
        // 魔界塔：每角色每天入场一次；只清本周绑定行的当日入场日，周内已绑角色数量不变。
        int dazzlement = Execute(db, tx, """
            UPDATE account_dazzlement_tower_bindings SET last_entry_day=0
            WHERE account=$account AND week=$week AND last_entry_day<>0
            """, ("$account", owner.Account), ("$week", dazzlementWeek));
        // 悲叹塔：账号日额度 + 角色当天通关锁；把日期挪到 1（合法的最早日期）即视为新的一天，楼层进度保留。
        int griefAccount = Execute(db, tx, """
            UPDATE account_grief_tower SET game_day=1,used=0
            WHERE account=$account AND (used<>0 OR game_day=$day)
            """, ("$account", owner.Account), ("$day", griefDay));
        int griefCharacter = Execute(db, tx, """
            UPDATE character_grief_tower SET game_day=1,cleared=0
            WHERE account=$account AND (cleared<>0 OR game_day=$day)
            """, ("$account", owner.Account), ("$day", griefDay));
        // 祭坛/绝望：当日入场回执挪到 1，计数按当日回执条数算，等于当日次数清零；回执身份与通关校验链不变。
        int special = Execute(db, tx, """
            UPDATE dungeon_special_entry_receipts SET service_day=1
            WHERE account=$account AND service_day=$day AND kind IN(1,2,3)
            """, ("$account", owner.Account), ("$day", calendarDay));
        int despair = Execute(db, tx, """
            UPDATE dungeon_despair_progress SET last_clear_day=0
            WHERE account=$account AND last_clear_day=$day
            """, ("$account", owner.Account), ("$day", calendarDay));
        // 四类团本仅推进入场轴，历史时间、阶段资格、奖励轴和引导绑定原样保留。
        var raidAdvances = AdvanceRaidQuotaEpochs(db, tx, owner.Account, RaidQuotaAxis.Entry, FiendwarRewards.Cycle.Key(now));
        int raid = raidAdvances.Sum(row => row.ArchivedRuns);
        // 再战魔界：周内通关 3 次上限按本周通关回执计数，回执挪到 1/1 周即不计入本周；
        // 跨日未完成的尝试原本也占本周次数，一并删掉。回执行本身保留给通关记录引用。
        int tag = Execute(db, tx, """
            UPDATE account_tag_evildom_clearances SET day=1,week=1
            WHERE account=$account AND (week=$week OR day=$day)
            """, ("$account", owner.Account), ("$week", tagPeriod.Week), ("$day", tagPeriod.Day));
        int tagChallenge = Execute(db, tx, """
            DELETE FROM account_tag_evildom_challenges
            WHERE account=$account AND week=$week AND day<$day AND state=0
            """, ("$account", owner.Account), ("$week", tagPeriod.Week), ("$day", tagPeriod.Day));
        tx.Commit();
        return new DungeonEntryResetResult(owner.Account, panelWeek, panel, daily, weekly, account, assault,
            dazzlement, griefAccount, griefCharacter, special, despair, raid, tag, tagChallenge)
        { RaidEpochsAdvanced = raidAdvances.Length, RaidGuideBindingsArchived = raidAdvances.Sum(row => row.ArchivedGuideBindings) };
    });

    // 账号次数一账号一副本一行，周期键可能是日键或任意周起点的周键；按当前可能命中的周期键一次清零。
    static int ClearAccountEntries(SqliteConnection db, SqliteTransaction tx, string account, int[] periods)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "UPDATE account_dungeon_incount SET used=0 WHERE account=$account AND used<>0 AND period IN ("
            + string.Join(",", Enumerable.Range(0, periods.Length).Select(index => "$p" + index)) + ")";
        command.Parameters.AddWithValue("$account", account);
        for (int index = 0; index < periods.Length; index++)
            command.Parameters.AddWithValue("$p" + index, periods[index]);
        return command.ExecuteNonQuery();
    }

    static int Execute(SqliteConnection db, SqliteTransaction tx, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return command.ExecuteNonQuery();
    }
}
