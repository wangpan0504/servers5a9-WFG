using System.Collections.Immutable;

namespace DfoServer.Game.Pvp;

// 与资源模式index不同：这里保留CMD50原始字段，正式入口还须验证任务、场景及实例资格。
internal sealed record PvpRoomRequest(string Title, ushort MapIndex, byte Setting, byte Kind, byte Mode)
{
    public byte TitlePreset { get; init; }
    public string Password { get; init; } = "";
}
internal readonly record struct PvpRoomSeatChange(byte Seat,byte State);
internal sealed record PvpRoomEnterRequest(ushort Room, string? Password);
internal readonly record struct PvpRoomSeat(byte State, ushort CharacterId);
internal sealed record PvpRoomSnapshot(ushort RoomId, byte TitlePreset, byte State, sbyte LeaderSeat,
    ushort MapIndex, byte Mode, ImmutableArray<PvpRoomSeat> Seats, byte ExtraFlag, uint ExtraValue)
{
    public string Title { get; init; } = "";
}
