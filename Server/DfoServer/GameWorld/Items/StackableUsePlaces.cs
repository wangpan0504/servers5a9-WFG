namespace DfoServer.GameWorld.Items;

internal enum StackableUsePlace { None, Village, SeriaRoom, Dungeon, Pvp, Tournament }

internal readonly record struct StackableUseContext(StackableUsePlace Place, bool IsPractice = false, bool IsAssault = false,
    bool InGuild = false, bool IsPvpPrivate = false, bool IsGuildMaster = false);

[Flags]
internal enum StackableUsePlaceFlags
{
    None = 0, Village = 1, Dungeon = 2, Pvp = 4, SeriaRoom = 8, Tournament = 16,
    ExcludeAssault = 32, DontOpen = 64, ExcludePractice = 128, All = 256
}

// ARAD 040C0070 解析八种地点值；029BB7E0 检查场景，02B2A223 另行排除练习模式。
internal static class StackableUsePlaces
{
    public static StackableUsePlaceFlags Parse(IReadOnlyList<string>? places)
    {
        var result = StackableUsePlaceFlags.None;
        if (places is null) return result;
        foreach (string place in places)
            result |= place switch
            {
                "[dungeon]" => StackableUsePlaceFlags.Dungeon,
                "[pvp]" => StackableUsePlaceFlags.Pvp,
                "[seria room]" => StackableUsePlaceFlags.SeriaRoom,
                "[tournament]" => StackableUsePlaceFlags.Tournament,
                "[dont use assult]" => StackableUsePlaceFlags.ExcludeAssault,
                "[dont open]" => StackableUsePlaceFlags.DontOpen,
                "[dont use exercise]" => StackableUsePlaceFlags.ExcludePractice,
                "[all]" => StackableUsePlaceFlags.All, // 保留本服既有全场景兼容项。
                _ => StackableUsePlaceFlags.Village // village 与真正未知的字符串均映射为 1。
            };
        return result;
    }

    public static bool TownOnly(IReadOnlyList<string>? places)
    {
        var flags = Parse(places);
        return (flags & (StackableUsePlaceFlags.Village | StackableUsePlaceFlags.SeriaRoom)) != 0 &&
            (flags & (StackableUsePlaceFlags.Dungeon | StackableUsePlaceFlags.Pvp | StackableUsePlaceFlags.Tournament | StackableUsePlaceFlags.All)) == 0;
    }

    public static bool SeriaRoomOnly(IReadOnlyList<string>? places) => TownOnly(places) &&
        (Parse(places) & StackableUsePlaceFlags.Village) == 0;

    public static bool Allows(IReadOnlyList<string>? places, string current) => Allows(places, new StackableUseContext(current switch
    {
        "[village]" => StackableUsePlace.Village,
        "[seria room]" => StackableUsePlace.SeriaRoom,
        "[dungeon]" => StackableUsePlace.Dungeon,
        "[pvp]" => StackableUsePlace.Pvp,
        "[tournament]" => StackableUsePlace.Tournament,
        _ => StackableUsePlace.None
    }));

    public static bool Allows(IReadOnlyList<string>? places, StackableUseContext current) => Allows(Parse(places), current);

    public static bool Allows(StackableUsePlaceFlags flags, StackableUseContext current)
    {
        if (flags == StackableUsePlaceFlags.None) return true;
        if ((flags & StackableUsePlaceFlags.DontOpen) != 0 ||
            current.IsPractice && (flags & StackableUsePlaceFlags.ExcludePractice) != 0 ||
            current.IsAssault && (flags & StackableUsePlaceFlags.ExcludeAssault) != 0)
            return false;
        var allowed = current.Place switch
        {
            StackableUsePlace.Village => StackableUsePlaceFlags.Village,
            StackableUsePlace.SeriaRoom => StackableUsePlaceFlags.SeriaRoom | StackableUsePlaceFlags.Village,
            StackableUsePlace.Dungeon => StackableUsePlaceFlags.Dungeon,
            StackableUsePlace.Pvp => StackableUsePlaceFlags.Pvp,
            // 竞技大赛频道单独要求 6，不将普通 PvP 许可当作大赛许可。
            StackableUsePlace.Tournament => StackableUsePlaceFlags.Tournament,
            _ => StackableUsePlaceFlags.None
        };
        return allowed != StackableUsePlaceFlags.None && (flags & (allowed | StackableUsePlaceFlags.All)) != 0;
    }
}
