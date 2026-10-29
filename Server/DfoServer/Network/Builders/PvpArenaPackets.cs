using System.Collections.Immutable;
using DfoServer.Game.Pvp;

namespace DfoServer.Network.Builders;

internal static class PvpArenaPackets
{
    // 普通房间的1/2与领域枚举分开；其它模式须由各自已核实的流程接入。
    public static byte Mode(PvpArenaMode mode) => mode switch
    {
        PvpArenaMode.Individual => 1,
        PvpArenaMode.Team => 2,
        _ => throw new InvalidDataException("该决斗模式尚无普通房间投影")
    };
    static byte Mode(PvpArenaRoomView room) => room.IsTotal ? (byte)8 : Mode(room.Mode);

    public static byte SeatState(PvpArenaRoomView room, PvpArenaMember member) =>
        member.Spectator ? (byte)3 : room.Mode == PvpArenaMode.Individual ? (byte)0 : (byte)(member.Team + 1);

    static ImmutableArray<PvpRoomSeat> Seats(PvpArenaRoomView room) => Enumerable.Range(0, 8).Select(index =>
        room.Members.FirstOrDefault(member => member.Seat == index) is { } member
            ? new PvpRoomSeat(SeatState(room, member), member.CharacterId)
            : new PvpRoomSeat(room.ClosedSeats.Contains((byte)index) ? (byte)254 : (byte)255, ushort.MaxValue)).ToImmutableArray();

    public static byte[] Room(PvpArenaRoomView room) => PvpTrainingPackets.Room(new(room.Id, room.TitlePreset,
        RoomState(room), room.Leader == byte.MaxValue ? (sbyte)-1 : (sbyte)room.Leader, room.Map, Mode(room), Seats(room), 0, 0)
        { Title = room.Title });

    // 023575A0成功分支按八席读准备标志和连接标志；大厅阶段尚未开始P2P，连接标志为0。
    public static byte[] Entered(PvpArenaRoomView room)
    {
        byte[] body = new byte[17];
        body[0] = 1;
        foreach (var member in room.Members) body[1 + member.Seat * 2] = member.Ready ? (byte)1 : (byte)0;
        return body;
    }

    public static byte[] Rejected(bool passwordRequired = false) => [0, passwordRequired ? (byte)4 : (byte)19, 0];

    // 0239B4F0：room/state/leader/map/mode/value；state0移除目录，state1保留等待房间。
    public static byte[] State(PvpArenaRoomView room)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(room.Id);
        writer.Write(RoomState(room));
        writer.Write(room.Leader);
        writer.Write(room.Map);
        writer.Write(Mode(room));
        writer.Write(0u);
        return stream.ToArray();
    }

    static byte RoomState(PvpArenaRoomView room) => room.Members.IsEmpty ? (byte)0 :
        room.MatchPhase is PvpArenaMatchPhase.Loading or PvpArenaMatchPhase.Fighting or PvpArenaMatchPhase.Selecting ? (byte)2 : (byte)1;

    // 023AE7F0只读两个u8；选择字段沿原地图顺序，不写地图脚本ID。
    public static byte[] Start(PvpArenaRoomView room) => [checked((byte)room.Map), Mode(room)];
    public static byte[] Loaded(ushort character) => [(byte)character, (byte)(character >> 8)];
    public static byte[] Death(byte victim, byte killer) => victim < 8 && (killer < 8 || killer == byte.MaxValue)
        ? [victim, killer] : throw new InvalidDataException("决斗死亡席位越界");

    // 023A2520：每席更新都携带UID；移动时一起清旧席，避免客户端保留重复成员。
    public static byte[] SeatList(PvpArenaRoomView room)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(room.Id);
        writer.Write(Mode(room));
        writer.Write((byte)8);
        var seats = Seats(room);
        for (byte index = 0; index < 8; index++)
        {
            writer.Write(index);
            writer.Write(seats[index].State);
            writer.Write(seats[index].CharacterId);
        }
        return stream.ToArray();
    }

    // 023A2A24 按原席UID撤销本人房间引用；退出只清本人的原席，不能用完整空房间目录代替。
    public static byte[] Left(PvpArenaRoomView room, byte seat) =>
        [(byte)room.Id, (byte)(room.Id >> 8), Mode(room), 1, seat, 0, 255, 255];

    // NOTI44的0239B830只读seat:u8/ready:u8，不附房间号。
    public static byte[] Ready(byte seat, bool ready)
    {
        if (seat > 7) throw new InvalidDataException("决斗准备席位越界");
        return [seat, ready ? (byte)1 : (byte)0];
    }
}
