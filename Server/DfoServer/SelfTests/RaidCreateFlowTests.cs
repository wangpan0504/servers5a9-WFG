using static DfoServer.Testing.TestCheck;
using DfoServer.Game.Characters;
using DfoServer.Game.Raids;
using DfoServer.GameWorld.Channels;
using DfoServer.GameWorld.Raids;
using DfoServer.Network.Builders;
using PvfLib;

namespace DfoServer;

// 攻坚队创建：记录/成员块字节对照 2026-10-21 实机探针包（22:59/23:07 t1 0x24E、23:07 op3 M=0 未崩溃并开窗），
// 频道类型 → 团本类型（01DC69B0）、raid.lst 人数上限、单人队表的 key 规则。
internal static class RaidCreateFlowTests
{
    // 实机 t1：key 0x02140001、标题 "T1"、其余记录字段 0、队长块角色号 1 + 名字 licocon、尾 u8 2（本实现写 0）。
    const string LiveT1 =
        "010000000100140202000000543100000000000000000000000000000000010000070000006C69636F636F6E00000000" +
        "00000000000000000000000000000000000000000000000000000000000000000000000000000000000002";

    public static void RunCore()
    {
        int before = Count;
        byte[] list = RaidPackets.RaidList(0x02140001, "T1"u8.ToArray(), 0, 1, CharacterName.Encoding.GetBytes("licocon"));
        byte[] live = Convert.FromHexString(LiveT1);
        That(list.Length == 91 && list.AsSpan(0, 90).SequenceEqual(live.AsSpan(0, 90)) && list[90] == 0,
            "0x24E 记录与实机 t1 逐字节一致（只有尾 u8 +0xC4 改写 0）");
        That(Convert.ToHexString(RaidPackets.OpenWithMembers(0x02140001, [])) == "010014020300000000",
            "0x24F op3 M=0 与实机 23:07 包一致");
        byte[] open = RaidPackets.OpenWithMembers(0x02140001, [(1, CharacterName.Encoding.GetBytes("licocon"))]);
        That(open.Length == 9 + 53 + 7 && open[8] == 1 && open.AsSpan(9).SequenceEqual(live.AsSpan(30, 60)),
            "op3 M=1：成员块与 t1 队长块同形（53 字节 + 名字）");
        That(RaidPackets.CreateSuccess(0x022A0001).SequenceEqual(new byte[] { 1, 0x01, 0x00, 0x2A, 0x02 }), "ACK664 成功 = 01 + u32 key");
        That(RaidPackets.LeaveSuccess.SequenceEqual(new byte[] { 1, 0 }) && RaidPackets.EntryCostSuccess.SequenceEqual(new byte[] { 1 }),
            "ACK665 成功 01 00、ACK658 成功 01");
        bool tooLong = false;
        try { RaidPackets.RaidList(1, new byte[0x100], 0, 1, []); } catch (InvalidDataException) { tooLong = true; }
        That(tooLong, "标题字节数 >= 0x100 拒绝（客户端会不消费字节导致错位）");

        That(RaidTypes.FromChannelType(0x17) == 0 && RaidTypes.FromChannelType(0x19) == 0 && RaidTypes.FromChannelType(0x20) == 1 &&
            RaidTypes.FromChannelType(0x2D) == 2 && RaidTypes.FromChannelType(0x32) == 3 && RaidTypes.FromChannelType(0x3E) == 4 &&
            RaidTypes.FromChannelType(0x3F) == 5 && RaidTypes.FromChannelType(0x36) == 6 && RaidTypes.FromChannelType(0x18) == 6,
            "01DC69B0 频道类型表");

        // 多人聚合保留key/槽位及精确Session清理；重复创建不再替换已有成员。
        RaidMemberTests.RunCore();
        AntonRaidStartTests.RunCore();
        AntonRaidResourceTests.RunCore();
        AntonRaidBattleTests.RunCore();
        LukeRaidResourceTests.RunCore();

        var tokens = new List<PvfToken>();
        var strings = new Dictionary<int, string>();
        void Tag(string tag, params int[] values)
        {
            strings[strings.Count + 1000] = tag; tokens.Add(new(3, strings.Count + 999));
            foreach (int value in values) tokens.Add(new(0, value));
        }
        Tag("[PHASE MAX]", 1); Tag("[FIENDWAR MEMBER MAX]", 8); Tag("[FIENDWAR START MINIMUM MEMBER]", 1); Tag("[MEMBER MAX]", 4);
        var fiend = RaidCatalog.Parse(2, "Etc/Raid/Fiendwar_KR/Fiendwar.etc", tokens, id => strings[id]);
        That(fiend is { MemberMax: 8, StartMinimum: 1, PhaseMax: 1 }, "超时空之战脚本用 FIENDWAR 标签，区内 [MEMBER MAX] 不混入");
        That(RaidCatalog.Parse(0, "x", [], id => strings[id]) is null, "缺人数标签的类型不可创建");
        Console.WriteLine($"PASS {Count - before} 团本建队包体、频道类型表、队伍 key 与脚本解析断言");
    }

    // 真实 PVF：etc/raid/raid.lst 0 Anton(20/10)、1 Luke(8/1)、2 Fiendwar_KR(8/1)、3 Prey(12/1)。
    public static void RunResources(PvfArchive pvf, DfoServer.GameWorld.Items.ItemCatalog items)
    {
        int before = Count;
        var catalog = RaidCatalog.Load(pvf);
        That(catalog.Find(0) is { MemberMax: 20, StartMinimum: 10, PhaseMax: 2 } anton && anton.Path.EndsWith("anton.etc"),
            "安徒恩 RAID MEMBER MAX 20、最低 10 人");
        That(catalog.Find(1) is { MemberMax: 8, StartMinimum: 1, PhaseMax: 2 }, "卢克 8 人");
        That(catalog.Find(2) is { MemberMax: 8, StartMinimum: 1 } fiend && fiend.Path.Contains("fiendwar_kr"), "超时空之战(raid.lst 2 = Fiendwar_KR) 8 人");
        That(catalog.Find(3) is { MemberMax: 12, StartMinimum: 1, PhaseMax: 2 }, "普雷 12 人");
        That(catalog.Find(6) is null, "类型 6（魔界大战频道 0x36 默认值）无脚本");
        AntonRaidResourceTests.RunResources(pvf, catalog, items);
        LukeRaidResourceTests.RunResources(pvf, catalog, items);
        Console.WriteLine($"PASS {Count - before} 团本 raid.lst 人数上限断言（真实 PVF）");
    }
}
