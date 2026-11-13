using System.Buffers.Binary;
using DfoServer.GameWorld.Raids;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static partial class AntonRaidGapTcpTests
{
    static void VerifyConstructorLast(TcpPeer peer, RaidDungeonCatalog catalog, AntonTcpProbe.AntonProbeReport report, LukeRaidClockTests.Clock clock, int before)
    {
        clock.Advance(180);
        var opened = AntonTcpProbe.Barrier(peer, "180秒正式开放孵化场");
        int infected = opened.Where(packet => packet is { Kind: 0, Opcode: 583 })
            .Select(packet => ConstructorSymbol(packet.Body, 8)).Last(value => value is >= 221 and <= 224);
        var resource = AntonConstructorDefinition.All.Single(row => row.Dungeon == infected);
        That(opened.Any(packet => packet is { Kind: 0, Opcode: 586, Body.Length: 6 } &&
            BinaryPrimitives.ReadInt32LittleEndian(packet.Body) == infected && packet.Body[4] == 0 && packet.Body[5] == 0),
            "自然180秒后感染身份对应正式已开放DGN");
        byte[] energy = ConstructorLast(110, 1000, 2), infection = ConstructorLast(110, 500, 2);
        byte[] signal = ConstructorLast(resource.Signal, 1, 0);
        peer.Send(662, energy);
        That(AntonTcpProbe.Barrier(peer, "候战室Constructor Last不能扣能量").All(packet => packet.Opcode != 583),
            "候战室伪报1000缺少正式Run与死亡，不发布变化");
        int symbolsBefore = report.Symbols.Count;
        int earlySignals = 0;
        AntonTcpProbe.Enter(peer, catalog, infected, report, clear: true, leave: false, beforeRoomClear: map =>
        {
            if (BinaryPrimitives.ReadInt32LittleEndian(map.AsSpan(14)) != resource.Map) return;
            peer.Send(662, signal);
            That(AntonTcpProbe.Barrier(peer, "Constructor死亡前独立火山信号").All(packet => packet.Opcode != 583),
                "真实房间已加载但Constructor尚存活，Last只能等待不能生效");
            earlySignals++;
        });
        That(report.Maps.Any(map => BinaryPrimitives.ReadInt32LittleEndian(map.AsSpan(14)) == resource.Map),
            "15/16/37/45/39/117自然进入并清理真实Constructor所在MAP");
        That(report.Symbols.Skip(symbolsBefore).Select(body => ConstructorSymbol(body, 110)).Where(value => value >= 0).All(value => value == before),
            "39和117真实死亡清图不会代替Constructor Last自动扣1000或500");
        That(earlySignals == 1 && report.Symbols.Skip(symbolsBefore).Any(body => ConstructorSymbol(body, resource.Signal) == 1),
            "先到Last跨Boss死亡业务Epoch后配对，20000至20003信号只在死亡提交后生效");
        peer.Send(662, energy);
        var applied = AntonTcpProbe.Barrier(peer, "Constructor普通1000 Last");
        That(applied.Any(packet => packet is { Kind: 0, Opcode: 583 } && ConstructorSymbol(packet.Body, 110) == before - 1000),
            "同一正式Run已提交死亡与Last配对后屏障扣1000");
        peer.Send(662, infection);
        var infectedApplied = AntonTcpProbe.Barrier(peer, "感染Constructor额外500 Last");
        That(infectedApplied.Any(packet => packet is { Kind: 0, Opcode: 583 } && ConstructorSymbol(packet.Body, 110) == before - 1500),
            "死亡时冻结的感染身份允许独立扣500");
        peer.Send(662, ConstructorLast(resource.Signal == 20000 ? 20001 : 20000, 1, 0));
        That(AntonTcpProbe.Barrier(peer, "错图Constructor火山信号").All(packet => packet.Opcode != 583),
            "当前Constructor不能写其它孵化场的火山信号");
        peer.Send(662, signal);
        That(AntonTcpProbe.Barrier(peer, "Constructor已配对火山信号重放").All(packet => packet.Opcode != 583),
            "死亡时已配对的独立火山信号不因死后再次上报而重复发布");
        peer.Send(662, energy); peer.Send(662, infection); peer.Send(662, signal);
        That(AntonTcpProbe.Barrier(peer, "Constructor三个Last重放").All(packet => packet.Opcode != 583),
            "重复1000、500与火山信号不重复提交或广播");
        peer.Send(42); AntonTcpProbe.Barrier(peer, "Constructor代表路径回候战室");
        peer.Send(662, energy); peer.Send(662, infection); peer.Send(662, signal);
        That(AntonTcpProbe.Barrier(peer, "已释放Constructor Run旧Last").All(packet => packet.Opcode != 583),
            "释放Run后旧Last不能作用后续副本");
    }

    static byte[] ConstructorLast(int symbol, int value, byte operation)
    {
        byte[] body = new byte[9]; BinaryPrimitives.WriteInt32LittleEndian(body, symbol);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(4), value); body[8] = operation; return body;
    }
    static int ConstructorSymbol(byte[] body, int symbol)
    {
        for (int row = 0; row < body[0]; row++)
            if (BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(1 + row * 8)) == symbol)
                return BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(5 + row * 8));
        return -1;
    }
}
