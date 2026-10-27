using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class EvildomWarPacketTests
{
    public static void RunCore()
    {
        int before = Count;
        That(EvildomWarRequests.IsStart([]) && !EvildomWarRequests.IsStart([0]) &&
            !EvildomWarRequests.IsStart([1, 0, 0, 0]), "1652空正文，不能接受伪造模式或填充");
        Bytes([1], EvildomWarPackets.Start(true), "1652成功无附加字段");
        Bytes([0, 0, 0], EvildomWarPackets.Start(false), "1652失败含公共u16，专用消费者忽略其值并统一显示68129");
        Bytes(Convert.FromHexString("0201B80B000002000000321500000002002E150000010003"),
            EvildomWarPackets.Info(1, 3000, [new(5426, 0, 2, 0), new(5422, 1, 0, 3)]),
            "1671按真实读宽投影状态、模式、秒数、记录数、逐图三字节，不混用拼图NOTI1652");
        Console.WriteLine($"PASS evildom war packets {Count - before}");
    }
}
