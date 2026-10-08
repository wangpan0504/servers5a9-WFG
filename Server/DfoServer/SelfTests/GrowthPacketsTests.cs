using static DfoServer.Testing.TestCheck;
using System.Buffers.Binary;
using DfoServer.Game.Characters;
using DfoServer.Network.Builders;

namespace DfoServer;

internal static class GrowthPacketsTests
{
    public static void Run()
    {
        int checks = Count;
        var state = new CharacterLearningState(1000, Array.AsReadOnly(new CharacterSkillTree[]
        {
            new(1, 30, 0, Array.AsReadOnly(Array.Empty<LearnedSkill>())),
            new(0, 30, 0, Array.AsReadOnly(Array.Empty<LearnedSkill>()))
        }));
        var ordinary = new CharacterExperienceDisplay(0, 0, 0, 0, 0, 0, 0,
            Array.AsReadOnly(Array.Empty<CharacterExperienceBonus>()), 0, 0, 0, 0, 0, 0, 0);
        var auxiliary = new CharacterGrowthAuxiliaryState(0, 0, 0, 0);
        byte[] golden = Convert.FromHexString(
            "02E803000000000000000000001E001E" +
            "00000000000000000000000000000000" +
            "00000000000000000000000000000000" +
            "00000000000000000000000000000000" +
            "00000000000000000000000000000000" +
            "0000000000000000000000");
        byte[] body = GrowthPackets.Experience(2, state, auxiliary, ordinary);
        That(body.Length == 91 && body.SequenceEqual(golden), "Lv1至2完整91B正文与文档静态样本逐字节一致");
        That(Decode(body).Bonuses.Length == 0, "零count仍读取完整40B尾部");

        var nonzero = state with { Experience = 0x0102030405060708UL,
            Trees = Array.AsReadOnly(new CharacterSkillTree[]
            {
                new(1, 0x2021, 0x4041, Array.AsReadOnly(Array.Empty<LearnedSkill>())),
                new(0, 0x1011, 0x3031, Array.AsReadOnly(Array.Empty<LearnedSkill>()))
            }) };
        var extraState = new CharacterGrowthAuxiliaryState(0x50515253, 0xF1F2F3F4, 0x91929394, 0xA1A2A3A4A5A6A7A8UL);
        var display = new CharacterExperienceDisplay(9, 25, 2, 30, 34, 42, 46,
            Array.AsReadOnly(new[] { new CharacterExperienceBonus(123, 456), new CharacterExperienceBonus(uint.MaxValue, 789) }),
            51, 55, 71, 75, 79, 83, 87);
        body = GrowthPackets.Experience(95, nonzero, extraState, display);
        var wire = Decode(body);
        That(body.Length == 107 && wire.Level == 95 && wire.Experience == nonzero.Experience && wire.Sp0 == 0x1011 && wire.Sp1 == 0x2021 &&
            wire.Tp0 == 0x3031 && wire.Tp1 == 0x4041, "经验完整u64及两树context顺序独立解码");
        That(wire.Auxiliary == extraState && wire.Display == (display with { Bonuses = wire.Bonuses }) &&
            wire.Bonuses.SequenceEqual(display.Bonuses), "非零决斗、胶囊位模式、熟练及全部显示分量保持且不互相覆盖");
        // 独立读取边界：count只控制8B对，不能吞掉固定尾部或借用后续包。
        Reject(() => Decode(body[..^1]), "短尾部拒绝");
        Reject(() => Decode([.. body, 0]), "正文尾随字节拒绝");
        Reject(() => Decode(body[..25]), "已知25B前缀不能作为完整通知");
        var wrongCount = body.ToArray();
        wrongCount[50] = 3;
        Reject(() => Decode(wrongCount), "count超过实际记录的正文拒绝");
        var maximum = Enumerable.Range(0, 255).Select(index => new CharacterExperienceBonus((uint)index, uint.MaxValue - (uint)index)).ToArray();
        body = GrowthPackets.Experience(2, state, auxiliary, ordinary with { Bonuses = Array.AsReadOnly(maximum) });
        That(body.Length == 2131 && Decode(body).Bonuses.SequenceEqual(maximum), "255额外记录及2131B精确边界");
        Reject(() => GrowthPackets.Experience(2, state, auxiliary,
            ordinary with { Bonuses = Array.AsReadOnly(maximum.Append(new CharacterExperienceBonus(255, 0)).ToArray()) }), "count超过u8拒绝");
        Reject(() => GrowthPackets.Experience(0, state, auxiliary, ordinary), "等级零拒绝");
        Reject(() => GrowthPackets.Experience(96, state, auxiliary, ordinary), "范围外等级拒绝");
        Reject(() => GrowthPackets.Experience(2, CharacterLearningState.Uninitialized, auxiliary, ordinary), "缺完整余额不补零");
        Reject(() => GrowthPackets.Experience(2, state, null!, ordinary), "缺附属owner输入不补零");
        Reject(() => GrowthPackets.Experience(2, state, auxiliary, null!), "缺事件分量输入不补零");
        Console.WriteLine($"PASS {Count - checks} NOTI37完整布局、显式附属状态及独立读取边界");

        void Reject(Action action, string name)
        {
            bool failed = false;
            try
            {
                action();
            }
            catch (Exception error) when (error is InvalidDataException or ArgumentNullException)
            {
                failed = true;
            }
            That(failed, name);
        }
    }

    private sealed record WireNotice(byte Level, ulong Experience, ushort Sp0, ushort Sp1, ushort Tp0, ushort Tp1,
        CharacterGrowthAuxiliaryState Auxiliary, CharacterExperienceDisplay Display, CharacterExperienceBonus[] Bonuses);

    private static WireNotice Decode(ReadOnlySpan<byte> body)
    {
        Require(body.Length >= 91);
        int count = body[50], tail = 51 + 8 * count;
        Require(body.Length == 91 + 8 * count);
        var bonuses = new CharacterExperienceBonus[count];
        for (int index = 0; index < count; index++)
        {
            int offset = 51 + 8 * index;
            bonuses[index] = new(BinaryPrimitives.ReadUInt32LittleEndian(body[offset..]),
                BinaryPrimitives.ReadUInt32LittleEndian(body[(offset + 4)..]));
        }
        var auxiliary = new CharacterGrowthAuxiliaryState(BinaryPrimitives.ReadUInt32LittleEndian(body[21..]),
            BinaryPrimitives.ReadUInt32LittleEndian(body[38..]), BinaryPrimitives.ReadUInt32LittleEndian(body[(tail + 8)..]),
            BinaryPrimitives.ReadUInt64LittleEndian(body[(tail + 12)..]));
        var display = new CharacterExperienceDisplay(BinaryPrimitives.ReadUInt32LittleEndian(body[9..]),
            BinaryPrimitives.ReadUInt32LittleEndian(body[25..]), body[29], BinaryPrimitives.ReadUInt32LittleEndian(body[30..]),
            BinaryPrimitives.ReadUInt32LittleEndian(body[34..]), BinaryPrimitives.ReadUInt32LittleEndian(body[42..]),
            BinaryPrimitives.ReadUInt32LittleEndian(body[46..]), bonuses, BinaryPrimitives.ReadUInt32LittleEndian(body[tail..]),
            BinaryPrimitives.ReadUInt32LittleEndian(body[(tail + 4)..]), BinaryPrimitives.ReadUInt32LittleEndian(body[(tail + 20)..]),
            BinaryPrimitives.ReadUInt32LittleEndian(body[(tail + 24)..]), BinaryPrimitives.ReadUInt32LittleEndian(body[(tail + 28)..]),
            BinaryPrimitives.ReadUInt32LittleEndian(body[(tail + 32)..]), BinaryPrimitives.ReadUInt32LittleEndian(body[(tail + 36)..]));
        return new(body[0], BinaryPrimitives.ReadUInt64LittleEndian(body[1..]), BinaryPrimitives.ReadUInt16LittleEndian(body[13..]),
            BinaryPrimitives.ReadUInt16LittleEndian(body[15..]), BinaryPrimitives.ReadUInt16LittleEndian(body[17..]),
            BinaryPrimitives.ReadUInt16LittleEndian(body[19..]), auxiliary, display, bonuses);
    }

    private static void Require(bool value)
    {
        if (!value)
            throw new InvalidDataException("经验通知独立读取边界不匹配");
    }
}
