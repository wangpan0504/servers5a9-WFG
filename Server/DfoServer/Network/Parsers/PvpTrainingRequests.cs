using System.Buffers.Binary;
using System.Text;
using DfoServer.Game.Characters;
using DfoServer.Game.Pvp;

namespace DfoServer.Network.Parsers;

internal static class PvpTrainingRequests
{
    // V31 015C1C12/015D94D5：u8(0)，字节长u32+CP936标题，u16，u8×3。
    // 连招入口尾三项为0/1/0；街机为0/3/(原模式index+1)，不能把kind直接当NOTI41模式。
    public static PvpRoomRequest Create(ReadOnlySpan<byte> body)
    {
        if(body.Length<10 || body[0]!=0) throw new InvalidDataException("训练建房请求头无效");
        uint length=BinaryPrimitives.ReadUInt32LittleEndian(body[1..]);
        if(length is 0 or >4096 || body.Length!=10+(int)length)
            throw new InvalidDataException("训练建房标题或尾部长度无效");
        string title;
        try { title=CharacterName.Encoding.GetString(body.Slice(5,(int)length)); }
        catch(DecoderFallbackException error) { throw new InvalidDataException("训练建房标题不是有效CP936",error); }
        if(string.IsNullOrWhiteSpace(title) || title.Any(char.IsControl))
            throw new InvalidDataException("训练建房标题包含控制字符");
        int at=5+(int)length;
        return new(title,BinaryPrimitives.ReadUInt16LittleEndian(body[at..]),body[at+2],body[at+3],body[at+4]);
    }
    // ExitPractice→03BD1A90→02E3E690：CMD52仅两个u8，原本人席位和目标状态。
    public static PvpRoomSeatChange Seat(ReadOnlySpan<byte> body)
    {
        if(body.Length!=2 || body[0]>7) throw new InvalidDataException("训练席位变更长度或席位越界");
        return new(body[0],body[1]);
    }
    // 015D7C60：0=加载完成、1=回合结算；006725F0/03B85438及015D7F19核准调用参数。
    public static PvpArcadeRoundReport Round(ReadOnlySpan<byte> body)
    {
        if(body.Length!=19 || body[0]>1 || body[5]==0 || body[6]==0 || body[6]>body[5] ||
            body[12]>1 || body[0]==0 && body[12]!=0)
            throw new InvalidDataException("街机回合报告长度或阶段字段无效");
        uint opponent=BinaryPrimitives.ReadUInt32LittleEndian(body[1..]);
        if(opponent is 0 or >int.MaxValue) throw new InvalidDataException("街机对手编号无效");
        return new(body[0],(int)opponent,body[5],body[6],BinaryPrimitives.ReadUInt32LittleEndian(body[7..]),
            body[11],body[12]!=0,BinaryPrimitives.ReadUInt16LittleEndian(body[13..]),BinaryPrimitives.ReadUInt32LittleEndian(body[15..]));
    }
}
