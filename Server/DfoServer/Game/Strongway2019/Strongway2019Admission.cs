using DfoServer.Game.Characters;
using DfoServer.Game.Events;
using DfoServer.GameWorld.Channels;
using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Game.Strongway2019;

internal enum Strongway2019Mode { Simple, Hero, Matching }

// AntiEvil只接受服务端穿戴资源计算结果；协议请求中不存在可授予准入的抗魔字段。
internal sealed record Strongway2019Admission(Character Character, Strongway2019Mode Mode,
    uint DungeonId, int MemberCount, int AntiEvil, ServerEventSchedule Schedule);

internal static class Strongway2019EntryPolicy
{
    public static bool Contains(uint dungeonId) => dungeonId is 5359 or 5360 or 5363 or 5364;

    public static Strongway2019Admission? Prepare(Strongway2019Catalog catalog, Character character,
        ChannelEntry? channel, uint dungeonId, int memberCount, int serverAntiEvil,
        ServerEventSchedule schedule, DateTimeOffset now, bool matched = false)
    {
        if (channel?.Type != 55 || character.Level != 95 || character.IsPvpPrivate ||
            character.Position is not { Town: 65, Area: 1 } || schedule.EventId != ServerEventService.Strongway2019Id ||
            !schedule.IsActive(now)) return null;
        schedule.Validate();
        Strongway2019Mode mode;
        int key;
        if (dungeonId == 5359 && !matched && memberCount is >= 1 and <= 4 && serverAntiEvil >= 4000)
            (mode, key) = (Strongway2019Mode.Simple, 0);
        else if (dungeonId == 5363 && !matched && memberCount == 4 && serverAntiEvil >= 5400)
            (mode, key) = (Strongway2019Mode.Hero, 2);
        else if (dungeonId == 5360 && matched && memberCount == 4 && serverAntiEvil >= 5400)
            (mode, key) = (Strongway2019Mode.Matching, 0);
        else return null;
        var definitions = matched ? catalog.MatchingDungeons : catalog.NormalDungeons;
        if (!definitions.Any(row => row.Key == key && row.DungeonId == dungeonId)) return null;
        return new(character, mode, dungeonId, memberCount, serverAntiEvil, schedule);
    }
}
