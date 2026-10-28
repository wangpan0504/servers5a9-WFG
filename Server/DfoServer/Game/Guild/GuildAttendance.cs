using DfoServer.Game.Accounts;
using DfoServer.Game.Session;

namespace DfoServer.Game.Guild;

internal static class GuildAttendanceDay
{
    // UTC+8 的06:00为公会业务日边界；返回对应日开始的Unix秒。
    public static long Start(long unix)
    {
        long shifted = checked(unix + 2 * 3600);
        long days = Math.DivRem(shifted, 86400, out long remainder);
        if (remainder < 0) days--;
        return checked(days * 86400 - 2 * 3600);
    }
}

internal sealed record GuildAttendanceRow(ushort CharacterId, string Name, ushort Level, byte Job, byte GrowType,
    byte Grade, long ObservedAtUnix);
internal sealed record GuildAttendanceView(uint GuildId, long BusinessDayUnix, GuildAttendanceRow[] Members);
internal sealed record GuildAttendanceFailure(ushort CharacterId, long ObservedAtUnix, Exception Error);

internal interface IGuildAttendanceStore
{
    bool ObserveGuildAttendance(AccountOwners.Lease owner, ushort characterId, long observedAtUnix);
    GuildAttendanceView? ReadGuildAttendance(AccountOwners.Lease owner, ushort characterId, long nowUnix);
}

internal sealed class GuildAttendanceService(IGuildAttendanceStore store, TimeProvider? clock = null)
{
    readonly TimeProvider time = clock ?? TimeProvider.System;
    public GuildAttendanceView? Read(GuildManagementIdentity actor) => actor.IsCurrent()
        ? store.ReadGuildAttendance(actor.Owner, actor.CharacterId, time.GetUtcNow().ToUnixTimeSeconds()) : null;
    public GuildManagementIdentity? Capture(GameSession session) => session.CaptureGuildManagement(requireTown: false);
}
