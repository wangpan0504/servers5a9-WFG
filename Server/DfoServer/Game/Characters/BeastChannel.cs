using DfoServer.GameWorld.Channels;
using DfoServer.GameWorld.Towns;

namespace DfoServer.Game.Characters;

// 改版魔兽频道独立身份与任务准入规则；城镇移动继续复用现有会话及TownTravel。
internal static class BeastChannel
{
    internal const int Type = 40;
    internal const string Group = "[beast]";
    internal const int Town = 51;
    internal const int EntryQuest = 5927;
    internal const ushort OpenEvent = 333;
    internal static bool IsEntry(ChannelEntry? channel) => channel is not null && IsEntry(channel.Type, channel.Group);
    internal static bool IsEntry(int type, string group) => type == Type && group == Group;
    // 频道专属地区不作为普通房间返回点；修复旧实验已经写入51号城镇的返回值。
    internal static Location OrdinaryReturn(TownCatalog towns, Character character, IReadOnlySet<int> completed)
    {
        if (character.Return is { } saved && saved.Town != Town)
            return towns.ResolveRoomReturn(towns.Room, saved, character.Level, completed);
        return towns.ResolveRoomReturn(towns.Room, towns.InitialReturn, character.Level, completed);
    }

    internal static bool CanProjectEntry(bool available, byte level, bool pvpPrivate, bool questCompleted) => questCompleted;
    internal static ushort[] EntryEvents(bool available, IReadOnlyList<ushort> existing) =>
        existing.Append(OpenEvent).Distinct().ToArray();
}
