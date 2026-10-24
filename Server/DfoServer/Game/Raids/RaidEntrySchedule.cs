namespace DfoServer.Game.Raids;

// 三张团本卡的每周入场表。时刻按 UTC+8，结束时刻不包含在内。空表表示全关。
internal static class RaidEntrySchedule
{
    public static readonly string[] Names = ["luke", "hyperspace", "prey", "anton"];
    public static readonly TimeSpan Zone = TimeSpan.FromHours(8);

    public readonly record struct Window(int Weekday, int StartMinute, int EndMinute);

    public static bool TryParse(string start, string end, int weekday, out Window window, out string error)
    {
        window = default;
        error = "";
        if (weekday is < 1 or > 7) { error = "星期须为 1 到 7"; return false; }
        if (!Minute(start, out int from) || !Minute(end, out int to) || to == 0) { error = "时刻须为 HH:mm"; return false; }
        if (to <= from || to > 24 * 60) { error = "结束时刻须晚于开始时刻，且不超过 24:00"; return false; }
        window = new Window(weekday, from, to);
        return true;
    }

    public static bool IsOpen(IReadOnlyList<Window> windows, DateTimeOffset instant)
    {
        DateTimeOffset local = instant.ToOffset(Zone);
        int weekday = local.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)local.DayOfWeek;
        int minute = local.Hour * 60 + local.Minute;
        foreach (var window in windows)
            if (window.Weekday == weekday && minute >= window.StartMinute && minute < window.EndMinute)
                return true;
        return false;
    }

    static bool Minute(string text, out int minute)
    {
        minute = 0;
        if (text is not { Length: 5 } || text[2] != ':') return false;
        if (!int.TryParse(text[..2], out int hour) || !int.TryParse(text[3..], out int min)) return false;
        if (hour == 24 && min == 0) { minute = 24 * 60; return true; }
        if (hour is < 0 or > 23 || min is < 0 or > 59) return false;
        minute = hour * 60 + min;
        return true;
    }
}
