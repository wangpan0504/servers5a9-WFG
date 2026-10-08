using System.Buffers.Binary;
using DfoServer.Game.Characters;
using DfoServer.GameWorld.Towns;
using DfoServer.Network.Builders;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class FatigueStateTests
{
    public static void RunCore()
    {
        int before = Count;
        // 刻意非互补，证明协议不把remaining误算为Maximum-Used，也不误读ACK4专属辅助为NOTI36字段。
        var state = new CharacterFatigueState(12, 156, 47, 90, 59, 63);
        byte[] packet = FatiguePackets.Current(state);
        That(packet.AsSpan().SequenceEqual(Convert.FromHexString("0C009C002F005A003B00")),
            "疲劳NOTI36独立固定样本完整五u16原序及10B宽度");
        var character = new Character(1, 0, "Fatigue", 7, new Location(38, 1, 450, 234, 0), null) { Fatigue = state };
        byte[] select = TownPackets.Select(character);
        That(U16(select, 11) == 12 && U16(select, 13) == 156 && U16(select, 15) == 47 &&
            U16(select, 226) == 63 && U16(select, 228) == 90 && U16(select, 231) == 59,
            "ACK4耗用/上限/usedMaximum及尾部三个不同owner来自同一角色快照");
        var consumed = state.ConsumeOrdinaryRoom();
        That(consumed == new CharacterFatigueState(13, 156, 47, 89, 59, 63),
            "本服首次房扣一点仅修改耗用和剩余，不覆盖三个附属owner");
        That(new CharacterFatigueState(1, 1, 11, 0, 13, 17).ConsumeOrdinaryRoom() ==
            new CharacterFatigueState(1, 1, 11, 0, 13, 17), "已有合法实例耗尽后按原码边界钳制，不发生ushort回绕");
        Reject<InvalidDataException>(() => new CharacterFatigueState(1, 0, 0, 0, 0, 0).Validate(), "疲劳上限不能为0");
        Reject<InvalidDataException>(() => new CharacterFatigueState(157, 156, 0, 0, 0, 0).Validate(), "非法耗用上限拒绝");
        Reject<InvalidDataException>(() => new CharacterFatigueState(0, 156, 0, 157, 0, 0).Validate(), "非法剩余上限拒绝");
        Console.WriteLine($"PASS {Count - before} 疲劳原序owner、选角/通知及本服边界断言");
    }

    static ushort U16(byte[] body, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(offset));

    static void Reject<T>(Action action, string name) where T : Exception
    {
        try { action(); }
        catch (T) { That(true, name); return; }
        throw new InvalidOperationException("FAIL expected " + typeof(T).Name + ": " + name);
    }
}
