using DfoServer.Game.Pvp;
using DfoServer.Game.Characters;

namespace DfoServer.Network.Builders;

internal static class PvpTrainingPackets
{
    // V31 ACK50 02357340：成功无附加正文，错误19走明确拒绝分支。
    public static byte[] Created() => [1];
    public static byte[] Rejected() => [0,19,0];

    // 015C6230/015C654B：两树count:u8及slot:u8/id:u16/level:u8；末尾SP0/SP1/TP0/TP1。
    // 015C57B5..57D3通过与普通技能相同的已核验SP/TP setter装回独立训练对象。
    public static byte[] Skills(CharacterLearningState state)
    {
        state.Validate();
        if(!state.Initialized || state.Trees.Any(tree=>tree.Skills.Count>255))
            throw new InvalidDataException("训练技能投影需要完整双树");
        var trees=state.Trees.OrderBy(tree=>tree.Context).ToArray();
        using var stream=new MemoryStream();using var writer=new BinaryWriter(stream);
        foreach(var tree in trees)
        {
            writer.Write((byte)tree.Skills.Count);
            foreach(var skill in tree.Skills.OrderBy(skill=>skill.Slot))
            {writer.Write(skill.Slot);writer.Write(skill.Id);writer.Write(skill.Level);}
        }
        writer.Write(trees[0].Sp);writer.Write(trees[1].Sp);writer.Write(trees[0].Tp);writer.Write(trees[1].Tp);
        return stream.ToArray();
    }

    // NOTI43 023A2520：room:u16/mode:u8/count:u8，每项seat:u8/state:u8/UID:u16。
    // 原席UID被FFFF替换时023A2A24调用setMyRoom(null)，不是把FE直接当座位状态。
    public static byte[] LeaveSeat(ushort roomId,byte mode) =>
        [(byte)roomId,(byte)(roomId>>8),mode,1,0,0,255,255];

    // V31 NOTI41 023AE2BF：非公平决斗分支；固定八席，preset不携带动态标题。
    // 原模型的附加字段显式传入，本Builder不凭默认值决定玩法状态。
    public static byte[] Room(PvpRoomSnapshot room)
    {
        if(room.RoomId==0 || room.TitlePreset>16 || room.LeaderSeat is < -1 or >7 || room.Seats.Length!=8)
            throw new InvalidDataException("训练房间编号、标题或席位表无效");
        using var stream=new MemoryStream(); using var writer=new BinaryWriter(stream);
        writer.Write((ushort)1);writer.Write(room.RoomId);writer.Write(room.TitlePreset);
        writer.Write(room.State);writer.Write(room.LeaderSeat);writer.Write(room.MapIndex);writer.Write(room.Mode);
        foreach(var seat in room.Seats) { writer.Write(seat.State);writer.Write(seat.CharacterId); }
        writer.Write(room.ExtraFlag);writer.Write(room.ExtraValue);
        return stream.ToArray();
    }
}
