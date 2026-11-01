using System.Buffers.Binary;
using DfoServer.Game.PrivilegeServices;

namespace DfoServer.Network.Builders;

internal static class PrivilegeServicePackets
{
    // 公共派发先消费success及失败error:u16，00978C80随后仍固定读取私有i32。
    public static byte[] RewardCommitted() => [1, 0, 0, 0, 0];
    public static byte[] RewardCapacityRejected() => [0, 0, 0, 3, 0, 0, 0];

    public static byte[] Information(PrivilegeServiceState state, DateTimeOffset now)
    {
        state.Validate();
        bool active = state.IsActive(now.ToUnixTimeSeconds());
        var body = new byte[21];
        BinaryPrimitives.WriteInt32LittleEndian(body, state.ExpiresAt == 0 ? 2 : active ? 0 : 1);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), active ? checked((uint)state.ExpiresAt) : 0);
        body[8] = state.DailyClaimDay == PrivilegeServicePolicy.Day(now) ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(9), state.LoginDays);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(13), state.ClaimedMilestones);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(17), state.RewardSeason);
        return body;
    }

    public static byte[] Roster(PrivilegeServiceSnapshot snapshot)
    {
        var body = new byte[62 * 8];
        for (int slot = 0; slot < 62; slot++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(slot * 8), -1);
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(slot * 8 + 4), 2);
        }
        foreach (var row in snapshot.Roster)
        {
            if (row.Slot >= 62 || row.ExpiresAt < 0 || row.ExpiresAt > uint.MaxValue)
                throw new InvalidDataException("礼遇购买名册超出原生范围");
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(row.Slot * 8), row.Slot);
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(row.Slot * 8 + 4), row.ExpiresAt == 0 ? 2 :
                row.ExpiresAt > snapshot.At.ToUnixTimeSeconds() ? 0 : 1);
        }
        return body;
    }
}
