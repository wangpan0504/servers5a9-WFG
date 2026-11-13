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
    public static bool AllowsMemberCount(int count) => count is >= 1 and <= 4;

    public static Strongway2019Admission? Prepare(Strongway2019Catalog catalog, Character character,
        ChannelEntry? channel, uint dungeonId, int memberCount, int serverAntiEvil,
        ServerEventSchedule schedule, DateTimeOffset now, bool matched = false)
        => Prepare(catalog, character, channel, dungeonId, memberCount, serverAntiEvil, schedule, now, out _, matched);

    public static Strongway2019Admission? Prepare(Strongway2019Catalog catalog, Character character,
        ChannelEntry? channel, uint dungeonId, int memberCount, int serverAntiEvil,
        ServerEventSchedule schedule, DateTimeOffset now, out string? rejection, bool matched = false)
    {
        rejection = !AllowsMemberCount(memberCount) ? "strongway2019-member-count-required" :
            channel?.Type != 55 ? "strongway2019-channel-required" :
            character.Level != 95 ? "strongway2019-level-required" :
            character.IsPvpPrivate ? "strongway2019-pvp-private" :
            character.Position is not { Town: 65, Area: 1 } ? "strongway2019-town-required" :
            schedule.EventId != ServerEventService.Strongway2019Id || !schedule.IsActive(now)
                ? "strongway2019-activity-inactive" : null;
        if (rejection is not null) return null;
        schedule.Validate();
        Strongway2019Mode mode;
        int key, required;
        if (dungeonId == 5359 && !matched)
            (mode, key, required) = (Strongway2019Mode.Simple, 0, 4000);
        else if (dungeonId == 5363 && !matched)
            (mode, key, required) = (Strongway2019Mode.Hero, 2, 4000);
        else if (dungeonId == 5360 && matched)
            (mode, key, required) = (Strongway2019Mode.Matching, 0, 5400);
        else { rejection = "strongway2019-mode-unavailable"; return null; }
        if (serverAntiEvil < required)
        {
            rejection = FormattableString.Invariant($"strongway2019-anti-evil-required:{required}:{serverAntiEvil}");
            return null;
        }
        var definitions = matched ? catalog.MatchingDungeons : catalog.NormalDungeons;
        if (!definitions.Any(row => row.Key == key && row.DungeonId == dungeonId))
        { rejection = "strongway2019-resource-unavailable"; return null; }
        return new(character, mode, dungeonId, memberCount, serverAntiEvil, schedule);
    }
}
