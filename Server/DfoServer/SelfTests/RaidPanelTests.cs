using System.Buffers.Binary;
using DfoServer.GameWorld.Raids;
using DfoServer.Network.Builders;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class RaidPanelTests
{
    internal static void RunCore()
    {
        That(DfoServer.Network.Parsers.RaidRequests.Search([1]).Closed, "833原生关闭模式只有单字节，不冒充新查询");
        Reject<InvalidDataException>(() => DfoServer.Network.Parsers.RaidRequests.Search([1, 0]));
        var user = DfoServer.Network.Parsers.RaidRequests.CheckUser([2, 0x34, 0x12, 39]);
        That(user == new DfoServer.Network.Parsers.RaidUserRequest(2, 0x1234, 39), "891角色定位保留原生server/character/channel顺序");
        Reject<InvalidDataException>(() => DfoServer.Network.Parsers.RaidRequests.CheckUser([2, 0, 0]));
        That(DfoServer.Network.Parsers.RaidRequests.MemberState([1]) && !DfoServer.Network.Parsers.RaidRequests.MemberState([0]),
            "825仅接受本人活动状态布尔值");
        Reject<InvalidDataException>(() => DfoServer.Network.Parsers.RaidRequests.MemberState([2]));
        Reject<InvalidDataException>(() => DfoServer.Network.Parsers.RaidRequests.MemberState([0, 0]));
        Bytes(RaidPackets.CheckedUser(null), new byte[] { 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, "在线未入团查询成功但不伪造团编号或成员");
        var boundary = new DateTimeOffset(2026, 11, 3, 6, 0, 0, TimeSpan.FromHours(8));
        var before = DfoServer.Game.Raids.RaidClearCounts.Periods(boundary.AddSeconds(-1));
        var after = DfoServer.Game.Raids.RaidClearCounts.Periods(boundary);
        That(before[2].Week == 20261027 && after[2].Week == 20261103 &&
            before[0].Week == after[0].Week && after[2].DayStart - before[2].DayStart == 86400,
            "漩涡周二06点同时重置日周，其他团本不跟随错误重置");
        var custom = new DfoServer.Game.Raids.RaidWeeklyCycle(DayOfWeek.Saturday, new(6, 0), TimeSpan.FromHours(8));
        That(DfoServer.Game.Raids.RaidClearCounts.Periods(boundary, custom)[1].Week == custom.Key(boundary),
            "卢克次数显示使用实际开战策略周期");
        var strings = new List<string>();
        RaidDefinition Parse(params object[] source)
        {
            PvfToken Token(object value)
            {
                if (value is int number) return new(0, number);
                strings.Add((string)value); return new(3, strings.Count - 1);
            }
            return RaidCatalog.Parse(3, "synthetic/raid.etc", new object[]
                { "[raid member max]", 12, "[raid start minimum member]", 1, "[phase max]", 2 }
                .Concat(source).Select(Token).ToArray(), index => strings[index])!;
        }
        var valid = Parse("[entry cost]", 100, 2, "[/entry cost]", "[single entry cost]", 200, 1, "[/single entry cost]");
        That(valid.Cost(false) == new RaidCost(100, 2) && valid.Cost(true) == new RaidCost(200, 1),
            "普通与引导费用独立解析，不依赖战斗资源装配");
        foreach (var source in new object[][]
        {
            ["[entry cost]", 100, 1], ["[entry cost]", 100, 0, "[/entry cost]"],
            ["[entry cost]", 100, 1, 2, "[/entry cost]"],
            ["[entry cost]", 100, 1, "[/entry cost]", "[entry cost]", 200, 1, "[/entry cost]"],
            ["[entry cost]", 100, 1, "[/entry cost]", 7],
            ["[entry cost]", 100, 1, "[/entry cost]", "[/entry cost]"],
            ["[phase]", "[entry cost]", 100, 1, "[/entry cost]", "[/phase]"]
        }) That(Parse(source).EntryCost is null, "缺闭合、非法数量、多列或重复费用不可投影就绪");
        That(Parse("[entry cost]", 100, 1, "[/entry cost]").Cost(true) is null, "缺引导票声明不回退为普通票");
        foreach (byte type in new byte[] { 0, 1, 2, 3 })
        {
            (ushort Character, byte[] Name, byte Squad)[] members = [(7, "leader"u8.ToArray(), 1), (9, "mate"u8.ToArray(), 2)];
            var plain = RaidPackets.Information(1, 123, [], type, 7, members[0].Name, members);
            var counted = RaidPackets.Information(1, 123, [], type, 7, members[0].Name, members,
                clearCounts: new Dictionary<ushort, byte> { [7] = 9, [9] = 255 });
            var expected = plain.ToArray(); expected[7 + 17 + 6] = 9; expected[7 + 59 + 17 + 4] = 255;
            Bytes(counted, expected, "四团原生成员包只改变已证通关字节，其余宽度和未知零值不变");
            var counts = ReadClears(counted);
            That(counts[7] == 9 && counts[9] == 255, "独立读包保留个人计数而非全团图数");
        }
        foreach (bool guide in new[] { false, true })
        foreach (bool canStart in new[] { false, true })
        {
            byte[] title = "panel"u8.ToArray(), name = "leader"u8.ToArray();
            (ushort Character, byte[] Name, byte Squad)[] members = [(7, name, 0)];
            var records = new[]
            {
                (RaidPackets.RaidList(123, title, 2, 7, name, canStart: canStart, guide: guide), 4),
                (RaidPackets.UpdateRecord(123, title, 2, 7, name, canStart: canStart, guide: guide), 8),
                (RaidPackets.Information(2, 123, title, 2, 7, name, members, canStart: canStart, guide: guide), 6),
                (RaidPackets.SearchResults(1, 0, [(123, title, 2, 7, name, 0, canStart, 1)], guide ? new HashSet<uint> { 123 } : null), 12)
            };
            foreach (var (body, offset) in records)
                That(ReadGuideFlag(body, offset) == guide, "漩涡列表、更新、信息及搜索只投影实际引导模式，不借可开战串模式");
        }
    }

    internal static bool ReadGuideFlag(byte[] body, int recordOffset)
    {
        int titleLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(recordOffset + 4)));
        int at = checked(recordOffset + 15 + titleLength);
        That(body.Length > at && body[at] <= 1, "原生团记录模式位宽度与位置有效");
        return body[at] == 1;
    }

    internal static Dictionary<byte, (uint Daily, uint Weekly)> ReadWeeklyClears(byte[] body)
    {
        That(body.Length > 0 && body[0] == 4 && body.Length == 41, "四类团本次数保留完整原生行宽");
        var result = new Dictionary<byte, (uint, uint)>();
        for (int index = 0; index < body[0]; index++)
        {
            int offset = 1 + index * 10;
            That(body[offset] < 4 && body[offset + 9] == 0, "次数类型有效且未猜造引导子表");
            That(result.TryAdd(body[offset], (BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(offset + 1)),
                BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(offset + 5)))), "次数表类型不能重复");
        }
        return result;
    }

    internal static Dictionary<ushort, bool> ReadMemberStates(byte[] body)
    {
        That(body.Length >= 4, "成员状态快照保留u32数量");
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(body);
        That(count <= byte.MaxValue && body.Length == 4 + count * 3, "成员状态快照完整消费每个u16角色及布尔值");
        var result = new Dictionary<ushort, bool>();
        for (int at = 4; at < body.Length; at += 3)
        {
            That(body[at + 2] <= 1 && result.TryAdd(BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(at)), body[at + 2] == 1),
                "活动状态不能重复成员或猜造标记");
        }
        return result;
    }

    internal static Dictionary<ushort, byte> ReadClears(byte[] body)
    {
        That(body.Length >= 7 && body[0] == 1 && body[1] == 1, "成员更新使用原生1361成功mode1");
        var result = new Dictionary<ushort, byte>();
        int at = 7;
        for (int index = 0; index < body[6]; index++)
        {
            int nameLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(at + 3)));
            result.Add(BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(at)), body[at + 17 + nameLength]);
            at += 53 + nameLength;
        }
        That(at == body.Length, "成员计数读取完整消费包体，不跨入下一成员或留下尾字节");
        return result;
    }

    internal static void RunResources(PvfArchive pvf)
    {
        var catalog = RaidCatalog.Load(pvf);
        foreach (var (type, item, guide) in new[] { (0, 10096296, 0), (1, 10159649, 0), (2, 10306760, 10309054), (3, 10308560, 10309342) })
        {
            var definition = catalog.Find((byte)type)!;
            That(definition.Cost(false) == new RaidCost(item, 1) && definition.Resources is null,
                "四团真实普通票费无需加载战斗资源：" + type);
            That(definition.Cost(true) == (guide == 0 ? null : new RaidCost(guide, 1)),
                "四团真实引导票声明独立于普通票：" + type);
        }
    }
}
