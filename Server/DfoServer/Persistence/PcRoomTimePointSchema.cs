namespace DfoServer.Persistence;

internal static class PcRoomTimePointSchema
{
    // 账号事实不依赖角色或可删除的邮件行；删角、删邮件不能刷新奖励资格。
    internal const string Definition = """
        CREATE TABLE account_pcroom_timepoint_days(
          account TEXT NOT NULL,
          day INTEGER NOT NULL CHECK(day BETWEEN 10101 AND 99991231),
          elapsed_ms INTEGER NOT NULL CHECK(elapsed_ms>=0),
          qualified INTEGER NOT NULL CHECK(qualified IN(0,1)),
          PRIMARY KEY(account,day)
        ) STRICT;
        CREATE TABLE account_pcroom_timepoint_segments(
          account TEXT NOT NULL,
          segment_id TEXT NOT NULL CHECK(length(segment_id)=32 AND segment_id NOT GLOB '*[^0-9a-f]*'),
          day INTEGER NOT NULL CHECK(day BETWEEN 10101 AND 99991231),
          total_ms INTEGER NOT NULL CHECK(total_ms>=0),
          PRIMARY KEY(account,segment_id,day)
        ) STRICT;
        CREATE TABLE account_pcroom_timepoint_claims(
          account TEXT NOT NULL,
          kind INTEGER NOT NULL CHECK(kind IN(0,1)),
          day INTEGER NOT NULL CHECK((kind=0 AND day BETWEEN 10101 AND 99991231) OR (kind=1 AND day=0)),
          reward_index INTEGER NOT NULL CHECK(reward_index BETWEEN 0 AND 3),
          recipient_id INTEGER NOT NULL CHECK(recipient_id BETWEEN 1 AND 65535),
          mail_id INTEGER NOT NULL CHECK(mail_id>0),
          item_id INTEGER NOT NULL CHECK(item_id>0),
          item_count INTEGER NOT NULL CHECK(item_count>0),
          claimed_at INTEGER NOT NULL CHECK(claimed_at>=0),
          PRIMARY KEY(account,kind,day,reward_index)
        ) STRICT;
        """;
}
