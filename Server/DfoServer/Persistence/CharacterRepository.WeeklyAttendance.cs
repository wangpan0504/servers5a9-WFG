using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Events;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Mailbox;
using DfoServer.GameWorld.OnlineEvents;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository : IWeeklyAttendanceStore
{
    WeeklyAttendanceCatalog? weeklyAttendanceCatalog;
    TimeProvider weeklyAttendanceClock = TimeProvider.System;
    public WeeklyAttendanceSeason? ReadWeeklyAttendanceSeason()
    {
        using var db = database.Open();
        using var tx = db.BeginRead();
        return ReadWeeklyAttendanceSeason(db, tx);
    }
    internal void ConfigureWeeklyAttendance(WeeklyAttendanceCatalog catalog, TimeProvider? clock = null)
    {
        weeklyAttendanceCatalog = catalog;
        weeklyAttendanceClock = clock ?? TimeProvider.System;
    }

    public WeeklyAttendanceSeason ActivateWeeklyAttendance(AccountOwners.Lease owner, Character expected,
        DateTimeOffset now, WeeklyAttendanceCatalog catalog) => owner.Execute(() =>
    {
        using var db = database.Open();
        for (bool writing = false; ; writing = true)
        {
            using var write = writing ? db.BeginWrite("WeeklyAttendance.Activate") : null;
            using var tx = write?.Transaction ?? db.BeginRead();
            RequireWeeklyAttendanceCharacter(db, tx, owner, expected);
            if (ReadWeeklyAttendanceSeason(db, tx) is { } existing)
            {
                if (existing.Weeks != catalog.Weeks) throw new InvalidDataException("周签到目录周期与已冻结赛季不符");
                return existing;
            }
            RequireWeeklyAttendanceEnabled(db, tx, now);
            if (!writing) continue;
            var season = WeeklyAttendanceSeason.Create(now, catalog.Weeks);
            using var command = db.CreateCommand();
            command.Transaction = tx;
            command.CommandText = "INSERT INTO weekly_attendance_season(id,starts_at,ends_at,activated_at,weeks) VALUES(1,$start,$end,$at,$weeks)";
            command.Parameters.AddWithValue("$start", season.StartsAt);
            command.Parameters.AddWithValue("$end", season.EndsAt);
            command.Parameters.AddWithValue("$at", season.ActivatedAt);
            command.Parameters.AddWithValue("$weeks", season.Weeks);
            command.ExecuteNonQuery();
            tx.Commit();
            return season;
        }
    });

    public WeeklyAttendanceState ReadWeeklyAttendance(AccountOwners.Lease owner, WeeklyAttendanceSeason season,
        WeeklyAttendanceCatalog catalog) => owner.Execute(() =>
    {
        using var db = database.Open();
        using var tx = db.BeginRead();
        return ReadWeeklyAttendanceState(db, tx, owner.Account, season, catalog);
    });

    public WeeklyAttendanceCommit CommitWeeklyAttendance(AccountOwners.Lease owner, Character expected,
        WeeklyAttendanceSeason season, DateTimeOffset now, WeeklyAttendanceCatalog catalog, bool claim, bool bonus,
        IReadOnlyList<ImmutableArray<InventoryItem>> attachments) => owner.Execute<WeeklyAttendanceCommit>(() =>
    {
        using var db = database.Open();
        for (bool writing = false; ; writing = true)
        {
            using var write = writing ? db.BeginWrite("WeeklyAttendance.Claim") : null;
            using var tx = write?.Transaction ?? db.BeginRead();
            RequireWeeklyAttendanceCharacter(db, tx, owner, expected);
            RequireWeeklyAttendanceEnabled(db, tx, now);
            if (ReadWeeklyAttendanceSeason(db, tx) != season) throw new InvalidOperationException("周签到赛季已变化");
            var state = ReadWeeklyAttendanceState(db, tx, owner.Account, season, catalog);
            var rewards = state.Claim(catalog, season, now, claim, bonus);
            if (rewards.IsEmpty) return new(state.Snapshot(catalog, season.Week(now)), []);
            if (rewards.Length != attachments.Count) throw new WeeklyAttendanceRejectedException("周签到领奖计划已变化");
            for (int i = 0; i < rewards.Length; i++)
                if (attachments[i].IsDefaultOrEmpty || attachments[i].Length > 10 ||
                    attachments[i].Any(item => item.Uid != 0 || item.Core.ItemId != rewards[i].ItemId || item.Core.Count <= 0) ||
                    attachments[i].Sum(item => (long)item.Core.Count) != rewards[i].Count)
                    throw new InvalidDataException("周签到邮件附件与目录不符");
            if (!writing) continue;
            var mails = ImmutableArray.CreateBuilder<uint>();
            foreach (var (reward, index) in rewards.Select((reward, index) => (reward, index)))
            {
                string key = $"weekly2208:{season.StartsAt}:{expected.Id}:{(bonus ? "bonus" : "sign")}:{reward.Threshold}";
                var delivery = new MailboxDelivery(key,
                    new(expected.Id, owner.Account, expected.Name, expected.Job, expected.GrowType, expected.Level),
                    "每周签到奖励", "推荐地下城签到奖励已发放，请查收附件。", 0, attachments[index], now.ToUnixTimeSeconds());
                var (error, mail) = DeliverMailInTransaction(db, tx, delivery);
                if (error != MailboxError.None) throw new InvalidOperationException("周签到邮件投递失败: " + error);
                mails.Add(mail);
            }
            SaveWeeklyAttendanceState(db, tx, owner.Account, season, state);
            tx.Commit();
            return new(state.Snapshot(catalog, season.Week(now)), mails.ToImmutable());
        }
    });

    // 只在原通关回执事务的新提交分支调用；重放、经验和周账本共用原提交边界。
    void RecordWeeklyAttendanceClear(SqliteConnection db, SqliteTransaction tx, AccountOwners.Lease owner)
    {
        if (weeklyAttendanceCatalog is not { } catalog) return;
        var now = weeklyAttendanceClock.GetUtcNow();
        if (!ServerEventRepository.Read(db, tx, ServerEventService.WeeklyAttendanceId).IsActive(now)) return;
        var season = ReadWeeklyAttendanceSeason(db, tx);
        if (season is null)
        {
            season = WeeklyAttendanceSeason.Create(now, catalog.Weeks);
            using var create = db.CreateCommand();
            create.Transaction = tx;
            create.CommandText = "INSERT INTO weekly_attendance_season(id,starts_at,ends_at,activated_at,weeks) VALUES(1,$start,$end,$at,$weeks)";
            create.Parameters.AddWithValue("$start", season.StartsAt);
            create.Parameters.AddWithValue("$end", season.EndsAt);
            create.Parameters.AddWithValue("$at", season.ActivatedAt);
            create.Parameters.AddWithValue("$weeks", season.Weeks);
            create.ExecuteNonQuery();
        }
        if (season.Weeks != catalog.Weeks) throw new InvalidDataException("周签到已冻结周期与目录不符");
        int week = season.Week(now);
        if (week < 0) return;
        var state = ReadWeeklyAttendanceState(db, tx, owner.Account, season, catalog);
        if (now.ToUnixTimeSeconds() < state.LastObserved || state.Clears[week] >= catalog.MaximumClears) return;
        state.Clears[week]++;
        state.LastObserved = now.ToUnixTimeSeconds();
        SaveWeeklyAttendanceState(db, tx, owner.Account, season, state);
    }

    static void RequireWeeklyAttendanceCharacter(SqliteConnection db, SqliteTransaction tx, AccountOwners.Lease owner, Character expected)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT revision FROM characters WHERE id=$id AND account=$account AND name=$name";
        command.Parameters.AddWithValue("$id", expected.Id);
        command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$name", expected.Name);
        if (command.ExecuteScalar() is not long revision || revision != expected.Revision)
            throw new InvalidOperationException("周签到角色快照已失效");
    }
    static void RequireWeeklyAttendanceEnabled(SqliteConnection db, SqliteTransaction tx, DateTimeOffset now)
    {
        if (!ServerEventRepository.Read(db, tx, ServerEventService.WeeklyAttendanceId).IsActive(now))
            throw new WeeklyAttendanceRejectedException("周签到活动已关闭");
    }
    static WeeklyAttendanceSeason? ReadWeeklyAttendanceSeason(SqliteConnection db, SqliteTransaction tx)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT starts_at,ends_at,activated_at,weeks FROM weekly_attendance_season WHERE id=1";
        using var reader = command.ExecuteReader();
        return reader.Read() ? new(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt32(3)) : null;
    }
    static WeeklyAttendanceState ReadWeeklyAttendanceState(SqliteConnection db, SqliteTransaction tx, string account,
        WeeklyAttendanceSeason season, WeeklyAttendanceCatalog catalog)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT state FROM account_weekly_attendance WHERE account=$account AND season_start=$start";
        command.Parameters.AddWithValue("$account", account);
        command.Parameters.AddWithValue("$start", season.StartsAt);
        var state = WeeklyAttendanceState.Parse(command.ExecuteScalar() as string, catalog);
        if (state.LastObserved != 0 && (state.LastObserved < season.ActivatedAt || state.LastObserved >= season.EndsAt))
            throw new InvalidDataException("周签到账本时间超出赛季");
        return state;
    }
    static void SaveWeeklyAttendanceState(SqliteConnection db, SqliteTransaction tx, string account,
        WeeklyAttendanceSeason season, WeeklyAttendanceState state)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            INSERT INTO account_weekly_attendance(account,season_start,state) VALUES($account,$start,$state)
            ON CONFLICT(account,season_start) DO UPDATE SET state=excluded.state
            """;
        command.Parameters.AddWithValue("$account", account);
        command.Parameters.AddWithValue("$start", season.StartsAt);
        command.Parameters.AddWithValue("$state", state.Serialize());
        command.ExecuteNonQuery();
    }
}
