using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class ExpertJobProtocolTests
{
    public static void Run()
    {
        int checks = Count;
        That(ExpertJobPackets.Fail(10) is [0, 10, 0] && ExpertJobPackets.Fail(19) is [0, 19, 0],
            "ACK 239/238 失败是 success:u8 + error:u16");
        That(ExpertJobPackets.GiveUpSuccess() is [1, 0, 0, 0, 0, 0, 0],
            "ACK 239 成功是 success + u32 + u8 + u8");
        That(ExpertJobPackets.GiveUpSuccess(1000, 1) is [1, 0xE8, 0x03, 0, 0, 1, 0],
            "ACK 239 重载写入实际金币与放弃档");
        That(ExpertJobPackets.CompoundEmpty() is [1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
            "ACK 238 count=0 仍带两个 u32 和一个 u8");
        That(ExpertJobPackets.EnchantStoreSuccess() is [1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
            "ACK 601 成功是 flag:u8 + u32 + u8 + 三个 u32");
        That(ExpertJobPackets.DisjointEmpty() is [1, 0, 0, 0, 0, 0, 0] && ExpertJobPackets.CreateStoreSuccess() is [1],
            "ACK 235 空产物与 ACK 598 无附加正文");
        That(ExpertJobPackets.CloseStoreNotice(0x1234) is [0x34, 0x12], "NOTI 539 只有 actor:u16");
        That(ExpertJobPackets.CreateStoreNotice(0, 0x1234) is
                [0, 0x34, 0x12, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
            "NOTI 538 mode0 空名是 u8+u16+u32长度0 再加 u8+u32+u16+u16+u32+u8");
        That(ExpertJobPackets.CreateStoreNotice(3, 1, "", [9, 8]) is
                [3, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 9, 8],
            "NOTI 538 mode3 在固定尾后再跟 count:u8 和字节");
        That(ExpertJobPackets.UpdateStoreNotice(0, 7) is [0, 7, 0, 0, 0, 0, 0],
            "NOTI 544 非 3 是 u8+u16+空字符串，没有附加列表");
        That(ExpertJobPackets.Info(3, [], 1, 300) is [0, 3, 1, 0, 0, 0, 44, 1, 0, 0],
            "NOTI 205 分解师是 10 字节 state+mode3+grade+endurance");
        That(ExpertJobPackets.Info(2, [0x1110, 0x1116]) is [0, 2, 2, 0x10, 0x11, 0, 0, 0x16, 0x11, 0, 0],
            "NOTI 205 炼金是 3+4n");
        That(ExpertJobPackets.Info(1, [], 1, 0) is [0, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0],
            "NOTI 205 附魔师即使 m=0 也带资格表和两个 u32");
        var extraction = ExpertJobRequests.ReadExtraction([2, 3, 0, 0, 4, 0]);
        That(extraction is { ExtractorType: 2, ExtractorSlot: 3, TargetList: 0, TargetSlot: 4 },
            "C2S 416 是 u8+i16+u8+i16");
        That(ExpertJobRequests.ReadEnterStore([0x34, 0x12]) == 0x1234, "C2S 599 是 actor:u16");
        That(BitConverter.ToUInt16(ExpertJobPackets.CreateStoreNotice(0, 42), 1) == 42,
            "NOTI 538 演员号是 character.Id");
        ExpertJobRequests.ReadEmpty([]);
        var compound = ExpertJobRequests.ReadCompound([1, 2, 3, 4, 5, 0, 6, 0]);
        That(compound is { First: 0x04030201, Second: 5, Third: 6 }, "C2S 238 是 u32+u16+u16");
        var enchant = ExpertJobRequests.ReadEnchantStore([1, 0, 2, 0, 0, 0, 2, 0, 3, 0, 0, 4, 0]);
        That(enchant is { First: 1, Second: 2, Third: 2, Fourth: 0, Fifth: 3, Sixth: 0, Seventh: 4 },
            "C2S 601 是 u16+u32+u8+u8+u16+u8+u16");
        var disjoint = ExpertJobRequests.ReadDisjoint([1, 0, 2, 0, 3]);
        That(disjoint is { First: 1, Second: 2, Third: 3 }, "C2S 235 是 u16+u16+u8");
        var store = ExpertJobRequests.ReadCreateStore([0, 0, 0, 0, 0, 7, 0, 0, 0, 8, 0, 9, 0, 10, 0]);
        That(store is { Mode: 0, Name: "", First: 7, Second: 8, Third: 9, Fourth: 10 },
            "C2S 598 是 mode:u8 + 03BDF850 字符串 + u32 + 三个 u16");
        Reject<InvalidDataException>(() => ExpertJobRequests.ReadEmpty([1]), "C2S 239/600 拒绝多余字节");
        Reject<InvalidDataException>(() => ExpertJobRequests.ReadCreateStore([1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]),
            "C2S 598 模式只接受 0 或 3");
        Console.WriteLine($"PASS {Count - checks} 副职业 C2S/ACK 闭合包体断言");
    }
}
