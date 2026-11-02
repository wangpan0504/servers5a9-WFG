using DfoServer.Game.Accounts;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Quests;
using DfoServer.Game.ImperialArena;
using DfoServer.Game.Strongway2019;
using DfoServer.Game.TagEvildom;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    // 奖励上限刷新只迁移当前周期的资格/状态键，保留所有奖励回执、通关事实和已发邮件。
    // 各表使用账号范围内的 MAX+1 作为历史键，避开唯一键冲突，也不会伪造当前周期记录。
    public RewardLimitResetResult ResetRewardLimits(AccountOwners.Lease owner) => owner.Execute(() =>
    {
        var now = DateTimeOffset.UtcNow;
        var strongway = Strongway2019RewardPeriod.At(now);
        var imperial = ImperialArenaPeriod.At(now);
        var tag = TagEvildomPeriod.At(now);
        int day = ShopPurchaseCount.Today(now);
        int week = WeeklyDungeonPolicy.WeekKey(now);
        int griefDay = QuestCycle.Day(now);
        using var db = database.Open();
        using var write = db.BeginWrite("CharacterRepository.ResetRewardLimits");
        using var tx = write.Transaction;

        int strongwayDay = NextKey(db, tx, "account_strongway2019_rewards", "day", owner.Account, strongway.Day);
        int strongwayWeek = NextKey(db, tx, "account_strongway2019_rewards", "week", owner.Account, strongway.Week);
        int strongwayReward = ExecuteReward(db, tx, "UPDATE account_strongway2019_rewards SET day=$newDay,week=$newWeek WHERE account=$account AND (day=$day OR week=$week)",
            ("$newDay", strongwayDay), ("$newWeek", strongwayWeek), ("$account", owner.Account), ("$day", strongway.Day), ("$week", strongway.Week));
        int strongwayCompletion = ExecuteReward(db, tx, "UPDATE character_strongway2019_completions SET day=$newDay,week=$newWeek WHERE account=$account AND (day=$day OR week=$week)",
            ("$newDay", strongwayDay), ("$newWeek", strongwayWeek), ("$account", owner.Account), ("$day", strongway.Day), ("$week", strongway.Week));
        int strongwayMatch = ExecuteReward(db, tx, "UPDATE character_strongway2019_match_facts SET day=$newDay,week=$newWeek WHERE account=$account AND (day=$day OR week=$week)",
            ("$newDay", strongwayDay), ("$newWeek", strongwayWeek), ("$account", owner.Account), ("$day", strongway.Day), ("$week", strongway.Week));

        int imperialDay = NextKey(db, tx, "account_imperial_arena_days", "day", owner.Account, imperial.Day);
        int imperialWeek = NextKey(db, tx, "account_imperial_arena_runs", "week", owner.Account, imperial.Week);
        // runs 外键引用 days，先建立新父行，再迁移子行，最后删除旧父行。
        int imperialDays = ExecuteReward(db, tx, """
            INSERT INTO account_imperial_arena_days(account,day,mode,hard_round)
            SELECT account,$newDay,mode,hard_round FROM account_imperial_arena_days
            WHERE account=$account AND day=$day
            """, ("$newDay", imperialDay), ("$account", owner.Account), ("$day", imperial.Day));
        int imperialRuns = ExecuteReward(db, tx, "UPDATE account_imperial_arena_runs SET day=$newDay,week=$newWeek WHERE account=$account AND (day=$day OR week=$week)",
            ("$newDay", imperialDay), ("$newWeek", imperialWeek), ("$account", owner.Account), ("$day", imperial.Day), ("$week", imperial.Week));
        int imperialRewards = ExecuteReward(db, tx, "UPDATE account_imperial_arena_rewards SET day=$newDay WHERE account=$account AND day=$day",
            ("$newDay", imperialDay), ("$account", owner.Account), ("$day", imperial.Day));
        int imperialChallenges = ExecuteReward(db, tx, "UPDATE account_imperial_arena_challenge_claims SET week=$newWeek WHERE account=$account AND week=$week",
            ("$newWeek", imperialWeek), ("$account", owner.Account), ("$week", imperial.Week));
        ExecuteReward(db, tx, "DELETE FROM account_imperial_arena_days WHERE account=$account AND day=$day",
            ("$account", owner.Account), ("$day", imperial.Day));

        int tagDay = NextKey(db, tx, "account_tag_evildom_clearances", "day", owner.Account, tag.Day);
        int tagWeek = NextKey(db, tx, "account_tag_evildom_clearances", "week", owner.Account, tag.Week);
        int tagClearance = ExecuteReward(db, tx, "UPDATE account_tag_evildom_clearances SET day=$newDay,week=$newWeek WHERE account=$account AND (day=$day OR week=$week)",
            ("$newDay", tagDay), ("$newWeek", tagWeek), ("$account", owner.Account), ("$day", tag.Day), ("$week", tag.Week));
        int tagChallenge = ExecuteReward(db, tx, "UPDATE account_tag_evildom_challenges SET day=$newDay,week=$newWeek WHERE account=$account AND (day=$day OR week=$week)",
            ("$newDay", tagDay), ("$newWeek", tagWeek), ("$account", owner.Account), ("$day", tag.Day), ("$week", tag.Week));

        int griefAccount = ExecuteReward(db, tx, "UPDATE account_grief_tower SET game_day=1,used=0 WHERE account=$account AND (used<>0 OR game_day=$day)",
            ("$account", owner.Account), ("$day", griefDay));
        int griefCharacter = ExecuteReward(db, tx, "UPDATE character_grief_tower SET game_day=1,cleared=0 WHERE account=$account AND (cleared<>0 OR game_day=$day)",
            ("$account", owner.Account), ("$day", griefDay));
        int specialDay = NextKey(db, tx, "dungeon_special_entry_receipts", "service_day", owner.Account, day);
        int special = ExecuteReward(db, tx, "UPDATE dungeon_special_entry_receipts SET service_day=$newDay WHERE account=$account AND service_day=$day",
            ("$newDay", specialDay), ("$account", owner.Account), ("$day", day));
        int despair = ExecuteReward(db, tx, "UPDATE dungeon_despair_progress SET last_clear_day=0 WHERE account=$account AND last_clear_day=$day",
            ("$account", owner.Account), ("$day", day));

        int raidWeek = NextKey(db, tx, "character_raid_runs", "clear_week", owner.Account, week);
        int raid = ExecuteReward(db, tx, "UPDATE character_raid_runs SET clear_week=$newWeek WHERE account=$account AND clear_week=$week",
            ("$newWeek", raidWeek), ("$account", owner.Account), ("$week", week));
        int raidReceiptWeek = NextKey(db, tx, "character_raid_reward_receipts", "week_key", owner.Account, week);
        int raidReceipts = ExecuteReward(db, tx, "UPDATE character_raid_reward_receipts SET week_key=$newWeek WHERE account=$account AND week_key=$week",
            ("$newWeek", raidReceiptWeek), ("$account", owner.Account), ("$week", week));
        tx.Commit();
        return new RewardLimitResetResult(owner.Account, day, week, strongwayReward, strongwayCompletion, strongwayMatch,
            imperialDays, imperialRuns, imperialRewards, imperialChallenges, tagClearance, tagChallenge,
            griefAccount, griefCharacter, special, despair, raid, raidReceipts);
    });

    static int NextKey(SqliteConnection db, SqliteTransaction tx, string table, string column, string account, int current)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = $"SELECT COALESCE(MAX({column}),$minimum)+1 FROM {table} WHERE account=$account";
        command.Parameters.AddWithValue("$minimum", Math.Max(current, 20000000));
        command.Parameters.AddWithValue("$account", account);
        return checked(Convert.ToInt32(command.ExecuteScalar()));
    }

    static int ExecuteReward(SqliteConnection db, SqliteTransaction tx, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return command.ExecuteNonQuery();
    }
}
