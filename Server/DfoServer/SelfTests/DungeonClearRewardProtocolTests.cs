using System.Collections.Immutable;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;
using DfoServer.Network.Builders;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class DungeonClearRewardProtocolTests
{
    // 固定样本独立按原码读取顺序编码，覆盖非零标量、两奖励、全部动态段及最终8bool。
    private const string FixedHex = "64000000650000006600000067000000A5C9000000CA000000CB000000CC000000CD000000CE000000CF000000D0000000D1000000D2000000D3000000D4000000D5000000D6000000D7000000D8000000D9000000DA000000DB000000DC000000DD000000DE00000002F5010000F6010000F7010000F801000001590200005A0200002D0100002E0100002F010000300100003101000032010000330100009101000092010000930100009401000002000000BD020000BE020000BF020000C0020000021700320000003412000078569A01002C00C8000000050000004200060100023200000034120000000000002A00000000000000000001C8000000030000004D0400000001C800000004000000000000000132000000F40100000000000001C80000006300000000000000B1040000150500007F0179050000DD05000041060000A50600000100010000010001";

    public static void RunCore()
    {
        int before = Count;
        var values = Fixture();
        byte[] expected = Convert.FromHexString(FixedHex);
        byte[] actual = DungeonClearRewardPackets.Build(values);
        That(expected.Length == 336 && actual.SequenceEqual(expected),
            "NOTI35独立固定非零样本逐字节匹配，15B直接奖励和所有动态段不丢字段");
        var read = Decode(actual)!;
        That(read.Base.SequenceEqual(new uint[] { 100, 101, 102, 103 }) && read.BaseFlag == 165 &&
            read.Bonus.SequenceEqual(Enumerable.Range(201, 22).Select(value => (uint)value)) &&
            read.Post.SequenceEqual(Enumerable.Range(301, 7).Select(value => (uint)value)) &&
            read.Score.SequenceEqual(new uint[] { 401, 402, 403, 404 }),
            "独立读器覆盖4个Base、1flag、22Bonus、7Post及4Stats精确顺序，reserved也保留");
        That(read.BonusA.SequenceEqual(new Pair[] { new(501, 502), new(503, 504) }) &&
            read.BonusB.SequenceEqual(new Pair[] { new(601, 602) }) &&
            read.Quest.SequenceEqual(new Pair[] { new(701, 702), new(703, 704) }),
            "两u8分量列表和i32统计列表保留完整key/value，不能混入奖励资格");
        That(read.Direct.SequenceEqual(new DirectRow[] {
                new(23, 50, 0x1234, 0x5678, 0x9A, 1), new(44, 200, 5, 0x42, 6, 1) }) &&
            values.DirectItems[1].CommittedItem.Core.Value == 19,
            "装备value为品质，堆叠序列化实际新增5而非最终19；slot/耐久/attr/seal来自真实提交物品");
        That(read.Free.Length == 8 && read.Free[0].SequenceEqual(new Pair[] { new(50, 0x1234), new(0, 42) }) &&
            read.Free[7].SequenceEqual(new Pair[] { new(200, 3) }) && read.Free.Skip(1).Take(6).All(items => items.Length == 0) &&
            read.Mode1[1].SequenceEqual(new Pair[] { new(200, 4) }) && read.Mode1[6].SequenceEqual(new Pair[] { new(50, 500) }) &&
            read.Mode2[3].SequenceEqual(new Pair[] { new(200, 99) }),
            "三组各完整8成员候选owner顺序独立读取，空成员不能省略count");
        That(read.Cost == 1101 && read.PreTail == 1201 && read.TailReserved == 1301 &&
            read.CardRule == 127 && read.ResultFlag == 1 &&
            read.TailValues.SequenceEqual(new uint[] { 1401, 1501, 1601, 1701 }) &&
            read.Appearance.SequenceEqual(new byte[] { 1, 0, 1, 0, 0, 1, 0, 1 }),
            "tail保留卡成本、弃用值、规则flag、70C两个endpoint及最终8bool，不套ShowResult假名");
        for (int length = 0; length < expected.Length; length++)
            That(Decode(expected[..length]) is null, "NOTI35独立读器拒绝每个截短位置: " + length);
        That(Decode([.. expected, 0]) is null, "NOTI35独立读器完整消费正文，无额外占位尾");

        var minimum = values with { ResultUi4DC = [], ResultUi4F0 = [], StatsB34 = [], DirectItems = [],
            FreeCandidates = EmptyMembers(), Mode1Candidates = EmptyMembers(), Mode2Candidates = EmptyMembers() };
        byte[] minimumBody = DungeonClearRewardPackets.Build(minimum);
        That(minimumBody.Length == 218 && Decode(minimumBody) is { Appearance.Length: 8 },
            "零动态段仍完整218B且保留8bool与显式非零标量；长度不是结算完成事实");
        Reject(values with { ResultUi4DC = default }, "未显式提供分量列表");
        Reject(values with { ResultUi4F0 = Enumerable.Repeat(new DungeonClearRewardPair(1, 2), 256).ToImmutableArray() },
            "u8分量count256不能截断");
        Reject(values with { StatsB34 = default }, "未显式提供i32统计列表");
        Reject(values with { DirectItems = default }, "未显式提供直接奖励列表");
        Reject(values with { MemberAppearanceFlags79D = default }, "未显式提供最终8bool");
        Reject(values with { MemberAppearanceFlags79D = [true, false] }, "最终成员flags不完整");
        Reject(values with { FreeCandidates = values.FreeCandidates[..7] }, "候选缺成员7");
        Reject(values with { Mode1Candidates = values.Mode1Candidates.Add(new(8, [])) }, "候选追加成员8");
        Reject(values with { Mode2Candidates = values.Mode2Candidates.SetItem(0, new(255, [])) }, "候选owner超7");
        Reject(values with { FreeCandidates = values.FreeCandidates.SetItem(1, new(0, [])) }, "候选owner重复/错序");
        Reject(values with { Mode1Candidates = values.Mode1Candidates.SetItem(0, new(0, default)) }, "候选items未显式提供");
        Reject(values with { Mode2Candidates = values.Mode2Candidates.SetItem(0,
            new(0, Enumerable.Repeat(new DungeonCardDisplayItem(200, 1), 256).ToImmutableArray())) }, "候选u8 count256");
        var maximumU8 = values with {
            ResultUi4DC = Enumerable.Repeat(new DungeonClearRewardPair(1, uint.MaxValue), 255).ToImmutableArray(),
            FreeCandidates = values.FreeCandidates.SetItem(0,
                new(0, Enumerable.Repeat(new DungeonCardDisplayItem(0, uint.MaxValue), 255).ToImmutableArray())) };
        var readMaximumU8 = Decode(DungeonClearRewardPackets.Build(maximumU8));
        That(readMaximumU8 is { BonusA.Length: 255 } && readMaximumU8.Free[0].Length == 255 &&
            readMaximumU8.Free[0][254].Value == uint.MaxValue,
            "NOTI35原码u8 count合法255与u32上界完整保留，不套ACK71 signed127界限");
        var manyDirect = Enumerable.Range(0, 256).Select(index => new DungeonClearRewardDirectItem(
            new InventoryItem(index + 1, new(InventoryListType.Main, (short)index), new(200, ItemKind.Material, 1)), 1)).ToImmutableArray();
        Reject(values with { DirectItems = manyDirect }, "直接奖励u8 count256不能截断");
        var first = values.DirectItems[0];
        Reject(values with { DirectItems = [first, first] }, "同一提交槽重复增量");
        Reject(values with { DirectItems = [first with { CommittedItem = first.CommittedItem with { Uid = 0 } }] }, "规划中的UID0不能发布");
        Reject(values with { DirectItems = [first with { CommittedItem = first.CommittedItem with {
            Position = new(InventoryListType.PersonalCargo, 23) } }] }, "直接奖励不能猜成仓库容器");
        Reject(values with { DirectItems = [first with { WireValue = 1 }] }, "装备品质不能误当数量1");
        Reject(values with { DirectItems = [values.DirectItems[1] with { WireValue = 20 }] }, "堆叠增量超过提交后实际数量");
        Reject(values with { DirectItems = [first with { CommittedItem = first.CommittedItem with {
            Core = first.CommittedItem.Core with { EnchantCardId = 123 } } }] }, "15B无法表达附魔不能静默简化Core");
        var maximumFrame = minimum with { DirectItems = values.DirectItems,
            StatsB34 = Enumerable.Range(1, 8159).Select(index => new DungeonClearRewardPair((uint)index, 1)).ToImmutableArray() };
        byte[] fullFrame = DungeonClearRewardPackets.Build(maximumFrame);
        That(fullFrame.Length == 65520 && Decode(fullFrame) is { Quest.Length: 8159, Direct.Length: 2 },
            "完整65520B边界合法：218+2*15+8159*8；不会先写再截断");
        Reject(maximumFrame with { StatsB34 = maximumFrame.StatsB34.Add(new(9999, 1)) }, "超过帧上限8B整包拒绝");
        That(actual.SequenceEqual(expected), "构造其他输入及拒绝路径不改变先前已编码冻结正文");
        Console.WriteLine($"PASS {Count - before} 完整NOTI35字段、动态段与帧边界断言");
    }

    private static DungeonClearRewardValues Fixture() => new(
        new(100, 101, 102, 103, 165),
        new(201, 202, 203, 204, 205, 206, 207, 208, 209, 210, 211, 212, 213, 214, 215, 216, 217, 218, 219, 220, 221, 222),
        [new(501, 502), new(503, 504)], [new(601, 602)], new(301, 302, 303, 304, 305, 306, 307),
        new(401, 402, 403, 404), [new(701, 702), new(703, 704)],
        [new(new InventoryItem(101, new(InventoryListType.Main, 23), new(50, ItemKind.Equipment, 0x1234) {
            Durability = 0x5678, Attr = 0x9A, SealFlag = 1 }), 0x1234),
         new(new InventoryItem(102, new(InventoryListType.Main, 44), new(200, ItemKind.Material, 19) {
            Durability = 0x42, Attr = 6, SealFlag = 1 }), 5)],
        EmptyMembers().SetItem(0, new(0, [new(50, 0x1234), new(0, 42)])).SetItem(7, new(7, [new(200, 3)])), 1101,
        EmptyMembers().SetItem(1, new(1, [new(200, 4)])).SetItem(6, new(6, [new(50, 500)])),
        EmptyMembers().SetItem(3, new(3, [new(200, 99)])), 1201, new(1301, 127, true, 1401, 1501, 1601, 1701),
        [true, false, true, false, false, true, false, true]);

    private static ImmutableArray<DungeonClearRewardMemberCandidates> EmptyMembers() =>
        Enumerable.Range(0, 8).Select(index => new DungeonClearRewardMemberCandidates((byte)index, [])).ToImmutableArray();

    private static void Reject(DungeonClearRewardValues values, string label)
    {
        bool rejected = false;
        try { DungeonClearRewardPackets.Build(values); }
        catch (InvalidDataException) { rejected = true; }
        That(rejected, "NOTI35拒绝: " + label);
    }

    private readonly record struct Pair(uint Key, uint Value);
    private readonly record struct DirectRow(ushort Slot, uint ItemId, uint Value, ushort Durability, byte Attr, ushort Seal);
    private sealed record Decoded(uint[] Base, byte BaseFlag, uint[] Bonus, Pair[] BonusA, Pair[] BonusB, uint[] Post,
        uint[] Score, Pair[] Quest, DirectRow[] Direct, Pair[][] Free, uint Cost, Pair[][] Mode1, Pair[][] Mode2,
        uint PreTail, uint TailReserved, byte CardRule, byte ResultFlag, uint[] TailValues, byte[] Appearance);

    // 独立reader按native固定序列消费，不调用builder的写入helper/长度计算或值模型。
    private static Decoded? Decode(byte[] body)
    {
        try
        {
            using var stream = new MemoryStream(body, false);
            using var reader = new BinaryReader(stream);
            uint[] baseValues = UInts(reader, 4);
            byte flag = reader.ReadByte();
            uint[] bonus = UInts(reader, 22);
            Pair[] bonusA = Pairs(reader, reader.ReadByte());
            Pair[] bonusB = Pairs(reader, reader.ReadByte());
            uint[] post = UInts(reader, 7);
            uint[] score = UInts(reader, 4);
            Pair[] quest = Pairs(reader, reader.ReadInt32());
            var direct = new DirectRow[reader.ReadByte()];
            for (int index = 0; index < direct.Length; index++)
                direct[index] = new(reader.ReadUInt16(), reader.ReadUInt32(), reader.ReadUInt32(),
                    reader.ReadUInt16(), reader.ReadByte(), reader.ReadUInt16());
            Pair[][] free = Members(reader);
            uint cost = reader.ReadUInt32();
            Pair[][] mode1 = Members(reader);
            Pair[][] mode2 = Members(reader);
            uint preTail = reader.ReadUInt32();
            uint tailReserved = reader.ReadUInt32();
            byte cardRule = reader.ReadByte();
            byte resultFlag = reader.ReadByte();
            uint[] tail = UInts(reader, 4);
            var appearance = new byte[8];
            for (int index = 0; index < appearance.Length; index++)
                appearance[index] = reader.ReadByte();
            return stream.Position == stream.Length ? new(baseValues, flag, bonus, bonusA, bonusB, post, score,
                quest, direct, free, cost, mode1, mode2, preTail, tailReserved, cardRule, resultFlag, tail, appearance) : null;
        }
        catch (EndOfStreamException) { return null; }
        catch (InvalidDataException) { return null; }
    }

    private static uint[] UInts(BinaryReader reader, int count)
    {
        var values = new uint[count];
        for (int index = 0; index < values.Length; index++)
            values[index] = reader.ReadUInt32();
        return values;
    }

    private static Pair[] Pairs(BinaryReader reader, int count)
    {
        if (count < 0 || count > (reader.BaseStream.Length - reader.BaseStream.Position) / 8)
            throw new InvalidDataException("动态行数超过完整正文");
        var pairs = new Pair[count];
        for (int index = 0; index < count; index++)
            pairs[index] = new(reader.ReadUInt32(), reader.ReadUInt32());
        return pairs;
    }

    private static Pair[][] Members(BinaryReader reader)
    {
        var members = new Pair[8][];
        for (int index = 0; index < members.Length; index++)
            members[index] = Pairs(reader, reader.ReadByte());
        return members;
    }
}
