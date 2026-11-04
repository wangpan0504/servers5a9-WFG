namespace DfoServer.Persistence;

internal static class DailyAttendanceSchema
{
    // 账号事实独立于可删除的角色和邮件，删除收件人不能刷新签到或领奖资格。
    internal const string Definition = """
        CREATE TABLE account_daily_attendance_days(
          account TEXT NOT NULL,
          day INTEGER NOT NULL CHECK(day BETWEEN 10101 AND 99991231),
          clears INTEGER NOT NULL CHECK(clears BETWEEN 0 AND 2),
          PRIMARY KEY(account,day)
        ) STRICT;
        CREATE TABLE account_daily_attendance_claims(
          account TEXT NOT NULL,
          kind INTEGER NOT NULL CHECK(kind IN(0,1)),
          reward_index INTEGER NOT NULL CHECK((kind=0 AND reward_index BETWEEN 0 AND 27) OR (kind=1 AND reward_index BETWEEN 0 AND 2)),
          day INTEGER NOT NULL CHECK(day BETWEEN 10101 AND 99991231),
          recipient_id INTEGER NOT NULL CHECK(recipient_id BETWEEN 1 AND 65535),
          mail_id INTEGER NOT NULL CHECK(mail_id>0),
          item_id INTEGER NOT NULL CHECK(item_id>0),
          item_count INTEGER NOT NULL CHECK(item_count>0),
          claimed_at INTEGER NOT NULL CHECK(claimed_at>=0),
          PRIMARY KEY(account,kind,reward_index)
        ) STRICT;
        """;
}
