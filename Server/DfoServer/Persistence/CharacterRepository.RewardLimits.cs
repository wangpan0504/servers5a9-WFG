using DfoServer.Game.Accounts;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Quests;
using DfoServer.Game.Raids;
using DfoServer.Game.ImperialArena;
using DfoServer.Game.Strongway2019;
using DfoServer.Game.TagEvildom;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    // 奖励上限刷新迁移资格/状态键，或单独标记已冻结的清图奖励；保留所有奖励回执、通关事实和已发邮件。
    // 各表使用账号范围内的 MAX+1 作为历史键，避开唯一键冲突；归档键超过合法日期，防止未来日切重新计入。
    public RewardLimitResetResult ResetRewardLimits(AccountOwners.Lease owner) => owner.Execute(() =>
    {
        using var db = database.Open();
        using var write = db.BeginWrite("CharacterRepository.ResetRewardLimits");
        using var tx = write.Transaction;
        var result = ResetRewardLimitsInTransaction(db, tx, owner, DateTimeOffset.UtcNow);
        tx.Commit();
        return result;
    });

    static RewardLimitResetResult ResetRewardLimitsInTransaction(SqliteConnection db, SqliteTransaction tx,
        AccountOwners.Lease owner, DateTimeOffset now)
    {
        var strongway = Strongway2019RewardPeriod.At(now);
        var imperial = ImperialArenaPeriod.At(now);
        var tag = TagEvildomPeriod.At(now);
        int day = ShopPurchaseCount.Today(now);
        int week = WeeklyDungeonPolicy.WeekKey(now);
        int griefDay = QuestCycle.Day(now);

        // 奖励冻结即占用次数，已领与未领都退出后续限次统计；领取回放仍只认原回执。
        int clearRewards = ExecuteReward(db, tx, """
            INSERT OR IGNORE INTO character_clear_reward_resets(character_id,instance_id)
            SELECT r.character_id,r.instance_id FROM characters c
            JOIN character_dungeon_card_rewards r ON r.character_id=c.id AND r.account=c.account
            WHERE c.account=$account AND json_type(r.plan_json,'$.Item')='object'
              AND NOT EXISTS(SELECT 1 FROM character_clear_reward_resets reset
                WHERE reset.character_id=r.character_id AND reset.instance_id=r.instance_id)
            """, ("$account", owner.Account));

        // 待投递完成和未消费匹配事实也预留归档键，不能只避让已投递账本。
        string[] strongwayTables = ["account_strongway2019_rewards", "character_strongway2019_completions", "character_strongway2019_match_facts"];
        int strongwayDay = strongwayTables.Max(table => NextKey(db, tx, table, "day", owner.Account, strongway.Day));
        int strongwayWeek = strongwayTables.Max(table => NextKey(db, tx, table, "week", owner.Account, strongway.Week));
        // 日、周资格分别迁移；周内旧日不能合并到同一归档日，否则会破坏外键与每日唯一约束。
        int strongwayReward = ExecuteReward(db, tx, "UPDATE account_strongway2019_rewards SET day=CASE WHEN day=$day THEN $newDay ELSE day END,week=CASE WHEN week=$week THEN $newWeek ELSE week END WHERE account=$account AND (day=$day OR week=$week)",
            ("$newDay", strongwayDay), ("$newWeek", strongwayWeek), ("$account", owner.Account), ("$day", strongway.Day), ("$week", strongway.Week));
        int strongwayCompletion = ExecuteReward(db, tx, "UPDATE character_strongway2019_completions SET day=CASE WHEN day=$day THEN $newDay ELSE day END,week=CASE WHEN week=$week THEN $newWeek ELSE week END WHERE account=$account AND (day=$day OR week=$week)",
            ("$newDay", strongwayDay), ("$newWeek", strongwayWeek), ("$account", owner.Account), ("$day", strongway.Day), ("$week", strongway.Week));
        // JSON中的Period只是同一资格键的镜像；同步它以保留离线胜者恢复，冻结事实和完整附件均不改。
        int strongwayMatch = ExecuteReward(db, tx, """
            UPDATE character_strongway2019_match_facts
            SET snapshot_json=json_set(snapshot_json,
                  '$.Completion.Period.Day',CASE WHEN day=$day THEN $newDay ELSE day END,
                  '$.Completion.Period.Week',CASE WHEN week=$week THEN $newWeek ELSE week END),
                day=CASE WHEN day=$day THEN $newDay ELSE day END,
                week=CASE WHEN week=$week THEN $newWeek ELSE week END
            WHERE account=$account AND (day=$day OR week=$week)
            """,
            ("$newDay", strongwayDay), ("$newWeek", strongwayWeek), ("$account", owner.Account), ("$day", strongway.Day), ("$week", strongway.Week));

        int imperialDay = NextKey(db, tx, "account_imperial_arena_days", "day", owner.Account, imperial.Day);
        int imperialWeek = NextKey(db, tx, "account_imperial_arena_runs", "week", owner.Account, imperial.Week);
        // runs 外键引用 days，先建立新父行，再迁移子行，最后删除旧父行。
        int imperialDays = ExecuteReward(db, tx, """
            INSERT INTO account_imperial_arena_days(account,day,mode,hard_round)
            SELECT account,$newDay,mode,hard_round FROM account_imperial_arena_days
            WHERE account=$account AND day=$day
            """, ("$newDay", imperialDay), ("$account", owner.Account), ("$day", imperial.Day));
        int imperialRuns = ExecuteReward(db, tx, "UPDATE account_imperial_arena_runs SET day=CASE WHEN day=$day THEN $newDay ELSE day END,week=CASE WHEN week=$week THEN $newWeek ELSE week END WHERE account=$account AND (day=$day OR week=$week)",
            ("$newDay", imperialDay), ("$newWeek", imperialWeek), ("$account", owner.Account), ("$day", imperial.Day), ("$week", imperial.Week));
        int imperialRewards = ExecuteReward(db, tx, "UPDATE account_imperial_arena_rewards SET day=$newDay WHERE account=$account AND day=$day",
            ("$newDay", imperialDay), ("$account", owner.Account), ("$day", imperial.Day));
        int imperialChallenges = ExecuteReward(db, tx, "UPDATE account_imperial_arena_challenge_claims SET week=$newWeek WHERE account=$account AND week=$week",
            ("$newWeek", imperialWeek), ("$account", owner.Account), ("$week", imperial.Week));
        ExecuteReward(db, tx, "DELETE FROM account_imperial_arena_days WHERE account=$account AND day=$day",
            ("$account", owner.Account), ("$day", imperial.Day));

        // 未通关挑战没有clearance行，其归档日也必须参与避让，否则下一轮刷新会撞历史主键。
        int tagDay = Math.Max(NextKey(db, tx, "account_tag_evildom_clearances", "day", owner.Account, tag.Day),
            NextKey(db, tx, "account_tag_evildom_challenges", "day", owner.Account, tag.Day));
        int tagWeek = NextKey(db, tx, "account_tag_evildom_clearances", "week", owner.Account, tag.Week);
        int tagClearance = ExecuteReward(db, tx, "UPDATE account_tag_evildom_clearances SET day=CASE WHEN day=$day THEN $newDay ELSE day END,week=CASE WHEN week=$week THEN $newWeek ELSE week END WHERE account=$account AND (day=$day OR week=$week)",
            ("$newDay", tagDay), ("$newWeek", tagWeek), ("$account", owner.Account), ("$day", tag.Day), ("$week", tag.Week));
        int tagChallenge = ExecuteReward(db, tx, "UPDATE account_tag_evildom_challenges SET day=CASE WHEN day=$day THEN $newDay ELSE day END,week=CASE WHEN week=$week THEN $newWeek ELSE week END WHERE account=$account AND (day=$day OR week=$week)",
            ("$newDay", tagDay), ("$newWeek", tagWeek), ("$account", owner.Account), ("$day", tag.Day), ("$week", tag.Week));

        int griefAccount = ExecuteReward(db, tx, "UPDATE account_grief_tower SET game_day=1,used=0 WHERE account=$account AND (used<>0 OR game_day=$day)",
            ("$account", owner.Account), ("$day", griefDay));
        int griefCharacter = ExecuteReward(db, tx, "UPDATE character_grief_tower SET game_day=1,cleared=0 WHERE account=$account AND (cleared<>0 OR game_day=$day)",
            ("$account", owner.Account), ("$day", griefDay));
        // service_day是DayNumber而非yyyyMMdd，沿入场刷新使用合法旧日1，不改变回执身份。
        int special = ExecuteReward(db, tx, "UPDATE dungeon_special_entry_receipts SET service_day=1 WHERE account=$account AND service_day=$day",
            ("$account", owner.Account), ("$day", day));
        int despair = ExecuteReward(db, tx, "UPDATE dungeon_despair_progress SET last_clear_day=0 WHERE account=$account AND last_clear_day=$day",
            ("$account", owner.Account), ("$day", day));

        // 改版魔兽以安妮阶段扣领奖资格；两张回执表必须在同一事务退出当前日/周计数。
        // 保留实例主键、冻结奖励JSON、积分与实际发奖事实，不删除回执或重新发奖。
        var beast = DungeonIncountPolicy.Period(now);
        string[] beastTables = ["character_revised_beast_stage_rewards", "character_revised_beast_rewards"];
        int beastDay = beastTables.Max(table => NextKey(db, tx, table, "day_key", owner.Account, beast.Day));
        int beastWeek = beastTables.Max(table => NextKey(db, tx, table, "week_key", owner.Account, beast.Week));
        int ArchiveBeast(string table) => ExecuteReward(db, tx,
            $"UPDATE {table} SET day_key=CASE WHEN day_key=$day THEN $newDay ELSE day_key END,week_key=CASE WHEN week_key=$week THEN $newWeek ELSE week_key END WHERE account=$account AND (day_key=$day OR week_key=$week)",
            ("$newDay", beastDay), ("$newWeek", beastWeek), ("$account", owner.Account), ("$day", beast.Day), ("$week", beast.Week));
        int beastStages = ArchiveBeast(beastTables[0]);
        int beastTerminals = ArchiveBeast(beastTables[1]);

        // 四类团本只推进奖励轴；未领取及迟到阶段仍归原run世代，不改原JSON和周期。
        var raidAdvances = AdvanceRaidQuotaEpochs(db, tx, owner.Account, RaidQuotaAxis.Reward, FiendwarRewards.Cycle.Key(now));
        int raid = raidAdvances.Sum(row => row.ArchivedRuns);
        int raidReceipts = raidAdvances.Sum(row => row.ArchivedRewardReceipts);
        return new RewardLimitResetResult(owner.Account, day, week, strongwayReward, strongwayCompletion, strongwayMatch,
            imperialDays, imperialRuns, imperialRewards, imperialChallenges, tagClearance, tagChallenge,
            griefAccount, griefCharacter, special, despair, raid, raidReceipts)
            { RaidEpochsAdvanced = raidAdvances.Length, ClearRewardRows = clearRewards,
                RevisedBeastStageRows = beastStages, RevisedBeastTerminalRows = beastTerminals };
    }

    static int NextKey(SqliteConnection db, SqliteTransaction tx, string table, string column, string account, int current)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = $"SELECT MAX(COALESCE(MAX({column}),0),$minimum)+1 FROM {table} WHERE account=$account";
        command.Parameters.AddWithValue("$minimum", Math.Max(current, 100000000));
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
