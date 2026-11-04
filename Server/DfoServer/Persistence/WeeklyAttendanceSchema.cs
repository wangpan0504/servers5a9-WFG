namespace DfoServer.Persistence;

internal static class WeeklyAttendanceSchema
{
    internal const string Definition = """
        CREATE TABLE weekly_attendance_season(
          id INTEGER PRIMARY KEY CHECK(id=1),
          starts_at INTEGER NOT NULL CHECK(starts_at>=0),
          ends_at INTEGER NOT NULL CHECK(ends_at>starts_at),
          activated_at INTEGER NOT NULL CHECK(activated_at>=starts_at AND activated_at<ends_at),
          weeks INTEGER NOT NULL CHECK(weeks BETWEEN 1 AND 20),
          CHECK(ends_at-starts_at=weeks*604800)
        ) STRICT;
        CREATE TABLE account_weekly_attendance(
          account TEXT NOT NULL,
          season_start INTEGER NOT NULL CHECK(season_start>=0),
          state TEXT NOT NULL CHECK(json_valid(state) AND json_type(state)='object'),
          PRIMARY KEY(account,season_start)
        ) STRICT;
        """;
}
