using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Game.Inventory.Models;
using System.Buffers.Binary;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class ExpertJobProtocolTests
{
    public static void Run()
    {
        int checks = Count;
        foreach (byte type in new byte[] { 1, 2, 3, 4 })
            That(QuestPackets.Finish(2710, 0, 0, [], 0, [], expertJobType: type) is
                [1, 150, 10, 0, 0, 0, 0, 0, 0, 20, var actual, 0, 0] && actual == type,
                "授职ACK34使用mode20、真实类型及两页count，不冒充零物品奖励");
        Reject<InvalidDataException>(() => QuestPackets.Finish(2710, 0, 0, [], 0, [], expertJobType: 5),
            "授职拒绝越界类型");
        Reject<InvalidDataException>(() => QuestPackets.Finish(2710, 0, 0, [], 0, [], growType: 1, expertJobType: 3),
            "主职业与副职业奖励不可混写同一mode");
        That(QuestPackets.Finish(10021, 17, 0, [], 0, []) is [1, 37, 39, 17, 0, 0, 0, 0, 0, 0],
            "分解师末步的self disjoint类型17走普通奖励头，不被writer白名单拒绝");
        That(ExpertJobPackets.Fail(10) is [0, 10, 0] && ExpertJobPackets.Fail(19) is [0, 19, 0],
            "ACK 239/238 失败是 success:u8 + error:u16");
        That(ExpertJobPackets.GiveUpSuccess() is [1, 0, 0, 0, 0, 0, 0],
            "ACK 239 成功是 success + u32 + u8 + u8");
        That(ExpertJobPackets.GiveUpSuccess(1000, 1) is [1, 0xE8, 0x03, 0, 0, 1, 0],
            "ACK 239 重载写入实际金币与放弃档");
        That(ExpertJobPackets.CompoundEmpty() is [1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
            "ACK 238 count=0 仍带两个 u32 和一个 u8");
        That(ExpertJobPackets.EnchantStoreSuccess(299, 5, 1) is [1, 1, 0, 0, 0, 0, 0, 43, 1, 0, 0, 5, 0, 0, 0, 1, 0, 0, 0],
            "ACK601第一个尾u32必须为真实耐久，不得用0覆盖205");
        That(ExpertJobPackets.CreateStoreSuccess() is [1], "ACK 598 无附加正文");
        That(ExpertJobPackets.EnterStoreKind0(2, 257, 299) is [1, 0, 2, 1, 1, 0, 0, 43, 1, 0, 0],
            "ACK599分解店先写success1，再写kind0及等级费用耐久，不能把kind0当失败标志");
        That(ExpertJobPackets.EnterStoreKind3(0x1234, 299) is [1, 3, 0x34, 0x12, 43, 1, 0, 0],
            "ACK599附魔店同样保留公共success1，kind3不得被吞作success");
        That(ExpertJobPackets.CloseStoreNotice(0x1234) is [0x34, 0x12], "NOTI 539 只有 actor:u16");
        That(ExpertJobPackets.CreateStoreNotice(0, 0x1234) is
                [0, 0x34, 0x12, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
            "NOTI 538 mode0 空名是 u8+u16+u32长度0 再加 u8+u32+u16+u16+u32+u8");
        That(ExpertJobPackets.CreateStoreNotice(3, 1, "", [9, 8]) is
                [3, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 9, 8],
            "NOTI 538 mode3 在固定尾后再跟 count:u8 和字节");
        var storeNotice = ExpertJobPackets.CreateStoreNotice(new DfoServer.Game.ExpertJob.ExpertJobStoreRecord(
            0x1234, 3, 0, "not-a-shop-name", 257, 0x102, 0x304, 9, 2, 299) { Town = 38, Area = 1 });
        That(storeNotice is [0, 0x34, 0x12, 1, 0, 0, 0, 2, 38, 1, 0, 0, 0, 2, 1, 4, 3, 1, 1, 0, 0, 0],
            "538可信等级原始字节、Town/Area、坐标和服务费逐字段匹配消费者，不回显名称或错抄598字段");
        That(ExpertJobPackets.UpdateStoreNotice(0, 7) is [0, 7, 0, 0, 0, 0, 0],
            "NOTI 544 非 3 是 u8+u16+空字符串，没有附加列表");
        That(ExpertJobPackets.Info(3, [], 1, 300) is [0, 3, 1, 0, 0, 0, 44, 1, 0, 0],
            "NOTI 205 分解师是 10 字节 state+mode3+grade+endurance");
        That(ExpertJobPackets.Info(2, [0x1110, 0x1116]) is [0, 2, 2, 0x10, 0x11, 0, 0, 0x16, 0x11, 0, 0],
            "NOTI 205 炼金是 3+4n");
        That(ExpertJobPackets.Info(1, [], 1, 0) is [0, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0],
            "NOTI 205 附魔师即使 m=0 也带资格表和两个 u32");
        That(ExpertJobPackets.Info(1, [10015129], 1, 300, [0], 2) is
            [2, 1, 1, 153, 209, 152, 0, 1, 0, 1, 0, 0, 0, 44, 1, 0, 0],
            "205完整投影已学设计图、稀有度资格、放弃档和耐久");
        Reject<InvalidDataException>(() => ExpertJobPackets.Info(1, Enumerable.Range(1, 256).ToArray()),
            "205不得静默截断已学列表");
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
        That(disjoint is { OwnerActor: 1, SourceSlot: 2, SourceList: 3 }, "C2S 235 是店主演员、源槽和源列表");
        Reject<InvalidDataException>(() => ExpertJobRequests.ReadDisjoint([1, 0, 2, 0]), "235 拒绝缺失源列表");
        Reject<InvalidDataException>(() => ExpertJobRequests.ReadDisjoint([1, 0, 2, 0, 0, 0]), "235 拒绝多余尾部");
        var fragment = new DisjointReceipt(new(new(InventoryListType.Main, 5), -1, 0), [new(-1, 9000, 90)]);
        var fragmentAck = ExpertJobPackets.DisjointResult(7, fragment, 123, 299);
        That(fragmentAck.Length == 25 && fragmentAck[6] == 1 && BinaryPrimitives.ReadUInt16LittleEndian(fragmentAck.AsSpan(7)) == ushort.MaxValue &&
            BinaryPrimitives.ReadInt32LittleEndian(fragmentAck.AsSpan(9)) == 9000 && BinaryPrimitives.ReadInt32LittleEndian(fragmentAck.AsSpan(13)) == 90 &&
            BinaryPrimitives.ReadUInt32LittleEndian(fragmentAck.AsSpan(17)) == 123 && BinaryPrimitives.ReadUInt32LittleEndian(fragmentAck.AsSpan(21)) == 299,
            "235碎片展示行保留FFFF、本次增量及完整金币耐久尾部");
        Reject<InvalidDataException>(() => ExpertJobPackets.DisjointResult(7, fragment with { Rewards = [new(-2, 9000, 90)] }, 0, 0),
            "235只允许已定义的FFFF展示哨兵，不接受其他负槽位");
        var store = ExpertJobRequests.ReadCreateStore([0, 0, 0, 0, 0, 7, 0, 0, 0, 8, 0, 9, 0, 10, 0]);
        That(store is { Mode: 0, Name: "", First: 7, Second: 8, Third: 9, Fourth: 10 },
            "C2S 598 是 mode:u8 + 03BDF850 字符串 + u32 + 三个 u16");
        Reject<InvalidDataException>(() => ExpertJobRequests.ReadEmpty([1]), "C2S 239/600 拒绝多余字节");
        Reject<InvalidDataException>(() => ExpertJobRequests.ReadCreateStore([1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]),
            "C2S 598 模式只接受 0 或 3");
        Console.WriteLine($"PASS {Count - checks} 副职业 C2S/ACK 闭合包体断言");
    }
}
