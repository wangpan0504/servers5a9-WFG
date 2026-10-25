using DfoServer.Game.Pvp;
using DfoServer.Game.Characters;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class PvpTrainingWireTests
{
    public static void Run()
    {
        int before=Count;
        // 固定字节来自各写包调用的宽度，不调用生产编码函数生成预期。
        byte[] combo=[0,2,0,0,0,0xD1,0xB5,0,0,0,1,0];
        That(PvpTrainingRequests.Create(combo) is {Title:"训",MapIndex:0,Setting:0,Kind:1,Mode:0},"CMD50连招入口完整正文");
        byte[] arcade=[0,2,0,0,0,0xD1,0xB5,0,0,0,3,83];
        That(PvpTrainingRequests.Create(arcade) is {Kind:3,Mode:83},"CMD50街机保留教学82加一，不误当难度83");
        Reject<InvalidDataException>(()=>PvpTrainingRequests.Create(combo[..^1]),"建房截断拒绝");
        Reject<InvalidDataException>(()=>PvpTrainingRequests.Create([..combo,0]),"建房未知尾部拒绝");
        Reject<InvalidDataException>(()=>PvpTrainingRequests.Create([0,255,255,255,255,0,0,0,0,0]),"建房长度溢出拒绝");
        Reject<InvalidDataException>(()=>PvpTrainingRequests.Create([0,1,0,0,0,0x81,0,0,0,1,0]),"建房截断CP936拒绝");
        Reject<InvalidDataException>(()=>PvpTrainingRequests.Create([0,1,0,0,0,0,0,0,0,1,0]),"建房NUL拒绝");
        That(PvpTrainingRequests.Seat([0,254]) is {Seat:0,State:254},"原生连招退出52双字节合同");
        Reject<InvalidDataException>(()=>PvpTrainingRequests.Seat([0]),"席位请求截断拒绝");
        Reject<InvalidDataException>(()=>PvpTrainingRequests.Seat([0,254,0]),"席位请求多余字节拒绝");
        Reject<InvalidDataException>(()=>PvpTrainingRequests.Seat([8,254]),"未知席位拒绝");
        Bytes(PvpTrainingPackets.LeaveSeat(0x1234,6),[0x34,0x12,6,1,0,0,255,255],"NOTI43原生退出独立金样，模式6单席清空");
        var room=new PvpRoomSnapshot(0x1234,0,2,-1,0x5678,10,
            [new(1,0x2345),new(0,0),new(0,0),new(0,0),new(0,0),new(0,0),new(0,0),new(0,0)],0,0x12345678);
        byte[] expected=[1,0,0x34,0x12,0,2,255,0x78,0x56,10,
            1,0x45,0x23,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,
            0,0x78,0x56,0x34,0x12];
        That(PvpTrainingPackets.Room(room).SequenceEqual(expected),"NOTI41独立39字节金样验证固定八席与尾字段");
        Reject<InvalidDataException>(()=>PvpTrainingPackets.Room(room with {Seats=[]}),"缺席位拒绝");
        Reject<InvalidDataException>(()=>PvpTrainingPackets.Room(room with {TitlePreset=17}),"客户端越界标题拒绝");
        Reject<InvalidDataException>(()=>PvpTrainingPackets.Room(room with {LeaderSeat=8}),"队长索引不得超出八席");
        That(PvpTrainingPackets.Created().SequenceEqual(new byte[]{1}) &&
            PvpTrainingPackets.Rejected().SequenceEqual(new byte[]{0,19,0}),"ACK50成功无附加字段、失败19");
        var learning=new CharacterLearningState(0,[new(0,0x1111,0x3333,[new(2,0x1234,5)]),new(1,0x2222,0x4444,[new(3,0x5678,6)])]);
        Bytes(PvpTrainingPackets.Skills(learning),[1,2,0x34,0x12,5,1,3,0x78,0x56,6,0x11,0x11,0x22,0x22,0x33,0x33,0x44,0x44],
            "NOTI1234独立两树金样：原槽、ID、等级，SP0/SP1/TP0/TP1，不夹普通19额外指令");
        Reject<InvalidDataException>(()=>PvpTrainingPackets.Skills(CharacterLearningState.Uninitialized),"不将未初始化普通树伪造成训练技能");
        Console.WriteLine($"PASS {Count-before} 决斗训练建房协议边界断言（未代表正式玩法接线）");
    }
}
