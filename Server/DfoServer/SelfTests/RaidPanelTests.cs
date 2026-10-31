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
