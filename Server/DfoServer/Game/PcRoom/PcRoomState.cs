using DfoServer.Game.Accounts;

namespace DfoServer.Game.PcRoom;

internal readonly record struct PcRoomClaimState(bool LoginClaimed, bool RentalClaimed);
internal sealed record PcRoomDailyClaim(int Day, bool Rental);

internal interface IPcRoomStore
{
    PcRoomClaimState ReadPcRoomClaims(AccountOwners.Lease owner, ushort characterId, int day);
}

internal sealed class PcRoomAlreadyClaimedException() : InvalidOperationException("网吧奖励已领取");

internal static class PcRoomDay
{
    // UTC+8 06:00，账号登录奖与角色租赁礼盒共用业务日。
    public static int At(DateTimeOffset now) => checked((int)((now.ToUnixTimeSeconds() + 7200) / 86400));
    public static long Next(DateTimeOffset now) => checked(((long)At(now) + 1) * 86400 - 7200);
}
