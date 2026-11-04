using System.Security.Cryptography;
using System.Text;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Events;
using DfoServer.Game.Mailbox;
using DfoServer.GameWorld.OnlineEvents;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository : IDailyAttendanceStore
{
    public DailyAttendanceSnapshot ReadDailyAttendance(AccountOwners.Lease owner, int day) => owner.Execute(() =>
    {
        PcRoomTimePointDay.Validate(day);
        using var db = database.Open();
        using var tx = db.BeginRead();
        return ReadDailyAttendance(db, tx, owner.Account, day);
    });

    public DailyAttendanceCommit ClaimDailyAttendance(AccountOwners.Lease owner, Character expected, DailyAttendancePlan plan) => owner.Execute(() =>
    {
        using var db = database.Open();
        // 无可领档位与关闭状态不申请writer；真正领取在事务内重验账号、角色及活动版本。
        using (var read = db.BeginRead())
        {
            var before = ReadDailyAttendance(db, read, owner.Account, plan.Before.Day);
            if (before != plan.Before || !DailyAttendanceActive(db, read, plan)) return new DailyAttendanceCommit(before);
        }
        using var write = db.BeginWrite("CharacterRepository.ClaimDailyAttendance");
        using var tx = write.Transaction;
        var current = Require(db, tx, owner.Account, expected.Name);
        if (current.Id != expected.Id || current.Revision != expected.Revision)
            throw new InvalidOperationException("日签到领奖角色快照已失效");
        var result = ApplyDailyAttendance(db, tx, owner.Account, expected, plan, claim: true);
        tx.Commit();
        return result;
    });

    internal static DailyAttendanceCommit ApplyDailyAttendance(SqliteConnection db, SqliteTransaction tx,
        string account, Character character, DailyAttendancePlan plan, bool claim)
    {
        var before = ReadDailyAttendance(db, tx, account, plan.Before.Day);
        if (!DailyAttendanceActive(db, tx, plan)) return new(before);
        if (before != plan.Before) throw new InvalidOperationException("日签到进度已改变");
        var reward = claim
            ? plan.Catalog.Accumulated.FirstOrDefault(row => before.Total >= row.RequiredDays && (before.Claimed & (1 << row.Index)) == 0)
            : before.Total < plan.Catalog.Daily.Length && before.Clears == DailyAttendanceCatalog.ClearTarget - 1
                ? plan.Catalog.Daily[before.Total] : null;
        if (reward != plan.Reward || plan.Before.Day != PcRoomTimePointDay.At(plan.At))
            throw new InvalidDataException("日签到奖励与当前资格不符");
        if (!claim && (before.Total >= plan.Catalog.Daily.Length || before.Clears >= DailyAttendanceCatalog.ClearTarget)) return new(before);
        if (claim && reward is null) return new(before);
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.Parameters.AddWithValue("$account", account);
        command.Parameters.AddWithValue("$day", before.Day);
        if (!claim)
        {
            command.CommandText = """
                INSERT INTO account_daily_attendance_days(account,day,clears) VALUES($account,$day,1)
                ON CONFLICT(account,day) DO UPDATE SET clears=clears+1 WHERE clears<2
                """;
            if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("日签到当天次数已改变");
        }
        uint mail = 0;
        if (reward is not null)
        {
            if (plan.Attachments.IsDefaultOrEmpty || plan.Attachments.Length > 10 ||
                plan.Attachments.Any(item => item.Uid != 0 || item.Core.ItemId != reward.ItemId || item.Core.Count <= 0) ||
                plan.Attachments.Sum(item => (long)item.Core.Count) != reward.Count)
                throw new InvalidDataException("日签到邮件附件与奖励不符");
            int kind = claim ? 1 : 0;
            string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(account)));
            var delivery = new MailboxDelivery($"attendance2370:{key}:{kind}:{reward.Index}",
                new(character.Id, account, character.Name, character.Job, character.GrowType, character.Level),
                "每日签到得好礼", claim ? "累计签到奖励已发放。" : "今日签到奖励已发放。", 0,
                plan.Attachments, plan.At.ToUnixTimeSeconds());
            var (error, id) = DeliverMailInTransaction(db, tx, delivery);
            if (error != MailboxError.None) throw new InvalidOperationException("日签到奖励邮件投递失败: " + error);
            mail = id;
            command.Parameters.AddWithValue("$kind", kind);
            command.Parameters.AddWithValue("$index", reward.Index);
            command.Parameters.AddWithValue("$recipient", character.Id);
            command.Parameters.AddWithValue("$mail", mail);
            command.Parameters.AddWithValue("$item", reward.ItemId);
            command.Parameters.AddWithValue("$count", reward.Count);
            command.Parameters.AddWithValue("$at", plan.At.ToUnixTimeSeconds());
            command.CommandText = """
                INSERT INTO account_daily_attendance_claims(account,kind,reward_index,day,recipient_id,mail_id,item_id,item_count,claimed_at)
                VALUES($account,$kind,$index,$day,$recipient,$mail,$item,$count,$at)
                """;
            command.ExecuteNonQuery();
        }
        else if (!plan.Attachments.IsEmpty) throw new InvalidDataException("未签到不能携带奖励附件");
        return new(ReadDailyAttendance(db, tx, account, before.Day), mail);
    }

    static bool DailyAttendanceActive(SqliteConnection db, SqliteTransaction tx, DailyAttendancePlan plan)
    {
        var schedule = ServerEventRepository.Read(db, tx, ServerEventService.DailyAttendanceId);
        return schedule.Revision == plan.ActivityRevision && schedule.IsActive(plan.At);
    }

    static DailyAttendanceSnapshot ReadDailyAttendance(SqliteConnection db, SqliteTransaction tx, string account, int day)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.Parameters.AddWithValue("$account", account);
        command.Parameters.AddWithValue("$day", day);
        command.CommandText = "SELECT clears FROM account_daily_attendance_days WHERE account=$account AND day=$day";
        int clears = command.ExecuteScalar() is long count ? checked((int)count) : 0;
        command.CommandText = "SELECT kind,reward_index FROM account_daily_attendance_claims WHERE account=$account";
        int total = 0; byte claimed = 0;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (reader.GetInt32(0) == 0) total++;
            else claimed |= checked((byte)(1 << reader.GetInt32(1)));
        }
        return new(day, clears, total, claimed);
    }
}
